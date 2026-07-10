using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeStudioShared;

namespace ClaudeStudioExtension.Agent;

public class AgentClient
{
    private Process? _process;

    private StreamWriter? _writer;

    private StreamReader? _reader;

    private TaskCompletionSource<bool>? _cancelTcs;

    // Serializes AskStreamingAsync calls. The StreamReader owned by this client
    // can't have two concurrent ReadLineAsync — if a new request arrives before
    // the previous turn's read loop has exited (e.g. user clicked cancel and
    // immediately typed a new question), the second call throws "stream in use".
    // The semaphore makes the new call wait for the old one to wind down
    // naturally (cancel triggers a hard cancel on the agent → done → loop exits
    // → semaphore released).
    private readonly SemaphoreSlim _streamingSemaphore = new(1, 1);

    private readonly JsonlWatcher _jsonlWatcher = new();

    public string? PendingResumeSessionId { get; set; }
    public string? CurrentSessionId { get; private set; }

    // User-configurable claude settings (V7). Set by the control from the UI;
    // attached to each outbound ChatRequest so the agent writes them into the
    // settings.json it passes to claude.
    public ClaudeStudioShared.ClaudeSettings? ClaudeSettings { get; set; }

    public void CancelCurrent()
    {
        OutputLog.Info("request cancel requested");
        _cancelTcs?.TrySetResult(true);
        // Hard cancel on the agent side so the read loop unblocks and the next
        // request can start without "stream in use" errors. Fire-and-forget —
        // we don't want to block the UI thread; the agent's response will
        // arrive via the existing read loop and emit `done`.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_writer == null) return;
                var json = JsonSerializer.Serialize(new ChatRequest { Message = "", CancelTurn = true });
                await _writer.WriteLineAsync(json);
                await _writer.FlushAsync();
            }
            catch (Exception ex)
            {
                OutputLog.Warn($"cancel-turn write failed: {ex.Message}");
            }
        });
    }

    public async Task StartAsync()
    {
        if (_process != null)
            return;

        var agentPath = GetAgentPath();
        OutputLog.Info($"starting agent: {agentPath}");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = agentPath,

                WorkingDirectory = Path.GetDirectoryName(agentPath),

                RedirectStandardInput = true,
                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Phase 2: enable the PreToolUse hook pipeline so the agent intercepts
        // tool calls under permissionMode == "ask" and routes prompts to the UI.
        _process.StartInfo.EnvironmentVariables["CLAUDESTUDIO_HOOK_ENABLE"] = "1";

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputLog.Warn($"agent stderr: {e.Data}");
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        var ready = await _reader.ReadLineAsync();

        if (ready != "READY")
        {
            OutputLog.Error($"agent failed to signal READY (got: {ready ?? "NULL"})");
            throw new Exception(
                $"ClaudeStudioAgent failed to start.\n" +
                $"Response: {ready ?? "NULL"}");
        }

        OutputLog.Info($"agent started (pid {_process.Id})");
    }

    public async Task SendPermissionResponseAsync(string toolUseId, bool allow, string? reason, string? allowSession = null)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"permission-response dropped — agent not running (toolUseId={toolUseId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            PermissionResponse = new PermissionResponse
            {
                ToolUseId = toolUseId,
                Allow = allow,
                Reason = reason,
                AllowSession = allowSession
            }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"permission-response → toolUseId={toolUseId} allow={allow} allowSession={allowSession ?? "-"}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // Answer to an AskUserQuestion. The agent translates tool_use_id → the pending
    // control_request's request_id and writes a control_response to claude.stdin.
    public async Task SendAskAnswerAsync(string toolUseId, string answersJson, bool dismissed)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"ask-answer dropped — agent not running (toolUseId={toolUseId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            AskAnswer = new AskAnswer
            {
                ToolUseId = toolUseId,
                AnswersJson = answersJson,
                Dismissed = dismissed
            }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"ask-answer → toolUseId={toolUseId} dismissed={dismissed}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // Reply to a diagnostics_request with the formatted VS Error List entries.
    public async Task SendDiagnosticsResponseAsync(string requestId, string text)
    {
        if (_writer == null)
        {
            OutputLog.Warn($"diagnostics-response dropped — agent not running (id={requestId})");
            return;
        }

        var request = new ChatRequest
        {
            Message = "",
            DiagnosticsResponse = new DiagnosticsResponse { RequestId = requestId, Text = text ?? "" }
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"diagnostics-response → id={requestId} chars={text?.Length ?? 0}");
        await _writer.WriteLineAsync(json);
        await _writer.FlushAsync();
    }

    // One-shot file rewind. Sends a rewind request and reads until the agent's
    // "rewind-result" chunk. Returns (resultJson, error): resultJson is the inner
    // control_response payload ({canRewind, filesChanged, insertions, deletions}),
    // or error is set when the agent reported a failure. Shares the streaming
    // semaphore so it never races an active turn's read loop.
    public async Task<(string? resultJson, string? error)> RewindAsync(string userMessageId, bool dryRun)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest
            {
                Message = "",
                RewindRequest = new RewindRequest { UserMessageId = userMessageId, DryRun = dryRun }
            };
            OutputLog.Info($"rewind → userMessageId={userMessageId} dryRun={dryRun}");
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? resultJson = null;
            string? error = null;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "rewind-result") resultJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"rewind error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (resultJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot context usage probe (V12). Same contract as RewindAsync: shares
    // the streaming semaphore, returns (usageJson, error) where usageJson is the
    // inner get_context_usage response payload.
    public async Task<(string? usageJson, string? error)> GetContextUsageAsync()
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", ContextUsage = true };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? usageJson = null;
            string? error = null;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "context-usage-result") usageJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"context-usage error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (usageJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot session title generation (V18). Same contract as RewindAsync:
    // shares the streaming semaphore, returns (title, error). Title may be
    // empty when claude declines to generate one.
    public async Task<(string? title, string? error)> GenerateSessionTitleAsync(string description)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", SessionTitleDescription = description };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? title = null;
            string? error = null;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "session-title-result") title = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"session-title error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (title, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot MCP status probe (V20): returns (serversJson, error) where
    // serversJson is the live session's mcpServers array.
    public async Task<(string? serversJson, string? error)> GetMcpStatusAsync()
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", McpStatus = true };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? serversJson = null;
            string? error = null;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "mcp-status-result") serversJson = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"mcp-status error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (serversJson, error);
        }
        finally { _streamingSemaphore.Release(); }
    }

    // One-shot MCP reconnect (V20). Returns the error, or null on success.
    public async Task<string?> ReconnectMcpServerAsync(string serverName)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return "agent not running";

            var request = new ChatRequest { Message = "", McpReconnectServer = serverName };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? error = null;
            var ok = false;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "mcp-reconnect-result") ok = true;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"mcp-reconnect error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return ok ? null : (error ?? "no response");
        }
        finally { _streamingSemaphore.Release(); }
    }

    public async Task AskStreamingAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            await AskStreamingCoreAsync(message, model, effort, permissionMode, onChunk, onTiming, onTokens, workingDirectory, autoResume, onSession, onTool, onPermissionRequest, onDiagnosticsRequest);
        }
        finally
        {
            _streamingSemaphore.Release();
        }
    }

    private async Task AskStreamingCoreAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null)
    {
        var resumeId = PendingResumeSessionId;
        PendingResumeSessionId = null;

        // If we already know the session id from a resume or prior turn, start the JSONL watcher early
        var preKnownSessionId = resumeId ?? CurrentSessionId;
        if (!string.IsNullOrEmpty(preKnownSessionId))
            StartJsonlWatcher(workingDirectory, preKnownSessionId, onTool);

        var request = new ChatRequest
        {
            Message = message,
            Model = model,
            Effort = effort,
            PermissionMode = permissionMode,
            ResumeSessionId = resumeId,
            WorkingDirectory = workingDirectory,
            AutoResume = autoResume,
            ClaudeSettings = ClaudeSettings
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"request → model={model} effort={effort ?? "-"} perm={permissionMode} cwd={workingDirectory ?? "-"} resume={resumeId ?? "-"} autoResume={autoResume} bytes={message?.Length ?? 0}");

        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        _cancelTcs = new TaskCompletionSource<bool>();
        bool cancelled = false;

        while (true)
        {
            var readTask = _reader!.ReadLineAsync();
            var winner = await Task.WhenAny(readTask, _cancelTcs.Task);
            if (winner == _cancelTcs.Task) cancelled = true;

            var responseJson = await readTask;

            if (responseJson == null) break;

            var chunk = JsonSerializer.Deserialize<ChatChunk>(responseJson);

            if (chunk == null) continue;

            if (chunk.Type == "done")
            {
                OutputLog.Info(cancelled ? "request done (after cancel)" : "request done");
                break;
            }

            // Drain silently after cancel — don't forward any callbacks
            if (cancelled) continue;

            if (chunk.Type == "timing")
            {
                OutputLog.Info($"timing: {chunk.Text}");
                onTiming?.Invoke(chunk.Text);
                continue;
            }

            if (chunk.Type == "tokens")
            {
                OutputLog.Info($"tokens: {chunk.Text}");
                onTokens?.Invoke(chunk.Text);
                continue;
            }

            if (chunk.Type == "tokens-live")
            {
                onTool?.Invoke("tokens-live", "", null, chunk.Text, null);
                continue;
            }

            if (chunk.Type == "session")
            {
                CurrentSessionId = chunk.Text;
                OutputLog.Info($"session: {chunk.Text}");
                onSession?.Invoke(chunk.Text);
                StartJsonlWatcher(workingDirectory, chunk.Text, onTool);
                continue;
            }

            if (chunk.Type == "error")
            {
                OutputLog.Error($"agent error: {chunk.Text}");
                if (!string.IsNullOrEmpty(chunk.Text))
                    onChunk(chunk.Text);
                break;
            }

            // claude.exe missing — route through onChunk with a sentinel so the
            // control can show a dedicated install card instead of a chat error.
            if (chunk.Type == "claude-not-found")
            {
                OutputLog.Error($"claude not found: {chunk.Text}");
                onChunk("CLAUDE_NOT_FOUND::" + (chunk.Text ?? ""));
                break;
            }

            if (chunk.Type == "chunk" && !string.IsNullOrEmpty(chunk.Text))
                onChunk(chunk.Text);

            if (chunk.Type == "tool_use" || chunk.Type == "tool_result" || chunk.Type == "tool_error")
            {
                OutputLog.Info($"{chunk.Type}: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} bytes={(chunk.ToolInput ?? chunk.Text ?? "").Length}");
                onTool?.Invoke(chunk.Type, chunk.Tool ?? "", chunk.ToolInput, chunk.Text, chunk.ToolId);
            }

            if (chunk.Type == "permission_request")
            {
                OutputLog.Info($"permission_request: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} cwd={chunk.Cwd ?? "-"}");
                onPermissionRequest?.Invoke(chunk.Tool ?? "", chunk.ToolInput, chunk.ToolId ?? "", chunk.Cwd);
            }

            if (chunk.Type == "diagnostics_request")
            {
                // Text carries the file path, ToolId the correlation id.
                OutputLog.Info($"diagnostics_request: file={chunk.Text} id={chunk.ToolId ?? "-"}");
                onDiagnosticsRequest?.Invoke(chunk.Text ?? "", chunk.ToolId ?? "");
            }
        }

        _cancelTcs = null;
    }

    private string? _watcherSessionId;
    private void StartJsonlWatcher(string? cwd, string sessionId, Action<string, string, string?, string?, string?>? onTool)
    {
        if (onTool == null) return;
        if (_watcherSessionId == sessionId) return; // already watching this session
        _watcherSessionId = sessionId;
        _jsonlWatcher.OnEvent = onTool;
        _jsonlWatcher.Start(cwd, sessionId);
    }

    public async Task StopAsync()
    {
        _jsonlWatcher.Stop();
        _watcherSessionId = null;

        // Take a local snapshot so concurrent StopAsync calls (e.g. solution
        // event reset firing while user clicks ✎) don't NullRef on _process
        // being cleared mid-finally by the other thread.
        var proc = _process;
        if (proc == null) return;
        _process = null; // claim it — subsequent callers see null and bail out

        // Claim the streams too, atomically with _process. Killing the agent can
        // take seconds (WaitForExit timeout), during which a `clear` may already
        // have called StartAsync and set _writer/_reader to the NEW agent. If we
        // disposed/nulled the live fields in the delayed finally we'd clobber the
        // new agent's streams (→ NullRef in the in-flight read loop). Operate only
        // on these locals so a concurrent StartAsync's fresh fields are untouched.
        var writer = _writer;
        var reader = _reader;
        _writer = null;
        _reader = null;

        var pid = proc.Id;
        OutputLog.Info($"stopping agent (pid {pid})");

        try
        {
            if (!proc.HasExited)
            {
                writer?.Close();

                var exited = await Task.Run(() =>
                    proc.WaitForExit(2000));

                if (!exited && !proc.HasExited)
                {
                    OutputLog.Warn($"agent (pid {pid}) didn't exit in 2s, killing");
                    proc.Kill();
                    proc.WaitForExit();
                }
            }
        }
        finally
        {
            reader?.Dispose();
            writer?.Dispose();
            proc.Dispose();
            OutputLog.Info($"agent (pid {pid}) stopped");
        }
    }

    private static string GetAgentPath()
    {
        var extensionDirectory = Path.GetDirectoryName(
            typeof(AgentClient).Assembly.Location)!;

        var agentPath = Path.Combine(
            extensionDirectory,
            "ClaudeStudioAgent",
            "ClaudeStudioAgent.exe");

        if (File.Exists(agentPath))
            return agentPath;

        throw new FileNotFoundException(
            $"ClaudeStudioAgent.exe not found at: {agentPath}");
    }

    //private static string GetAgentPath()
    //{
    //    var currentDirectory = new DirectoryInfo(
    //        Path.GetDirectoryName(typeof(AgentClient).Assembly.Location)!);

    //    while (currentDirectory != null)
    //    {
    //        var agentProjectPath = Path.Combine(
    //            currentDirectory.FullName,
    //            "ClaudeStudioAgent");

    //        if (Directory.Exists(agentProjectPath))
    //        {
    //            var agentPath = Directory
    //                .GetFiles(
    //                    agentProjectPath,
    //                    "ClaudeStudioAgent.exe",
    //                    SearchOption.AllDirectories)
    //                .FirstOrDefault(path =>
    //                    path.Contains(@"\bin\Debug\") ||
    //                    path.Contains(@"\bin\Release\"));

    //            if (agentPath != null)
    //                return agentPath;
    //        }

    //        currentDirectory = currentDirectory.Parent;
    //    }

    //    throw new FileNotFoundException(
    //        "ClaudeStudioAgent.exe not found. Build the ClaudeStudioAgent project first.");
    //}
}