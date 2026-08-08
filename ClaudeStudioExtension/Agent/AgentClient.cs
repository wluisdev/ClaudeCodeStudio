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
    // #12: consumed together with PendingResumeSessionId on the next turn —
    // set only by the History "resume as fork" action.
    public bool PendingForkSession { get; set; }
    public string? CurrentSessionId { get; private set; }

    // True while the in-flight/last turn was sent with --resume or --continue
    // (its session forks the previous transcript instead of starting empty).
    public bool LastTurnResumed { get; private set; }

    // U4: fired with claude.exe's PID right after the agent spawns it, so the
    // control can point a FileSystemWatcher at the CLI's presence file
    // (~/.claude/sessions/<pid>.json — carries status/waitingFor).
    public Action<int>? OnClaudePid { get; set; }

    // User-configurable claude settings (V7). Set by the control from the UI;
    // attached to each outbound ChatRequest so the agent writes them into the
    // settings.json it passes to claude.
    public ClaudeStudioShared.ClaudeSettings? ClaudeSettings { get; set; }

    // Explicit claude.exe path (D7). Set by the control from the UI; attached
    // to each outbound ChatRequest so the agent spawns that binary instead of
    // searching PATH (a change respawns claude via the session key).
    public string? CliPath { get; set; }

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
        {
            // A crashed/killed agent leaves its carcass here — early-returning
            // would hand the caller dead streams (rodada 12). Sweep and respawn.
            bool alive;
            try { alive = !_process.HasExited; } catch { alive = false; }
            if (alive) return;
            OutputLog.Warn("agent process was dead — restarting");
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
            _writer = null;
            _reader = null;
        }

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
                // No OutputLog.Error here: the caller classifies. Canceling or
                // clearing right after a turn kills claude before this request
                // lands ("no active session") — an expected race, not a failure.
                if (chunk.Type == "session-title-result") title = chunk.Text;
                else if (chunk.Type == "error") error = chunk.Text;
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

    // One-shot side question (V19): returns (answer, error). Answer is empty
    // when claude declines to answer.
    public async Task<(string? answer, string? error)> AskSideQuestionAsync(string question)
    {
        await _streamingSemaphore.WaitAsync();
        try
        {
            if (_writer == null || _reader == null)
                return (null, "agent not running");

            var request = new ChatRequest { Message = "", SideQuestion = question };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            string? answer = null;
            string? error = null;
            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
                if (chunk == null) continue;
                if (chunk.Type == "side-question-result") answer = chunk.Text;
                else if (chunk.Type == "error") { error = chunk.Text; OutputLog.Error($"side-question error: {chunk.Text}"); }
                else if (chunk.Type == "done") break;
            }
            return (answer, error);
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

    // True while a turn is in flight. The rename-session handler consults this
    // before appending a native custom-title line to the live session's JSONL
    // (claude is appending to the same file mid-turn).
    public bool IsStreaming { get; private set; }

    public async Task AskStreamingAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null, Action<string>? onThinking = null, decimal? maxBudgetUsd = null, string? fallbackModel = null, Action<ChatChunk>? onSubagentEvent = null, Action<string, string>? onPermissionResolved = null)
    {
        await _streamingSemaphore.WaitAsync();
        IsStreaming = true;
        try
        {
            await AskStreamingCoreAsync(message, model, effort, permissionMode, onChunk, onTiming, onTokens, workingDirectory, autoResume, onSession, onTool, onPermissionRequest, onDiagnosticsRequest, onThinking, maxBudgetUsd, fallbackModel, onSubagentEvent, onPermissionResolved);
        }
        finally
        {
            IsStreaming = false;
            _streamingSemaphore.Release();
        }
    }

    private async Task AskStreamingCoreAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null, Action<string, string, string?, string?, string?>? onTool = null, Action<string, string?, string, string?>? onPermissionRequest = null, Action<string, string>? onDiagnosticsRequest = null, Action<string>? onThinking = null, decimal? maxBudgetUsd = null, string? fallbackModel = null, Action<ChatChunk>? onSubagentEvent = null, Action<string, string>? onPermissionResolved = null)
    {
        // A send can queue on the semaphore behind a stuck turn; by the time it
        // runs here, a clear/stop may have nulled the streams (rodada 12: NRE at
        // WriteLineAsync) or the agent may have died. The caller's StartAsync
        // ran BEFORE the semaphore wait and can be stale — revalidate under it.
        bool agentDown;
        try { agentDown = _writer == null || _reader == null || _process == null || _process.HasExited; }
        catch { agentDown = true; }
        if (agentDown)
        {
            OutputLog.Info("agent not running at turn start — restarting");
            await StartAsync();
        }

        var resumeId = PendingResumeSessionId;
        PendingResumeSessionId = null;
        var forkSession = PendingForkSession;
        PendingForkSession = false;
        // Whether this turn carries the previous transcript forward (--resume /
        // --continue). The control forwards it with session-info so the UI can
        // tell a forked-with-history session from a fresh one (rewind base).
        LastTurnResumed = resumeId != null || autoResume;

        // If we already know the session id from a resume or prior turn, start
        // the JSONL watcher early. Forking is the exception: --fork-session
        // mints a NEW id different from resumeId, so pre-arming on resumeId
        // would attach to the original session's file — wait for the real
        // "session" chunk instead, same as any brand-new session (#12).
        var preKnownSessionId = forkSession ? null : (resumeId ?? CurrentSessionId);
        if (!string.IsNullOrEmpty(preKnownSessionId))
            StartJsonlWatcher(workingDirectory, preKnownSessionId, onTool);

        var request = new ChatRequest
        {
            Message = message,
            Model = model,
            Effort = effort,
            PermissionMode = permissionMode,
            ResumeSessionId = resumeId,
            ForkSession = forkSession,
            WorkingDirectory = workingDirectory,
            AutoResume = autoResume,
            ClaudeSettings = ClaudeSettings,
            CliPath = CliPath,
            MaxBudgetUsd = maxBudgetUsd,
            FallbackModel = fallbackModel,
            // U2: when resuming a session whose custom title we know, pass it
            // along so the agent spawns claude with --name — the CLI re-appends
            // its native custom-title line and the terminal picker stays in
            // sync even for renames done while the session was live. Custom
            // only: generated titles already persist natively as ai-title.
            // Gated on an actual resume (--resume/--continue): without it, a
            // respawn that starts a FRESH session (e.g. model switch with
            // auto-resume off) would stamp the previous session's title onto
            // the new one (audit 2026-07-10 #1).
            SessionName = (resumeId != null || autoResume) && !string.IsNullOrEmpty(preKnownSessionId)
                ? SessionTitlesStore.GetCustom(preKnownSessionId!) : null
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"request → model={model} effort={effort ?? "-"} perm={permissionMode} cwd={workingDirectory ?? "-"} resume={resumeId ?? "-"} autoResume={autoResume} bytes={message?.Length ?? 0}");

        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        _cancelTcs = new TaskCompletionSource<bool>();
        bool cancelled = false;

        while (true)
        {
            // The agent can be torn down mid-turn: a cancel puts this loop into
            // drain mode (see `if (cancelled) continue` below), and a following
            // clear/reset runs StopAsync, which nulls _reader AND disposes the
            // stream. The rodada-12 guard (~442) only revalidates at turn START,
            // so the drain loop would re-read a now-null _reader here and NRE
            // (rodada 15). Snapshot the field and bail if it's already gone; the
            // try/catch covers the reader being disposed while a read is pending.
            var reader = _reader;
            if (reader == null) break;

            string? responseJson;
            try
            {
                var readTask = reader.ReadLineAsync();
                var winner = await Task.WhenAny(readTask, _cancelTcs.Task);
                if (winner == _cancelTcs.Task) cancelled = true;
                responseJson = await readTask;
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is IOException)
            {
                OutputLog.Info("read loop ended: agent stream closed mid-turn");
                break;
            }

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

            if (chunk.Type == "warn")
            {
                OutputLog.Warn(chunk.Text);
                continue;
            }

            if (chunk.Type == "claude-pid")
            {
                if (int.TryParse(chunk.Text, out var claudePid))
                {
                    try { OnClaudePid?.Invoke(claudePid); }
                    catch (Exception ex) { OutputLog.Warn($"presence watcher start failed: {ex.Message}"); }
                }
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

            // CLI informational notices ("Unknown command: /x") — shown as a
            // muted system line so the turn doesn't look like a silent no-op.
            if (chunk.Type == "system-info")
            {
                OutputLog.Info($"system-info: {chunk.Text}");
                onTool?.Invoke("system-info", "", null, chunk.Text, null);
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

            if (chunk.Type == "thinking")
            {
                onThinking?.Invoke(chunk.Text);
                continue;
            }

            // #7/#9: piggyback on the existing wide onTool callback (kind, name,
            // input, text, id) the same way "system-info" already does — avoids
            // growing this signature with two more single-purpose Actions.
            if (chunk.Type == "user-ack")
            {
                onTool?.Invoke("user-ack", "", null, null, null);
                continue;
            }

            if (chunk.Type == "rate-limit")
            {
                onTool?.Invoke("rate-limit", "", null, chunk.Text, null);
                continue;
            }

            // #21: same onTool piggyback as user-ack/rate-limit/model-used —
            // "compacting" is a start/stop presence signal, "compact-boundary"
            // carries the pre/post token metadata as a raw JSON blob in text.
            if (chunk.Type == "compacting" || chunk.Type == "compact-boundary")
            {
                onTool?.Invoke(chunk.Type, "", null, chunk.Text, null);
                continue;
            }

            // #14: --fallback-model engagement/recovery signal — same onTool
            // piggyback as user-ack/rate-limit above.
            if (chunk.Type == "model-used")
            {
                onTool?.Invoke("model-used", "", null, chunk.Text, null);
                continue;
            }

            // #13: subagent events carry more correlation fields (ParentToolId,
            // SubagentType, TaskDescription) than onTool's 5 positional params
            // fit — pass the whole ChatChunk through instead of piggybacking.
            if (chunk.Type.StartsWith("subagent-", StringComparison.Ordinal))
            {
                onSubagentEvent?.Invoke(chunk);
                continue;
            }

            // #11: same sentinel-on-onChunk trick as CLAUDE_NOT_FOUND:: below —
            // a terminal, turn-ending condition that needs distinct UI treatment
            // (not a new callback param, not a plain chat error bubble).
            if (chunk.Type == "budget-exceeded")
            {
                onChunk("BUDGET_EXCEEDED::" + (chunk.Text ?? ""));
                continue;
            }

            // NEVER break on error/claude-not-found: the agent always follows a
            // fatal one with "done" (and mid-turn errors are informational — the
            // turn keeps streaming). Breaking early left that "done" orphaned in
            // the pipe, and every subsequent request then completed instantly
            // against the previous request's done — the chat ran one message
            // behind until the agent restarted (D7, validation 2026-07-18).
            if (chunk.Type == "error")
            {
                OutputLog.Error($"agent error: {chunk.Text}");
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    // An expired session survives the pre-flight IsSignedIn() check
                    // (oauthAccount stays in ~/.claude.json — it holds profile fields
                    // only, no token and no expiry), so the CLI is the first thing to
                    // notice. Same sentinel trick as CLAUDE_NOT_FOUND:: below, routing
                    // it to the sign-in card instead of a dead-end error bubble.
                    onChunk(ClaudeStudioShared.AuthErrors.IsAuthFailure(chunk.Text)
                        ? "AUTH_REQUIRED::" + chunk.Text
                        : chunk.Text);
                }
                continue;
            }

            // claude.exe missing — route through onChunk with a sentinel so the
            // control can show a dedicated install card instead of a chat error.
            if (chunk.Type == "claude-not-found")
            {
                OutputLog.Error($"claude not found: {chunk.Text}");
                onChunk("CLAUDE_NOT_FOUND::" + (chunk.Text ?? ""));
                continue;
            }

            if (chunk.Type == "chunk" && !string.IsNullOrEmpty(chunk.Text))
                onChunk(chunk.Text);

            if (chunk.Type == "tool_use" || chunk.Type == "tool_result" || chunk.Type == "tool_error")
            {
                OutputLog.Info($"{chunk.Type}: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} bytes={(chunk.ToolInput ?? chunk.Text ?? "").Length}");
                onTool?.Invoke(chunk.Type, chunk.Tool ?? "", chunk.ToolInput, chunk.Text, chunk.ToolId);
            }

            // Decided by a rule / session allowlist, so no modal and no UI trace —
            // the log line is the only way to tell it apart from a tool that
            // simply never needed permission.
            if (chunk.Type == "permission_auto")
            {
                OutputLog.Info($"permission auto: {chunk.Tool ?? "-"} — {chunk.Text}");
                continue;
            }

            if (chunk.Type == "permission_request")
            {
                OutputLog.Info($"permission_request: {chunk.Tool ?? "-"} id={chunk.ToolId ?? "-"} cwd={chunk.Cwd ?? "-"}");
                onPermissionRequest?.Invoke(chunk.Tool ?? "", chunk.ToolInput, chunk.ToolId ?? "", chunk.Cwd);
            }

            // Timeout/cancel/shutdown resolved it without the user, so the card
            // has to come down before claude moves on — otherwise it is answered
            // into a tool_use_id the agent already closed.
            if (chunk.Type == "permission_resolved")
            {
                OutputLog.Info($"permission_resolved: id={chunk.ToolId ?? "-"} ({chunk.Text})");
                onPermissionResolved?.Invoke(chunk.ToolId ?? "", chunk.Text ?? "");
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

    // New-chat reset (rodada 12): without this a cleared chat kept the dead
    // session's id — the JSONL watcher re-attached to the old file and title/
    // branch/rewind kept targeting the previous session.
    public void ResetSession()
    {
        CurrentSessionId = null;
        PendingResumeSessionId = null;
        PendingForkSession = false;
        LastTurnResumed = false;
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