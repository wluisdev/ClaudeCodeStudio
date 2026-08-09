using System.Text.Json;
using ClaudeStudioShared;

namespace ClaudeStudioAgent;

/// <summary>
/// The three rule buckets the permission hook consumes.
/// </summary>
/// <remarks>
/// Deliberately not a <c>ClaudeSettings</c>: nothing downstream reads the other
/// fields, and rebuilding the whole type field by field would silently drop anything
/// added to it later.
/// </remarks>
internal sealed record MergedPermissionRules(
    List<string>? Allow,
    List<string>? Ask,
    List<string>? Deny)
{
    public override string ToString() =>
        $"{string.Join(",", Allow ?? [])}|{string.Join(",", Ask ?? [])}|{string.Join(",", Deny ?? [])}";
}

/// <summary>
/// Combines the permission rules configured in the extension's settings panel with
/// the ones written into claude's own settings files.
/// </summary>
/// <remarks>
/// Ask and plan mode spawn the CLI under <c>bypassPermissions</c>, so it never
/// evaluates its own settings files and this hook is the only checkpoint — which used
/// to see the extension's rules and nothing else, so <c>permissions.allow</c> written
/// into <c>.claude/settings.json</c> had no effect anywhere
/// (github.com/wluisdev/ClaudeCodeStudio/issues/1). Everything lands in the same three
/// buckets; precedence is applied later, at evaluation, as deny &gt; ask &gt; allow.
/// </remarks>
internal static class PermissionRuleMerge
{
    /// <summary>Parsed settings files, keyed by path and invalidated by write time.</summary>
    private static readonly Dictionary<string, (DateTime Stamp, (List<string> Allow, List<string> Ask, List<string> Deny) Rules)>
        Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Signature of the last merged set, so the log line is not repeated per turn.</summary>
    private static string? _lastShape;

    public static MergedPermissionRules Merge(ClaudeSettings? settings, string? workingDirectory)
    {
        var allow = new List<string>(settings?.PermissionAllow ?? []);
        var ask = new List<string>(settings?.PermissionAsk ?? []);
        var deny = new List<string>(settings?.PermissionDeny ?? []);
        int fromFiles = 0;

        // mayAllow separates the two kinds of file. deny and ask can only ever make the
        // session more restrictive, so they are safe to take from anywhere. allow is the
        // one direction that grants, and a file that travels with a repository is not
        // the user speaking — cloning a project shipping allow:["PowerShell"] would
        // otherwise auto-approve every shell command on the first message, the same
        // bypass this release exists to close. The user-level file and
        // settings.local.json (gitignored by convention) are the user's own, so they
        // keep full authority.
        void Absorb(string path, bool mayAllow)
        {
            List<string> a, k, d;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Cache.Remove(path);
                    return;
                }

                // Keyed on the write stamp: this runs in front of every message the
                // user sends, and re-reading plus re-parsing three files each time is
                // blocking I/O on the send path — noticeable on a network share.
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == info.LastWriteTimeUtc)
                {
                    (a, k, d) = hit.Rules;
                }
                else
                {
                    var parsed = SettingsPermissions.Parse(File.ReadAllText(path));
                    Cache[path] = (info.LastWriteTimeUtc, parsed);
                    (a, k, d) = parsed;
                }
            }
            catch (Exception ex)
            {
                Warn($"could not read permissions from {path}: {ex.Message}");
                return;
            }

            if (!mayAllow && a.Count > 0)
            {
                // Said out loud, because silence here reads exactly like the bug this
                // feature was written to fix: the user sees their rules ignored again,
                // with no way to tell why or where to move them.
                Warn($"{a.Count} allow rule(s) in {path} ignored — a file that ships with a " +
                     "repository cannot grant permissions. Move them to " +
                     ".claude/settings.local.json or ⚙ → Permission rules.");
            }
            else
            {
                allow.AddRange(a);
                fromFiles += a.Count;
            }

            fromFiles += k.Count + d.Count;
            ask.AddRange(k);
            deny.AddRange(d);
        }

        Absorb(ClaudeConfig.UserSettingsPath, mayAllow: true);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var projectDir = Path.Combine(workingDirectory!, ".claude");
            Absorb(Path.Combine(projectDir, "settings.json"), mayAllow: false);
            Absorb(Path.Combine(projectDir, "settings.local.json"), mayAllow: true);
        }

        static List<string>? Clean(List<string> list) =>
            list.Count == 0 ? null : list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var merged = new MergedPermissionRules(Clean(allow), Clean(ask), Clean(deny));

        // Once per change, not once per message: a line repeated every turn for a value
        // that almost never moves pushes the interesting ones out of the Output window.
        if (fromFiles > 0)
        {
            var shape = $"{fromFiles}|{merged}";
            if (shape != _lastShape)
            {
                _lastShape = shape;
                Info($"permission rules: {fromFiles} loaded from settings files");
            }
        }
        else
        {
            _lastShape = null;
        }

        return merged;
    }

    private static void Info(string text) => Emit("info", text);

    private static void Warn(string text) => Emit("warn", text);

    private static void Emit(string type, string text)
    {
        Console.WriteLine(JsonSerializer.Serialize(new ChatChunk { Type = type, Text = text }));
        Console.Out.Flush();
    }
}
