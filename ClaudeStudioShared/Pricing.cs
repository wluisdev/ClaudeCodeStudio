using System;
using System.Collections.Generic;

namespace ClaudeStudioShared;

// Relocated out of ClaudeStudioExtension.Usage.UsageReader.cs so it's testable
// without the VS SDK (mirrors SessionOrdinals).
public static class Pricing
{
    // USD per 1M tokens. Cache write = input * 1.25 (5m TTL) or input * 2 (1h
    // TTL) on every current model. cacheRead is stored explicitly per model: it
    // used to be a flat input * 0.10, but newer models dropped below that ratio
    // (Opus 5.5 reads at $0.20 with a $4 input = 5%; Fable 5.1 at $0.25 with a
    // $10 input = 2.5%), so it can't be derived from input anymore.
    private static readonly Dictionary<string, (decimal input, decimal output, decimal cacheRead)> _base = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5"]   = (3m, 15m, 0.30m), // sticker price; intro $2/$10 runs through 2026-08-31
        ["claude-sonnet-4-6"] = (3m, 15m, 0.30m),
        ["claude-opus-5-5"]   = (4m, 20m, 0.20m), // cheaper than Opus 5; cache read 5% of input, not 10%
        ["claude-opus-5"]     = (5m, 25m, 0.50m), // same rate as Opus 4.8
        ["claude-opus-4-8"]   = (5m, 25m, 0.50m),
        ["claude-fable-5-1"]  = (10m, 50m, 0.25m), // cache read 2.5% of input
        ["claude-fable-5"]    = (10m, 50m, 1.00m),
        ["claude-mythos-5"]   = (10m, 50m, 1.00m),
        ["claude-haiku-4-5"]  = (1m, 5m, 0.10m),
    };

    // Longest-prefix match, not first-match: "claude-opus-5-5" starts with
    // "claude-opus-5" too, so a first-match scan could bill Opus 5.5 at Opus 5's
    // rate depending on dictionary order. Picking the longest matching key makes
    // the more specific model win regardless of iteration order, and dated
    // suffixes (claude-opus-5-20260401) still fall back to their base entry.
    private static (decimal input, decimal output, decimal cacheRead) Resolve(string model)
    {
        (decimal input, decimal output, decimal cacheRead)? best = null;
        var bestLen = -1;
        foreach (var kv in _base)
            if (model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) && kv.Key.Length > bestLen)
            {
                best = kv.Value;
                bestLen = kv.Key.Length;
            }
        if (best is { } hit) return hit;
        // Unknown variant: assume the old input * 0.10 cache-read ratio.
        if (model.Contains("fable", StringComparison.OrdinalIgnoreCase)
            || model.Contains("mythos", StringComparison.OrdinalIgnoreCase)) return (10m, 50m, 1.00m);
        if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))   return (5m, 25m, 0.50m);
        if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))  return (1m, 5m, 0.10m);
        return (3m, 15m, 0.30m); // sonnet default
    }

    public static decimal Calculate(string model, long input, long output, long cacheRead, long cache5m, long cache1h)
    {
        var (i, o, cr) = Resolve(model);
        return (input * i + output * o + cacheRead * cr + cache5m * (i * 1.25m) + cache1h * (i * 2.0m)) / 1_000_000m;
    }
}
