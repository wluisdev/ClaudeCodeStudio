using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeStudioExtension;

// Sidecar store for session titles (V18): %APPDATA%\ClaudeStudio\session-titles.json.
// Generated titles come from claude's generate_session_title control_request;
// Custom is reserved for manual renames (dliedke D4) and always wins over
// Generated. persist:false on the RPC — we own the storage, like the official
// extension does.
public static class SessionTitlesStore
{
    public class Entry
    {
        public string? Generated { get; set; }
        public string? Custom { get; set; }
    }

    private static readonly object _lock = new();
    private static Dictionary<string, Entry>? _cache;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeStudio", "session-titles.json");

    private static Dictionary<string, Entry> Load()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(StorePath))
                    _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(StorePath));
            }
            catch { /* corrupt store — start fresh */ }
            return _cache ??= new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    // Custom (manual rename) wins over Generated; null when the session has neither.
    public static string? GetTitle(string sessionId)
    {
        var store = Load();
        lock (_lock)
            return store.TryGetValue(sessionId, out var e)
                ? (string.IsNullOrWhiteSpace(e.Custom) ? e.Generated : e.Custom)
                : null;
    }

    public static bool HasEntry(string sessionId)
    {
        var store = Load();
        lock (_lock) return store.ContainsKey(sessionId);
    }

    // Split accessors (U2): history interleaves sidecar titles with the native
    // custom-title/ai-title lines read from the session JSONL, so it needs the
    // two halves separately (precedence: sidecar Custom > native custom >
    // sidecar Generated > native ai).
    public static string? GetCustom(string sessionId)
    {
        var store = Load();
        lock (_lock)
            return store.TryGetValue(sessionId, out var e) && !string.IsNullOrWhiteSpace(e.Custom)
                ? e.Custom : null;
    }

    public static string? GetGenerated(string sessionId)
    {
        var store = Load();
        lock (_lock)
            return store.TryGetValue(sessionId, out var e) && !string.IsNullOrWhiteSpace(e.Generated)
                ? e.Generated : null;
    }

    public static void SetGenerated(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(title)) return;
        var store = Load();
        lock (_lock)
        {
            if (!store.TryGetValue(sessionId, out var e)) store[sessionId] = e = new Entry();
            e.Generated = title.Trim();
            Save();
        }
    }

    // D4-ready: manual rename. Empty/null clears the custom title (falls back
    // to Generated). A clear is stored as "" rather than null so History can
    // tell "explicitly cleared" (also suppress the native custom-title line
    // left in the JSONL) from "never renamed" (native line may apply).
    public static void SetCustom(string sessionId, string? title)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        var store = Load();
        lock (_lock)
        {
            if (!store.TryGetValue(sessionId, out var e)) store[sessionId] = e = new Entry();
            e.Custom = string.IsNullOrWhiteSpace(title) ? "" : title!.Trim();
            Save();
        }
    }

    public static bool WasCustomCleared(string sessionId)
    {
        var store = Load();
        lock (_lock)
            return store.TryGetValue(sessionId, out var e) && e.Custom != null && e.Custom.Length == 0;
    }
}
