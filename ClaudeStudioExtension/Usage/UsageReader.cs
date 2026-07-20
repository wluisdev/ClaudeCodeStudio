using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClaudeStudioExtension.Usage;

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

public static class Pricing
{
    // USD per 1M tokens. Cache write = input * 1.25 (5m TTL) or input * 2 (1h
    // TTL); cache read = input * 0.10.
    private static readonly Dictionary<string, (decimal input, decimal output)> _base = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5"]   = (3m, 15m), // sticker price; intro $2/$10 runs through 2026-08-31
        ["claude-sonnet-4-6"] = (3m, 15m),
        ["claude-opus-4-8"]   = (5m, 25m),
        ["claude-fable-5"]    = (10m, 50m),
        ["claude-mythos-5"]   = (10m, 50m),
        ["claude-haiku-4-5"]  = (1m, 5m),
    };

    private static (decimal input, decimal output) Resolve(string model)
    {
        foreach (var kv in _base)
            if (model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        if (model.Contains("fable", StringComparison.OrdinalIgnoreCase)
            || model.Contains("mythos", StringComparison.OrdinalIgnoreCase)) return (10m, 50m);
        if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))   return (5m, 25m);
        if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))  return (1m, 5m);
        return (3m, 15m); // sonnet default
    }

    public static decimal Calculate(string model, long input, long output, long cacheRead, long cache5m, long cache1h)
    {
        var (i, o) = Resolve(model);
        return (input * i + output * o + cacheRead * (i * 0.10m) + cache5m * (i * 1.25m) + cache1h * (i * 2.0m)) / 1_000_000m;
    }
}

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

    private static SessionUsage? ParseFile(string path)
    {
        string? sessionId = null;
        string? cwd = null;
        string model = "claude-sonnet-5";
        DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
        long inp = 0, outp = 0, cr = 0, cc = 0, cc5m = 0, cc1h = 0;
        int turns = 0;
        string nativeCustom = "", nativeAi = "", preview = "";

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
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
                    if (m.StartsWith("<")) continue;
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
