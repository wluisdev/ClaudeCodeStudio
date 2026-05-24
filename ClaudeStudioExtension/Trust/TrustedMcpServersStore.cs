using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeStudioExtension.Mcp;

namespace ClaudeStudioExtension.Trust;

/// <summary>
/// Persistent list of MCP servers the user has explicitly approved. An entry
/// is bound to (name, scope, hash) — renaming, switching scope or changing
/// the payload (transport/command/url/env-keys) invalidates the trust and
/// the user is prompted again on the next session.
/// </summary>
public static class TrustedMcpServersStore
{
    private static readonly object _lock = new();
    // In-memory "skip for this VS session" entries — survive until the process
    // exits. Lets users dismiss a re-prompt loop without trusting the server.
    private static readonly HashSet<string> _sessionSkipped = new(StringComparer.Ordinal);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeStudio",
        "trusted-mcp-servers.json");

    public sealed class Entry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "";   // "user" | "project"

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        // Only set for scope="project". Bound to the resolved project folder so
        // a trust granted in RepoA doesn't carry over to RepoB even if both
        // declare an MCP server with the same name/payload.
        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("trustedAt")]
        public string TrustedAt { get; set; } = "";
    }

    public static IReadOnlyList<Entry> GetAll()
    {
        lock (_lock) return Load();
    }

    public static bool IsTrusted(string name, McpScope scope, string hash, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var scopeStr = ScopeToString(scope);
        var normalizedPath = scope == McpScope.Project ? NormalizePath(projectPath) : null;
        var key = SessionKey(name, scopeStr, hash, normalizedPath);
        lock (_lock)
        {
            if (_sessionSkipped.Contains(key)) return true;
            return Load().Any(e =>
                string.Equals(e.Name, name, StringComparison.Ordinal) &&
                string.Equals(e.Scope, scopeStr, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Hash, hash, StringComparison.Ordinal) &&
                ProjectMatches(e.ProjectPath, normalizedPath));
        }
    }

    public static void SkipForSession(string name, McpScope scope, string hash, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hash)) return;
        var normalizedPath = scope == McpScope.Project ? NormalizePath(projectPath) : null;
        var key = SessionKey(name, ScopeToString(scope), hash, normalizedPath);
        lock (_lock) _sessionSkipped.Add(key);
    }

    private static string SessionKey(string name, string scope, string hash, string? projectPath) =>
        $"{scope}\0{name}\0{hash}\0{projectPath ?? ""}";

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path!.Trim().TrimEnd('\\', '/');
        try { p = Path.GetFullPath(p); } catch { }
        return p.ToLowerInvariant();
    }

    private static bool ProjectMatches(string? stored, string? incoming)
    {
        // User-scope entries (stored == null) always match — they're global.
        if (string.IsNullOrEmpty(stored)) return true;
        return string.Equals(stored, incoming, StringComparison.OrdinalIgnoreCase);
    }

    public static void Trust(string name, McpScope scope, string hash, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(hash)) return;
        var scopeStr = ScopeToString(scope);
        var normalizedPath = scope == McpScope.Project ? NormalizePath(projectPath) : null;
        lock (_lock)
        {
            var list = Load();
            // Replace any prior trust for the same (name, scope, projectPath) — newer
            // hash wins, older ones are stale and just clutter the file.
            list.RemoveAll(e =>
                string.Equals(e.Name, name, StringComparison.Ordinal) &&
                string.Equals(e.Scope, scopeStr, StringComparison.OrdinalIgnoreCase) &&
                ProjectMatches(e.ProjectPath, normalizedPath));
            list.Add(new Entry
            {
                Name = name,
                Scope = scopeStr,
                Hash = hash,
                ProjectPath = normalizedPath,
                TrustedAt = DateTime.UtcNow.ToString("O")
            });
            Save(list);
        }
    }

    private static string ScopeToString(McpScope scope) =>
        scope == McpScope.Project ? "project" : "user";

    private static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Entry>();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!doc.RootElement.TryGetProperty("servers", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<Entry>();

            var list = new List<Entry>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(el, "name", "Name");
                var scope = ReadString(el, "scope", "Scope");
                var hash = ReadString(el, "hash", "Hash");
                var projectPath = ReadString(el, "projectPath", "ProjectPath");
                var trustedAt = ReadString(el, "trustedAt", "TrustedAt") ?? "";
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(scope))
                {
                    list.Add(new Entry
                    {
                        Name = name!,
                        Scope = scope!,
                        Hash = hash!,
                        ProjectPath = projectPath,
                        TrustedAt = trustedAt
                    });
                }
            }
            return list;
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private static string? ReadString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static void Save(List<Entry> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                new { servers = list },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"trusted-mcp-servers save failed: {ex.Message}");
        }
    }
}
