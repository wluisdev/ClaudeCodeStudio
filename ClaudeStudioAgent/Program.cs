using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClaudeStudioAgent;
using ClaudeStudioShared;

// Hook mode: when claude.exe spawns us as a PreToolUse hook, route to HookMode
// and exit. This branch never enters the normal request loop.
if (args.Length >= 2 && args[0] == "--hook")
{
    return await HookMode.RunAsync(args[1]);
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

        var wantKey = ClaudeSession.MakeKey(request);
        if (session == null || session.Key != wantKey)
        {
            if (session != null) await session.DisposeAsync();
            session = new ClaudeSession(request, pipeServer?.PipeName);
            try
            {
                await session.StartAsync();
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
    private readonly bool _autoResume;
    private readonly string? _pipeName;

    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly StringBuilder _stderrBuffer = new();
    private Task? _stderrPump;
    private string? _settingsTempDir;
    public string? SessionId { get; private set; }
    public string Key { get; }

    public ClaudeSession(ChatRequest request, string? pipeName)
    {
        _model = request.Model ?? "claude-sonnet-4-6";
        _effort = request.Effort;
        _permissionMode = request.PermissionMode ?? "ask";
        _workingDirectory = request.WorkingDirectory;
        _resumeSessionId = request.ResumeSessionId;
        _autoResume = request.AutoResume;
        _pipeName = pipeName;
        Key = MakeKey(request);
    }

    public static string MakeKey(ChatRequest r) =>
        $"{r.Model}|{r.Effort}|{r.PermissionMode}|{r.WorkingDirectory}|{r.ResumeSessionId}|{r.AutoResume}";

    public async Task StartAsync()
    {
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = FindClaudeExe(),
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

        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--include-partial-messages");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_model);

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
                var settingsPath = WriteHookSettings();
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add(settingsPath);
                psi.ArgumentList.Add("--include-hook-events");
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

        await _stdin.WriteLineAsync(ndjson);
        await _stdin.FlushAsync();

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

    private string WriteHookSettings()
    {
        var tempBase = Path.GetTempPath();
        _settingsTempDir = Path.Combine(tempBase, $"claudestudio-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsTempDir);

        var settingsPath = Path.Combine(_settingsTempDir, "settings.json");
        var agentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve agent executable path");

        // PoC confirmed: paths in settings.json must use forward slashes — claude's
        // shell layer interprets backslashes as escape characters and strips them.
        var agentPathFwd = agentPath.Replace('\\', '/');

        var settings = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash|KillBash|Edit|Write|NotebookEdit|Task|WebFetch|CronCreate|mcp__.*",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = $"\"{agentPathFwd}\" --hook {_pipeName}"
                            }
                        }
                    }
                }
            }
        };

        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settingsPath;
    }

    private static string FindClaudeExe()
    {
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

        throw new FileNotFoundException(
            "claude.exe not found. Verify that Claude Code is installed and on PATH.");
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
