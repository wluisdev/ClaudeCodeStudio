using System;
using System.IO;

namespace ClaudeStudioExtension;

// Central resolution of claude's config locations, honoring the CLI's
// CLAUDE_CONFIG_DIR env var (dliedke D1). Matches the CLI's own rules:
//   unset → transcripts ~/.claude/projects, config ~/.claude.json
//   set   → $CLAUDE_CONFIG_DIR/projects,    $CLAUDE_CONFIG_DIR/.claude.json
public static class ClaudePaths
{
    private static string? EnvDir
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(v) ? null : v!.Trim().TrimEnd('\\', '/');
        }
    }

    private static string Home =>
        Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ~/.claude (or $CLAUDE_CONFIG_DIR) — root for projects/, commands/, etc.
    public static string ConfigDir => EnvDir ?? Path.Combine(Home, ".claude");

    // Transcript store. Tolerates CLAUDE_CONFIG_DIR pointed directly at the
    // projects folder itself: if <config>/projects doesn't exist but the env
    // dir is named "projects" and exists, use it as-is.
    public static string ProjectsDir
    {
        get
        {
            var dir = Path.Combine(ConfigDir, "projects");
            if (!Directory.Exists(dir) && EnvDir != null
                && string.Equals(Path.GetFileName(EnvDir), "projects", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(EnvDir))
                return EnvDir;
            return dir;
        }
    }

    // OAuth/account state + user-scope MCP config. Default lives in the HOME
    // root (~/.claude.json, not inside ~/.claude); with CLAUDE_CONFIG_DIR set
    // it moves inside that dir — mirrors the CLI.
    public static string ClaudeJsonPath =>
        EnvDir != null ? Path.Combine(EnvDir, ".claude.json") : Path.Combine(Home, ".claude.json");

    // User-scope custom slash commands.
    public static string UserCommandsDir => Path.Combine(ConfigDir, "commands");

    // User-scope skills (one folder per skill, each with a SKILL.md).
    public static string UserSkillsDir => Path.Combine(ConfigDir, "skills");
}
