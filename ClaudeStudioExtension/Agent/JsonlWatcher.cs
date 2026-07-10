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
    // Stateful decoder: a multibyte UTF-8 char split across two reads must not
    // be decoded per-chunk (audit 2026-07-10 #7 — per-chunk GetString turned
    // accented chars on the 8KB boundary into mojibake).
    private readonly System.Text.Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _charBuffer = new char[Encoding.UTF8.GetMaxCharCount(8192)];
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
        _decoder.Reset();

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

    // Set when an event arrives while another thread holds _gate, so the
    // holder loops once more instead of the notification being lost (audit
    // 2026-07-10 #9). A sub-microsecond race window remains between the final
    // check and the lock release — acceptable for a reinforcement channel
    // (the next write fires a fresh event anyway).
    private int _rerun;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Coalesce bursts — FileSystemWatcher fires multiple events per write
        if (!Monitor.TryEnter(_gate))
        {
            Interlocked.Exchange(ref _rerun, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _rerun, 0);
                try
                {
                    ReadNewLines();
                }
                catch (Exception ex)
                {
                    OutputLog.Warn($"jsonl watcher read failed: {ex.Message}");
                }
            } while (Interlocked.CompareExchange(ref _rerun, 0, 1) == 1);
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
            _decoder.Reset();
        }

        fs.Seek(_offset, SeekOrigin.Begin);
        int read;
        while ((read = fs.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            var chars = _decoder.GetChars(_buffer, 0, read, _charBuffer, 0);
            _pending.Append(_charBuffer, 0, chars);
            _offset += read;
        }

        ExtractAndDispatchLines();
    }

    private void ExtractAndDispatchLines()
    {
        // One snapshot + one Remove per batch (audit 2026-07-10 #10) — the old
        // ToString-per-line was quadratic on large bursts.
        if (_pending.Length == 0) return;
        var s = _pending.ToString();
        int start = 0;

        while (true)
        {
            var nl = s.IndexOf('\n', start);
            if (nl < 0) break;

            var line = s.Substring(start, nl - start).TrimEnd('\r');
            start = nl + 1;

            if (string.IsNullOrWhiteSpace(line)) continue;

            try { DispatchLine(line); }
            catch (Exception ex) { OutputLog.Warn($"jsonl parse failed: {ex.Message}"); }
        }

        if (start > 0) _pending.Remove(0, start);
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
