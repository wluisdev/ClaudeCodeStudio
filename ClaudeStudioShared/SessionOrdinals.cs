using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeStudioShared;

// Pure JSONL-ordinal logic relocated out of AgentToolWindowControl.xaml.cs
// (branch/rewind used to each re-derive this inline, and the two copies once
// drifted from what the DOM actually renders — rodadas 11/12). Having exactly
// one implementation, testable without the VS SDK, is the point.
public static class SessionOrdinals
{
    // Mirrors app.js's msgCounter/dataset.msgIndex: mixed user+assistant,
    // 0-based, counts only non-meta lines that produce visible text. Returns
    // the 0-based index into `lines` of the line whose visible-bubble ordinal
    // equals msgIndex (inclusive — that line IS the target), or null if the
    // transcript has fewer than msgIndex+1 visible bubbles.
    public static int? FindBranchBoundaryLineIndex(IReadOnlyList<string> lines, int msgIndex)
    {
        int visibleCount = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? text;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var tEl)) continue;
                var entryType = tEl.GetString();
                if (entryType != "user" && entryType != "assistant") continue;
                if (IsMetaLine(root)) continue;
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;
                text = ExtractText(content);
            }
            catch { continue; }

            if (string.IsNullOrEmpty(text)) continue;

            if (visibleCount == msgIndex) return i;
            visibleCount++;
        }
        return null;
    }

    // Mirrors app.js's userMsgCounter/dataset.userIndex: user-only, 0-based.
    // Walks the WHOLE list (no early stop — matches today's rewind behavior,
    // unlike the branch boundary's early return above). Returns the uuid of
    // the msgIndex-th qualifying user line and the total count found (the
    // count is returned even on a miss so callers can log it, matching the
    // existing "(found {N} user messages)" diagnostic).
    public static (string? uuid, int totalCount) FindRewindUserUuid(IReadOnlyList<string> lines, int msgIndex)
    {
        var userUuids = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var tEl) || tEl.GetString() != "user") continue;
                if (IsMetaLine(root)) continue;
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;

                var text = ExtractText(content);
                if (string.IsNullOrEmpty(text)) continue;

                if (root.TryGetProperty("uuid", out var uEl) && uEl.GetString() is { } u)
                    userUuids.Add(u);
            }
            catch { continue; }
        }
        if (msgIndex < 0 || msgIndex >= userUuids.Count) return (null, userUuids.Count);
        return (userUuids[msgIndex], userUuids.Count);
    }

    // CLI-injected lines (skill expansions, local-command caveats) are flagged
    // isMeta:true. Anything counting visible bubbles or user ordinals must
    // skip them, or the ⎇/⟲ ordinal desyncs from what the DOM actually shows.
    public static bool IsMetaLine(JsonElement root) =>
        root.TryGetProperty("isMeta", out var metaEl) && metaEl.ValueKind == JsonValueKind.True;

    // Plain string content, or (array form) the concatenation of every
    // {type:"text"} block's .text — multiple text blocks in one content array
    // count as ONE ordinal, not one per block. Returns null (not "") when no
    // text block/string is present at all, e.g. tool_use/tool_result-only
    // content — callers treat null/empty as "no visible bubble here".
    public static string? ExtractText(JsonElement content)
    {
        string? text = null;
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("type", out var itEl) && itEl.GetString() == "text" &&
                    item.TryGetProperty("text", out var txEl))
                    text = (text ?? "") + (txEl.GetString() ?? "");
        }
        else if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
        }
        return text;
    }
}
