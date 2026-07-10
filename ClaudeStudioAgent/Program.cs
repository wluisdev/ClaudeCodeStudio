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
                if (pipeServer == null)
                {
                    EmitError("permission response received but hook server is disabled");
                    continue;
                }
                var pr = request.PermissionResponse;
                if (!string.IsNullOrEmpty(pr.AllowSession))
                    pipeServer.AllowForSession(pr.AllowSession);
                if (!pipeServer.Respond(pr.ToolUseId, pr.Allow, pr.Reason))
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
        pipeServer?.SetRules(request.ClaudeSettings);

        var wantKey = ClaudeSession.MakeKey(request);
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

// Thrown by FindClaudeExe when claude.exe is on neither PATH nor any known
// install location. The main loop turns it into a dedicated "claude-not-found"
// chunk so the UI can show an install card instead of a generic error.
sealed class ClaudeNotFoundException(string message) : Exception(message);

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
    private readonly bool _autoResume;
    private readonly string? _pipeName;
    private readonly ClaudeSettings? _claudeSettings;
    private readonly string? _cliPath;

    private Process? _proc;
    private StreamWriter? _stdin;
    // Serializes writes to claude.stdin so partial NDJSON lines can't interleave.
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    // Pending AskUserQuestion control_requests, keyed by tool_use_id (the only id
    // the UI knows). Maps to the request_id needed for the control_response and
    // the original tool input (echoed back with an `answers` field).
    private readonly ConcurrentDictionary<string, PendingAsk> _pendingAsks = new();
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
        _autoResume = request.AutoResume;
        _pipeName = pipeName;
        _claudeSettings = request.ClaudeSettings;
        _cliPath = request.CliPath;
        Key = MakeKey(request);
    }

    public static string MakeKey(ChatRequest r) =>
        $"{r.Model}|{r.Effort}|{r.PermissionMode}|{r.WorkingDirectory}|{r.ResumeSessionId}|{r.AutoResume}|{r.CliPath}";

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
            FileName = FindClaudeExe(_cliPath),
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
        psi.ArgumentList.Add("--append-system-prompt");
        psi.ArgumentList.Add(VsContextPrompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_model);

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

        if (_permissionMode == "plan")
        {
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("plan");
        }
        else if (_permissionMode == "ask")
        {
            // When a pipe is available (CLAUDESTUDIO_HOOK_ENABLE=1), wire a PreToolUse
            // hook pointing back to ourselves so prompts route to the UI. Without a
            // pipe, claude auto-approves everything in stdio mode (PoC-confirmed).
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
                psi.ArgumentList.Add("bypassPermissions");

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
        }
        else // "yolo" (default)
        {
            psi.ArgumentList.Add("--dangerously-skip-permissions");
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

        if (_resumeSessionId != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(_resumeSessionId);
        }
        else if (_autoResume)
        {
            psi.ArgumentList.Add("--continue");
        }

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

        EmitTiming("message sent", sw.ElapsedMilliseconds);

        bool firstLine = true;
        bool firstChunk = true;

        string? line;
        while ((line = await _stdout.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (firstLine)
            {
                EmitTiming("first stdout line", sw.ElapsedMilliseconds);
                firstLine = false;
            }

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (!evt.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            if (type == "system")
            {
                // `system/init` is the first event claude emits after receiving the
                // first stdin line (no `-p` mode). Capture session_id, emit a session
                // chunk on first sighting, and record timing. Other system subtypes
                // (status, rate_limit_event) are intentionally ignored.
                if (!evt.TryGetProperty("subtype", out var subProp)) continue;
                if (subProp.GetString() != "init") continue;

                if (evt.TryGetProperty("session_id", out var sidProp))
                {
                    var sid = sidProp.GetString();
                    if (!string.IsNullOrEmpty(sid) && sid != SessionId)
                    {
                        SessionId = sid;
                        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session", Text = sid! }));
                        Console.Out.Flush();
                    }
                }
                EmitTiming("claude init", sw.ElapsedMilliseconds);
            }
            else if (type == "assistant")
            {
                var msgObj = evt.GetProperty("message");

                // Synthetic responses (slash commands like /cost, /context) come as a
                // single complete assistant message with text content and no preceding
                // stream_event deltas. Detect by model == "<synthetic>" and emit the
                // text directly as a chunk.
                bool isSynthetic = msgObj.TryGetProperty("model", out var modelEl)
                    && modelEl.GetString() == "<synthetic>";

                if (msgObj.TryGetProperty("usage", out var usageLive))
                    EmitTokensLive(usageLive);

                var content = msgObj.GetProperty("content");
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (!item.TryGetProperty("type", out var itemType)) continue;
                        var itemTypeStr = itemType.GetString();

                        if (itemTypeStr == "tool_use")
                        {
                            var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            string? inputJson = null;
                            if (item.TryGetProperty("input", out var inputProp))
                                inputJson = inputProp.GetRawText();

                            Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
                            {
                                Type = "tool_use",
                                Tool = name,
                                ToolInput = inputJson,
                                ToolId = id
                            }));
                            Console.Out.Flush();
                        }
                        else if (itemTypeStr == "text" && isSynthetic)
                        {
                            var text = item.TryGetProperty("text", out var tp) ? tp.GetString() : null;
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (firstChunk)
                                {
                                    EmitTiming("first chunk", sw.ElapsedMilliseconds);
                                    firstChunk = false;
                                }
                                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "chunk", Text = text }));
                                Console.Out.Flush();
                            }
                        }
                    }
                }
            }
            else if (type == "stream_event")
            {
                if (!evt.TryGetProperty("event", out var streamEvt)) continue;
                if (!streamEvt.TryGetProperty("type", out var evtType)) continue;
                var evtTypeStr = evtType.GetString();

                if (evtTypeStr == "content_block_delta")
                {
                    if (!streamEvt.TryGetProperty("delta", out var delta)) continue;
                    if (!delta.TryGetProperty("type", out var deltaType)) continue;
                    if (deltaType.GetString() != "text_delta") continue;
                    if (!delta.TryGetProperty("text", out var deltaText)) continue;

                    var text = deltaText.GetString();
                    if (string.IsNullOrEmpty(text)) continue;

                    if (firstChunk)
                    {
                        EmitTiming("first chunk", sw.ElapsedMilliseconds);
                        firstChunk = false;
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "chunk", Text = text }));
                    Console.Out.Flush();
                }
                else if (evtTypeStr == "message_delta" && streamEvt.TryGetProperty("usage", out var deltaUsage))
                {
                    EmitTokensLive(deltaUsage);
                }
                else if (evtTypeStr == "message_start"
                    && streamEvt.TryGetProperty("message", out var startMsg)
                    && startMsg.TryGetProperty("usage", out var startUsage))
                {
                    EmitTokensLive(startUsage);
                }
            }
            else if (type == "user")
            {
                // tool_result content emitted by claude when a tool finished
                if (!evt.TryGetProperty("message", out var userMsgObj)) continue;
                if (!userMsgObj.TryGetProperty("content", out var userContent)) continue;
                if (userContent.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in userContent.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var itemType)) continue;
                    if (itemType.GetString() != "tool_result") continue;

                    var id = item.TryGetProperty("tool_use_id", out var idProp) ? idProp.GetString() : null;
                    string? summary = null;

                    if (item.TryGetProperty("content", out var contentProp))
                    {
                        if (contentProp.ValueKind == JsonValueKind.String)
                        {
                            summary = contentProp.GetString();
                        }
                        else if (contentProp.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new StringBuilder();
                            foreach (var c in contentProp.EnumerateArray())
                            {
                                if (c.TryGetProperty("type", out var ct) && ct.GetString() == "text" &&
                                    c.TryGetProperty("text", out var tt))
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(tt.GetString());
                                }
                            }
                            summary = sb.ToString();
                        }
                    }

                    if (summary != null && summary.Length > 240)
                        summary = summary.Substring(0, 237) + "...";

                    bool isError = item.TryGetProperty("is_error", out var errProp) && errProp.GetBoolean();

                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
                    {
                        Type = isError ? "tool_error" : "tool_result",
                        Text = summary ?? "",
                        ToolId = id
                    }));
                    Console.Out.Flush();
                }
            }
            else if (type == "control_request")
            {
                // Bidirectional control channel (--permission-prompt-tool stdio).
                // claude blocks waiting for our control_response, so every one MUST
                // be answered or the turn hangs.
                var reqId = evt.TryGetProperty("request_id", out var ridProp) ? ridProp.GetString() : null;
                if (reqId != null && evt.TryGetProperty("request", out var reqObj)
                    && reqObj.TryGetProperty("subtype", out var subEl) && subEl.GetString() == "can_use_tool")
                {
                    var toolName = reqObj.TryGetProperty("tool_name", out var tnEl) ? tnEl.GetString() : null;
                    var toolUseId = reqObj.TryGetProperty("tool_use_id", out var tuEl) ? tuEl.GetString() : null;
                    var inputRaw = reqObj.TryGetProperty("input", out var inEl) ? inEl.GetRawText() : "{}";

                    if (toolName == "AskUserQuestion" && toolUseId != null)
                    {
                        // The card is already rendered from the preceding tool_use
                        // chunk. Just remember the request_id so SendControlResponseAsync
                        // can answer it when the user picks. claude waits on stdin.
                        _pendingAsks[toolUseId] = new PendingAsk(reqId, inputRaw);
                    }
                    else
                    {
                        // Non-interactive tool routed through the prompt tool. Under
                        // bypassPermissions this shouldn't happen (the hook is the gate),
                        // but auto-allow so claude never hangs on an unhandled request.
                        EmitError($"auto-allowing unexpected control_request for tool '{toolName}'");
                        try { await WriteControlAllowAsync(reqId, inputRaw); }
                        catch (Exception ex) { EmitError($"control auto-allow write failed: {ex.Message}"); }
                    }
                }
            }
            else if (type == "result")
            {
                EmitTiming("result received", sw.ElapsedMilliseconds);

                // session id is stable across turns once set, but the result event
                // re-emits it — refresh + re-broadcast in case the extension missed it
                if (evt.TryGetProperty("session_id", out var sidProp))
                {
                    var newSid = sidProp.GetString();
                    if (!string.IsNullOrEmpty(newSid) && newSid != SessionId)
                    {
                        SessionId = newSid;
                        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session", Text = newSid! }));
                        Console.Out.Flush();
                    }
                }

                if (evt.TryGetProperty("usage", out var usage))
                {
                    var inputTok = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0;
                    var outputTok = usage.TryGetProperty("output_tokens", out var out_) ? out_.GetInt32() : 0;
                    var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
                    var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
                    var newIn = inputTok + cacheCreate;
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "tokens", Text = $"{newIn}/{outputTok}/{cacheRead}" }));
                    Console.Out.Flush();
                }

                if (evt.TryGetProperty("is_error", out var isErrProp) && isErrProp.GetBoolean() &&
                    evt.TryGetProperty("result", out var resultProp))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = resultProp.GetString() ?? "" }));
                    Console.Out.Flush();
                }

                EmitTiming("total", sw.ElapsedMilliseconds);
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "done" }));
                Console.Out.Flush();
                return;
            }
            // Other event types (status, rate_limit_event, etc.) are intentionally ignored.
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
    // JSON: {canRewind, filesChanged, insertions, deletions}). Called between
    // turns (claude is idle), so this owns stdout while waiting for the reply.
    public async Task RewindAsync(string userMessageId, bool dryRun)
    {
        if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");

        var requestId = "rewind-" + Guid.NewGuid().ToString("N");
        var msg = new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "rewind_files", user_message_id = userMessageId, dry_run = dryRun }
        };
        var ndjson = JsonSerializer.Serialize(msg);

        await _stdinLock.WaitAsync();
        try
        {
            await _stdin.WriteLineAsync(ndjson);
            await _stdin.FlushAsync();
        }
        finally { _stdinLock.Release(); }

        // claude is idle; read until our matching control_response. Skip anything
        // else it might emit (rate_limit_event, keep-alive, etc.).
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
                var errMsg = resp.TryGetProperty("error", out var er) ? er.GetString() : "rewind failed";
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = $"rewind: {errMsg}" }));
                Console.Out.Flush();
                return;
            }

            // success → forward the inner response payload verbatim
            var inner = resp.TryGetProperty("response", out var rr) ? rr.GetRawText() : "{}";
            Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "rewind-result", Text = inner }));
            Console.Out.Flush();
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = "rewind: claude exited before responding" }));
        Console.Out.Flush();
    }

    // Sends a get_context_usage control_request and forwards the inner response
    // ({model, totalTokens, rawMaxTokens, percentage, categories, memoryFiles,
    // agents}) back as a "context-usage-result" chunk. Same idle-stdout contract
    // as RewindAsync: called between turns, so this owns stdout while waiting.
    public async Task GetContextUsageAsync()
    {
        if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");

        var requestId = "ctxusage-" + Guid.NewGuid().ToString("N");
        var msg = new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "get_context_usage" }
        };
        var ndjson = JsonSerializer.Serialize(msg);

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
                var errMsg = resp.TryGetProperty("error", out var er) ? er.GetString() : "context usage failed";
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = $"context-usage: {errMsg}" }));
                Console.Out.Flush();
                return;
            }

            var inner = resp.TryGetProperty("response", out var rr) ? rr.GetRawText() : "{}";
            Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "context-usage-result", Text = inner }));
            Console.Out.Flush();
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = "context-usage: claude exited before responding" }));
        Console.Out.Flush();
    }

    // Asks the live claude for a short generated session title (V18). Same
    // idle-stdout contract as RewindAsync. persist:false — the extension keeps
    // titles in its own sidecar store, mirroring the official extension.
    public async Task GenerateSessionTitleAsync(string description)
    {
        if (_stdin == null || _stdout == null || _proc == null || _proc.HasExited)
            throw new InvalidOperationException("session not started or already exited");

        var requestId = "title-" + Guid.NewGuid().ToString("N");
        var msg = new
        {
            type = "control_request",
            request_id = requestId,
            request = new { subtype = "generate_session_title", description, persist = false }
        };
        var ndjson = JsonSerializer.Serialize(msg);

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
                var errMsg = resp.TryGetProperty("error", out var er) ? er.GetString() : "title generation failed";
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = $"session-title: {errMsg}" }));
                Console.Out.Flush();
                return;
            }

            string? title = null;
            if (resp.TryGetProperty("response", out var rr) &&
                rr.ValueKind == JsonValueKind.Object &&
                rr.TryGetProperty("title", out var t) &&
                t.ValueKind == JsonValueKind.String)
                title = t.GetString();

            Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session-title-result", Text = title ?? "" }));
            Console.Out.Flush();
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = "session-title: claude exited before responding" }));
        Console.Out.Flush();
    }

    // Generic one-shot client-initiated control_request (V20): writes the
    // request, drains stdout until the matching control_response, and returns
    // the inner response element — or null after emitting an error chunk.
    // Same idle-stdout contract as RewindAsync (called between turns only).
    // NOTE: rewind/context-usage/title predate this helper and keep their own
    // copies until the current validation round closes; folding them in is a
    // follow-up cleanup.
    private async Task<JsonElement?> SendControlRequestAsync(object request, string label)
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
                Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = $"{label}: {errMsg}" }));
                Console.Out.Flush();
                return null;
            }
            if (resp.TryGetProperty("response", out var rr))
                return rr.Clone();
            return JsonSerializer.Deserialize<JsonElement>("{}");
        }

        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = $"{label}: claude exited before responding" }));
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

    private static void EmitTokensLive(JsonElement usage)
    {
        var inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
        var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
        {
            Type = "tokens-live",
            Text = $"{inTok + cacheCreate}/{outTok}/{cacheRead}"
        }));
        Console.Out.Flush();
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
                    matcher = "Bash|KillBash|Edit|Write|NotebookEdit|Task|WebFetch|CronCreate|mcp__.*",
                    hooks = new[]
                    {
                        new { type = "command", command = $"\"{agentPathFwd}\" --hook {_pipeName}" }
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

    private static string FindClaudeExe(string? configured)
    {
        // Explicit path from the UI (D7) wins; a bad value fails loudly (with
        // the value in the message) instead of silently falling back to PATH.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = configured!.Trim();
            if (File.Exists(p))
                return p;
            var inDir = Path.Combine(p, "claude.exe");
            if (Directory.Exists(p) && File.Exists(inDir))
                return inDir;
            throw new ClaudeNotFoundException(
                $"The configured CLI path was not found: {p} — fix or clear it in settings (Claude Code → CLI path).");
        }

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var fallbacks = new[]
        {
            Path.Combine(appData, @"npm\claude.exe"),
            Path.Combine(appData, @"npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\claude.exe"),
        };

        foreach (var path in fallbacks)
            if (File.Exists(path))
                return path;

        throw new ClaudeNotFoundException(
            "claude.exe was not found on your PATH or any standard install location.");
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
                        try { _proc.WaitForExit(); } catch { }
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
