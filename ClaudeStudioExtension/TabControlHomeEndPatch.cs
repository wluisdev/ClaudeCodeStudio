using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmonyLib;

namespace ClaudeStudioExtension;

/// <summary>
/// Stops the docking tab well from stealing Home and End while the chat panel has
/// focus, without breaking the browser's own caret movement.
/// </summary>
/// <remarks>
/// Root cause of github.com/wluisdev/ClaudeCodeStudio/issues/2: WPF's
/// <see cref="TabControl"/> handles Home/End inside its own <c>OnKeyDown</c> (moving
/// to the first/last tab), and Visual Studio's docking wells derive from it. A
/// floating panel has no TabControl above it, which is why floating always worked.
/// <para>
/// Two earlier approaches were measured and abandoned:
/// </para>
/// <list type="bullet">
/// <item><description><c>IVsWindowPane.PreProcessMessage</c> on the tool window
/// pane: instrumented, never once called for this pane.</description></item>
/// <item><description>A WPF class handler on <c>TabControl</c>
/// (<c>EventManager.RegisterClassHandler</c>) that stopped the tab switch by
/// marking the routed key event handled. This worked for the tab switch, but marking
/// <c>Handled</c> also tells WPF the input was consumed, so the hosted WebView2 never
/// received the key at all &#8212; the caret stopped responding instead, in every
/// phase tried, and it broke the floating-panel case that used to work.</description></item>
/// </list>
/// <para>
/// A Harmony prefix on <c>TabControl.OnKeyDown</c> sidesteps that trade-off: it skips
/// the method's own tab-switching body for Home/End while the chat panel has focus,
/// but never touches <c>Handled</c>. Nothing is marked consumed, so WPF's normal
/// routing carries on afterward and the key still reaches the browser. This is the
/// approach Rick Strahl documents for WebView2 inside a WPF TabControl
/// (weblog.west-wind.com, "WebView2 Home and End Key Problems inside of WPF
/// TabControl Containers", 2021) as the one that actually works for a docking host,
/// where subclassing the TabControl itself is not an option because the control
/// belongs to the shell.
/// </para>
/// <para>
/// The trade-off being accepted: this patches a shared WPF type inside
/// <c>devenv.exe</c>, at the CLR level, for the lifetime of the process. It is scoped
/// as tightly as the mechanism allows &#8212; one method, two keys, no modifiers, and
/// only while a chat panel has focus &#8212; but a Harmony patch is still something
/// another extension's own patch to the same method could in principle collide with.
/// </para>
/// </remarks>
internal static class TabControlHomeEndPatch
{
    private const string HarmonyId = "wluisdev.claudestudio.tabcontrol-homeend";

    private static Harmony? _harmony;

    public static void Install()
    {
        if (_harmony != null) return;

        try
        {
            var method = AccessTools.Method(typeof(TabControl), "OnKeyDown", new[] { typeof(KeyEventArgs) });
            if (method == null)
            {
                OutputLog.Warn("tab control patch: TabControl.OnKeyDown not found (WPF version drift?) — Home/End fix not installed");
                return;
            }

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TabControlHomeEndPatch), nameof(Prefix)));
            _harmony = harmony;

            OutputLog.Info("tab control Home/End patch installed");
        }
        catch (Exception ex)
        {
            // Never let a failed patch take the extension down with it — Home/End
            // stays broken while docked, same as before this fix existed.
            OutputLog.Warn($"tab control patch failed to install: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        try
        {
            _harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"tab control patch failed to uninstall: {ex.Message}");
        }
        finally
        {
            _harmony = null;
        }
    }

    /// <summary>
    /// Harmony prefix for <c>TabControl.OnKeyDown</c>. Returning <c>false</c> skips the
    /// original method's body entirely for this call; returning <c>true</c> runs it
    /// normally.
    /// </summary>
    private static bool Prefix(KeyEventArgs e)
    {
        if (e.Key != Key.Home && e.Key != Key.End) return true;

        // Ctrl and Alt combinations stay with Visual Studio.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return true;

        // Skipping the original body is the whole fix; Handled is never touched, so a
        // tool unrelated to the chat panel having a TabControl of its own is completely
        // unaffected by this check being false there.
        if (!FocusIsInChatPanel(e)) return true;

        return false;
    }

    /// <summary>
    /// True when the key belongs to a chat panel: either the routed event's original
    /// source sits inside one, or the raw Win32 focus does. Two signals because either
    /// can go quiet on its own &#8212; <c>OriginalSource</c> reflects WPF's routing, but
    /// WPF focus does not reach inside a WebView2 (the hosted HWND owns real Win32
    /// focus), so the native walk is what actually answers the question in practice.
    /// </summary>
    private static bool FocusIsInChatPanel(KeyEventArgs e)
    {
        return TreeContainsChat(e.OriginalSource as System.Windows.DependencyObject) || NativeFocusIsInChat();
    }

    private static bool TreeContainsChat(System.Windows.DependencyObject? node)
    {
        // Bounded: this runs for every Home/End pressed anywhere in the IDE, on the UI
        // thread, so a cyclic logical parent chain must never hang devenv on a keystroke.
        for (int depth = 0; node != null && depth < 64; depth++)
        {
            if (node is AgentToolWindowControl) return true;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? System.Windows.LogicalTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static bool NativeFocusIsInChat()
    {
        if (AgentToolWindowControl.Live.Count == 0) return false;

        IntPtr focused = GetFocus();
        if (focused == IntPtr.Zero) return false;

        for (int depth = 0; focused != IntPtr.Zero && depth < 32; depth++)
        {
            foreach (var panel in AgentToolWindowControl.Live)
            {
                var handle = panel.BrowserHandle;
                if (handle != IntPtr.Zero && handle == focused) return true;
            }

            focused = GetParent(focused);
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);
}
