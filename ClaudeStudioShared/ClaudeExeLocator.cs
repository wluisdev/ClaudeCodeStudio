using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeStudioShared;

// Thrown when claude.exe can't be found anywhere ClaudeExeLocator knows to
// look. Shared so the agent (which throws it) and tests (which assert on it)
// use one definition; ClaudeSession's main loop catches this type by name to
// render a dedicated "claude-not-found" chunk instead of a generic error.
public sealed class ClaudeNotFoundException(string message) : Exception(message);

// Locates claude.exe, relocated out of ClaudeSession (ClaudeStudioAgent/Program.cs)
// so the configured/native/PATH/fallback precedence is testable without
// depending on whatever happens to be installed on the machine running the
// tests. All I/O is behind optional seams that default to the real
// File/Directory/Environment calls — production call sites pass none of them
// and get identical behavior to before this was extracted.
public static class ClaudeExeLocator
{
    public static string FindClaudeExe(
        string? configured,
        Action<string>? warn = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        IReadOnlyList<string>? pathDirs = null,
        string? nativeInstallPath = null,
        IReadOnlyList<string>? fallbackPaths = null,
        Action<string, string>? onPathShadow = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        pathDirs ??= Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        nativeInstallPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin\claude.exe");
        fallbackPaths ??= new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\claude.exe"),
        };

        // Explicit path from the UI (D7) wins; a bad value fails loudly (with
        // the value in the message) instead of silently falling back to PATH.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = configured!.Trim();
            if (fileExists(p))
                return p;
            var inDir = Path.Combine(p, "claude.exe");
            if (directoryExists(p) && fileExists(inDir))
                return inDir;
            throw new ClaudeNotFoundException(
                $"The configured CLI path was not found: {p} — fix or clear it in settings (Claude Code → CLI path).");
        }

        // Native installer / `claude update` target. Checked before the PATH scan:
        // a stale shim earlier on PATH (e.g. chocolatey) would otherwise shadow
        // this with an older version (drift found 2026-07-19 — choco stuck at
        // 2.1.205 while ~\.local\bin had already moved to 2.1.215).
        var nativeExists = fileExists(nativeInstallPath);

        string? pathMatch = null;
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (fileExists(candidate)) { pathMatch = candidate; break; }
        }

        if (nativeExists)
        {
            if (pathMatch != null && !string.Equals(pathMatch, nativeInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                warn?.Invoke($"another claude.exe found on PATH at {pathMatch} — using {nativeInstallPath} instead");
                // issue #7: the native install we prefer can itself be the STALE
                // one while PATH has a newer build (a WinGet copy, in that case).
                // Hand both paths back so the caller can probe versions and
                // surface the duplicate visibly (the warn line above is log-only).
                onPathShadow?.Invoke(nativeInstallPath, pathMatch);
            }
            return nativeInstallPath;
        }

        if (pathMatch != null)
            return pathMatch;

        foreach (var path in fallbackPaths)
            if (fileExists(path))
                return path;

        throw new ClaudeNotFoundException(
            "claude.exe was not found on your PATH or any standard install location.");
    }

    // Extracts a leading dotted-numeric version from a `claude --version` line
    // ("2.1.229 (Claude Code)" -> [2, 1, 229]). Stops at the first non-numeric
    // segment (so "2.1.229-beta" -> [2, 1, 229]). Returns null when the string
    // does not start with a number.
    public static int[]? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var token = s!.TrimStart().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0];
        var nums = new List<int>();
        foreach (var part in token.Split('.'))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            nums.Add(int.Parse(digits));
        }
        return nums.Count > 0 ? nums.ToArray() : null;
    }

    // Compares two `claude --version` strings: >0 if a is newer than b, &lt;0 if
    // older, 0 if equal or either can't be parsed (never guess "newer" from an
    // unparseable version).
    public static int CompareVersions(string? a, string? b)
    {
        var va = ParseVersion(a);
        var vb = ParseVersion(b);
        if (va == null || vb == null) return 0;
        var n = Math.Max(va.Length, vb.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < va.Length ? va[i] : 0;
            var y = i < vb.Length ? vb[i] : 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }
}
