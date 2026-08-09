using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeStudioShared;

/// <summary>
/// Reads the <c>permissions</c> block out of a Claude Code settings file, and
/// decides whether a rule's tool name covers a given tool.
/// </summary>
/// <remarks>
/// Needed because ask/plan mode spawns the CLI under <c>bypassPermissions</c>, which
/// turns off its native permission evaluation — the extension's hook becomes the
/// only checkpoint, and it only ever knew about rules typed into the extension's own
/// settings panel. Rules the user had written into <c>.claude/settings.json</c> were
/// read by nobody (github.com/wluisdev/ClaudeCodeStudio/issues/1).
/// </remarks>
public static class SettingsPermissions
{
    /// <summary>
    /// Pulls <c>permissions.allow / ask / deny</c> out of a settings.json body.
    /// Never throws: a missing block, a wrong shape, or malformed JSON yields empty
    /// lists, because a settings file the user is mid-edit must not break a turn.
    /// </summary>
    public static (List<string> Allow, List<string> Ask, List<string> Deny) Parse(string? json)
    {
        var allow = new List<string>();
        var ask = new List<string>();
        var deny = new List<string>();

        if (string.IsNullOrWhiteSpace(json)) return (allow, ask, deny);

        try
        {
            using var doc = JsonDocument.Parse(json!, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (allow, ask, deny);
            if (!doc.RootElement.TryGetProperty("permissions", out var perms)) return (allow, ask, deny);
            if (perms.ValueKind != JsonValueKind.Object) return (allow, ask, deny);

            Fill(perms, "allow", allow);
            Fill(perms, "ask", ask);
            Fill(perms, "deny", deny);
        }
        catch (JsonException)
        {
            // Half-written file — treat as "no rules yet" rather than failing the turn.
        }

        return (allow, ask, deny);
    }

    private static void Fill(JsonElement perms, string name, List<string> into)
    {
        if (!perms.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) into.Add(s!.Trim());
        }
    }

    /// <summary>
    /// True when a rule written for <paramref name="ruleTool"/> should decide a call
    /// to <paramref name="toolName"/>.
    /// </summary>
    /// <remarks>
    /// Exact match, plus one special case: an MCP rule naming only the server
    /// (<c>mcp__github</c>) covers every tool that server exposes
    /// (<c>mcp__github__search_code</c>). Claude's own docs use the server-wide form,
    /// so a user copying a rule from there would otherwise write something that
    /// silently matches nothing. The <c>__</c> boundary is required, so
    /// <c>mcp__git</c> does not swallow <c>mcp__github__*</c>.
    /// </remarks>
    public static bool ToolMatches(string? ruleTool, string? toolName)
    {
        if (string.IsNullOrEmpty(ruleTool) || string.IsNullOrEmpty(toolName)) return false;

        if (string.Equals(ruleTool, toolName, StringComparison.OrdinalIgnoreCase)) return true;

        if (ruleTool!.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) &&
            toolName!.StartsWith(ruleTool + "__", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
