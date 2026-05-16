using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeVsShared;

string? sessionId = null;

try
{
    Console.WriteLine("READY");
    Console.Out.Flush();

    while (true)
    {
        var line = Console.ReadLine();

        if (line == null)
            break;

        if (string.IsNullOrWhiteSpace(line))
            continue;

        var request = JsonSerializer.Deserialize<ChatRequest>(line);

        if (request == null)
            continue;

        if (request.ResetSession)
        {
            sessionId = null;
            continue;
        }

        if (request.ResumeSessionId != null)
            sessionId = request.ResumeSessionId;

        var message = request.Message ?? "";
        var model = request.Model ?? "claude-sonnet-4-6";
        var effort = request.Effort;
        var permissionMode = request.PermissionMode ?? "yolo";

        sessionId = await StreamClaudeAsync(message, model, effort, permissionMode, request.WorkingDirectory, sessionId, request.AutoResume);
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = ex.ToString() }));
    Console.Out.Flush();
}

static void EmitTiming(string label, long ms)
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "timing", Text = $"{label}: {ms}ms" }));
    Console.Out.Flush();
}

static async Task<string?> StreamClaudeAsync(string message, string model, string? effort, string permissionMode, string? workingDirectory, string? sessionId, bool autoResume)
{
    var sw = Stopwatch.StartNew();
    string? newSessionId = null;

    var psi = new ProcessStartInfo
    {
        FileName = FindClaude(),

        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,

        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,

        UseShellExecute = false,
        CreateNoWindow = true
    };

    if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
        psi.WorkingDirectory = workingDirectory;

    psi.ArgumentList.Add("--output-format");
    psi.ArgumentList.Add("stream-json");
    psi.ArgumentList.Add("--verbose");
    psi.ArgumentList.Add("-p");
    psi.ArgumentList.Add(message);
    psi.ArgumentList.Add("--model");
    psi.ArgumentList.Add(model);
    if (permissionMode == "plan")
    {
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("plan");
    }
    else if (permissionMode == "ask")
    {
        // sem flag extra — comportamento padrão do claude
    }
    else // "yolo" (padrão)
    {
        psi.ArgumentList.Add("--dangerously-skip-permissions");
    }

    if (!string.IsNullOrEmpty(effort))
    {
        psi.ArgumentList.Add("--effort");
        psi.ArgumentList.Add(effort);
    }

    if (sessionId != null)
    {
        psi.ArgumentList.Add("--resume");
        psi.ArgumentList.Add(sessionId);
    }
    else if (autoResume)
    {
        psi.ArgumentList.Add("--continue");
    }

    using var process = Process.Start(psi);

    if (process == null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = "Não foi possível iniciar Claude." }));
        Console.Out.Flush();
        return sessionId;
    }

    EmitTiming("process started", sw.ElapsedMilliseconds);
    process.StandardInput.Close();

    var errorTask = process.StandardError.ReadToEndAsync();
    string? line;
    bool firstLine = true;
    bool firstChunk = true;

    while ((line = await process.StandardOutput.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;

        if (firstLine)
        {
            EmitTiming("first stdout line", sw.ElapsedMilliseconds);
            firstLine = false;
        }

        try
        {
            var evt = JsonSerializer.Deserialize<JsonElement>(line);

            if (!evt.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            if (type == "assistant")
            {
                var content = evt.GetProperty("message").GetProperty("content");
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString() == "text" &&
                        item.TryGetProperty("text", out var textProp))
                    {
                        var text = textProp.GetString();
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
            else if (type == "result")
            {
                EmitTiming("result received", sw.ElapsedMilliseconds);

                if (evt.TryGetProperty("session_id", out var sidProp))
                    newSessionId = sidProp.GetString();

                if (evt.TryGetProperty("usage", out var usage))
                {
                    var inputTok    = usage.TryGetProperty("input_tokens",                out var inp)   ? inp.GetInt32()   : 0;
                    var outputTok   = usage.TryGetProperty("output_tokens",               out var out_)  ? out_.GetInt32()  : 0;
                    var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc)    ? cc.GetInt32()    : 0;
                    var cacheRead   = usage.TryGetProperty("cache_read_input_tokens",     out var cr)    ? cr.GetInt32()    : 0;
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
                break;
            }
        }
        catch { /* ignora linhas malformadas */ }
    }

    EmitTiming("total", sw.ElapsedMilliseconds);
    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "done" }));
    Console.Out.Flush();

    var error = await errorTask;
    process.WaitForExit();

    if (!string.IsNullOrWhiteSpace(error) && process.ExitCode != 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = error.Trim() }));
        Console.Out.Flush();
    }

    return newSessionId ?? sessionId;
}

static string FindClaude()
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
        "claude.exe não encontrado. Verifique se o Claude Code está instalado e no PATH.");
}

