using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClaudeStudioAgent;

internal static class HookMode
{
    private const int PipeConnectTimeoutMs = 5000;

    public static async Task<int> RunAsync(string pipeName)
    {
        try
        {
            var payload = await Console.In.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(payload))
            {
                Console.Error.WriteLine("hook: empty stdin payload");
                return 2;
            }

            JsonElement hook;
            try { hook = JsonSerializer.Deserialize<JsonElement>(payload); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hook: failed to parse payload: {ex.Message}");
                return 2;
            }

            string? toolName = hook.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
            string? toolUseId = hook.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
            string? toolInputJson = hook.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : null;

            if (string.IsNullOrEmpty(toolName) || string.IsNullOrEmpty(toolUseId))
            {
                Console.Error.WriteLine("hook: payload missing tool_name or tool_use_id");
                return 2;
            }

            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(PipeConnectTimeoutMs);
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine($"hook: agent pipe '{pipeName}' not reachable (timeout)");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hook: agent pipe '{pipeName}' not reachable: {ex.Message}");
                return 2;
            }

            var request = new
            {
                type = "perm_request",
                tool_use_id = toolUseId,
                tool_name = toolName,
                tool_input = JsonSerializer.Deserialize<JsonElement>(toolInputJson ?? "null"),
                hook_pid = Environment.ProcessId
            };
            var requestLine = JsonSerializer.Serialize(request) + "\n";
            var requestBytes = Encoding.UTF8.GetBytes(requestLine);
            await pipe.WriteAsync(requestBytes);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                Console.Error.WriteLine("hook: empty response from agent");
                return 2;
            }

            JsonElement response;
            try { response = JsonSerializer.Deserialize<JsonElement>(responseLine); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"hook: failed to parse response: {ex.Message}");
                return 2;
            }

            var decision = response.TryGetProperty("decision", out var dec) ? dec.GetString() : null;
            var reason = response.TryGetProperty("reason", out var rsn) ? rsn.GetString() : null;

            if (decision == "allow")
                return 0;

            var denyMsg = !string.IsNullOrEmpty(reason)
                ? $"Permission denied: {reason}"
                : "Permission denied by user";
            Console.Error.WriteLine(denyMsg);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hook: unexpected error: {ex.Message}");
            return 2;
        }
    }
}
