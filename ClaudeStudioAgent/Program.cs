using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ClaudeStudioAgent;
using ClaudeStudioShared;

// Hook mode: when claude.exe spawns us as a PreToolUse hook, route to HookMode
// and exit. This branch never enters the normal request loop.
if (args.Length >= 2 && args[0] == "--hook")
{
    return await HookMode.RunAsync(args[1]);
}

// PostToolUse hook: queries the extension for the edited file's diagnostics and
// surfaces them to claude. Also a one-shot subprocess, never the request loop.
if (args.Length >= 2 && args[0] == "--hook-post")
{
    return await HookMode.RunPostAsync(args[1]);
}

// Phase 2 (gated): when enabled, the agent stands up a named pipe server that
// hook helper processes connect to in order to broker permission prompts.
PermissionPipeServer? pipeServer = null;
if (Environment.GetEnvironmentVariable("CLAUDESTUDIO_HOOK_ENABLE") == "1")
{
    pipeServer = new PermissionPipeServer();
    pipeServer.Start();
}

// Sweep stale hook-settings temp dirs left behind by crashed agent processes.
// Each "ask" session creates `%TEMP%/claudestudio-<pid>-<guid>/` and deletes it
// in DisposeAsync — but a VS crash, Task Manager kill, or power loss bypasses
// that path, leaving orphans accumulating forever. Age-based (>24h) cleanup
// in background so it doesn't delay startup, and so an active sibling session
// from minutes ago is never touched.
_ = Task.Run(() =>
{
    try
    {
        var tempBase = Path.GetTempPath();
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var dir in Directory.EnumerateDirectories(tempBase, "claudestudio-*"))
        {
            try
            {
                var name = Path.GetFileName(dir);
                // Defensive: name must look like `claudestudio-<digits>-<hex>` —
                // skip anything else that happened to match the wildcard.
                if (string.IsNullOrEmpty(name) || !name.Contains("-")) continue;
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* in-use, locked, or already gone — ignore */ }
        }
    }
    catch { }
});

ClaudeSession? session = null;

// Stdin reading is decoupled from request processing. The main loop spends most
// of its time inside SendMessageAsync, blocked on claude's stdout. If stdin were
// read on that same thread, PermissionResponse lines arriving mid-turn would
// queue up until the turn finished — but the turn can't finish because the hook
// (and thus claude) is waiting on that very response. Deadlock.
//
// Fix: a dedicated reader Task drains stdin into a Channel. PermissionResponse
// is dispatched inline (it just signals a TCS and returns); everything else is
// queued for the main loop.
var requestChannel = Channel.CreateUnbounded<ChatRequest>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = true
});

var stdinReader = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ChatRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ChatRequest>(line);
            }
            catch (Exception ex)
            {
                EmitError($"failed to parse request: {ex.Message}");
                continue;
            }
            if (request == null) continue;

            if (request.PermissionResponse != null)
            {
                var pr = request.PermissionResponse;
                if (!string.IsNullOrEmpty(pr.AllowSession))
                    pipeServer?.AllowForSession(pr.AllowSession);

                // Hook-pipe prompts first; control-channel gates (ExitPlanMode
                // plan approval) ride the same PermissionResponse message.
                if (pipeServer?.Respond(pr.ToolUseId, pr.Allow, pr.Reason) == true)
                    continue;
                var controlHandled = false;
                if (session != null)
                {
                    try { controlHandled = await session.TryRespondControlPermissionAsync(pr); }
                    catch (Exception ex) { EmitError($"control permission response failed: {ex.Message}"); controlHandled = true; }
                }
                if (!controlHandled)
                    EmitError($"no pending permission request for tool_use_id {pr.ToolUseId}");
                continue;
            }

            if (request.DiagnosticsResponse != null)
            {
                if (pipeServer == null)
                {
                    EmitError("diagnostics response received but hook server is disabled");
                    continue;
                }
                var dr = request.DiagnosticsResponse;
                pipeServer.RespondDiagnostics(dr.RequestId, dr.Text);
                continue;
            }

            if (request.AskAnswer != null)
            {
                // AskUserQuestion answer → write control_response to claude.stdin.
                // The main loop is blocked awaiting claude's stdout (claude is
                // blocked awaiting this very reply), so bypass the request channel.
                if (session == null)
                {
                    EmitError($"ask-answer received but no active session (toolUseId={request.AskAnswer.ToolUseId})");
                    continue;
                }
                try { await session.SendControlResponseAsync(request.AskAnswer); }
                catch (Exception ex) { EmitError($"ask-answer write failed: {ex.Message}"); }
                continue;
            }

            if (request.CancelTurn)
            {
                // Unblock any hook still waiting on a permission decision FIRST:
                // claude sits inside PreToolUse until it gets an answer, and
                // disposing while it was blocked wedged this stdin loop — the
                // second cancel was never read and no done was ever emitted
                // (rodada 12). With the denies delivered, claude can wind down
                // and the dispose below completes normally.
                pipeServer?.FailPending("Turn canceled by user");

                // Hard cancel: dispose the active session so SendMessageAsync's
                // read loop in the main loop sees ObjectDisposedException, the
                // catch block then EmitDone()s — extension's reader is freed
                // and ready for the next request. Setting session = null is
                // done by that catch block (we can't touch the local var here).
                if (session != null)
                {
                    try { await session.DisposeAsync(); }
                    catch (Exception ex) { EmitError($"cancel dispose failed: {ex.Message}"); }
                }
                continue;
            }

            await requestChannel.Writer.WriteAsync(request);
        }
    }
    catch (Exception ex)
    {
        EmitError($"stdin reader failed: {ex.Message}");
    }
    finally
    {
        requestChannel.Writer.TryComplete();
    }
});

try
{
    Console.WriteLine("READY");
    Console.Out.Flush();

    await foreach (var request in requestChannel.Reader.ReadAllAsync())
    {
        if (request.ResetSession)
        {
            if (session != null)
            {
                await session.DisposeAsync();
                session = null;
            }
            continue;
        }

        if (request.RewindRequest != null)
        {
            if (session == null || !session.IsAlive)
                EmitError("rewind: no active session");
            else
            {
                try { await session.RewindAsync(request.RewindRequest.UserMessageId, request.RewindRequest.DryRun); }
                catch (Exception ex) { EmitError($"rewind failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        if (request.ContextUsage)
        {
            if (session == null || !session.IsAlive)
                EmitError("context-usage: no active session");
            else
            {
                try { await session.GetContextUsageAsync(); }
                catch (Exception ex) { EmitError($"context-usage failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        if (!string.IsNullOrEmpty(request.SessionTitleDescription))
        {
            if (session == null || !session.IsAlive)
                EmitError("session-title: no active session");
            else
            {
                try { await session.GenerateSessionTitleAsync(request.SessionTitleDescription!); }
                catch (Exception ex) { EmitError($"session-title failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        if (request.McpStatus)
        {
            if (session == null || !session.IsAlive)
                EmitError("mcp-status: no active session");
            else
            {
                try { await session.GetMcpStatusAsync(); }
                catch (Exception ex) { EmitError($"mcp-status failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        if (!string.IsNullOrEmpty(request.McpReconnectServer))
        {
            if (session == null || !session.IsAlive)
                EmitError("mcp-reconnect: no active session");
            else
            {
                try { await session.ReconnectMcpServerAsync(request.McpReconnectServer!); }
                catch (Exception ex) { EmitError($"mcp-reconnect failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        if (!string.IsNullOrEmpty(request.SideQuestion))
        {
            if (session == null || !session.IsAlive)
                EmitError("side-question: no active session");
            else
            {
                try { await session.AskSideQuestionAsync(request.SideQuestion!); }
                catch (Exception ex) { EmitError($"side-question failed: {ex.Message}"); }
            }
            EmitDone();
            continue;
        }

        // Permission rules ride along with every chat request; refreshing them
        // here (not at spawn) means edits in the UI apply on the next send
        // without restarting the session.
        pipeServer?.SetRules(PermissionRuleMerge.Merge(request.ClaudeSettings, request.WorkingDirectory));
        pipeServer?.SetPermissionTimeout(request.ClaudeSettings?.PermissionTimeoutMinutes ?? 0);

        var wantKey = ClaudeSession.MakeKey(request);

        // ask↔plan share a spawn profile (same key), so a mode flip reuses the
        // warm session and switches live via set_permission_mode (PoC-confirmed
        // over stdio on 2.1.144). If the switch fails, drop the session and let
        // the respawn block below rebuild it under the requested mode — the
        // user's message still goes through either way.
        if (session != null && session.Key == wantKey && session.IsAlive &&
            !string.Equals(session.UiPermissionMode, request.PermissionMode ?? "ask", StringComparison.Ordinal))
        {
            var switched = false;
            try { switched = await session.SwitchPermissionModeAsync(request.PermissionMode ?? "ask"); }
            catch (Exception ex)
            {
                // Not user-facing (the respawn fallback recovers) — the timing
                // channel lands in the extension's OutputLog.
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "timing", Text = $"set-permission-mode failed, respawning: {ex.Message}" }));
                Console.Out.Flush();
            }
            if (!switched)
            {
                await session.DisposeAsync();
                session = null;
            }
        }

        // !IsAlive covers the post-cancel case: the session object lingers
        // (disposed) because SendMessageAsync returned via stdout EOF instead of
        // throwing, so the catch below never nulled it. Respawn instead of
        // sending into a dead process (which would throw "session not started").
        if (session == null || session.Key != wantKey || !session.IsAlive)
        {
            if (session != null) await session.DisposeAsync();
            session = new ClaudeSession(request, pipeServer?.PipeName);
            try
            {
                await session.StartAsync();
            }
            catch (ClaudeNotFoundException ex)
            {
                // Dedicated signal → UI renders an install card, not a raw error.
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "claude-not-found", Text = ex.Message }));
                Console.Out.Flush();
                session = null;
                EmitDone();
                continue;
            }
            catch (Exception ex)
            {
                EmitError($"failed to start claude session: {ex.Message}");
                session = null;
                EmitDone();
                continue;
            }
        }

        try
        {
            await session.SendMessageAsync(request.Message ?? "");
        }
        catch (Exception ex)
        {
            EmitError($"send failed: {ex.Message}");
            EmitDone();
            // Session may be dead — drop it so next request respawns
            await session.DisposeAsync();
            session = null;
        }
    }
}
catch (Exception ex)
{
    EmitError(ex.ToString());
}
finally
{
    requestChannel.Writer.TryComplete();
    try { await stdinReader; } catch { }
    if (session != null) await session.DisposeAsync();
    if (pipeServer != null) await pipeServer.DisposeAsync();
}

return 0;

static void EmitError(string text)
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = text }));
    Console.Out.Flush();
}

static void EmitDone()
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "done" }));
    Console.Out.Flush();
}


/// <summary>
/// Holds a single persistent claude.exe instance running with --input-format stream-json
/// --output-format stream-json. Messages are written to its stdin as NDJSON; output is
/// parsed and forwarded to the extension via ChatChunk lines on our stdout.
///
/// Restart is triggered externally (via key mismatch in the main loop) when model,
/// effort, permissionMode, working dir, or resume params change.
/// </summary>
sealed class ClaudeSession : IAsyncDisposable
{
    private readonly string _model;
    private readonly string? _effort;
    private readonly string _permissionMode;
    private readonly string? _workingDirectory;
    private readonly string? _resumeSessionId;
    private readonly bool _forkSession;
    private readonly bool _autoResume;
    private readonly string? _pipeName;
    private readonly ClaudeSettings? _claudeSettings;
    private readonly string? _cliPath;
    private readonly string? _sessionName;
    private readonly decimal? _maxBudgetUsd;
    private readonly string? _fallbackModel;

    // #14: tracks the actually-used model across turns so a --fallback-model
    // engagement (or recovery) only signals the UI once, on the real change.
    private string? _lastActiveModel;

    // Set once by StartAsync's ProbeClaudeAsync call; read by the perf-log
    // writer (#22b) so each request line records which CLI build produced it.
    private string? _claudeVersion;

    // Set by FindClaudeExe's onPathShadow when the preferred native install
    // (~\.local\bin) differs from a claude.exe on PATH (issue #7). Consumed once
    // after the version probe to surface the duplicate visibly (chosen, other).
    private (string chosen, string other)? _duplicateInstall;

    private Process? _proc;
    private StreamWriter? _stdin;
    // Serializes writes to claude.stdin so partial NDJSON lines can't interleave.
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    // Pending AskUserQuestion control_requests, keyed by tool_use_id (the only id
    // the UI knows). Maps to the request_id needed for the control_response and
    // the original tool input (echoed back with an `answers` field).
    private readonly ConcurrentDictionary<string, PendingAsk> _pendingAsks = new();

    // Pending control-channel permission gates (ExitPlanMode plan approval),
    // keyed by tool_use_id. Answered via the same PermissionResponse the hook
    // modal uses — the stdin reader tries the pipe first, then this registry.
    private readonly ConcurrentDictionary<string, PendingAsk> _pendingControlPerms = new();
    private StreamReader? _stdout;
    private readonly StringBuilder _stderrBuffer = new();
    private Task? _stderrPump;
    private string? _settingsTempDir;
    public string? SessionId { get; private set; }
    public string Key { get; }

    // True only while the underlying claude.exe is started and running. A hard
    // cancel (CancelTurn) disposes the session out-of-band, after which claude's
    // stdout EOF makes SendMessageAsync return normally (no exception) — so the
    // main loop can't rely on a catch to drop the dead session. The reuse check
    // consults this instead and respawns when false.
    public bool IsAlive => _proc != null && !_proc.HasExited && _stdin != null && _stdout != null;

    public ClaudeSession(ChatRequest request, string? pipeName)
    {
        _model = request.Model ?? "claude-sonnet-5";
        _effort = request.Effort;
        _permissionMode = request.PermissionMode ?? "ask";
        _workingDirectory = request.WorkingDirectory;
        _resumeSessionId = request.ResumeSessionId;
        _forkSession = request.ForkSession;
        _autoResume = request.AutoResume;
        _pipeName = pipeName;
        _claudeSettings = request.ClaudeSettings;
        _cliPath = request.CliPath;
        _maxBudgetUsd = request.MaxBudgetUsd;
        _fallbackModel = request.FallbackModel;
        _lastActiveModel = _model;
        // Spawn-time hint only — deliberately NOT in MakeKey (a title change
        // alone must not force a respawn; it re-persists at the next natural one).
        _sessionName = request.SessionName;
        UiPermissionMode = _permissionMode;
        Key = MakeKey(request);
    }

    // The UI-level mode currently in effect ("ask"/"plan"/"yolo") — updated by
    // SwitchPermissionModeAsync when the main loop flips a reused session.
    public string UiPermissionMode { get; private set; }

    // Live permission-mode switch without a respawn (set_permission_mode over
    // stdio, PoC-confirmed on 2.1.144). Maps UI modes to CLI modes the same way
    // the spawn does: plan → plan; ask → bypassPermissions when the hook pipe
    // is the gatekeeper, else claude's default mode.
    public async Task<bool> SwitchPermissionModeAsync(string uiMode)
    {
        var cliMode = uiMode == "plan" ? "plan" : (_pipeName != null ? "bypassPermissions" : "default");
        var resp = await SendControlRequestAsync(new { subtype = "set_permission_mode", mode = cliMode }, "set-permission-mode", quietErrors: true);
        if (resp == null) return false;
        UiPermissionMode = uiMode;
        return true;
    }

    // ForkSession must be in the key: a live session resumed WITHOUT it would
    // otherwise look identical (same ResumeSessionId) to a fork request and
    // get reused, silently dropping --fork-session (#12).
    public static string MakeKey(ChatRequest r) =>
        $"{r.Model}|{r.Effort}|{PermissionProfile(r.PermissionMode)}|{r.WorkingDirectory}|{r.ResumeSessionId}|{r.ForkSession}|{r.AutoResume}|{r.CliPath}|{r.MaxBudgetUsd}|{r.FallbackModel}|{SpawnSettingsFingerprint(r.ClaudeSettings)}";

    // Settings claude only reads at startup (V7: attribution / cleanup /
    // auto-compact). Baking them into the key makes a toggle flip respawn the
    // session so the new settings.json actually applies — without this, a
    // mid-conversation change was silently ignored until some unrelated respawn
    // (validation round 2026-07-16). Permission rules stay OUT of the key: the
    // pipe re-reads them on every request, no respawn needed.
    private static string SpawnSettingsFingerprint(ClaudeSettings? s) =>
        s == null ? "True||True" : $"{s.CoAuthoredBy}|{s.CleanupPeriodDays}|{s.AutoCompact}";

    // ask and plan share a spawn profile (hooks + prompt tool + settings) and
    // flip between each other live via set_permission_mode — so the key must
    // not change between them. yolo spawns with --dangerously-skip-permissions
    // (a flag, not a mode) and keeps its own profile (mode changes to/from
    // yolo still respawn). #8: dontAsk is a genuine CLI-native mode (probed
    // 2026-07-20) — the CLI blocks all writes itself before a PreToolUse hook
    // would ever see them, so it needs neither hooks nor bypassPermissions;
    // its own profile means switching to/from it always respawns too, same as
    // yolo (no attempt to live-switch into/out of a mode with a different
    // spawn shape — set_permission_mode is only PoC-confirmed for plan<->ask).
    private static string PermissionProfile(string? mode) =>
        mode == "yolo" ? "yolo" : mode == "dontAsk" ? "dontAsk" : "hooked";

    // #19: what claude's own init.permissionMode should read back as, given
    // how each UI mode actually spawns (mirrors the branches ~line 622+).
    // "ask" reports "bypassPermissions" too when a hook pipe is gatekeeping —
    // that's the expected, correct case, not a mismatch.
    private string ExpectedCliPermissionMode() => _permissionMode switch
    {
        "yolo" => "bypassPermissions",
        "dontAsk" => "dontAsk",
        "plan" => "plan",
        _ => _pipeName != null ? "bypassPermissions" : "default", // "ask"
    };

    // Appended to claude's system prompt (--append-system-prompt). Mirrors the
    // official VS Code extension's append (v2.1.145): markdown-link file
    // references make them clickable in the webview (the UI turns non-http
    // links into open-file requests), and the ide_selection paragraph tells the
    // model what the selection block attached to user messages means.
    private const string VsContextPrompt = """
# Visual Studio Extension Context

You are running inside a Visual Studio 2022 extension environment.

## Code References in Text
IMPORTANT: When referencing files or code locations, use markdown link syntax to make them clickable:
- For files: [Program.cs](src/Program.cs)
- For specific lines: [Program.cs:42](src/Program.cs#L42)
- For a range of lines: [Program.cs:42-51](src/Program.cs#L42-L51)
- For folders: [src/utils/](src/utils/)
Unless explicitly asked for by the user, DO NOT USE backticks ` or HTML tags like code for file references - always use markdown [text](link) format.
The URL links should be relative paths from the working directory.

## User Selection Context
The user's IDE selection (if any) is included in the conversation context and marked with ide_selection tags. This represents code or text the user has highlighted in their editor and may or may not be relevant to their request.
""";

    public async Task StartAsync()
    {
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = ClaudeExeLocator.FindClaudeExe(_cliPath, EmitWarn,
                onPathShadow: (chosen, other) => _duplicateInstall = (chosen, other)),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory))
            psi.WorkingDirectory = _workingDirectory;

        // Enable native file checkpointing so the UI can later drive a
        // rewind_files control_request to revert edits to a prior message.
        // PoC-confirmed lever; harmless when the feature isn't used.
        psi.EnvironmentVariables["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true";

        // Telemetry entrypoint marker (V14) — distinguishes this extension from
        // plain CLI / official VS Code usage in claude's logs and telemetry.
        psi.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = "claude-vs2022";

        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--include-partial-messages");
        // #7: probed 2026-07-20 — the echoed line carries isReplay:true, so the
        // UI's delivered-ack can match it without any text comparison. Always
        // on: purely additive (one extra `type:"user"` line per turn) and the
        // agent already ignores unrecognized events.
        psi.ArgumentList.Add("--replay-user-messages");
        // #16: benchmarked 2026-07-20 against the real CLI — moving cwd/env/git
        // status out of the system prompt (into the first user message) kept
        // the big cached prefix stable across git-status changes that would
        // otherwise force a respawn to recreate it. Measured ~57% less cache
        // write and roughly half the cost per fresh session in the benchmark
        // (throwaway repo, cache_creation ~6970 → ~2985 tokens). Always on —
        // help says "ignored with --system-prompt", but we only ever use
        // --append-system-prompt (a different flag), which the exclusion
        // doesn't skip.
        psi.ArgumentList.Add("--exclude-dynamic-system-prompt-sections");
        // #13: probed 2026-07-20 — the --help text says "only works with
        // --print", but it works fine here (3rd flag with that caveat that
        // turned out fine outside -p, after --session-id and --max-budget-usd).
        // Forwards a subagent's thinking/text AND its own tool_use/tool_result
        // calls, tagged with parent_tool_use_id/subagent_type/task_description.
        // Always on — how much of it to actually render is a client-side
        // setting (app.js), not a spawn-time choice.
        psi.ArgumentList.Add("--forward-subagent-text");
        psi.ArgumentList.Add("--append-system-prompt");
        psi.ArgumentList.Add(VsContextPrompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_model);

        // U2: display name for the session — claude appends its native
        // custom-title line to the JSONL, keeping the terminal /resume picker
        // in sync with titles set in the VS UI.
        if (!string.IsNullOrWhiteSpace(_sessionName))
        {
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add(_sessionName);
        }

        // Bidirectional control channel. Without this, AskUserQuestion auto-errors
        // in stream-json mode (no TTY to prompt through) and the model falls back
        // to plain-text noise. With it, claude emits a `control_request`
        // {subtype:"can_use_tool", tool_name:"AskUserQuestion"} on stdout and we
        // reply with a `control_response` on stdin that becomes the real
        // tool_result. PoC-confirmed to coexist with bypassPermissions + the
        // PreToolUse hook (permission for Bash/Edit/Write still flows through the
        // named pipe; only intrinsically-interactive tools use this channel).
        psi.ArgumentList.Add("--permission-prompt-tool");
        psi.ArgumentList.Add("stdio");

        // Set true once we've added a --settings arg (ask mode writes one with the
        // hooks block); other modes write a settings-only file afterwards if the
        // user configured any claude settings.
        bool settingsWritten = false;

        if (_permissionMode == "yolo")
        {
            psi.ArgumentList.Add("--dangerously-skip-permissions");
        }
        else if (_permissionMode == "dontAsk")
        {
            // #8: probed 2026-07-20 — real, CLI-enforced deny-by-default for
            // writes (Edit/Write/file-modifying Bash all get refused with a
            // clear message, no prompt); reads/searches run freely. No hooks,
            // no bypass — the CLI itself is the sole gatekeeper here.
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("dontAsk");

            // Same as the hook-pipe branch below: pasted images (Ctrl+V) land
            // in %TEMP%/ClaudeStudio — allow Read there even though writes
            // are blocked everywhere in this mode.
            var dontAskTempDir = Path.Combine(Path.GetTempPath(), "ClaudeStudio");
            if (!Directory.Exists(dontAskTempDir))
            {
                try { Directory.CreateDirectory(dontAskTempDir); } catch { }
            }
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dontAskTempDir);
        }
        else // "ask" / "plan" — one spawn profile, live-switchable via set_permission_mode
        {
            // When a pipe is available (CLAUDESTUDIO_HOOK_ENABLE=1), wire a PreToolUse
            // hook pointing back to ourselves so prompts route to the UI. Without a
            // pipe, claude auto-approves everything in stdio mode (PoC-confirmed).
            // plan gets the same hooks since 2026-07-10: ask↔plan flips reuse the
            // session (same key), so the spawn must carry everything either mode
            // needs. Side effect (deliberate, more conservative than before): plan
            // mode now shows permission modals for matched tools too.
            if (_pipeName != null)
            {
                var settingsPath = WriteSettings(includeHooks: true)!;
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add(settingsPath);
                psi.ArgumentList.Add("--include-hook-events");
                settingsWritten = true;
                // Claude has an internal permission check for file-modifying tools
                // (Edit/Write/MultiEdit) that runs BEFORE PreToolUse hooks in stdio
                // mode. Without acceptEdits, claude blocks these tools with
                // "Claude requested permissions to write to X, but you haven't
                // granted it yet" — the hook never gets a chance. acceptEdits
                // tells claude the internal check should auto-pass; the hook still
                // fires for these tools (matcher includes Edit|Write|MultiEdit),
                // so the user still sees the permission modal.
                // Use bypassPermissions instead of acceptEdits so claude doesn't
                // refuse internally for non-edit tools either. acceptEdits only
                // covers Edit/Write/MultiEdit; Bash compound commands (`cd X && Y`,
                // `cd X; Y`) and other "needs approval" cases were still being
                // auto-denied by claude in stdio mode (no TTY to ask via). With
                // bypass, claude lets everything through and the hook (configured
                // via --settings + --include-hook-events) remains the sole
                // gatekeeper — modal still appears for matched tools.
                psi.ArgumentList.Add("--permission-mode");
                psi.ArgumentList.Add(_permissionMode == "plan" ? "plan" : "bypassPermissions");

                // Allow Read of transient files the extension drops here —
                // pasted screenshots (Ctrl+V) land in %TEMP%/ClaudeStudio/.
                // Without --add-dir, claude refuses to Read paths outside the
                // workspace, breaking the "paste image and ask about it" flow.
                var sharedTempDir = Path.Combine(Path.GetTempPath(), "ClaudeStudio");
                if (!Directory.Exists(sharedTempDir))
                {
                    try { Directory.CreateDirectory(sharedTempDir); } catch { }
                }
                psi.ArgumentList.Add("--add-dir");
                psi.ArgumentList.Add(sharedTempDir);
            }
            else if (_permissionMode == "plan")
            {
                psi.ArgumentList.Add("--permission-mode");
                psi.ArgumentList.Add("plan");
            }
            // ask without a pipe: no mode arg (claude's default; auto-approves
            // in stdio) — SwitchPermissionModeAsync uses "default" to come back.
        }

        // Apply user-configured claude settings in modes that didn't already write
        // a settings.json (plan / yolo / ask-without-pipe). No-op if nothing set.
        if (!settingsWritten)
        {
            var claudeSettingsPath = WriteSettings(includeHooks: false);
            if (claudeSettingsPath != null)
            {
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add(claudeSettingsPath);
            }
        }

        if (!string.IsNullOrEmpty(_effort))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(_effort);
        }

        // #14: probed 2026-07-20 — no --print restriction in --help (unlike
        // #11/#13), spawns and completes cleanly outside it.
        if (!string.IsNullOrEmpty(_fallbackModel))
        {
            psi.ArgumentList.Add("--fallback-model");
            psi.ArgumentList.Add(_fallbackModel);
        }

        // #11: probed 2026-07-20 — works outside --print despite the --help
        // text ("only works with --print"), same as --session-id before it.
        // The turn still completes normally; claude reports the overage only
        // in the terminal `result` (subtype "error_max_budget_usd") and then
        // exits, so no special handling is needed here beyond passing it.
        if (_maxBudgetUsd is decimal budget && budget > 0)
        {
            psi.ArgumentList.Add("--max-budget-usd");
            psi.ArgumentList.Add(budget.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Pre-generate the id for a brand-new session (not resume/continue,
        // where the id is already fixed by claude). This lets the extension's
        // JSONL watcher attach at offset 0 right after spawn instead of
        // waiting for `system/init` to reveal claude's own generated id — the
        // race that window left open fed the branch/rewind bugs of rounds
        // 11-14. The `sid != SessionId` guard at init below stays as a
        // safety net in case claude ever rejects the pre-set id.
        string? pregeneratedSessionId = null;
        if (_resumeSessionId != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_resumeSessionId);
            if (_forkSession)
                psi.ArgumentList.Add("--fork-session");
        }
        else if (_autoResume)
        {
            psi.ArgumentList.Add("--continue");
        }
        else
        {
            pregeneratedSessionId = Guid.NewGuid().ToString();
            psi.ArgumentList.Add("--session-id");
            psi.ArgumentList.Add(pregeneratedSessionId);
        }

        // Snapshot the fully-built argument list so the spawn can be retried with a
        // flag removed if this claude build rejects one at launch.
        var plannedArgs = psi.ArgumentList.ToList();

        // Self-healing spawn against CLI version drift (issue #7). An optional flag
        // this build doesn't recognise (e.g. --forward-subagent-text) makes claude
        // exit at once with "unknown option '--X'", which otherwise surfaces as
        // "session not started" on the first send and blocks the extension entirely.
        // Drop the offending optional flag and respawn. Load-bearing and value-taking
        // flags are never dropped (see DroppableOptionalFlags): if one of those is
        // rejected the session genuinely can't run, so the CLI's own message is
        // surfaced instead of looping.
        const int maxSpawnAttempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            psi.ArgumentList.Clear();
            foreach (var a in plannedArgs) psi.ArgumentList.Add(a);
            lock (_stderrBuffer) _stderrBuffer.Clear();

            _proc = Process.Start(psi)
                ?? throw new Exception("Process.Start returned null");

            // Force UTF-8 without BOM on stdin. .NET 10's default is already UTF-8 no BOM,
            // but be explicit so this never regresses (the PoC hit a BOM bug on PS 5.1).
            _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = false
            };
            _stdout = _proc.StandardOutput;

            // Drain stderr in background. Without this, a full stderr pipe buffer would
            // deadlock claude before it could emit init on stdout.
            _stderrPump = Task.Run(async () =>
            {
                var buf = new char[4096];
                try
                {
                    while (true)
                    {
                        int n = await _proc.StandardError.ReadAsync(buf, 0, buf.Length);
                        if (n == 0) break;
                        lock (_stderrBuffer) _stderrBuffer.Append(buf, 0, n);
                    }
                }
                catch { }
            });

            EmitTiming("claude spawned", sw.ElapsedMilliseconds);

            // Which claude.exe actually launched, and its version — a stale install
            // shadowing the expected one on PATH cost a whole validation round to
            // diagnose (2026-07-16: chocolatey 2.1.144 vs ~/.local/bin 2.1.211).
            // The probe spawns a separate `claude --version` and awaits it, which is
            // enough wall-clock for a launch-time flag rejection to have already
            // exited the main process — so the HasExited check right after is a free
            // detection window, with no artificial delay added to the healthy path.
            var claudeVersion = await ProbeClaudeAsync(psi.FileName);

            if (!_proc.HasExited)
            {
                // Healthy: claude is up and blocked waiting for the first stdin line.
                _claudeVersion = claudeVersion;
                var exeLabel = claudeVersion != null ? $"{psi.FileName} ({claudeVersion})" : psi.FileName;
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "timing", Text = $"claude exe: {exeLabel}" }));
                Console.Out.Flush();

                // issue #7: we chose the native ~\.local\bin install but a
                // different claude.exe sits on PATH. Probe its version too and
                // surface the duplicate as a visible system line (not just the
                // log-only warn), flagging when the PATH one is newer — that was
                // the reporter's case (stale .local\bin, fresh WinGet on PATH).
                if (_duplicateInstall is { } dup)
                    await EmitDuplicateInstallNoticeAsync(dup.chosen, claudeVersion, dup.other);

                if (pregeneratedSessionId != null)
                {
                    SessionId = pregeneratedSessionId;
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session", Text = pregeneratedSessionId }));
                    Console.Out.Flush();
                }

                // U4: hand the claude PID to the extension so it can watch the CLI's
                // live presence file (~/.claude/sessions/<pid>.json) for status /
                // waitingFor updates. Emitted mid-turn, so the read loop picks it up.
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "claude-pid", Text = _proc!.Id.ToString() }));
                Console.Out.Flush();
                break;
            }

            // Exited at launch. Let the stderr pump finish capturing the message,
            // then decide whether a droppable optional flag caused it.
            try { await Task.WhenAny(_stderrPump ?? Task.CompletedTask, Task.Delay(500)); } catch { }
            string launchErr = SnapshotStderr();

            bool dropped = LaunchFlagRecovery.TryDropRejectedFlags(plannedArgs, launchErr, out string droppedDesc);

            // Tear down the dead spawn before retrying or giving up.
            try { _stdin?.Dispose(); } catch { }
            try { _stdout?.Dispose(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _stdin = null; _stdout = null; _proc = null;

            if (dropped && attempt < maxSpawnAttempts)
            {
                EmitWarn($"claude rejected {droppedDesc} at launch, dropping it and retrying (CLI version drift, issue #7)");
                continue;
            }

            var trimmed = launchErr.Trim();
            throw new Exception(string.IsNullOrEmpty(trimmed)
                ? "claude exited immediately at launch"
                : $"claude exited at launch: {trimmed}");
        }

        // NOTE: we do NOT wait for `system/init` here. With `--input-format stream-json`
        // (no `-p`), claude does not emit any stdout until it receives the first stdin
        // line — waiting for init before sending the first message deadlocks both sides.
        // Instead, init arrives as the first event of the first SendMessageAsync call
        // and is processed by the same loop as everything else.
    }

    internal readonly record struct PendingAsk(string RequestId, string InputJson);

    // Answers a pending AskUserQuestion control_request (or denies it if the user
    // dismissed the card). Called from the stdin reader task while SendMessageAsync's
    // read loop is draining stdout — claude is blocked waiting for this reply.
    public async Task SendControlResponseAsync(AskAnswer answer)
    {
        if (_stdin == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");
        if (!_pendingAsks.TryRemove(answer.ToolUseId, out var pending))
            throw new InvalidOperationException($"no pending control_request for tool_use_id {answer.ToolUseId}");

        object inner;
        if (answer.Dismissed)
        {
            inner = new { behavior = "deny", message = "User dismissed the question." };
        }
        else
        {
            var inputNode = JsonNode.Parse(pending.InputJson) as JsonObject ?? new JsonObject();
            inputNode["answers"] = JsonNode.Parse(
                string.IsNullOrWhiteSpace(answer.AnswersJson) ? "{}" : answer.AnswersJson);
            inner = new { behavior = "allow", updatedInput = inputNode };
        }

        await WriteControlResponseAsync(pending.RequestId, inner);
    }

    // Answers a pending control-channel permission gate (ExitPlanMode). Returns
    // false when the tool_use_id isn't ours — the caller falls back to an error.
    public async Task<bool> TryRespondControlPermissionAsync(PermissionResponse pr)
    {
        if (!_pendingControlPerms.TryRemove(pr.ToolUseId, out var pending)) return false;
        if (pr.Allow)
        {
            await WriteControlAllowAsync(pending.RequestId, pending.InputJson);
            // Approving the plan gate makes the CLI leave plan mode on its own.
            // Track that here so a later UI flip back to plan actually issues
            // set_permission_mode (with UiPermissionMode stuck on "plan" the
            // main loop would see no change and skip the switch — bloco 20,
            // validation 2026-07-18). The UI flips its pill to ask in parallel.
            if (UiPermissionMode == "plan")
                UiPermissionMode = "ask";
        }
        else
            await WriteControlResponseAsync(pending.RequestId,
                new { behavior = "deny", message = string.IsNullOrEmpty(pr.Reason) ? "User rejected the plan — keep planning." : pr.Reason });
        return true;
    }

    // Auto-allow an unexpected control_request, echoing the input unchanged.
    private Task WriteControlAllowAsync(string requestId, string inputRawJson)
    {
        var input = JsonNode.Parse(string.IsNullOrWhiteSpace(inputRawJson) ? "{}" : inputRawJson);
        return WriteControlResponseAsync(requestId, new { behavior = "allow", updatedInput = input });
    }

    private async Task WriteControlResponseAsync(string requestId, object innerResponse)
    {
        var msg = new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = innerResponse
            }
        };
        var ndjson = JsonSerializer.Serialize(msg);

        await _stdinLock.WaitAsync();
        try
        {
            await _stdin!.WriteLineAsync(ndjson);
            await _stdin.FlushAsync();
        }
        finally { _stdinLock.Release(); }
    }

    public async Task SendMessageAsync(string userText)
    {
        if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");

        var sw = Stopwatch.StartNew();

        // Checkpoints for the perf.jsonl line (#22b) — mirrors the EmitTiming
        // calls below so the harness measures exactly what the timing chunks
        // already show the UI, just persisted instead of transient.
        long msgSentMs = 0, firstLineMs = 0, firstChunkMs = 0, resultMs = 0;
        int finalInputTok = 0, finalOutputTok = 0, finalCacheCreate = 0, finalCacheRead = 0;

        var userMsg = new
        {
            type = "user",
            message = new { role = "user", content = userText },
            parent_tool_use_id = (string?)null,
            session_id = SessionId ?? ""
        };
        var ndjson = JsonSerializer.Serialize(userMsg);

        await _stdinLock.WaitAsync();
        try
        {
            await _stdin.WriteLineAsync(ndjson);
            await _stdin.FlushAsync();
        }
        finally { _stdinLock.Release(); }

        msgSentMs = sw.ElapsedMilliseconds;
        EmitTiming("message sent", msgSentMs);

        bool firstLine = true;
        bool firstChunk = true;
        bool thinkingActive = false;

        string? line;
        while ((line = await _stdout.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (firstLine)
            {
                firstLineMs = sw.ElapsedMilliseconds;
                EmitTiming("first stdout line", firstLineMs);
                firstLine = false;
            }

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            // #22a: the type/subtype classification that used to live inline
            // here now lives in ClaudeStudioShared.StreamEventParser, unit
            // tested there. This loop body is orchestration only: apply the
            // returned state deltas, write the chunks it built, perform the
            // side effects it can't (pending-ask registration, the stdin
            // auto-allow write, the perf log) and stop on Done.
            var state = new StreamEventState { SessionId = SessionId, LastActiveModel = _lastActiveModel, ThinkingActive = thinkingActive };
            var lineElapsedMs = sw.ElapsedMilliseconds;
            var result = StreamEventParser.Process(evt, state, lineElapsedMs, _workingDirectory, ExpectedCliPermissionMode(), _permissionMode);

            SessionId = state.SessionId;
            _lastActiveModel = state.LastActiveModel;
            thinkingActive = state.ThinkingActive;

            foreach (var chunk in result.Chunks)
            {
                if (firstChunk && chunk.Type == "chunk")
                {
                    firstChunkMs = sw.ElapsedMilliseconds;
                    EmitTiming("first chunk", firstChunkMs);
                    firstChunk = false;
                }
                Console.WriteLine(JsonSerializer.Serialize(chunk));
                Console.Out.Flush();
            }

            if (result.PendingAsk is { } ask)
                _pendingAsks[ask.ToolUseId] = new PendingAsk(ask.RequestId, ask.InputJson);
            if (result.PendingControlPerm is { } perm)
                _pendingControlPerms[perm.ToolUseId] = new PendingAsk(perm.RequestId, perm.InputJson);
            if (result.ControlAllowRequest is { } allow)
            {
                try { await WriteControlAllowAsync(allow.RequestId, allow.InputJson); }
                catch (Exception ex) { EmitError($"control auto-allow write failed: {ex.Message}"); }
            }

            if (result.Outcome == StreamEventOutcome.Done)
            {
                if (result.FinalTokens is { } t)
                {
                    finalInputTok = t.Input;
                    finalOutputTok = t.Output;
                    finalCacheCreate = t.CacheCreate;
                    finalCacheRead = t.CacheRead;
                }
                resultMs = lineElapsedMs;
                var totalMs = sw.ElapsedMilliseconds;
                EmitTiming("total", totalMs);
                AppendPerfLog(new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    cliVersion = _claudeVersion,
                    model = _model,
                    effort = _effort,
                    permissionMode = _permissionMode,
                    sessionId = SessionId,
                    timings = new
                    {
                        messageSentMs = msgSentMs,
                        firstStdoutLineMs = firstLineMs,
                        firstChunkMs,
                        resultMs,
                        totalMs
                    },
                    tokens = new
                    {
                        input = finalInputTok,
                        output = finalOutputTok,
                        cacheCreate = finalCacheCreate,
                        cacheRead = finalCacheRead
                    }
                });
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "done" }));
                Console.Out.Flush();
                return;
            }
        }

        // stdout closed mid-turn — claude died
        var stderr = SnapshotStderr();
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
        {
            Type = "error",
            Text = string.IsNullOrWhiteSpace(stderr) ? "claude session ended unexpectedly" : stderr.Trim()
        }));
        Console.Out.Flush();
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "done" }));
        Console.Out.Flush();
    }

    // Sends a rewind_files control_request to the running claude and forwards the
    // control_response back as a "rewind-result" chunk (Text = the inner response
    // JSON: {canRewind, filesChanged, insertions, deletions}).
    public async Task RewindAsync(string userMessageId, bool dryRun)
    {
        var resp = await SendControlRequestAsync(
            new { subtype = "rewind_files", user_message_id = userMessageId, dry_run = dryRun }, "rewind");
        if (resp == null) return;
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "rewind-result", Text = resp.Value.GetRawText() }));
        Console.Out.Flush();
    }

    // Sends a get_context_usage control_request and forwards the inner response
    // ({model, totalTokens, rawMaxTokens, percentage, categories, memoryFiles,
    // agents}) back as a "context-usage-result" chunk.
    public async Task GetContextUsageAsync()
    {
        var resp = await SendControlRequestAsync(new { subtype = "get_context_usage" }, "context-usage");
        if (resp == null) return;
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "context-usage-result", Text = resp.Value.GetRawText() }));
        Console.Out.Flush();
    }

    // Asks the live claude for a short generated session title (V18).
    // persist:true → claude appends {"type":"ai-title"} to the session JSONL
    // (PoC-confirmed on 2.1.144), so the terminal /resume picker shows our
    // generated titles too. The sidecar store stays as cache.
    public async Task GenerateSessionTitleAsync(string description)
    {
        var resp = await SendControlRequestAsync(
            new { subtype = "generate_session_title", description, persist = true }, "session-title");
        if (resp == null) return;

        string? title = null;
        if (resp.Value.ValueKind == JsonValueKind.Object &&
            resp.Value.TryGetProperty("title", out var t) &&
            t.ValueKind == JsonValueKind.String)
            title = t.GetString();

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session-title-result", Text = title ?? "" }));
        Console.Out.Flush();
    }

    // Generic one-shot client-initiated control_request (V20): writes the
    // request, drains stdout until the matching control_response, and returns
    // the inner response element — or null after emitting an error chunk.
    // Called between turns only (claude idle), so it owns stdout while waiting;
    // anything else claude emits meanwhile (rate_limit_event, keep-alives) is
    // skipped.
    // quietErrors: failures go to the timing channel (extension OutputLog) instead
    // of a user-visible error bubble — for requests with a silent fallback, like
    // set_permission_mode (a failed switch just respawns).
    private async Task<JsonElement?> SendControlRequestAsync(object request, string label, bool quietErrors = false)
    {
        if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");

        var requestId = label + "-" + Guid.NewGuid().ToString("N");
        var ndjson = JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request });

        await _stdinLock.WaitAsync();
        try
        {
            await _stdin.WriteLineAsync(ndjson);
            await _stdin.FlushAsync();
        }
        finally { _stdinLock.Release(); }

        string? line;
        while ((line = await _stdout.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }
            if (!evt.TryGetProperty("type", out var tp) || tp.GetString() != "control_response") continue;
            if (!evt.TryGetProperty("response", out var resp)) continue;
            if (!resp.TryGetProperty("request_id", out var rid) || rid.GetString() != requestId) continue;

            var subtype = resp.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            if (subtype == "error")
            {
                var errMsg = resp.TryGetProperty("error", out var er) ? er.GetString() : $"{label} failed";
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = quietErrors ? "timing" : "error", Text = $"{label}: {errMsg}" }));
                Console.Out.Flush();
                return null;
            }
            if (resp.TryGetProperty("response", out var rr))
                return rr.Clone();
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = quietErrors ? "timing" : "error", Text = $"{label}: claude exited before responding" }));
        Console.Out.Flush();
        return null;
    }

    public async Task GetMcpStatusAsync()
    {
        var resp = await SendControlRequestAsync(new { subtype = "mcp_status" }, "mcp-status");
        if (resp == null) return;
        var servers = resp.Value.TryGetProperty("mcpServers", out var s) ? s.GetRawText() : "[]";
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "mcp-status-result", Text = servers }));
        Console.Out.Flush();
    }

    public async Task ReconnectMcpServerAsync(string serverName)
    {
        var resp = await SendControlRequestAsync(new { subtype = "mcp_reconnect", serverName }, "mcp-reconnect");
        if (resp == null) return;
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "mcp-reconnect-result", Text = serverName }));
        Console.Out.Flush();
    }

    // Side question (V19): answered with the session's context, kept out of the
    // main transcript. The inner response is {response: string|null, synthetic}.
    public async Task AskSideQuestionAsync(string question)
    {
        var resp = await SendControlRequestAsync(new { subtype = "side_question", question }, "side-question");
        if (resp == null) return;
        string answer = "";
        if (resp.Value.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String)
            answer = r.GetString() ?? "";
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "side-question-result", Text = answer }));
        Console.Out.Flush();
    }

    private string SnapshotStderr()
    {
        lock (_stderrBuffer) return _stderrBuffer.ToString();
    }

    private static void EmitTiming(string label, long ms)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "timing", Text = $"{label}: {ms}ms" }));
        Console.Out.Flush();
    }

    private static void EmitError(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = text }));
        Console.Out.Flush();
    }

    private static void EmitWarn(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "warn", Text = text }));
        Console.Out.Flush();
    }

    // issue #7: two claude.exe installs. Probes the PATH copy's version and posts
    // a visible system-info line naming both, with a hint when the PATH one is
    // newer than the native one we launched (the reporter's exact case).
    private async Task EmitDuplicateInstallNoticeAsync(string chosen, string? chosenVer, string other)
    {
        var otherVer = await ProbeClaudeAsync(other);
        string Label(string path, string? ver) => ver != null ? $"v{ver.Split(' ')[0]} at {path}" : path;

        var msg = $"Two Claude CLI installs found. Using {Label(chosen, chosenVer)}; your PATH also has {Label(other, otherVer)}.";
        if (ClaudeExeLocator.CompareVersions(otherVer, chosenVer) > 0)
            msg += " The one on PATH is newer, so if the extension seems to use an outdated CLI, remove the older install or set the CLI path in Settings (Claude Code, CLI path).";

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "system-info", Text = msg }));
        Console.Out.Flush();
    }

    private static readonly object _perfLogLock = new();

    // #22b: measurement, not a test — one line per completed turn so A/B
    // comparisons (e.g. did #5's --session-id change init latency?) can be
    // made from real data instead of impression. Best-effort: a logging
    // failure must never surface as a turn failure.
    private static void AppendPerfLog(object record)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStudio");
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            lock (_perfLogLock)
            {
                File.AppendAllText(Path.Combine(dir, "perf.jsonl"), line);
            }
        }
        catch { }
    }

    private object BuildHooks()
    {
        var agentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve agent executable path");

        // PoC confirmed: paths in settings.json must use forward slashes — claude's
        // shell layer interprets backslashes as escape characters and strips them.
        var agentPathFwd = agentPath.Replace('\\', '/');

        return new
        {
            PreToolUse = new[]
            {
                new
                {
                    // FAIL-CLOSED: gate everything except tools that only read.
                    //
                    // The old matcher listed the dangerous tools by name, so every
                    // tool the CLI added later ran with NO gate at all — in ask/plan
                    // mode we spawn under bypassPermissions, so a name this regex
                    // misses is simply allowed and no modal ever appears. That is
                    // how PowerShell, the default shell tool on Windows since CLI
                    // 2.1.226, ended up running "git status" unprompted (rodada 17).
                    //
                    // Inverting it means a new tool defaults to *asking*, which is
                    // recoverable (allow for the session, or add a permission rule)
                    // instead of silent. The exclusions are read-only or
                    // conversation-local: Read/Glob/Grep/NotebookRead inspect,
                    // ToolSearch loads a schema, TodoWrite writes the task list,
                    // BashOutput reads an existing shell's buffer, WebSearch returns
                    // results (WebFetch, which pulls a URL, is deliberately NOT here).
                    //
                    // Negative lookahead verified against CLI 2.1.226 (rodada 17):
                    // the hook fires for a shell command and does not fire for Read.
                    // If a future CLI stops honouring it the regex matches nothing
                    // and NOTHING is gated, so re-run that probe when bumping the CLI.
                    //
                    // AskUserQuestion and ExitPlanMode are excluded because they are already
                    // gated on the control channel — the question card and the "Approve plan?"
                    // modal. The old name list left them out by omission; inverting the rule
                    // swept them in, which surfaces as the same plan being approved twice, once
                    // per gate. Neither executes anything, so this hook has nothing to protect.
                    matcher = "^(?!Read$|Glob$|Grep$|NotebookRead$|ToolSearch$|TodoWrite$|BashOutput$|WebSearch$|AskUserQuestion$|ExitPlanMode$).*",
                    hooks = new[]
                    {
                        // timeout must outlive the longest the permission modal can wait.
                        // A PreToolUse hook that hits its own timeout does NOT block the
                        // tool (measured); under bypassPermissions the tool would then run
                        // unapproved. The CLI default is 600s, so leaving it unset let a
                        // wait past ten minutes execute without approval — and the killed
                        // hook is what produced "pipe is broken" when the server finally
                        // answered. PermissionPipeServer.HookTimeoutSeconds is kept above
                        // its own ceiling so the server always denies first.
                        new
                        {
                            type = "command",
                            command = $"\"{agentPathFwd}\" --hook {_pipeName}",
                            timeout = PermissionPipeServer.HookTimeoutSeconds
                        }
                    }
                }
            },
            // After a file edit, pull the VS Error List for that file and feed
            // any errors/warnings back to claude as additionalContext so it can
            // self-correct. Informational only — the hook never blocks.
            PostToolUse = new[]
            {
                new
                {
                    matcher = "Edit|Write|MultiEdit|NotebookEdit",
                    hooks = new[]
                    {
                        new { type = "command", command = $"\"{agentPathFwd}\" --hook-post {_pipeName}" }
                    }
                }
            }
        };
    }

    // User-configurable claude.exe settings surfaced via the extension UI (V7).
    // Only deviations from claude's defaults are written, so we never clobber the
    // user's own global/project config when a toggle is left at its default.
    private Dictionary<string, object?> BuildClaudeSettings()
    {
        var d = new Dictionary<string, object?>();
        if (_claudeSettings == null) return d;
        if (!_claudeSettings.CoAuthoredBy)
            d["attribution"] = new { commit = "", pr = "" }; // empty strings hide the trailers
        if (_claudeSettings.CleanupPeriodDays is int days && days > 0)
            d["cleanupPeriodDays"] = days;
        if (!_claudeSettings.AutoCompact)
            d["autoCompactEnabled"] = false;

        // V6 permission rules — claude enforces these natively (deny always;
        // allow/ask in its own permission flow). The per-turn hook auto-decision
        // lives in PermissionPipeServer, independent of this block.
        var perms = new Dictionary<string, object>();
        if (_claudeSettings.PermissionAllow is { Count: > 0 } al) perms["allow"] = al;
        if (_claudeSettings.PermissionAsk is { Count: > 0 } ak) perms["ask"] = ak;
        if (_claudeSettings.PermissionDeny is { Count: > 0 } dn) perms["deny"] = dn;
        if (perms.Count > 0)
            d["permissions"] = perms;

        return d;
    }

    // Writes the settings.json passed to claude via --settings. Includes the hooks
    // block only when requested (ask mode with a pipe), plus any user-configured
    // claude settings (all modes). Returns null when there's nothing to write.
    private string? WriteSettings(bool includeHooks)
    {
        var settings = BuildClaudeSettings();
        if (includeHooks) settings["hooks"] = BuildHooks();
        if (settings.Count == 0) return null;

        _settingsTempDir = Path.Combine(Path.GetTempPath(), $"claudestudio-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsTempDir);
        var settingsPath = Path.Combine(_settingsTempDir, "settings.json");
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settingsPath;
    }

    private static async Task<string?> ProbeClaudeAsync(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                // --permission-prompt-tool stdio dropped out of --help as of
                // 2.1.215 but still parses (validated 2026-07-19); every inline
                // permission prompt (AskUserQuestion, ask/plan mode) depends on
                // it. Tacking it onto the version probe validates both in one
                // spawn: if the flag were ever actually removed, claude would
                // reject it before getting to --version.
                ArgumentList = { "--permission-prompt-tool", "stdio", "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            if (proc.ExitCode != 0 || stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit {proc.ExitCode}" : stderr.Trim();
                EmitWarn(
                    $"this claude CLI rejected --permission-prompt-tool ({detail}) — " +
                    "inline permission prompts (AskUserQuestion, ask/plan mode) will stop working; " +
                    "pin an older CLI version or update the extension");
            }

            return string.IsNullOrWhiteSpace(stdout) ? null : stdout.Trim();
        }
        catch
        {
            // Timeout, missing exe, or unexpected --version behavior — the
            // caller falls back to an unversioned log line, not fatal.
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_proc != null)
            {
                try { _stdin?.Close(); } catch { }
                if (!_proc.HasExited)
                {
                    if (!_proc.WaitForExit(2000))
                    {
                        try { _proc.Kill(entireProcessTree: true); } catch { }
                        // Bounded wait: Kill can fail partially on shim trees
                        // (chocolatey claude.exe → node), and an infinite wait
                        // here wedged the stdin loop for good (rodada 12).
                        try { _proc.WaitForExit(2000); } catch { }
                    }
                }
            }
        }
        finally
        {
            try { _stdout?.Dispose(); } catch { }
            try { _stdin?.Dispose(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _stdin = null;
            _stdout = null;
            _proc = null;

            if (_settingsTempDir != null)
            {
                try { Directory.Delete(_settingsTempDir, recursive: true); } catch { }
                _settingsTempDir = null;
            }
        }
        await Task.CompletedTask;
    }
}
