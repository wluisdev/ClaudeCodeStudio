using System;
using System.Collections.Generic;
using System.IO;

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
        IReadOnlyList<string>? fallbackPaths = null)
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
                warn?.Invoke($"another claude.exe found on PATH at {pathMatch} — using {nativeInstallPath} instead");
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
}
