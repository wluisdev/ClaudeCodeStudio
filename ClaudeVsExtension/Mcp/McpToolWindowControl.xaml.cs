using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVsExtension.Mcp;

public partial class McpToolWindowControl : UserControl
{
    public static McpToolWindowControl? ActiveControl { get; private set; }

    private McpScope _scope = McpScope.Project;
    private string? _projectDir;
    private List<McpServer> _servers = new();
    private McpServer? _editing;
    private bool _isNew;

    private static readonly SolidColorBrush AccentBrush = new(Color.FromRgb(0xd8, 0x87, 0x63));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x8a, 0x8a, 0x8a));
    private static readonly SolidColorBrush FgBrush = new(Color.FromRgb(0xf4, 0xf4, 0xf4));
    private static readonly SolidColorBrush BorderBrushColor = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush SurfaceBrush = new(Color.FromRgb(0x2a, 0x2a, 0x2a));
    private static readonly SolidColorBrush HoverBrush = new(Color.FromRgb(0x3a, 0x3a, 0x3a));

    public McpToolWindowControl()
    {
        InitializeComponent();

        foreach (var t in McpTemplates.All)
            TemplateCombo.Items.Add(new ComboBoxItem { Content = t.Label, Tag = t });
        TemplateCombo.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            ActiveControl = this;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (ReferenceEquals(ActiveControl, this)) ActiveControl = null;
        };
    }

    private void CaptureCurrentCwd()
    {
#pragma warning disable VSTHRD010
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionPath = dte?.Solution?.FullName;
            _projectDir = !string.IsNullOrEmpty(solutionPath) ? Path.GetDirectoryName(solutionPath) : null;
        }
        catch { _projectDir = null; }
#pragma warning restore VSTHRD010
    }

    public void Refresh()
    {
        try
        {
            CaptureCurrentCwd();

            if (_scope == McpScope.Project && string.IsNullOrEmpty(_projectDir))
            {
                _servers = new List<McpServer>();
                if (_editing != null) CancelEdit();
                UpdateScopePathLabel();
                RenderList();
                return;
            }

            _servers = McpConfigStore.Load(_scope, _projectDir);
            UpdateScopePathLabel();

            if (_editing != null && !_isNew)
            {
                var editingName = _editing.Name;
                var rebound = _servers.FirstOrDefault(x => x.Name == editingName);
                if (rebound != null)
                {
                    BeginEdit(rebound, isNew: false);
                    return;
                }
                CancelEdit();
                return;
            }

            RenderList();
        }
        catch (Exception ex)
        {
            ServerListPanel.Children.Clear();
            ServerListPanel.Children.Add(new TextBlock
            {
                Text = "Error loading: " + ex.Message,
                Foreground = MutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void UpdateScopePathLabel()
    {
        try
        {
            if (_scope == McpScope.Project && string.IsNullOrEmpty(_projectDir))
            {
                ScopePathLabel.Text = "No solution open. Project scope unavailable.";
                BtnAdd.IsEnabled = false;
                return;
            }
            BtnAdd.IsEnabled = true;
            var path = McpConfigStore.GetPath(_scope, _projectDir);
            ScopePathLabel.Text = path + (File.Exists(path) ? "" : "  (will be created)");
        }
        catch (Exception ex)
        {
            ScopePathLabel.Text = ex.Message;
        }
    }

    private void RenderList()
    {
        ServerListPanel.Children.Clear();

        if (_servers.Count == 0)
        {
            ServerListPanel.Children.Add(new TextBlock
            {
                Text = "No MCP servers configured.",
                Foreground = MutedBrush,
                FontSize = 11,
                Margin = new Thickness(4, 8, 0, 0),
            });
            return;
        }

        foreach (var s in _servers)
            ServerListPanel.Children.Add(BuildServerRow(s));
    }

    private Border BuildServerRow(McpServer s)
    {
        var transportText = s.Transport switch
        {
            McpTransport.Http => "http",
            McpTransport.Sse => "sse",
            _ => "stdio",
        };

        var subtitle = s.Transport == McpTransport.Stdio
            ? (string.IsNullOrEmpty(s.Command) ? "(no command)" : s.Command)
            : (string.IsNullOrEmpty(s.Url) ? "(no url)" : s.Url);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(new TextBlock
        {
            Text = s.Name,
            Foreground = FgBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        });
        topRow.Children.Add(new Border
        {
            Background = SurfaceBrush,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = transportText,
                Foreground = MutedBrush,
                FontSize = 9,
                Typography = { Capitals = FontCapitals.AllSmallCaps },
            },
        });
        stack.Children.Add(topRow);

        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = MutedBrush,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var border = new Border
        {
            Background = (_editing == s) ? HoverBrush : Brushes.Transparent,
            BorderBrush = (_editing == s) ? AccentBrush : BorderBrushColor,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
            Child = stack,
        };

        border.MouseEnter += (_, _) =>
        {
            if (_editing != s) border.Background = HoverBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            if (_editing != s) border.Background = Brushes.Transparent;
        };
        border.MouseLeftButtonUp += (_, _) => BeginEdit(s, isNew: false);

        return border;
    }

    private void OnTabProjectClick(object sender, RoutedEventArgs e) => SwitchScope(McpScope.Project);
    private void OnTabUserClick(object sender, RoutedEventArgs e) => SwitchScope(McpScope.User);

    private void SwitchScope(McpScope scope)
    {
        if (_scope == scope) return;
        _scope = scope;
        TabProject.Style = (Style)FindResource(scope == McpScope.Project ? "TabButtonActive" : "TabButton");
        TabUser.Style = (Style)FindResource(scope == McpScope.User ? "TabButtonActive" : "TabButton");
        CancelEdit();
        Refresh();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var s = new McpServer { Name = "" };
        BeginEdit(s, isNew: true);
    }

    private void BeginEdit(McpServer s, bool isNew)
    {
        _editing = s;
        _isNew = isNew;

        EditTitle.Text = isNew ? "New server" : "Edit: " + s.Name;
        TemplateCombo.Visibility = Visibility.Visible;
        TemplateCombo.IsEnabled = isNew;
        TemplateCombo.SelectedIndex = isNew ? 0 : McpTemplates.DetectIndex(s);

        NameBox.Text = s.Name;
        NameBox.IsEnabled = isNew;
        RbStdio.IsChecked = s.Transport == McpTransport.Stdio;
        RbHttp.IsChecked = s.Transport == McpTransport.Http;
        RbSse.IsChecked = s.Transport == McpTransport.Sse;
        CommandBox.Text = s.Command;
        ArgsBox.Text = string.Join(Environment.NewLine, s.Args);
        UrlBox.Text = s.Url;
        RebuildEnvList(s.Env);
        RebuildHeadersList(s.Headers);
        UpdateTransportPanels();

        BtnDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        ErrorLabel.Text = "";
        EditPanel.Visibility = Visibility.Visible;
        RenderList();

        if (isNew) NameBox.Focus();
    }

    private void CancelEdit()
    {
        _editing = null;
        _isNew = false;
        EditPanel.Visibility = Visibility.Collapsed;
        ErrorLabel.Text = "";
        RenderList();
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e) => CancelEdit();

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editing == null || !_isNew) return;
        if (TemplateCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not McpTemplate t) return;
        if (t.Server.Transport == McpTransport.Stdio && string.IsNullOrEmpty(t.Server.Command) && string.IsNullOrEmpty(t.Server.Name))
            return;

        var clone = t.Server.Clone();
        if (string.IsNullOrEmpty(NameBox.Text)) NameBox.Text = clone.Name;
        RbStdio.IsChecked = clone.Transport == McpTransport.Stdio;
        RbHttp.IsChecked = clone.Transport == McpTransport.Http;
        RbSse.IsChecked = clone.Transport == McpTransport.Sse;
        CommandBox.Text = clone.Command;
        ArgsBox.Text = string.Join(Environment.NewLine, clone.Args);
        UrlBox.Text = clone.Url;
        RebuildEnvList(clone.Env);
        RebuildHeadersList(clone.Headers);
        UpdateTransportPanels();
    }

    private void OnTransportChanged(object sender, RoutedEventArgs e) => UpdateTransportPanels();

    private void UpdateTransportPanels()
    {
        if (StdioPanel == null || HttpPanel == null) return;
        bool stdio = RbStdio.IsChecked == true;
        StdioPanel.Visibility = stdio ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = stdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildEnvList(Dictionary<string, string> env)
    {
        EnvList.Children.Clear();
        foreach (var kv in env) EnvList.Children.Add(BuildKvRow(kv.Key, kv.Value, EnvList));
    }

    private void RebuildHeadersList(Dictionary<string, string> headers)
    {
        HeadersList.Children.Clear();
        foreach (var kv in headers) HeadersList.Children.Add(BuildKvRow(kv.Key, kv.Value, HeadersList));
    }

    private void OnAddEnvClick(object sender, RoutedEventArgs e) => EnvList.Children.Add(BuildKvRow("", "", EnvList));
    private void OnAddHeaderClick(object sender, RoutedEventArgs e) => HeadersList.Children.Add(BuildKvRow("", "", HeadersList));

    private Grid BuildKvRow(string key, string value, StackPanel parent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var k = new TextBox { Text = key, Tag = "key" };
        var v = new TextBox { Text = value, Tag = "value", FontFamily = new FontFamily("Cascadia Code, Consolas, monospace") };
        var del = new Button { Content = "x", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
        del.Click += (_, _) => parent.Children.Remove(grid);

        Grid.SetColumn(k, 0);
        Grid.SetColumn(v, 2);
        Grid.SetColumn(del, 3);
        grid.Children.Add(k);
        grid.Children.Add(v);
        grid.Children.Add(del);
        return grid;
    }

    private (Dictionary<string, string> map, string? error) ReadKvList(StackPanel panel, string label)
    {
        var map = new Dictionary<string, string>();
        foreach (var child in panel.Children)
        {
            if (child is not Grid g) continue;
            string? k = null, v = null;
            foreach (var c in g.Children)
                if (c is TextBox tb)
                {
                    if ((string?)tb.Tag == "key") k = tb.Text;
                    else if ((string?)tb.Tag == "value") v = tb.Text;
                }
            if (string.IsNullOrWhiteSpace(k))
            {
                if (!string.IsNullOrEmpty(v)) return (map, $"Empty {label} key");
                continue;
            }
            if (map.ContainsKey(k!)) return (map, $"Duplicate {label}: {k}");
            map[k!] = v ?? "";
        }
        return (map, null);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        ErrorLabel.Text = "";

        var name = NameBox.Text.Trim();
        var nameErr = McpConfigStore.ValidateName(name);
        if (!string.IsNullOrEmpty(nameErr)) { ErrorLabel.Text = nameErr; return; }

        if (_isNew && _servers.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorLabel.Text = "A server with this name already exists";
            return;
        }

        var transport = RbStdio.IsChecked == true ? McpTransport.Stdio
                      : RbSse.IsChecked == true ? McpTransport.Sse
                      : McpTransport.Http;

        var draft = new McpServer { Name = name, Transport = transport };

        if (transport == McpTransport.Stdio)
        {
            draft.Command = CommandBox.Text.Trim();
            if (string.IsNullOrEmpty(draft.Command)) { ErrorLabel.Text = "Command is required"; return; }
            draft.Args = (ArgsBox.Text ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            var (env, envErr) = ReadKvList(EnvList, "env var");
            if (envErr != null) { ErrorLabel.Text = envErr; return; }
            draft.Env = env;
        }
        else
        {
            draft.Url = UrlBox.Text.Trim();
            if (string.IsNullOrEmpty(draft.Url)) { ErrorLabel.Text = "URL is required"; return; }
            if (!Uri.TryCreate(draft.Url, UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.Authority) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ErrorLabel.Text = "URL must be a valid http or https URL (e.g. https://example.com)";
                return;
            }
            var (hdrs, hdrErr) = ReadKvList(HeadersList, "header");
            if (hdrErr != null) { ErrorLabel.Text = hdrErr; return; }
            draft.Headers = hdrs;
        }

        try
        {
            if (_isNew)
            {
                _servers.Add(draft);
            }
            else
            {
                var idx = _servers.IndexOf(_editing);
                if (idx < 0) idx = _servers.FindIndex(x => x.Name == _editing.Name);
                draft.Name = _editing.Name;
                if (idx >= 0) _servers[idx] = draft;
                else _servers.Add(draft);
            }

            McpConfigStore.Save(_scope, _projectDir, _servers);
            _editing = null;
            _isNew = false;
            EditPanel.Visibility = Visibility.Collapsed;
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Save failed: " + ex.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing == null || _isNew) return;
        var result = MessageBox.Show($"Delete server '{_editing.Name}'?", "Confirm delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            _servers.RemoveAll(x => x.Name == _editing.Name);
            McpConfigStore.Save(_scope, _projectDir, _servers);
            _editing = null;
            EditPanel.Visibility = Visibility.Collapsed;
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = "Delete failed: " + ex.Message;
        }
    }
}
