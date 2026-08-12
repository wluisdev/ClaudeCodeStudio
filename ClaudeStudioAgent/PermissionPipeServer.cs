using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

internal sealed class PermissionPipeServer : IAsyncDisposable
{
    // 0 = wait for the answer, bounded by HardCeiling (the "wait (max 1 hour)"
    // option); a positive value denies after that many minutes. Swapped per chat
    // request like _rules, so changing it in the UI applies on the next send
    // without a respawn.
    private volatile int _timeoutMinutes;

    /// <summary>
    /// Longest this server will wait for an answer, even under the "wait" setting.
    /// </summary>
    /// <remarks>
    /// "Wait for answer" is about the user taking their time; it is not a promise to
    /// block forever on a prompt nobody can see. If the card never renders — the
    /// webview reloaded, the tool window was closed without cancelling, the dispatcher
    /// call threw — claude stays parked inside PreToolUse and both it and the resident
    /// hook process leak for the rest of the session. The old three-minute clock
    /// papered over all of those; this restores a floor without touching what the
    /// setting means.
    /// <para>
    /// This is also a security bound, not just a convenience one. Measured against the
    /// live CLI: a PreToolUse command hook that hits its own timeout does NOT block the
    /// tool — the call proceeds through the normal permission flow, which under
    /// bypassPermissions means it runs. The CLI's default hook timeout is 600s, so a
    /// wait longer than that used to let the tool execute unapproved once the hook was
    /// killed (the "pipe is broken" the extension logged is the server then writing to
    /// the dead hook). This ceiling MUST stay strictly below <see cref="HookTimeoutSeconds"/>
    /// so the server always denies before the hook can time out and open that window.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan HardCeiling = TimeSpan.FromHours(1);

    /// <summary>
    /// The `timeout` (seconds) written onto the CLI's PreToolUse hook, so the hook
    /// outlives the longest wait this server can perform. Verified against the live CLI:
    /// a large explicit value is accepted and honoured (the default is only 600s). Kept
    /// above <see cref="HardCeiling"/> by a margin so the ceiling always fires first —
    /// if the hook timed out first, the tool would run without approval.
    /// </summary>
    public static int HookTimeoutSeconds => (int)HardCeiling.TotalSeconds + 300;

    public void SetPermissionTimeout(int minutes) => _timeoutMinutes = minutes > 0 ? minutes : 0;

    public string PipeName { get; }

    // Diagnostics requests time out faster than permission — they're a courtesy,
    // never worth stalling the turn for long.
    private static readonly TimeSpan DiagnosticsTimeout = TimeSpan.FromSeconds(8);

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Decision>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingDiag = new();
    private readonly HashSet<string> _sessionAllowed = new();
    private readonly object _sessionAllowedLock = new();
    private Task? _acceptLoop;

    public PermissionPipeServer()
    {
        PipeName = $"claudestudio-perm-{Environment.ProcessId}";
    }

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public bool Respond(string toolUseId, bool allow, string? reason)
    {
        if (_pending.TryGetValue(toolUseId, out var tcs))
            return tcs.TrySetResult(new Decision(allow, reason));
        return false;
    }

    public bool RespondDiagnostics(string requestId, string text)
    {
        if (_pendingDiag.TryGetValue(requestId, out var tcs))
            return tcs.TrySetResult(text ?? "");
        return false;
    }

    // Deny every wait still blocking a hook (turn canceled). claude sits inside
    // PreToolUse until the hook returns — disposing the session while it was
    // blocked left the process ignoring stdin-close and the cancel wedged
    // (rodada 12). Entries are removed by each handler's finally, not here.
    public void FailPending(string reason)
    {
        foreach (var kvp in _pending)
            kvp.Value.TrySetResult(new Decision(false, reason, Auto: true));
        foreach (var kvp in _pendingDiag)
            kvp.Value.TrySetResult("");
    }

    public void AllowForSession(string toolName)
    {
        lock (_sessionAllowedLock) _sessionAllowed.Add(toolName);
    }

    private bool IsSessionAllowed(string toolName)
    {
        lock (_sessionAllowedLock) return _sessionAllowed.Contains(toolName);
    }

    // ── V6 permission rules ────────────────────────────────────────────────
    // Claude-style rule strings ("Bash(git *)", "Read") evaluated before the
    // modal round-trip. Snapshot is swapped whole on every chat request, so
    // reads need no lock.
    private sealed record PermRule(string Tool, string? Spec, string Raw);
    private sealed record RuleSet(List<PermRule> Allow, List<PermRule> Ask, List<PermRule> Deny);
    private volatile RuleSet? _rules;

    private enum RuleVerdict { None, Allow, Ask, Deny }

    public void SetRules(MergedPermissionRules? settings)
    {
        if (settings == null ||
            ((settings.Allow?.Count ?? 0) == 0 &&
             (settings.Ask?.Count ?? 0) == 0 &&
             (settings.Deny?.Count ?? 0) == 0))
        {
            _rules = null;
            return;
        }

        static List<PermRule> Parse(List<string>? raw, string bucket)
        {
            var list = new List<PermRule>();
            foreach (var r in raw ?? [])
            {
                // Hyphens are legal in MCP server names, so `mcp__github-tools__search`
                // has to parse — the stricter pattern dropped exactly the rules the
                // settings-file merge was written to honour, and dropped them silently.
                var m = Regex.Match(r.Trim(), @"^([A-Za-z_][A-Za-z0-9_.-]*)\s*(?:\((.*)\))?$", RegexOptions.Singleline);
                if (m.Success)
                {
                    list.Add(new PermRule(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null, r.Trim()));
                }
                else
                {
                    // A rule that cannot be parsed does nothing, and a `deny` the user
                    // believes is protecting them doing nothing is worth a line. The UI
                    // rejects malformed rules as they are typed; rules coming from a
                    // settings file have no such gate, so this is their only feedback.
                    EmitWarn($"ignoring malformed permission rule in {bucket}: {r.Trim()}");
                }
            }
            return list;
        }

        _rules = new RuleSet(
            Parse(settings.Allow, "allow"),
            Parse(settings.Ask, "ask"),
            Parse(settings.Deny, "deny"));
    }

    private (RuleVerdict verdict, string? rule) EvaluateRules(string toolName, string? toolInputJson)
    {
        var rules = _rules;
        if (rules == null) return (RuleVerdict.None, null);

        var input = ExtractPrimaryInput(toolInputJson);
        string? Match(List<PermRule> bucket)
        {
            foreach (var r in bucket)
            {
                if (!SettingsPermissions.ToolMatches(r.Tool, toolName)) continue;
                if (r.Spec is null || r.Spec.Length == 0 || SpecMatches(r.Spec, input)) return r.Raw;
            }
            return null;
        }

        if (Match(rules.Deny) is { } d) return (RuleVerdict.Deny, d);
        if (Match(rules.Ask) is { } a) return (RuleVerdict.Ask, a);
        if (Match(rules.Allow) is { } al) return (RuleVerdict.Allow, al);
        return (RuleVerdict.None, null);
    }

    // The value a rule specifier is matched against, per tool shape: the first
    // present of command (Bash) / file_path (Edit|Write|Read) / path / url
    // (WebFetch) / pattern (Glob|Grep).
    private static string? ExtractPrimaryInput(string? toolInputJson)
    {
        if (string.IsNullOrEmpty(toolInputJson)) return null;
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(toolInputJson!);
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "command", "file_path", "path", "url", "pattern" })
                if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
        }
        catch { }
        return null;
    }

    // True when the tool call is claude writing/updating its own plan document
    // (plan mode keeps them under <config>/plans/).
    private static readonly string PlansDir = ClaudeConfig.PlansDir;

    private static bool IsPlanFileWrite(string toolName, string? toolInputJson)
    {
        if (toolName is not ("Write" or "Edit" or "MultiEdit" or "NotebookEdit")) return false;
        var filePath = ExtractPrimaryInput(toolInputJson);
        if (string.IsNullOrEmpty(filePath)) return false;
        try
        {
            var full = Path.GetFullPath(filePath!);
            var plans = Path.GetFullPath(PlansDir);
            return full.StartsWith(plans + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SpecMatches(string spec, string? value)
    {
        if (value == null) return false;
        // Claude's Bash prefix idiom: "npm run test:*" matches "npm run test" + anything.
        if (spec.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = spec.Substring(0, spec.Length - 2);
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!spec.Contains('*'))
            return string.Equals(spec, value, StringComparison.OrdinalIgnoreCase);
        var pattern = "^" + Regex.Escape(spec).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                var connection = server;
                server = null;
                _ = Task.Run(() => HandleConnectionAsync(connection, ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                EmitError($"pipe accept loop error: {ex.Message}");
                server?.Dispose();
                try { await Task.Delay(100, ct); } catch { break; }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string? toolUseId = null;
        TaskCompletionSource<Decision>? tcs = null;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(requestLine)) return;

            JsonElement req;
            try { req = JsonSerializer.Deserialize<JsonElement>(requestLine); }
            catch (Exception ex)
            {
                EmitError($"pipe: failed to parse request: {ex.Message}");
                return;
            }

            var reqType = req.TryGetProperty("type", out var rt) ? rt.GetString() : "perm_request";
            if (reqType == "diag_request")
            {
                await HandleDiagRequestAsync(pipe, req, ct);
                return;
            }

            toolUseId = req.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
            var toolName = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
            var toolInput = req.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : null;
            var cwd = req.TryGetProperty("cwd", out var cw) ? cw.GetString() : null;

            if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(toolName))
            {
                EmitError("pipe: request missing tool_use_id or tool_name");
                return;
            }

            // Rule evaluation first (deny > ask > allow), then the session
            // allowlist short-circuit. An ask-rule match forces the modal even
            // when the tool was session-allowed or has an allow rule.
            var (verdict, matchedRule) = EvaluateRules(toolName!, toolInput);

            // A decision taken here never reaches the UI, so without a line in the
            // log an auto-approval is indistinguishable from "the tool just ran"
            // — which is exactly why 44.5 took a whole round to pin down
            // (rodada 16, 48.3).
            void LogAuto(string what) => EmitChunk(new ChatChunk
            {
                Type = "permission_auto",
                Tool = toolName,
                Text = what
            });

            Decision decision;
            if (IsPlanFileWrite(toolName!, toolInput))
            {
                // Plan mode saves its plan document to ~/.claude/plans/<slug>.md
                // through the Write tool — CLI bookkeeping, not a workspace edit.
                // Without this bypass every plan-mode turn pops a scary Write
                // modal for the plan file (validation round 2026-07-16).
                decision = new Decision(true, null);
                LogAuto("allowed: plan-file write");
            }
            else if (verdict == RuleVerdict.Deny)
            {
                decision = new Decision(false, $"Denied by permission rule: {matchedRule}");
                LogAuto($"denied by rule: {matchedRule}");
            }
            else if (verdict == RuleVerdict.Allow)
            {
                decision = new Decision(true, null);
                LogAuto($"allowed by rule: {matchedRule}");
            }
            else if (verdict != RuleVerdict.Ask && IsSessionAllowed(toolName!))
            {
                decision = new Decision(true, null);
                LogAuto("allowed for session");
            }
            else
            {
                if (verdict == RuleVerdict.Ask)
                    LogAuto($"prompting: forced by ask rule: {matchedRule}");
                tcs = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pending.TryAdd(toolUseId!, tcs))
                {
                    EmitError($"pipe: duplicate tool_use_id {toolUseId}");
                    return;
                }

                EmitChunk(new ChatChunk
                {
                    Type = "permission_request",
                    Tool = toolName,
                    ToolInput = toolInput,
                    ToolId = toolUseId,
                    Cwd = cwd
                });

                // The user's setting, bounded by the ceiling. Cancel and shutdown still
                // release the wait through ct, exactly as before.
                int minutes = _timeoutMinutes;
                var wait = minutes > 0 ? TimeSpan.FromMinutes(minutes) : HardCeiling;

                // Linked source so the timer is disposed the moment the user answers:
                // a bare Task.Delay(…, ct) leaves its registration on the token until
                // the server shuts down, and at one per prompt a long session
                // accumulates them.
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeoutTask = Task.Delay(wait, timer.Token);

                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                timer.Cancel();

                if (completed == tcs.Task)
                {
                    decision = await tcs.Task;
                }
                else if (ct.IsCancellationRequested)
                {
                    decision = new Decision(false, "Agent shutting down", Auto: true);
                }
                else if (minutes <= 0)
                {
                    // Only reachable through the ceiling, so say so rather than
                    // reporting a timeout the user never configured.
                    decision = new Decision(
                        false,
                        $"No answer after {HardCeiling.TotalHours:F0}h — the prompt may never have appeared",
                        Auto: true);
                }
                else
                {
                    decision = new Decision(false, $"Permission timed out ({minutes}min)", Auto: true);
                }
            }

            // Nobody clicked: retract the card before claude moves on, so it can
            // never be answered into a tool_use_id that no longer exists.
            if (decision.Auto)
            {
                EmitChunk(new ChatChunk
                {
                    Type = "permission_resolved",
                    ToolId = toolUseId,
                    Text = decision.Reason ?? "Resolved without an answer"
                });
            }

            var responseObj = new
            {
                type = "perm_response",
                tool_use_id = toolUseId,
                decision = decision.Allow ? "allow" : "deny",
                reason = decision.Reason
            };
            var responseLine = JsonSerializer.Serialize(responseObj) + "\n";
            var bytes = Encoding.UTF8.GetBytes(responseLine);
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            EmitError($"pipe handler error: {ex.Message}");
        }
        finally
        {
            if (toolUseId != null)
                _pending.TryRemove(toolUseId, out _);
            try { pipe.Disconnect(); } catch { }
            await pipe.DisposeAsync();
        }
    }

    private async Task HandleDiagRequestAsync(NamedPipeServerStream pipe, JsonElement req, CancellationToken ct)
    {
        string? requestId = req.TryGetProperty("request_id", out var ri) ? ri.GetString() : null;
        var filePath = req.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        var text = "";

        if (!string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(filePath))
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pendingDiag.TryAdd(requestId!, tcs))
            {
                try
                {
                    // Ask the extension to read the VS Error List for this file.
                    EmitChunk(new ChatChunk
                    {
                        Type = "diagnostics_request",
                        ToolId = requestId,
                        Text = filePath
                    });

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(DiagnosticsTimeout, ct));
                    if (completed == tcs.Task) text = await tcs.Task;
                }
                catch (OperationCanceledException) { }
                finally { _pendingDiag.TryRemove(requestId!, out _); }
            }
        }

        var responseObj = new { type = "diag_response", request_id = requestId, text };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(responseObj) + "\n");
        try
        {
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private static void EmitChunk(ChatChunk chunk)
    {
        Console.WriteLine(JsonSerializer.Serialize(chunk));
        Console.Out.Flush();
    }

    private static void EmitError(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = text }));
        Console.Out.Flush();
    }

    /// <summary>Goes to the Output window only — never into the chat.</summary>
    private static void EmitWarn(string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "warn", Text = text }));
        Console.Out.Flush();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        foreach (var kvp in _pending)
            kvp.Value.TrySetCanceled();
        _pending.Clear();
        foreach (var kvp in _pendingDiag)
            kvp.Value.TrySetResult("");
        _pendingDiag.Clear();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _cts.Dispose();
    }

    // Auto marks a decision nobody clicked — timeout, cancel, shutdown. The card
    // is still on screen at that point, so the UI has to be told, or the user
    // answers a request the agent already closed and gets "no pending permission
    // request for tool_use_id" on their next message (rodada 15, item 43.5).
    internal readonly record struct Decision(bool Allow, string? Reason, bool Auto = false);
}
