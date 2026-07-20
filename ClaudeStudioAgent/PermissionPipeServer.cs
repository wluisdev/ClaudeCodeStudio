using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

internal sealed class PermissionPipeServer : IAsyncDisposable
{
    // TODO: surface as setting in v2
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(3);

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
            kvp.Value.TrySetResult(new Decision(false, reason));
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

    public void SetRules(ClaudeSettings? settings)
    {
        if (settings == null ||
            ((settings.PermissionAllow?.Count ?? 0) == 0 &&
             (settings.PermissionAsk?.Count ?? 0) == 0 &&
             (settings.PermissionDeny?.Count ?? 0) == 0))
        {
            _rules = null;
            return;
        }

        static List<PermRule> Parse(List<string>? raw)
        {
            var list = new List<PermRule>();
            foreach (var r in raw ?? [])
            {
                var m = Regex.Match(r.Trim(), @"^([A-Za-z_][A-Za-z0-9_]*)\s*(?:\((.*)\))?$", RegexOptions.Singleline);
                if (m.Success)
                    list.Add(new PermRule(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null, r.Trim()));
            }
            return list;
        }

        _rules = new RuleSet(Parse(settings.PermissionAllow), Parse(settings.PermissionAsk), Parse(settings.PermissionDeny));
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
                if (!string.Equals(r.Tool, toolName, StringComparison.OrdinalIgnoreCase)) continue;
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
    // (plan mode keeps them under <config>/plans/). Honors CLAUDE_CONFIG_DIR.
    private static readonly string PlansDir = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "plans");

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

            Decision decision;
            if (IsPlanFileWrite(toolName!, toolInput))
            {
                // Plan mode saves its plan document to ~/.claude/plans/<slug>.md
                // through the Write tool — CLI bookkeeping, not a workspace edit.
                // Without this bypass every plan-mode turn pops a scary Write
                // modal for the plan file (validation round 2026-07-16).
                decision = new Decision(true, null);
            }
            else if (verdict == RuleVerdict.Deny)
            {
                decision = new Decision(false, $"Denied by permission rule: {matchedRule}");
            }
            else if (verdict == RuleVerdict.Allow)
            {
                decision = new Decision(true, null);
            }
            else if (verdict != RuleVerdict.Ask && IsSessionAllowed(toolName!))
            {
                decision = new Decision(true, null);
            }
            else
            {
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

                var timeoutTask = Task.Delay(ResponseTimeout, ct);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completed == tcs.Task)
                {
                    decision = await tcs.Task;
                }
                else if (ct.IsCancellationRequested)
                {
                    decision = new Decision(false, "Agent shutting down");
                }
                else
                {
                    decision = new Decision(false, $"Permission timed out ({ResponseTimeout.TotalMinutes:F0}min)");
                }
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

    internal readonly record struct Decision(bool Allow, string? Reason);
}
