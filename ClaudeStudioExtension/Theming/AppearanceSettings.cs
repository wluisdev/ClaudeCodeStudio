using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStudioExtension.Theming;

// Shared appearance preferences for the extension's native WPF tool windows
// (MCP, Usage, and any future window). Mirrors the chat webview's Appearance
// settings so every surface follows one choice. Persisted to disk (rather than
// living only in the webview's localStorage) so a native window can honor the
// choice even when it is opened before the chat webview has ever loaded.
//
// Same on-disk pattern as CostLimits: a small JSON file under %LocalAppData%,
// with a forward-compat bucket so an older binary does not drop fields a newer
// one added.
public class AppearanceSettings
{
    // "auto" (follow the live VS theme), "dark", or "light".
    public string ThemeMode { get; set; } = "auto";

    // Custom accent as #rrggbb, or null/empty for the brand default
    // (#d88763 in dark, #b05c35 in light).
    public string? Accent { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownProperties { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeStudio", "appearance.json");

    public static AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppearanceSettings();
            return JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(FilePath))
                   ?? new AppearanceSettings();
        }
        catch { return new AppearanceSettings(); }
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
