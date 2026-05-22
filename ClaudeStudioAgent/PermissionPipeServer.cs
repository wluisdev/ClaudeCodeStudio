using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

internal sealed class PermissionPipeServer : IAsyncDisposable
{
    // TODO: surface as setting in v2
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(3);

    public string PipeName { get; }

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Decision>> _pending = new();
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

    public void AllowForSession(string toolName)
    {
        lock (_sessionAllowedLock) _sessionAllowed.Add(toolName);
    }

    private bool IsSessionAllowed(string toolName)
    {
        lock (_sessionAllowedLock) return _sessionAllowed.Contains(toolName);
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

            toolUseId = req.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
            var toolName = req.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
            var toolInput = req.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : null;
            var cwd = req.TryGetProperty("cwd", out var cw) ? cw.GetString() : null;

            if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(toolName))
            {
                EmitError("pipe: request missing tool_use_id or tool_name");
                return;
            }

            // Session allowlist short-circuit — auto-approve without bothering the UI.
            // Set is populated when the user clicks "Allow for session" on a prior
            // prompt for this tool.
            Decision decision;
            if (IsSessionAllowed(toolName!))
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
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _cts.Dispose();
    }

    internal readonly record struct Decision(bool Allow, string? Reason);
}
