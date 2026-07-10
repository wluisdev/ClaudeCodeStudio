using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ClaudeStudioExtension.Agent;

/// <summary>
/// Watches the Claude Code session JSONL file and emits real-time events with
/// authoritative numbers (tokens, tool input/output) — used as a reinforcement
/// channel alongside the stream-json output of the agent (strategy B).
/// </summary>
public sealed class JsonlWatcher : IDisposable
{
    public Action<string, string, string?, string?, string?>? OnEvent { get; set; }

    private FileSystemWatcher? _watcher;
    private string? _filePath;
    private long _offset;
    private readonly byte[] _buffer = new byte[8192];
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    public void Start(string? workingDirectory, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var cwd = string.IsNullOrEmpty(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;

        var encoded = EncodePath(cwd);
        var projDir = Path.Combine(ClaudePaths.ProjectsDir, encoded);

        var filePath = Path.Combine(projDir, $"{sessionId}.jsonl");

        Stop();

        _filePath = filePath;
        _offset = 0;
        _pending.Clear();

        // If the file already exists (resume case), skip to the end so we only see new lines
        try
        {
            if (File.Exists(filePath))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _offset = fs.Length;
            }
            else if (!Directory.Exists(projDir))
            {
                Directory.CreateDirectory(projDir);
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"jsonl watcher init failed: {ex.Message}");
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(projDir, $"{sessionId}.jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;

            OutputLog.Info($"jsonl watcher started: {filePath} (skip to offset {_offset})");
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"jsonl watcher could not attach: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Dispose();
        }
        catch { }
        _watcher = null;
        _filePath = null;
        _offset = 0;
        _pending.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Coalesce bursts — FileSystemWatcher fires multiple events per write
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            ReadNewLines();
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"jsonl watcher read failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void ReadNewLines()
    {
        if (_filePath == null || !File.Exists(_filePath)) return;

        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < _offset)
        {
            // File was truncated/rewritten — reset
            _offset = 0;
            _pending.Clear();
        }

        fs.Seek(_offset, SeekOrigin.Begin);
        int read;
        while ((read = fs.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            _pending.Append(Encoding.UTF8.GetString(_buffer, 0, read));
            _offset += read;
        }

        ExtractAndDispatchLines();
    }

    private void ExtractAndDispatchLines()
    {
        while (true)
        {
            var s = _pending.ToString();
            var nl = s.IndexOf('\n');
            if (nl < 0) return;

            var line = s.Substring(0, nl).TrimEnd('\r');
            _pending.Remove(0, nl + 1);

            if (string.IsNullOrWhiteSpace(line)) continue;

            try { DispatchLine(line); }
            catch (Exception ex) { OutputLog.Warn($"jsonl parse failed: {ex.Message}"); }
        }
    }

    private void DispatchLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var tEl)) return;
        var type = tEl.GetString();
        if (type != "assistant" && type != "user") return;

        if (!root.TryGetProperty("message", out var msg)) return;

        // Emit tokens-live from assistant.usage with cumulative numbers for this turn
        if (type == "assistant" && msg.TryGetProperty("usage", out var usage))
        {
            var inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
            var outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
            var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
            var cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
            OnEvent?.Invoke("tokens-live", "", null, $"{inTok + cacheCreate}/{outTok}/{cacheRead}", "jsonl");
        }

        if (!msg.TryGetProperty("content", out var content)) return;

        if (type == "assistant" && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType)) continue;
                if (itemType.GetString() != "tool_use") continue;

                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string? inputJson = null;
                if (item.TryGetProperty("input", out var inp))
                    inputJson = inp.GetRawText();

                OnEvent?.Invoke("tool_use", name, inputJson, null, id);
            }
        }
        else if (type == "user" && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType)) continue;
                if (itemType.GetString() != "tool_result") continue;

                var id = item.TryGetProperty("tool_use_id", out var idEl) ? idEl.GetString() : null;
                string? summary = null;

                if (item.TryGetProperty("content", out var cProp))
                {
                    if (cProp.ValueKind == JsonValueKind.String)
                    {
                        summary = cProp.GetString();
                    }
                    else if (cProp.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var c in cProp.EnumerateArray())
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
                    summary = summary.Substring(0, 237) + "...";

                bool isError = item.TryGetProperty("is_error", out var err) && err.GetBoolean();
                OnEvent?.Invoke(isError ? "tool_error" : "tool_result", "", null, summary ?? "", id);
            }
        }
    }

    private static string EncodePath(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }
        return sb.ToString();
    }
}
