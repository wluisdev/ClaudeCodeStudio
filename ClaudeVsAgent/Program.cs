using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

        var message = request?.Message ?? "";
        var model = request?.Model ?? "claude-sonnet-4-6";

        var responseText = await AskClaudeAsync(message, model);

        var response = new ChatResponse
        {
            Text = responseText
        };

        Console.WriteLine(JsonSerializer.Serialize(response));
        Console.Out.Flush();
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new ChatResponse
    {
        Text = ex.ToString()
    }));

    Console.Out.Flush();
}

static async Task<string> AskClaudeAsync(string message, string model)
{
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

    psi.ArgumentList.Add("-p");
    psi.ArgumentList.Add(message);
    psi.ArgumentList.Add("--model");
    psi.ArgumentList.Add(model);
    psi.ArgumentList.Add("--dangerously-skip-permissions");

    using var process = Process.Start(psi);

    if (process == null)
        return "Não foi possível iniciar Claude.";

    process.StandardInput.Close();

    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();

    await Task.WhenAll(outputTask, errorTask);
    process.WaitForExit();

    var error = errorTask.Result;
    if (!string.IsNullOrWhiteSpace(error))
        return error;

    return outputTask.Result.Trim();
}

static string FindClaude()
{
    // Busca no PATH primeiro
    var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
    foreach (var dir in pathDirs)
    {
        var candidate = Path.Combine(dir, "claude.exe");
        if (File.Exists(candidate))
            return candidate;
    }

    // Fallback: locais comuns de instalação global do npm
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

public class ChatRequest
{
    public string Message { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6";
}

public class ChatResponse
{
    public string Text { get; set; } = "";
}