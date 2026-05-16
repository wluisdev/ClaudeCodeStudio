using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVsExtension.Usage;

public partial class UsageToolWindowControl : UserControl
{
    private List<SessionUsage> _all = new();
    private string? _currentCwd;
    private bool _loadedOnce;

    public UsageToolWindowControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            Refresh();
        };
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();
    private void OnRangeChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilters();

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
            var solutionPath = dte?.Solution?.FullName;
            if (!string.IsNullOrEmpty(solutionPath))
                _currentCwd = Path.GetDirectoryName(solutionPath);
        }
        catch { }
#pragma warning restore VSTHRD010
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

        if (CurrentProjectOnly?.IsChecked == true && !string.IsNullOrEmpty(_currentCwd))
            filtered = filtered.Where(s => s.Cwd.Equals(_currentCwd, StringComparison.OrdinalIgnoreCase));

        var list = filtered.Select(s => new SessionRow(s)).ToList();
        SessionsGrid.ItemsSource = list;

        var totalCost  = list.Sum(r => r.Cost);
        var totalIn    = list.Sum(r => r.InputTokens);
        var totalOut   = list.Sum(r => r.OutputTokens);
        var totalCache = list.Sum(r => r.CacheReadTokens + r.CacheCreationTokens);

        TotalCostText.Text     = $"${totalCost:F4}";
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
