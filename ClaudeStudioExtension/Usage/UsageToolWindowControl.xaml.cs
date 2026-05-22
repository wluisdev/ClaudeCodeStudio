using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeStudioExtension.Usage;

public partial class UsageToolWindowControl : UserControl
{
    private List<SessionUsage> _all = new();
    private string? _currentCwd;
    private bool _loadedOnce;
    // Tracks whether the AllProjects checkbox is checked because we forced it on
    // (no workspace detected) vs the user clicking it. Distinguishes the two so
    // we can uncheck after transitioning to a real workspace without overriding
    // a manual choice.
    private bool _autoCheckedDueToNoCwd;
    private bool _suppressManualClearOnNextToggle;

    public UsageToolWindowControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            Refresh();
        };

        // Auto-refresh whenever the tool window becomes visible again (after a
        // tab switch, dock-undock, or returning to it from another panel).
        // First transition is already covered by Loaded above.
        IsVisibleChanged += (_, e) =>
        {
            if (_loadedOnce && e.NewValue is bool visible && visible)
                Refresh();
        };
    }

    /// <summary>
    /// Refreshes the Usage tool window if it's currently open. Safe to call from
    /// anywhere — silently no-ops when the window isn't instantiated. Must run on
    /// the UI thread.
    /// </summary>
    public static void RefreshIfOpen()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var pkg = ClaudeStudioExtensionPackage.Instance;
            // pass create:false so we don't materialize the window just to refresh it
            var window = pkg?.FindToolWindow(typeof(UsageToolWindow), 0, false);
            if (window?.Content is UsageToolWindowControl ctrl)
                ctrl.Refresh();
        }
        catch { /* best-effort */ }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();
    private void OnRangeChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // Programmatic IsChecked changes also fire Checked/Unchecked. The guard
        // distinguishes a user click from our internal toggle so we don't
        // accidentally clear the auto-flag when WE set IsChecked.
        if (!_suppressManualClearOnNextToggle)
            _autoCheckedDueToNoCwd = false;
        _suppressManualClearOnNextToggle = false;
        ApplyFilters();
    }

    public void Refresh()
    {
        try
        {
            CaptureCurrentCwd();
            _all = UsageReader.ReadAll();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            TotalCostText.Text = "error";
            SessionCountText.Text = ex.Message;
        }
    }

    private void CaptureCurrentCwd()
    {
#pragma warning disable VSTHRD010
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            // Use workspace-only resolver — null means "no real workspace"
            // (no .sln, no Open Folder). The filter falls through to "all" in
            // that case instead of picking up ActiveDocument/UserProfile.
            _currentCwd = AgentToolWindowControl.ResolveWorkspaceCwd(dte);
        }
        catch { _currentCwd = null; }
#pragma warning restore VSTHRD010

        // Toggle checkbox availability based on whether we have a workspace.
        // When no project is detectable, force "all projects" mode and disable
        // the toggle so the user isn't misled by an inert filter.
        if (AllProjects != null)
        {
            if (string.IsNullOrEmpty(_currentCwd))
            {
                if (AllProjects.IsChecked != true)
                {
                    _suppressManualClearOnNextToggle = true;
                    AllProjects.IsChecked = true;
                }
                AllProjects.IsEnabled = false;
                _autoCheckedDueToNoCwd = true;
            }
            else
            {
                // Transitioning from no-cwd → has-cwd: if WE forced the check on,
                // restore the default (uncheck) so Usage filters to the new
                // workspace. A manual user check survives (flag stays false).
                if (_autoCheckedDueToNoCwd && AllProjects.IsChecked == true)
                {
                    _suppressManualClearOnNextToggle = true;
                    AllProjects.IsChecked = false;
                }
                _autoCheckedDueToNoCwd = false;
                AllProjects.IsEnabled = true;
            }
        }
    }

    private void ApplyFilters()
    {
        if (SessionsGrid == null) return;

        var cutoff = (RangeCombo?.SelectedIndex) switch
        {
            1 => DateTime.Today,
            2 => DateTime.Today.AddDays(-7),
            3 => DateTime.Today.AddDays(-30),
            _ => DateTime.MinValue
        };

        var filtered = _all.Where(s => s.LastTimestamp >= cutoff);

        // Default: filter to current project. The "all projects" checkbox opts out.
        // If no workspace is detected, _currentCwd is null and we fall through to all.
        var showAll = AllProjects?.IsChecked == true;
        if (!showAll && !string.IsNullOrEmpty(_currentCwd))
            filtered = filtered.Where(s => s.Cwd.Equals(_currentCwd, StringComparison.OrdinalIgnoreCase));

        var list = filtered.Select(s => new SessionRow(s)).ToList();
        SessionsGrid.ItemsSource = list;

        var totalCost  = list.Sum(r => r.Cost);
        var totalIn    = list.Sum(r => r.InputTokens);
        var totalOut   = list.Sum(r => r.OutputTokens);
        var totalCache = list.Sum(r => r.CacheReadTokens + r.CacheCreationTokens);

        TotalCostText.Text     = $"${totalCost:F2}";
        TotalInText.Text       = totalIn.ToString("N0");
        TotalOutText.Text      = totalOut.ToString("N0");
        TotalCacheText.Text    = totalCache.ToString("N0");
        SessionCountText.Text  = list.Count.ToString();
    }

    public class SessionRow
    {
        public DateTime LastTimestamp { get; }
        public string ProjectName { get; }
        public string ShortModel { get; }
        public long InputTokens { get; }
        public long OutputTokens { get; }
        public long CacheReadTokens { get; }
        public long CacheCreationTokens { get; }
        public long TotalInputTokens => InputTokens + CacheReadTokens + CacheCreationTokens;
        public int TurnCount { get; }
        public decimal Cost { get; }

        public SessionRow(SessionUsage s)
        {
            LastTimestamp = s.LastTimestamp;
            ProjectName = string.IsNullOrEmpty(s.Cwd) ? "(unknown)" : Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
            ShortModel = ShortenModel(s.Model);
            InputTokens = s.InputTokens;
            OutputTokens = s.OutputTokens;
            CacheReadTokens = s.CacheReadTokens;
            CacheCreationTokens = s.CacheCreationTokens;
            TurnCount = s.TurnCount;
            Cost = s.Cost;
        }

        private static string ShortenModel(string m)
        {
            if (m.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (m.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return "haiku";
            if (m.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "sonnet";
            return m;
        }
    }
}
