using System;
using System.IO;
using System.Text.Json;

namespace ClaudeStudioExtension.Usage;

public class CostLimits
{
    public decimal? SessionLimit { get; set; }
    public decimal? DailyLimit { get; set; }
    public bool Block { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeStudio", "cost_limits.json");

    public static CostLimits Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CostLimits();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<CostLimits>(json) ?? new CostLimits();
        }
        catch { return new CostLimits(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public bool IsEmpty => (SessionLimit is null or <= 0m) && (DailyLimit is null or <= 0m);

    public string? BuildWarning(decimal sessionCost, decimal dailyCost)
    {
        var msgs = new System.Collections.Generic.List<string>();
        if (SessionLimit is decimal sl && sl > 0m && sessionCost >= sl)
            msgs.Add($"session ${sessionCost:F2} / ${sl:F2}");
        if (DailyLimit is decimal dl && dl > 0m && dailyCost >= dl)
            msgs.Add($"today ${dailyCost:F2} / ${dl:F2}");
        return msgs.Count == 0 ? null : string.Join(" · ", msgs);
    }

    public bool ShouldBlock(decimal sessionCost, decimal dailyCost)
    {
        if (!Block) return false;
        if (SessionLimit is decimal sl && sl > 0m && sessionCost >= sl) return true;
        if (DailyLimit is decimal dl && dl > 0m && dailyCost >= dl) return true;
        return false;
    }
}
