using System;
using System.IO;

namespace ClaudeStudioShared;

/// <summary>
/// Where the CLI keeps its configuration, honoring <c>CLAUDE_CONFIG_DIR</c>.
/// </summary>
/// <remarks>
/// Matches the CLI's own rule: unset means <c>~/.claude</c>, set means the given
/// directory verbatim.
/// <para>
/// This lives in Shared because the resolution had drifted into three copies — the
/// extension's <c>ClaudePaths</c>, the agent's settings-file merge, and
/// <c>PermissionPipeServer.PlansDir</c> — and they had already stopped agreeing:
/// only some of them honoured a redirected <c>USERPROFILE</c>, and only some trimmed
/// a trailing separator, so <c>CLAUDE_CONFIG_DIR=D:\cfg\</c> resolved to two
/// different places depending on which one you asked.
/// </para>
/// </remarks>
public static class ClaudeConfig
{
    /// <summary><c>~/.claude</c>, or whatever <c>CLAUDE_CONFIG_DIR</c> points at.</summary>
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env!.Trim().TrimEnd('\\', '/');

            // USERPROFILE first: it is what the CLI itself reads, and it is the one a
            // test harness or a service account can redirect.
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(home, ".claude");
        }
    }

    /// <summary>Where plan mode writes its plan documents.</summary>
    public static string PlansDir => Path.Combine(Dir, "plans");

    /// <summary>The user-level settings file.</summary>
    public static string UserSettingsPath => Path.Combine(Dir, "settings.json");
}
