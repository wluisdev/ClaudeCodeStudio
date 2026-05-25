using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeStudioExtension.Trust;

/// <summary>
/// Persistent list of workspace folders the user has explicitly trusted.
/// Match is prefix-based and case-insensitive, so "trust parent" automatically
/// covers descendants — exactly what the user wants when they clone many repos
/// into the same root.
/// </summary>
public static class TrustedWorkspacesStore
{
    private static readonly object _lock = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeStudio",
        "trusted-workspaces.json");

    public static IReadOnlyList<string> GetAll()
    {
        lock (_lock) return Load();
    }

    public static bool IsTrusted(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = Normalize(path!);
        lock (_lock)
        {
            foreach (var trusted in Load())
            {
                if (IsPathUnderOrEqual(normalized, trusted)) return true;
            }
        }
        return false;
    }

    public static void Trust(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
        lock (_lock)
        {
            var list = Load();
            // Drop any existing entries that the new one would cover (or be covered by).
            var cleaned = list.Where(p =>
                !IsPathUnderOrEqual(normalized, p) &&
                !IsPathUnderOrEqual(p, normalized)).ToList();
            cleaned.Add(normalized);
            Save(cleaned);
        }
    }

    public static void Untrust(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Normalize(path);
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
    }

    private static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<string>();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!doc.RootElement.TryGetProperty("paths", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            var list = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var normalized = Normalize(s!);
                    // Passive cleanup of orphans: drop entries whose folder no
                    // longer exists. The file isn't rewritten here — Trust/Untrust
                    // will persist the cleaned list naturally on the next mutation.
                    // Network paths that are temporarily unreachable also fail
                    // Directory.Exists; users dealing with those can re-trust.
                    try { if (!Directory.Exists(normalized)) continue; } catch { continue; }
                    list.Add(normalized);
                }
            }
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void Save(List<string> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                new { paths = list },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"trusted-workspaces save failed: {ex.Message}");
        }
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().TrimEnd('\\', '/');
        try { p = Path.GetFullPath(p); } catch { }
        return p;
    }

    private static bool IsPathUnderOrEqual(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        var rootWithSep = root.EndsWith("\\") || root.EndsWith("/") ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
