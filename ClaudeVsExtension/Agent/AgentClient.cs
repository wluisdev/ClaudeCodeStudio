using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClaudeVsShared;

namespace ClaudeVsExtension.Agent;

public class AgentClient
{
    private Process? _process;

    private StreamWriter? _writer;

    private StreamReader? _reader;

    private TaskCompletionSource<bool>? _cancelTcs;

    public string? PendingResumeSessionId { get; set; }
    public string? CurrentSessionId { get; private set; }

    public void CancelCurrent()
    {
        OutputLog.Info("request cancel requested");
        _cancelTcs?.TrySetResult(true);
    }

    public async Task StartAsync()
    {
        if (_process != null)
            return;

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
                $"ClaudeVsAgent failed to start.\n" +
                $"Response: {ready ?? "NULL"}");
        }

        OutputLog.Info($"agent started (pid {_process.Id})");
    }

    public async Task AskStreamingAsync(string message, string model, string? effort, string permissionMode, Action<string> onChunk, Action<string>? onTiming = null, Action<string>? onTokens = null, string? workingDirectory = null, bool autoResume = false, Action<string>? onSession = null)
    {
        var resumeId = PendingResumeSessionId;
        PendingResumeSessionId = null;

        var request = new ChatRequest
        {
            Message = message,
            Model = model,
            Effort = effort,
            PermissionMode = permissionMode,
            ResumeSessionId = resumeId,
            WorkingDirectory = workingDirectory,
            AutoResume = autoResume
        };
        var json = JsonSerializer.Serialize(request);

        OutputLog.Info($"request → model={model} effort={effort ?? "-"} perm={permissionMode} cwd={workingDirectory ?? "-"} resume={resumeId ?? "-"} autoResume={autoResume} bytes={message?.Length ?? 0}");

        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();

        _cancelTcs = new TaskCompletionSource<bool>();
        bool cancelled = false;

        while (true)
        {
            var readTask = _reader!.ReadLineAsync();
            var winner = await Task.WhenAny(readTask, _cancelTcs.Task);
            if (winner == _cancelTcs.Task) cancelled = true;

            var responseJson = await readTask;

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

            if (chunk.Type == "tokens")
            {
                OutputLog.Info($"tokens: {chunk.Text}");
                onTokens?.Invoke(chunk.Text);
                continue;
            }

            if (chunk.Type == "session")
            {
                CurrentSessionId = chunk.Text;
                OutputLog.Info($"session: {chunk.Text}");
                onSession?.Invoke(chunk.Text);
                continue;
            }

            if (chunk.Type == "error")
            {
                OutputLog.Error($"agent error: {chunk.Text}");
                if (!string.IsNullOrEmpty(chunk.Text))
                    onChunk(chunk.Text);
                break;
            }

            if (chunk.Type == "chunk" && !string.IsNullOrEmpty(chunk.Text))
                onChunk(chunk.Text);
        }

        _cancelTcs = null;
    }

    public async Task StopAsync()
    {
        if (_process == null)
            return;

        var pid = _process.Id;
        OutputLog.Info($"stopping agent (pid {pid})");

        try
        {
            if (!_process.HasExited)
            {
                _writer?.Close();

                var exited = await Task.Run(() =>
                    _process.WaitForExit(2000));

                if (!exited && !_process.HasExited)
                {
                    OutputLog.Warn($"agent (pid {pid}) didn't exit in 2s, killing");
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
            OutputLog.Info($"agent (pid {pid}) stopped");
        }
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
            $"ClaudeVsAgent.exe not found at: {agentPath}");
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
    //        "ClaudeVsAgent.exe not found. Build the ClaudeVsAgent project first.");
    //}
}