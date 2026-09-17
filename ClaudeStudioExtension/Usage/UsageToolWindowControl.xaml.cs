using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeStudioShared;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;

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
    // Whether the plan-limit bars currently show data (from cache or a live run).
    // A failed refresh keeps them instead of blanking to an error.
    private bool _haveLimits;

    public UsageToolWindowControl()
    {
        InitializeComponent();

        ApplyTheme();
        Theming.NativeTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theming.NativeTheme.Changed -= OnThemeChanged;

        Loaded += (_, _) =>
        {
            ApplyTheme();
            if (_loadedOnce) return;
            _loadedOnce = true;
            Refresh();
            LoadCachedLimits();       // instant bars from the last snapshot…
            _ = LoadPlanLimitsAsync(); // …then refresh in the background (the CLI is ~3s)
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

    // Native WPF tool window: the chat's CSS theming does not reach it. Theming is
    // delegated to the shared NativeTheme helper, which follows the appearance
    // override (auto/dark/light + custom accent) or the live VS theme and swaps the
    // DynamicResource brush entries in place. NativeTheme.Changed fires for both a
    // VS theme switch and an appearance-setting change from the chat.
    private void OnThemeChanged() => Dispatcher.Invoke(ApplyTheme);

    private void ApplyTheme()
    {
        try
        {
            Theming.NativeTheme.Apply(this);
        }
        catch (Exception ex) { OutputLog.Info($"Usage theme failed: {ex.GetType().Name}: {ex.Message}"); }
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

    // Suppresses OnSessionChanged while ApplyFilters repopulates the combo —
    // programmatic item churn fires SelectionChanged just like a user click.
    private bool _rebuildingSessionCombo;

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();
    private void OnRangeChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingSessionCombo) ApplyFilters();
    }
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

    // ── Plan limits (issue #15) ───────────────────────────────────────────
    // The subscription session/week limits are not in the local JSONL; they come
    // from `claude -p "/usage"`, which prints them as text. Run the CLI once on
    // open (and on the refresh button) and parse the two lines into progress bars.
    private async Task LoadPlanLimitsAsync()
    {
        var sw = Stopwatch.StartNew();
        if (LimitsRefreshButton != null) LimitsRefreshButton.IsEnabled = false;
        // Keep the bars up while refreshing if we already have data; only show the
        // full "Loading…" placeholder on the first run with nothing cached.
        if (_haveLimits)
        {
            if (LimitsStatusText != null) { LimitsStatusText.Text = "Updating…"; LimitsStatusText.Visibility = Visibility.Visible; }
        }
        else
        {
            ShowLimitsStatus("Loading…");
        }

        string output;
        try
        {
            output = await Task.Run(RunUsageCommand);
        }
        catch (ClaudeNotFoundException)
        {
            FinishLimitsRefresh("Claude CLI not found.");
            return;
        }
        catch (Exception ex)
        {
            OutputLog.Info($"plan limits failed: {ex.GetType().Name}: {ex.Message}");
            FinishLimitsRefresh("Could not read plan limits.");
            return;
        }
        finally
        {
            if (LimitsRefreshButton != null) LimitsRefreshButton.IsEnabled = true;
        }

        var session = ParseLimit(output, @"Current session:\s*(\d+)% used[^\n]*?resets\s+([^\n]+)");
        var week = ParseLimit(output, @"Current week[^:]*:\s*(\d+)% used[^\n]*?resets\s+([^\n]+)");
        if (session == null && week == null)
        {
            FinishLimitsRefresh("Could not read plan limits.");
            return;
        }

        if (LimitsStatusText != null) LimitsStatusText.Visibility = Visibility.Collapsed;
        ApplyLimit(session, SessionLimitRow, SessionPctText, SessionResetText, SessionFillCol, SessionRestCol);
        ApplyLimit(week, WeekLimitRow, WeekPctText, WeekResetText, WeekFillCol, WeekRestCol);
        _haveLimits = true;
        new PlanLimitsCache
        {
            SessionPct = session?.pct, SessionReset = session?.reset,
            WeekPct = week?.pct, WeekReset = week?.reset,
        }.Save();
        OutputLog.Info($"plan limits rendered in {sw.ElapsedMilliseconds}ms (total, incl. CLI)");
    }

    // A failed refresh keeps already-shown bars (just hides the "Updating…" hint);
    // the error text only appears when there is nothing cached to show.
    private void FinishLimitsRefresh(string errorIfEmpty)
    {
        if (_haveLimits)
        {
            if (LimitsStatusText != null) LimitsStatusText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowLimitsStatus(errorIfEmpty);
        }
    }

    // Shows the last cached snapshot instantly (before the ~3s CLI run) so the
    // window is never blank on open.
    private void LoadCachedLimits()
    {
        var c = PlanLimitsCache.Load();
        var session = c.SessionPct is int sp ? ((int pct, string reset)?)(sp, c.SessionReset ?? "") : null;
        var week = c.WeekPct is int wp ? ((int pct, string reset)?)(wp, c.WeekReset ?? "") : null;
        if (session == null && week == null) return;
        if (LimitsStatusText != null) LimitsStatusText.Visibility = Visibility.Collapsed;
        ApplyLimit(session, SessionLimitRow, SessionPctText, SessionResetText, SessionFillCol, SessionRestCol);
        ApplyLimit(week, WeekLimitRow, WeekPctText, WeekResetText, WeekFillCol, WeekRestCol);
        _haveLimits = true;
    }

    private void OnRefreshLimitsClick(object sender, RoutedEventArgs e) => _ = LoadPlanLimitsAsync();

    private void ShowLimitsStatus(string text)
    {
        if (LimitsStatusText != null) { LimitsStatusText.Text = text; LimitsStatusText.Visibility = Visibility.Visible; }
        if (SessionLimitRow != null) SessionLimitRow.Visibility = Visibility.Collapsed;
        if (WeekLimitRow != null) WeekLimitRow.Visibility = Visibility.Collapsed;
    }

    // Runs `claude -p "/usage"` and returns its stdout. Blocking — call via Task.Run.
    private static string RunUsageCommand()
    {
        var sw = Stopwatch.StartNew();
        var exe = ClaudeExeLocator.FindClaudeExe(null); // may throw ClaudeNotFoundException
        var resolveMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-p /usage",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, _) => { /* drained so stderr can't deadlock; ignored */ };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(); } catch { /* already gone */ }
            throw new TimeoutException("claude -p /usage timed out");
        }
        proc.WaitForExit(); // let the async readers flush
        OutputLog.Info($"plan limits: exe resolve={resolveMs}ms, claude -p /usage={sw.ElapsedMilliseconds}ms");
        return sb.ToString();
    }

    private static (int pct, string reset)? ParseLimit(string output, string pattern)
    {
        var m = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var pct)) return null;
        var reset = m.Groups[2].Value.Trim();
        // Drop a trailing " (timezone)" so the line stays short.
        reset = Regex.Replace(reset, @"\s*\([^)]*\)\s*$", "").Trim();
        return (pct, reset);
    }

    private static void ApplyLimit((int pct, string reset)? data, StackPanel row, TextBlock pctText,
        TextBlock resetText, ColumnDefinition fillCol, ColumnDefinition restCol)
    {
        if (data == null) { row.Visibility = Visibility.Collapsed; return; }
        var pct = Math.Max(0, Math.Min(100, data.Value.pct));
        pctText.Text = data.Value.pct + "%";
        resetText.Text = "resets " + data.Value.reset;
        fillCol.Width = new GridLength(pct, GridUnitType.Star);
        restCol.Width = new GridLength(100 - pct, GridUnitType.Star);
        row.Visibility = Visibility.Visible;
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

        // Session filter: the combo lists the sessions inside the current
        // range+project scope; picking one narrows tiles and grid to it.
        var scope = filtered.ToList();
        var selectedSession = RebuildSessionCombo(scope);
        if (selectedSession != null)
            scope = scope.Where(s => s.SessionId == selectedSession).ToList();

        var list = scope.Select(s => new SessionRow(s)).ToList();
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

    /// <summary>
    /// Repopulates the session combo from the sessions in scope, preserving the
    /// current pick when that session is still present. Returns the selected
    /// session id, or null for "All sessions" (or when the pick left the scope).
    /// </summary>
    private string? RebuildSessionCombo(List<SessionUsage> scope)
    {
        if (SessionCombo == null) return null;

        var previous = (SessionCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        _rebuildingSessionCombo = true;
        try
        {
            SessionCombo.Items.Clear();
            SessionCombo.Items.Add(new ComboBoxItem { Content = "All sessions" });

            foreach (var s in scope)
            {
                var item = new ComboBoxItem
                {
                    Content = $"{ResolveSessionLabel(s)} · {s.LastTimestamp:dd/MM HH:mm}",
                    Tag = s.SessionId,
                    ToolTip = s.SessionId,
                };
                SessionCombo.Items.Add(item);
                if (s.SessionId == previous) SessionCombo.SelectedItem = item;
            }

            if (SessionCombo.SelectedItem == null) SessionCombo.SelectedIndex = 0;
        }
        finally { _rebuildingSessionCombo = false; }

        return (SessionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    // Same precedence History uses: store custom > native custom (unless the
    // user explicitly cleared it) > store generated > native ai > preview.
    private static string ResolveSessionLabel(SessionUsage s)
    {
        var title = SessionTitlesStore.GetCustom(s.SessionId)
            ?? (s.NativeCustomTitle.Length > 0 && !SessionTitlesStore.WasCustomCleared(s.SessionId)
                ? s.NativeCustomTitle : null)
            ?? SessionTitlesStore.GetGenerated(s.SessionId)
            ?? (s.NativeAiTitle.Length > 0 ? s.NativeAiTitle : null)
            ?? (s.Preview.Length > 0 ? s.Preview : "(untitled)");
        return title.Length > 46 ? title.Substring(0, 46) + "…" : title;
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
            if (m.Contains("fable", StringComparison.OrdinalIgnoreCase)
                || m.Contains("mythos", StringComparison.OrdinalIgnoreCase)) return "fable";
            if (m.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (m.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return "haiku";
            if (m.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "sonnet";
            return m;
        }
    }
}
