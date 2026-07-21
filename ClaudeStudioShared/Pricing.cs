using System;
using System.Collections.Generic;

namespace ClaudeStudioShared;

// Relocated out of ClaudeStudioExtension.Usage.UsageReader.cs so it's testable
// without the VS SDK (mirrors SessionOrdinals).
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
