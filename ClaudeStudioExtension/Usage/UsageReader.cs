using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeStudioShared;

namespace ClaudeStudioExtension.Usage;

public static class UsageReader
{
    public static string GetProjectsRoot() => ClaudePaths.ProjectsDir;

    public static List<SessionUsage> ReadAll()
    {
        var root = GetProjectsRoot();
        if (!Directory.Exists(root)) return new();

        var result = new List<SessionUsage>();
        foreach (var projDir in Directory.EnumerateDirectories(root))
        {
            foreach (var jsonl in Directory.EnumerateFiles(projDir, "*.jsonl"))
            {
                try
                {
                    var session = ParseFile(jsonl);
                    if (session != null) result.Add(session);
                }
                catch { /* skip malformed */ }
            }
        }
        return result.OrderByDescending(s => s.LastTimestamp).ToList();
    }

    // #22a: line-parsing logic lives in ClaudeStudioShared.UsageParser now,
    // unit-tested there. FileShare.ReadWrite is deliberate — the CLI may
    // still be actively writing to this transcript while we read it.
    private static SessionUsage? ParseFile(string path) => UsageParser.ParseLines(ReadLinesShared(path));

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }
}
