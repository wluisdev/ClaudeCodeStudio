using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeStudioShared;

/// <summary>
/// Recovers a claude launch when the CLI rejects an optional command-line flag it
/// doesn't recognise.
/// </summary>
/// <remarks>
/// The extension passes flags that some CLI builds don't have (version drift): a
/// build without <c>--forward-subagent-text</c> exits at launch with
/// <c>error: unknown option '--forward-subagent-text'</c>, which the user saw as
/// "session not started" with the whole extension blocked
/// (github.com/wluisdev/ClaudeCodeStudio/issues/7). The agent retries the spawn with
/// the offending flag removed. This is the pure decision logic, kept here so it can be
/// unit-tested without launching a process.
/// </remarks>
public static class LaunchFlagRecovery
{
    /// <summary>
    /// Value-less, purely additive flags a session can run without. Dropping one only
    /// makes a feature go quiet (subagent-text forwarding, token-level streaming, the
    /// cache trim, the delivered-ack). Deliberately excludes value-taking flags
    /// (dropping the name would strand its value as a positional argument), the
    /// stream-json format flags and <c>--verbose</c> (the protocol needs them), and
    /// <c>--include-hook-events</c> (dropping it would silently disable the permission
    /// gate). Any of those, if rejected, must surface as an error rather than be
    /// dropped.
    /// </summary>
    public static readonly IReadOnlyList<string> DroppableOptionalFlags = new[]
    {
        "--forward-subagent-text",
        "--replay-user-messages",
        "--exclude-dynamic-system-prompt-sections",
        "--include-partial-messages",
    };

    // Loose on wording (unknown|unrecognized, quotes optional) so a reworded CLI
    // message doesn't silently defeat detection.
    private static readonly Regex RejectedFlagPattern = new(
        @"(?:unknown|unrecognized)\s+option\s+'?(--[A-Za-z0-9][A-Za-z0-9-]*)'?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnknownOptionPattern = new(
        @"(?:unknown|unrecognized)\s+option",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The specific flag named in an "unknown option" message, or null if none is
    /// present or parseable.
    /// </summary>
    public static string? ParseRejectedFlag(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        var m = RejectedFlagPattern.Match(stderr);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when the text reads like a launch-time unknown-option rejection.</summary>
    public static bool LooksLikeUnknownOption(string? stderr) =>
        !string.IsNullOrEmpty(stderr) && UnknownOptionPattern.IsMatch(stderr);

    /// <summary>
    /// Removes from <paramref name="args"/> the optional flag(s) responsible for a
    /// launch rejection, when safe. Returns true (with a description) when it changed
    /// the list.
    /// </summary>
    /// <remarks>
    /// If the culprit is parsed and IS droppable, only that flag is removed. If it's
    /// parsed but NOT droppable (a value or load-bearing flag), nothing is removed and
    /// false is returned, so the caller surfaces the real error rather than retrying
    /// pointlessly. If the message is clearly an unknown-option error but no flag can
    /// be parsed, every optional flag still present is shed at once as a last resort.
    /// </remarks>
    public static bool TryDropRejectedFlags(List<string> args, string? stderr, out string description)
    {
        description = "";
        if (args == null) return false;

        var flag = ParseRejectedFlag(stderr);
        if (flag != null)
        {
            if (DroppableOptionalFlags.Contains(flag) && args.Remove(flag))
            {
                description = flag;
                return true;
            }
            // Named a flag we won't (or can't safely) drop: don't shotgun.
            return false;
        }

        if (LooksLikeUnknownOption(stderr))
        {
            int removed = args.RemoveAll(a => DroppableOptionalFlags.Contains(a));
            if (removed > 0)
            {
                description = $"{removed} optional flag(s)";
                return true;
            }
        }

        return false;
    }
}
