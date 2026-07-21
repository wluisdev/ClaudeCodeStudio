using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeStudioShared;

public class SessionUsage
{
    public string SessionId { get; set; } = "";
    public string Cwd { get; set; } = "";
    public DateTime FirstTimestamp { get; set; }
    public DateTime LastTimestamp { get; set; }
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    // Split by TTL for pricing (5m writes cost 1.25x input, 1h writes cost
    // 2x). Falls back to Cache5mTokens when a turn's usage predates the
    // nested cache_creation breakdown (older CLI versions).
    public long Cache5mTokens { get; set; }
    public long Cache1hTokens { get; set; }
    public int TurnCount { get; set; }
    public decimal Cost { get; set; }
    // Naming inputs for the session filter combo — resolved against
    // SessionTitlesStore by the UI (same precedence History uses).
    public string NativeCustomTitle { get; set; } = "";
    public string NativeAiTitle { get; set; } = "";
    public string Preview { get; set; } = "";
}

// Per-transcript-file usage accumulation, relocated out of
// ClaudeStudioExtension.Usage.UsageReader.ParseFile so it's testable without
// touching disk (inline JSONL fixtures instead of temp files) — mirrors
// SessionOrdinals. The caller still owns opening the file; this only owns
// interpreting its lines.
public static class UsageParser
{
    public static SessionUsage? ParseLines(IEnumerable<string> lines)
    {
        string? sessionId = null;
        string? cwd = null;
        string model = "claude-sonnet-5";
        DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
        long inp = 0, outp = 0, cr = 0, cc = 0, cc5m = 0, cc1h = 0;
        int turns = 0;
        string nativeCustom = "", nativeAi = "", preview = "";

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement el;
            try { el = JsonDocument.Parse(line).RootElement; }
            catch { continue; }

            if (sessionId == null && el.TryGetProperty("sessionId", out var sid))
                sessionId = sid.GetString();
            if (cwd == null && el.TryGetProperty("cwd", out var cwdEl))
                cwd = cwdEl.GetString();
            if (el.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(tsEl.GetString(), out var ts))
            {
                if (ts < first) first = ts;
                if (ts > last) last = ts;
            }

            if (!el.TryGetProperty("type", out var typeEl)) continue;
            var entryType = typeEl.GetString();

            // Title lines the CLI appends on rename / auto-naming (U2).
            if (entryType == "custom-title")
            {
                if (el.TryGetProperty("customTitle", out var ctEl)) nativeCustom = ctEl.GetString() ?? "";
                continue;
            }
            if (entryType == "ai-title")
            {
                if (el.TryGetProperty("aiTitle", out var atEl)) nativeAi = atEl.GetString() ?? "";
                continue;
            }

            // First user text = preview fallback for the session filter combo.
            if (preview.Length == 0 && entryType == "user" &&
                el.TryGetProperty("message", out var userMsg) &&
                userMsg.TryGetProperty("content", out var content))
            {
                string? text = null;
                if (content.ValueKind == JsonValueKind.String)
                    text = content.GetString();
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                        if (item.TryGetProperty("type", out var itEl) && itEl.GetString() == "text" &&
                            item.TryGetProperty("text", out var txEl))
                        { text = txEl.GetString(); break; }
                }
                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text!.Trim().Replace('\n', ' ');
                    preview = text.Length > 60 ? text.Substring(0, 60) : text;
                }
            }

            if (entryType != "assistant") continue;
            if (!el.TryGetProperty("message", out var msg)) continue;

            // Claude Code emits "<synthetic>" for internal assistant turns
            // (quota warnings, injected error messages, etc.) that did not hit
            // the API. Skip them entirely so tokens/cost reflect real usage.
            if (msg.TryGetProperty("model", out var modelEl))
            {
                var m = modelEl.GetString();
                if (!string.IsNullOrEmpty(m))
                {
                    if (m!.StartsWith("<")) continue;
                    model = m;
                }
            }

            if (!msg.TryGetProperty("usage", out var usage)) continue;

            turns++;
            inp  += usage.TryGetProperty("input_tokens",                 out var x1) ? x1.GetInt64() : 0;
            outp += usage.TryGetProperty("output_tokens",                out var x2) ? x2.GetInt64() : 0;
            cr   += usage.TryGetProperty("cache_read_input_tokens",      out var x3) ? x3.GetInt64() : 0;
            var ccTurn = usage.TryGetProperty("cache_creation_input_tokens", out var x4) ? x4.GetInt64() : 0;
            cc += ccTurn;

            // Split by TTL for pricing (#4 — 1h writes cost 2x, not 1.25x).
            // Older entries lack the nested breakdown; treat those as 5m,
            // matching the flat rate this code charged before the split.
            if (usage.TryGetProperty("cache_creation", out var ccObj) && ccObj.ValueKind == JsonValueKind.Object)
            {
                cc5m += ccObj.TryGetProperty("ephemeral_5m_input_tokens", out var e5) ? e5.GetInt64() : 0;
                cc1h += ccObj.TryGetProperty("ephemeral_1h_input_tokens", out var e1h) ? e1h.GetInt64() : 0;
            }
            else
            {
                cc5m += ccTurn;
            }
        }

        if (sessionId == null || turns == 0) return null;

        return new SessionUsage
        {
            SessionId = sessionId,
            Cwd = cwd ?? "",
            FirstTimestamp = first,
            LastTimestamp = last,
            Model = model,
            InputTokens = inp,
            OutputTokens = outp,
            CacheReadTokens = cr,
            CacheCreationTokens = cc,
            Cache5mTokens = cc5m,
            Cache1hTokens = cc1h,
            TurnCount = turns,
            Cost = Pricing.Calculate(model, inp, outp, cr, cc5m, cc1h),
            NativeCustomTitle = nativeCustom,
            NativeAiTitle = nativeAi,
            Preview = preview,
        };
    }
}
