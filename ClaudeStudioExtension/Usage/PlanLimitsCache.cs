using System;
using System.IO;
using System.Text.Json;

namespace ClaudeStudioExtension.Usage;

// Last-seen subscription plan limits (issue #15), cached to disk so the Usage
// window can show them instantly on open while `claude -p /usage` (~3s) refreshes
// in the background. Same on-disk pattern as CostLimits.
public class PlanLimitsCache
{
    public int? SessionPct { get; set; }
    public string? SessionReset { get; set; }
    public int? WeekPct { get; set; }
    public string? WeekReset { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeStudio", "plan_limits.json");

    public static PlanLimitsCache Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new PlanLimitsCache();
            return JsonSerializer.Deserialize<PlanLimitsCache>(File.ReadAllText(FilePath)) ?? new PlanLimitsCache();
        }
        catch { return new PlanLimitsCache(); }
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
}
