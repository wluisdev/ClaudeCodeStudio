using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ClaudeStudioAgent;

internal static class HookMode
{
    private const int PipeConnectTimeoutMs = 5000;

    // claude writes the hook payload to our stdin as UTF-8. Console.In would
    // decode it with the console's OEM code page (CP850/437 on pt-BR Windows),
    // mangling accents (e.g. "execução" → "execu├º├úo") which then surface in
    // the permission popup. Read the raw stream as UTF-8 instead.
    private static async Task<string> ReadStdinUtf8Async()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        return await stdin.ReadToEndAsync();
    }

    public static async Task<int> RunAsync(string pipeName)
    {
        try
        {
            var payload = await ReadStdinUtf8Async();
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
            string? cwd = hook.TryGetProperty("cwd", out var cw) ? cw.GetString() : null;

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
                cwd,
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

    // PostToolUse hook (Edit/Write/MultiEdit). Asks the agent (→ extension) for
    // the VS Error List entries of the edited file and surfaces them to claude
    // via hookSpecificOutput.additionalContext. Always exits 0 — diagnostics are
    // informational, never blocking. A failure just means no extra context.
    public static async Task<int> RunPostAsync(string pipeName)
    {
        try
        {
            var payload = await ReadStdinUtf8Async();
            if (string.IsNullOrWhiteSpace(payload)) return 0;

            JsonElement hook;
            try { hook = JsonSerializer.Deserialize<JsonElement>(payload); }
            catch { return 0; }

            string? toolUseId = hook.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() : null;
            string? filePath = null;
            if (hook.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object
                && ti.TryGetProperty("file_path", out var fp))
                filePath = fp.GetString();

            if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(filePath))
                return 0; // nothing to query

            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(PipeConnectTimeoutMs); }
            catch { return 0; }

            var request = new
            {
                type = "diag_request",
                request_id = toolUseId,
                file_path = filePath,
                hook_pid = Environment.ProcessId
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(responseLine)) return 0;

            JsonElement response;
            try { response = JsonSerializer.Deserialize<JsonElement>(responseLine); }
            catch { return 0; }

            var diagText = response.TryGetProperty("text", out var txt) ? txt.GetString() : null;
            if (string.IsNullOrWhiteSpace(diagText)) return 0; // no problems → no context

            var output = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PostToolUse",
                    additionalContext = diagText
                }
            };
            // Write UTF-8 explicitly: additionalContext carries the VS Error List
            // text, whose messages are localized (pt-BR errors have accents). The
            // OEM-codepage default would garble them on the way back to claude.
            await using (var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
                await stdout.WriteLineAsync(JsonSerializer.Serialize(output));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"hook-post: {ex.Message}");
            return 0;
        }
    }
}
