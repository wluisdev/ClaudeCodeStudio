using System;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeStudioExtension.Theming;

// The resolved Claude palette for a native WPF tool window: dark/light decision
// plus the eight brush colors the windows are built from.
public readonly struct ThemePalette
{
    public bool IsDark { get; }
    public Color Background { get; }
    public Color Foreground { get; }
    public Color Muted { get; }
    public Color Border { get; }
    public Color Accent { get; }
    public Color Surface { get; }
    public Color Hover { get; }
    public Color Danger { get; }

    public ThemePalette(bool isDark, Color bg, Color fg, Color muted, Color border,
                        Color accent, Color surface, Color hover, Color danger)
    {
        IsDark = isDark; Background = bg; Foreground = fg; Muted = muted; Border = border;
        Accent = accent; Surface = surface; Hover = hover; Danger = danger;
    }
}

// Central theming for the extension's native WPF tool windows. Resolves dark vs
// light from the shared AppearanceSettings override (auto/dark/light) or, when
// "auto", from the live VS theme; computes the Claude palette honoring a custom
// accent; and applies it to a window by swapping the brush entries in its
// ResourceDictionary.
//
// Why swap instead of mutating brush.Color: a ResourceDictionary freezes a
// shared Freezable the moment it is realized through a resource reference, so a
// brush referenced from XAML is read-only and mutating it throws. Windows must
// therefore reference the brush keys via DynamicResource, which re-resolves to
// the new brush when the entry is replaced.
//
// Reuse from a new native window:
//   1. Define the brush keys below in XAML via DynamicResource.
//   2. Subscribe to NativeTheme.Changed (and unsubscribe on Unloaded).
//   3. Call NativeTheme.Apply(this) from that handler and on Loaded.
public static class NativeTheme
{
    // The resource keys Apply swaps. A window need not define all of them; extra
    // entries are harmless (nothing references them).
    private const string BgKey = "bgBrush", FgKey = "fgBrush", MutedKey = "mutedBrush",
        BorderKey = "borderBrush", AccentKey = "accentBrush", SurfaceKey = "surfaceBrush",
        HoverKey = "hoverBrush", DangerKey = "dangerBrush";

    // Raised when the resolved theme may have changed: the VS theme switched, or
    // the user changed the appearance override/accent in the chat settings.
    // Handlers run on whatever thread raises it, so marshal to the UI thread.
    public static event Action? Changed;

    static NativeTheme()
    {
        // One process-wide subscription; re-raised as Changed for every window.
        VSColorTheme.ThemeChanged += _ => Changed?.Invoke();
    }

    // Called by the chat message handler after persisting a new appearance choice.
    public static void NotifyChanged() => Changed?.Invoke();

    // Resolves the palette from the persisted override (or the live VS theme).
    public static ThemePalette Resolve()
    {
        var settings = AppearanceSettings.Load();
        bool isDark = ResolveIsDark(settings.ThemeMode);
        Color accent = ParseHex(settings.Accent) ?? Hex(isDark ? "#d88763" : "#b05c35");

        return isDark
            ? new ThemePalette(true,  Hex("#1f1f1f"), Hex("#f4f4f4"), Hex("#8a8a8a"),
                               Hex("#333333"), accent, Hex("#2a2a2a"), Hex("#3a3a3a"), Hex("#c75450"))
            : new ThemePalette(false, Hex("#f5f5f5"), Hex("#1e1e1e"), Hex("#666666"),
                               Hex("#d0d0d0"), accent, Hex("#ececec"), Hex("#dcdcdc"), Hex("#c0392b"));
    }

    // Applies the resolved palette to the control by replacing the brush entries
    // in its ResourceDictionary (DynamicResource references re-resolve) and setting
    // Background/Foreground. Returns the palette so callers can also drive any
    // code-built brushes that never enter a ResourceDictionary.
    public static ThemePalette Apply(Control el)
    {
        var p = Resolve();
        var bg = new SolidColorBrush(p.Background);
        var fg = new SolidColorBrush(p.Foreground);
        el.Resources[BgKey] = bg;
        el.Resources[FgKey] = fg;
        el.Resources[MutedKey] = new SolidColorBrush(p.Muted);
        el.Resources[BorderKey] = new SolidColorBrush(p.Border);
        el.Resources[AccentKey] = new SolidColorBrush(p.Accent);
        el.Resources[SurfaceKey] = new SolidColorBrush(p.Surface);
        el.Resources[HoverKey] = new SolidColorBrush(p.Hover);
        el.Resources[DangerKey] = new SolidColorBrush(p.Danger);
        el.Background = bg;
        el.Foreground = fg;
        return p;
    }

    private static bool ResolveIsDark(string? mode)
    {
        if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) return false;
        return VsIsDark();
    }

    private static bool VsIsDark()
    {
        var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        return bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 < 128;
    }

    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    private static Color? ParseHex(string? h)
    {
        if (string.IsNullOrWhiteSpace(h)) return null;
        try { return (Color)ColorConverter.ConvertFromString(h!); } catch { return null; }
    }
}
