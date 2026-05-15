using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeVsExtension.Agent;

public class AgentClient
{
    private Process? _process;

    private StreamWriter? _writer;

    private StreamReader? _reader;

    public async Task StartAsync()
    {
        if (_process != null)
            return;

        var agentPath = GetAgentPath();

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

        _process.Start();

        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;

        var ready = await _reader.ReadLineAsync();

        if (ready != "READY")
        {
            var error = await _process.StandardError.ReadToEndAsync();

            throw new Exception(
                $"ClaudeVsAgent falhou ao iniciar.\n" +
                $"Resposta: {ready ?? "NULL"}\n" +
                $"Erro: {error}");
        }
    }

    public async Task<string> AskAsync(string message, string model = "claude-sonnet-4-6")
    {
        var request = new
        {
            Message = message,
            Model = model
        };

        var json = JsonSerializer.Serialize(request);

        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        var responseJson = await _reader!.ReadLineAsync();

        if (responseJson == null)
            return "Agent não respondeu.";

        var response = JsonSerializer.Deserialize<AgentResponse>(responseJson);

        return response?.Text ?? "";
    }

    public async Task StopAsync()
    {
        if (_process == null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _writer?.Close();

                var exited = await Task.Run(() =>
                    _process.WaitForExit(2000));

                if (!exited && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit();
                }
            }
        }
        finally
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _process.Dispose();

            _reader = null;
            _writer = null;
            _process = null;
        }
    }

    private class AgentResponse
    {
        public string Text { get; set; } = "";
    }

    private static string GetAgentPath()
    {
        var extensionDirectory = Path.GetDirectoryName(
            typeof(AgentClient).Assembly.Location)!;

        var agentPath = Path.Combine(
            extensionDirectory,
            "ClaudeVsAgent",
            "ClaudeVsAgent.exe");

        if (File.Exists(agentPath))
            return agentPath;

        throw new FileNotFoundException(
            $"ClaudeVsAgent.exe não encontrado em: {agentPath}");
    }

    //private static string GetAgentPath()
    //{
    //    var currentDirectory = new DirectoryInfo(
    //        Path.GetDirectoryName(typeof(AgentClient).Assembly.Location)!);

    //    while (currentDirectory != null)
    //    {
    //        var agentProjectPath = Path.Combine(
    //            currentDirectory.FullName,
    //            "ClaudeVsAgent");

    //        if (Directory.Exists(agentProjectPath))
    //        {
    //            var agentPath = Directory
    //                .GetFiles(
    //                    agentProjectPath,
    //                    "ClaudeVsAgent.exe",
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
    //        "ClaudeVsAgent.exe não encontrado. Compile o projeto ClaudeVsAgent primeiro.");
    //}
}