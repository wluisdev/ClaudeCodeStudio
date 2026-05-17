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
    psi.ArgumentList.Add("--include-partial-messages");
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
        // no extra flag — default claude behavior
    }
    else // "yolo" (default)
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
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "error", Text = "Could not start Claude." }));
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
                var msgObj = evt.GetProperty("message");

                if (msgObj.TryGetProperty("usage", out var usageLive))
                {
                    var inTok = usageLive.TryGetProperty("input_tokens", out var iL) ? iL.GetInt32() : 0;
                    var outTok = usageLive.TryGetProperty("output_tokens", out var oL) ? oL.GetInt32() : 0;
                    var cacheRead = usageLive.TryGetProperty("cache_read_input_tokens", out var crL) ? crL.GetInt32() : 0;
                    var cacheCreate = usageLive.TryGetProperty("cache_creation_input_tokens", out var ccL) ? ccL.GetInt32() : 0;
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
                    {
                        Type = "tokens-live",
                        Text = $"{inTok + cacheCreate}/{outTok}/{cacheRead}"
                    }));
                    Console.Out.Flush();
                }

                // Text content is emitted incrementally via stream_event deltas
                // (see --include-partial-messages). Only tool_use is parsed here
                // because the final assistant event carries the fully-resolved input JSON.
                var content = msgObj.GetProperty("content");
                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var itemType)) continue;
                    if (itemType.GetString() != "tool_use") continue;

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
                else if (evtTypeStr == "message_delta")
                {
                    if (!streamEvt.TryGetProperty("usage", out var deltaUsage)) continue;
                    var inTok = deltaUsage.TryGetProperty("input_tokens", out var iD) ? iD.GetInt32() : 0;
                    var outTok = deltaUsage.TryGetProperty("output_tokens", out var oD) ? oD.GetInt32() : 0;
                    var cacheRead = deltaUsage.TryGetProperty("cache_read_input_tokens", out var crD) ? crD.GetInt32() : 0;
                    var cacheCreate = deltaUsage.TryGetProperty("cache_creation_input_tokens", out var ccD) ? ccD.GetInt32() : 0;
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
                    {
                        Type = "tokens-live",
                        Text = $"{inTok + cacheCreate}/{outTok}/{cacheRead}"
                    }));
                    Console.Out.Flush();
                }
                else if (evtTypeStr == "message_start")
                {
                    if (!streamEvt.TryGetProperty("message", out var startMsg)) continue;
                    if (!startMsg.TryGetProperty("usage", out var startUsage)) continue;
                    var inTok = startUsage.TryGetProperty("input_tokens", out var iS) ? iS.GetInt32() : 0;
                    var outTok = startUsage.TryGetProperty("output_tokens", out var oS) ? oS.GetInt32() : 0;
                    var cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var crS) ? crS.GetInt32() : 0;
                    var cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var ccS) ? ccS.GetInt32() : 0;
                    Console.WriteLine(JsonSerializer.Serialize(new ChatChunk
                    {
                        Type = "tokens-live",
                        Text = $"{inTok + cacheCreate}/{outTok}/{cacheRead}"
                    }));
                    Console.Out.Flush();
                }
            }
            else if (type == "user")
            {
                if (!evt.TryGetProperty("message", out var userMsg)) continue;
                if (!userMsg.TryGetProperty("content", out var userContent)) continue;
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
                                    c.TryGetProperty("text", out var t))
                                {
                                    if (sb.Length > 0) sb.Append('\n');
                                    sb.Append(t.GetString());
                                }
                            }
                            summary = sb.ToString();
                        }
                    }

                    if (summary != null && summary.Length > 240)
                        summary = summary.Substring(0, 240) + "…";

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

                if (evt.TryGetProperty("session_id", out var sidProp))
                {
                    newSessionId = sidProp.GetString();
                    if (!string.IsNullOrEmpty(newSessionId))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = "session", Text = newSessionId }));
                        Console.Out.Flush();
                    }
                }

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
        "claude.exe not found. Verify that Claude Code is installed and on PATH.");
}

