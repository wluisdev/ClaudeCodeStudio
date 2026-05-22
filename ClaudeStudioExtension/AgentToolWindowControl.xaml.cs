using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using ClaudeStudioExtension.Agent;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;

namespace ClaudeStudioExtension;

public partial class AgentToolWindowControl : UserControl
{
    private readonly AgentClient _agentClient = new();
    private bool _initialized;
    private string? _lastWorkingDir;
    private DateTime _sessionStart = DateTime.Now;

    public AgentToolWindowControl()
    {
        InitializeComponent();

        Loaded += AgentToolWindowControl_Loaded;
        Unloaded += AgentToolWindowControl_Unloaded;
        IsVisibleChanged += AgentToolWindowControl_IsVisibleChanged;

        AgentToolWindowCommand.ActiveControl = this;
    }

    private void AgentToolWindowControl_IsVisibleChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && Browser?.CoreWebView2 != null)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(FocusTextarea));
        }
    }

    public void FocusTextarea()
    {
        Browser.Focus();
        Browser.CoreWebView2?.PostWebMessageAsJson(
            JsonSerializer.Serialize(new { type = "focus" }));
    }

    private async void AgentToolWindowControl_Loaded(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        var userDataFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeStudio",
            "WebView2");

        var environment = await CoreWebView2Environment
            .CreateAsync(null, userDataFolder);

        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        var extensionAssemblyPath = System.IO.Path.GetDirectoryName(typeof(AgentToolWindowControl).Assembly.Location)!;

        var htmlPath = System.IO.Path.Combine(
            extensionAssemblyPath,
            "Ui",
            "index.html");

        Browser.Source = new Uri(htmlPath);

        Browser.NavigationCompleted += (_, _) =>
        {
            var version = typeof(AgentToolWindowControl).Assembly.GetName().Version;
            var versionString = $"{version?.Major}.{version?.Minor}.{version?.Build}";
            Browser.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "version", text = versionString }));

            SendCurrentTheme();
        };

        VSColorTheme.ThemeChanged += OnVsThemeChanged;
    }

    private void OnVsThemeChanged(ThemeChangedEventArgs e)
    {
        SendCurrentTheme();
    }

    private void SendCurrentTheme()
    {
        try
        {
            if (Browser?.CoreWebView2 == null) return;
            var bgColor = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var brightness = bgColor.R * 0.299 + bgColor.G * 0.587 + bgColor.B * 0.114;
            var isDark = brightness < 128;
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "theme", isDark })));
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"theme change propagation failed: {ex.Message}");
        }
    }

    private void AgentToolWindowControl_Unloaded(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        VSColorTheme.ThemeChanged -= OnVsThemeChanged;
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var messageJson = e.WebMessageAsJson;

            var request = JsonSerializer.Deserialize<WebChatMessage>(messageJson);

            if (request == null)
                return;

            string? currentSolutionDir = null;
#pragma warning disable VSTHRD010
            try
            {
                var dteForCwd = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var solutionPath = dteForCwd?.Solution?.FullName;
                if (!string.IsNullOrEmpty(solutionPath))
                    currentSolutionDir = Path.GetDirectoryName(solutionPath);

                ApplyAutoSave(dteForCwd, request.AutoSave);
            }
            catch { }
#pragma warning restore VSTHRD010

            if (request.Type == "clear")
            {
                OutputLog.Info("ui: clear");
                await _agentClient.StopAsync();
                _sessionStart = DateTime.Now;
                return;
            }

            if (request.Type == "get-cost-limits")
            {
                var limits = Usage.CostLimits.Load();
                var dispatcher2 = System.Windows.Application.Current.Dispatcher;
                dispatcher2.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new
                    {
                        type = "cost-limits",
                        sessionLimit = limits.SessionLimit,
                        dailyLimit = limits.DailyLimit,
                        block = limits.Block
                    })));
                return;
            }

            if (request.Type == "set-cost-limits")
            {
                new Usage.CostLimits
                {
                    SessionLimit = request.SessionLimit,
                    DailyLimit = request.DailyLimit,
                    Block = request.Block
                }.Save();
                return;
            }

            if (request.Type == "cancel")
            {
                OutputLog.Info("ui: cancel");
                _agentClient.CancelCurrent();
                VsStatusBar.Clear();
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "stream-done" })));
                return;
            }

            if (request.Type == "permission-response")
            {
                if (string.IsNullOrEmpty(request.ToolUseId))
                {
                    OutputLog.Warn("permission-response missing toolUseId");
                    return;
                }
                await _agentClient.SendPermissionResponseAsync(request.ToolUseId, request.Allow, request.Reason, request.AllowSession);
                return;
            }

            if (request.Type == "get-history")
            {
                await HandleGetHistoryAsync();
                return;
            }

            if (request.Type == "resume-session")
            {
                OutputLog.Info($"ui: resume session {request.SessionId}");
                await _agentClient.StopAsync();
                _agentClient.PendingResumeSessionId = request.SessionId;
                await HandleResumeSessionAsync(request.SessionId);
                return;
            }

            if (request.Type == "delete-session")
            {
                await HandleDeleteSessionAsync(request.SessionId);
                return;
            }

            if (request.Type == "get-diff")
            {
#pragma warning disable VSTHRD010
                try
                {
                    var dteForSave = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    ApplyAutoSave(dteForSave, request.AutoSave);
                }
                catch { }
#pragma warning restore VSTHRD010
                await HandleGetDiffAsync();
                return;
            }

            if (request.Type == "open-file")
            {
                if (!string.IsNullOrEmpty(request.Path) && File.Exists(request.Path))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dteOpen = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    try { dteOpen?.ItemOperations.OpenFile(request.Path); } catch { }
                }
                return;
            }

            if (request.Type == "open-usage")
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var pkg = ClaudeStudioExtensionPackage.Instance;
                if (pkg != null)
                {
                    var window = pkg.FindToolWindow(typeof(Usage.UsageToolWindow), 0, true);
                    if (window?.Frame is IVsWindowFrame frame)
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                    if (window?.Content is Usage.UsageToolWindowControl ctrl)
                        ctrl.Refresh();
                }
                return;
            }

            if (request.Type == "open-mcp")
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var pkg = ClaudeStudioExtensionPackage.Instance;
                if (pkg != null)
                {
                    var window = pkg.FindToolWindow(typeof(Mcp.McpToolWindow), 0, true);
                    if (window?.Frame is IVsWindowFrame frame)
                        Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                    if (window?.Content is Mcp.McpToolWindowControl ctrl)
                        ctrl.Refresh();
                }
                return;
            }

            if (request.Type == "unfocus")
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dteUnfocus = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                dteUnfocus?.ActiveDocument?.Activate();
                return;
            }

            if (request.Type == "apply-to-editor")
            {
                await HandleApplyToEditorAsync(request.Code, request.Language);
                return;
            }

            if (request.Type == "set-caption")
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var pkg = ClaudeStudioExtensionPackage.Instance;
                var pane = pkg?.FindToolWindow(typeof(AgentToolWindow), 0, false) as AgentToolWindow;
                if (pane != null && !string.IsNullOrEmpty(request.Text))
                    pane.Caption = request.Text;
                return;
            }

            if (request.Type == "export-markdown")
            {
                await HandleExportMarkdownAsync(request.Content);
                return;
            }

            if (request.Type == "branch")
            {
                await HandleBranchAsync(request.MsgIndex);
                return;
            }

            if (request.Type == "add-file")
            {
                await HandleAddFileAsync();
                return;
            }

            if (request.Type == "get-clipboard-image")
            {
                await HandleGetClipboardImageAsync();
                return;
            }

            if (request.Type == "get-clipboard-files")
            {
                await HandleGetClipboardFilesAsync();
                return;
            }

            if (request.Type == "save-dropped-file")
            {
                if (!string.IsNullOrEmpty(request.Filename) && !string.IsNullOrEmpty(request.Data))
                    await HandleSaveDroppedFileAsync(request.Filename, request.Data);
                return;
            }

            if (request.Type == "get-selection")
            {
                await HandleGetSelectionAsync();
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Text))
                return;

            var workingDir = (!string.IsNullOrEmpty(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory))
                ? request.WorkingDirectory
                : currentSolutionDir;

            var preLimits = Usage.CostLimits.Load();
            if (preLimits.Block && !preLimits.IsEmpty)
            {
                var (sCost, dCost) = await Task.Run(() => ComputeCosts(workingDir));
                if (preLimits.ShouldBlock(sCost, dCost))
                {
                    var blockJson = JsonSerializer.Serialize(new
                    {
                        type = "cost-warning",
                        text = preLimits.BuildWarning(sCost, dCost) ?? "limit reached",
                        blocked = true,
                        sessionCost = sCost,
                        dailyCost = dCost,
                        sessionLimit = preLimits.SessionLimit,
                        dailyLimit = preLimits.DailyLimit
                    });
                    var disp = System.Windows.Application.Current.Dispatcher;
                    disp.Invoke(() =>
                    {
                        Browser.CoreWebView2.PostWebMessageAsJson(blockJson);
                        Browser.CoreWebView2.PostWebMessageAsJson(
                            JsonSerializer.Serialize(new { type = "stream-done" }));
                    });
                    return;
                }
            }

            if (!string.Equals(workingDir, _lastWorkingDir, StringComparison.OrdinalIgnoreCase))
            {
                OutputLog.Info($"working dir changed: {_lastWorkingDir ?? "-"} → {workingDir ?? "-"} (restarting agent)");
                await _agentClient.StopAsync();
                _lastWorkingDir = workingDir;
            }

            await _agentClient.StartAsync();

            VsStatusBar.ShowThinking();

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            await _agentClient.AskStreamingAsync(request.Text, request.Model, request.Effort, request.PermissionMode,
                chunk => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "chunk", text = chunk }))),
                timing => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "timing", text = timing }))),
                tokens => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "tokens", text = tokens }))),
                workingDirectory: workingDir,
                autoResume: request.AutoResume,
                onSession: sid => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "session-info", sessionId = sid }))),
                onTool: (kind, name, input, text, id) => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = kind, name, input, text, id }))),
                onPermissionRequest: (tool, input, id) => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "permission_request", tool, input, id }))));

            VsStatusBar.Clear();

            dispatcher.Invoke(() =>
            {
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "stream-done" }));
            });

            _ = Task.Run(() => CheckCostLimitsAsync(workingDir));
        }
        catch (Exception ex)
        {
            OutputLog.Error($"unhandled in WebMessageReceived: {ex}");
            VsStatusBar.Clear();
            var responseJson = JsonSerializer.Serialize(new
            {
                type = "assistant",
                text = ex.Message
            });

            Browser.CoreWebView2.PostWebMessageAsJson(responseJson);
        }
    }

    private static readonly System.Collections.Generic.HashSet<string> _binaryExtensions =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff",
            ".pdf", ".zip", ".rar", ".7z", ".tar", ".gz",
            ".exe", ".dll", ".bin", ".dat", ".pdb",
            ".mp3", ".mp4", ".wav", ".avi", ".mov"
        };

    private async Task HandleExportMarkdownAsync(string? content)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (string.IsNullOrEmpty(content))
        {
            VsStatusBar.SetText("Nothing to export — chat is empty.");
            return;
        }

        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export chat to Markdown",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            FileName = $"claude-chat-{ts}.md",
            DefaultExt = ".md",
            AddExtension = true
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, content);
            VsStatusBar.SetText($"Chat exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            VsStatusBar.SetText($"Export failed: {ex.Message}");
        }
    }

    private async Task HandleAddFileAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select file",
            Filter = "All files (*.*)|*.*|Text files|*.txt;*.cs;*.js;*.ts;*.html;*.css;*.json;*.xml;*.md;*.py;*.cpp;*.h"
        };

        if (dialog.ShowDialog() != true)
            return;

        var filename = Path.GetFileName(dialog.FileName);
        var ext = Path.GetExtension(dialog.FileName);

        var isBinary = _binaryExtensions.Contains(ext);
        var content = isBinary
            ? null
            : $"[{filename}]\n```\n{File.ReadAllText(dialog.FileName)}\n```";
        var filePath = dialog.FileName;

        var json = JsonSerializer.Serialize(new { type = "attach-file", filename, content, isBinary, filePath });
        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    public Task SendActiveSelectionAsync() => HandleGetSelectionAsync();

#pragma warning disable VSTHRD010
    private static void ApplyAutoSave(EnvDTE.DTE? dte, string? mode)
    {
        if (dte == null || string.IsNullOrEmpty(mode) || mode == "none") return;
        try
        {
            if (mode == "active")
            {
                dte.ActiveDocument?.Save();
            }
            else if (mode == "all")
            {
                if (dte.Documents == null) return;
                foreach (EnvDTE.Document doc in dte.Documents)
                    try { if (!doc.Saved) doc.Save(); } catch { }
            }
        }
        catch { }
    }
#pragma warning restore VSTHRD010

    public void InsertFileReference(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;

#pragma warning disable VSTHRD010
        string displayPath = fullPath;
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionDir = Path.GetDirectoryName(dte?.Solution?.FullName ?? "");
            if (!string.IsNullOrEmpty(solutionDir) && fullPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
                displayPath = fullPath.Substring(solutionDir.Length).TrimStart('\\', '/');
        }
        catch { }
#pragma warning restore VSTHRD010

        var text = "@" + displayPath.Replace('\\', '/') + " ";
        var json = JsonSerializer.Serialize(new { type = "insert-text", text });
        Browser.CoreWebView2?.PostWebMessageAsJson(json);
    }

    public async Task ResetSessionAsync()
    {
        await _agentClient.StopAsync();
        _lastWorkingDir = null;
        _sessionStart = DateTime.Now;
    }

    private (decimal sessionCost, decimal dailyCost) ComputeCosts(string? cwd)
    {
        var all = Usage.UsageReader.ReadAll();
        var today = DateTime.Today;
        decimal sessionCost = 0m;
        decimal dailyCost = 0m;
        foreach (var s in all)
        {
            if (s.LastTimestamp >= today)
                dailyCost += s.Cost;

            var inCwd = !string.IsNullOrEmpty(cwd) &&
                        s.Cwd.Equals(cwd, StringComparison.OrdinalIgnoreCase);
            if (inCwd && s.LastTimestamp >= _sessionStart)
                sessionCost += s.Cost;
        }
        return (sessionCost, dailyCost);
    }

    private Task CheckCostLimitsAsync(string? cwd)
    {
        try
        {
            var limits = Usage.CostLimits.Load();
            if (limits.IsEmpty) return Task.CompletedTask;

            var (sessionCost, dailyCost) = ComputeCosts(cwd);

            var warning = limits.BuildWarning(sessionCost, dailyCost);
            if (warning == null) return Task.CompletedTask;

            var json = JsonSerializer.Serialize(new
            {
                type = "cost-warning",
                text = warning,
                sessionCost,
                dailyCost,
                sessionLimit = limits.SessionLimit,
                dailyLimit = limits.DailyLimit
            });

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.Invoke(() => Browser.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch { }
        return Task.CompletedTask;
    }

    private async Task HandleApplyToEditorAsync(string? code, string? language)
    {
        if (string.IsNullOrEmpty(code))
            return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var doc = dte?.ActiveDocument;
            if (doc == null)
            {
                OutputLog.Warn("apply-to-editor: no active document");
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "toast", text = "No active editor — open a file first." }));
                return;
            }

            doc.Activate();
            var selection = doc.Selection as EnvDTE.TextSelection;
            if (selection == null)
            {
                OutputLog.Warn("apply-to-editor: active document has no text selection");
                return;
            }

            var insert = code.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            selection.Insert(insert, (int)EnvDTE.vsInsertFlags.vsInsertFlagsContainNewText);
            OutputLog.Info($"apply-to-editor: inserted {insert.Length} chars ({language ?? "?"}) into {doc.Name}");
        }
        catch (Exception ex)
        {
            OutputLog.Error($"apply-to-editor failed: {ex.Message}");
        }
    }

    private async Task HandleGetSelectionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        var doc = dte?.ActiveDocument;
        var selection = doc?.Selection as EnvDTE.TextSelection;
        var code = selection?.Text;

        if (string.IsNullOrWhiteSpace(code))
            return;

        var filePath = doc?.FullName ?? "";
        var startLine = selection?.TopLine ?? 0;
        var endLine = selection?.BottomLine ?? 0;

        var solutionDir = Path.GetDirectoryName(dte?.Solution?.FullName ?? "");
        var displayPath = (!string.IsNullOrEmpty(solutionDir) && filePath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
            ? filePath.Substring(solutionDir.Length).TrimStart('\\', '/')
            : filePath;

        var lang = GetLanguageId(Path.GetExtension(filePath).ToLowerInvariant());
        var lineInfo = startLine == endLine ? $"line {startLine}" : $"lines {startLine}-{endLine}";
        var text = $"File: {displayPath} ({lineInfo})\n```{lang}\n{code.TrimEnd('\r', '\n')}\n```\n";

        var json = JsonSerializer.Serialize(new { type = "insert-text", text });
        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string GetLanguageId(string ext) => ext switch
    {
        ".cs" => "csharp", ".vb" => "vb", ".fs" => "fsharp",
        ".py" => "python", ".js" => "javascript", ".ts" => "typescript",
        ".jsx" => "jsx", ".tsx" => "tsx",
        ".java" => "java", ".kt" => "kotlin",
        ".cpp" or ".cc" or ".cxx" => "cpp", ".c" => "c", ".h" => "c", ".hpp" => "cpp",
        ".go" => "go", ".rs" => "rust", ".swift" => "swift",
        ".rb" => "ruby", ".php" => "php",
        ".html" or ".htm" => "html", ".css" => "css",
        ".xml" or ".xaml" => "xml", ".json" => "json",
        ".yaml" or ".yml" => "yaml", ".sql" => "sql",
        ".sh" or ".bash" => "bash", ".ps1" or ".psm1" => "powershell",
        ".bat" or ".cmd" => "batch", ".md" => "markdown",
        _ => ""
    };

    private static readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ClaudeStudio");

    private async Task HandleGetClipboardFilesAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dispatcher = System.Windows.Application.Current.Dispatcher;

        // Files copied from Explorer
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            foreach (string filePath in files)
            {
                var filename = Path.GetFileName(filePath);
                var ext = Path.GetExtension(filePath);
                var isBinary = _binaryExtensions.Contains(ext);
                var content = isBinary ? null : $"[{filename}]\n```\n{File.ReadAllText(filePath)}\n```";
                var returnPath = isBinary ? filePath : (string?)null;
                var fileJson = JsonSerializer.Serialize(new { type = "attach-file", filename, content, isBinary, filePath = returnPath });
                dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(fileJson));
            }
            return;
        }

        // Image
        if (System.Windows.Clipboard.ContainsImage())
        {
            await HandleGetClipboardImageAsync();
            return;
        }

        // Text
        if (System.Windows.Clipboard.ContainsText())
        {
            var text = System.Windows.Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
            {
                var json = JsonSerializer.Serialize(new { type = "insert-text", text });
                dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
            }
        }
    }

    private async Task HandleGetClipboardImageAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var image = System.Windows.Clipboard.GetImage();
        if (image == null) return;

        Directory.CreateDirectory(_tempDir);
        var filename = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var filePath = Path.Combine(_tempDir, filename);

        using (var stream = File.Create(filePath))
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            encoder.Save(stream);
        }

        var json = JsonSerializer.Serialize(new { type = "attach-file", filename, content = (string?)null, isBinary = true, filePath });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private async Task HandleSaveDroppedFileAsync(string filename, string base64Data)
    {
        Directory.CreateDirectory(_tempDir);
        var filePath = Path.Combine(_tempDir, filename);

        var bytes = Convert.FromBase64String(base64Data);
        await Task.Run(() => File.WriteAllBytes(filePath, bytes));

        var json = JsonSerializer.Serialize(new { type = "attach-file", filename, content = (string?)null, isBinary = true, filePath });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private async Task HandleGetHistoryAsync()
    {
        var claudeDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        var sessions = new System.Collections.Generic.List<object>();

        if (Directory.Exists(claudeDir))
        {
            var files = Directory.GetFiles(claudeDir, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .Take(30);

            foreach (var file in files)
            {
                var sessionId = System.IO.Path.GetFileNameWithoutExtension(file);
                var preview = "";
                var date = File.GetLastWriteTime(file).ToString("MM/dd/yyyy HH:mm");
                var tokenCount = 0;

                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    string? jsonLine;
                    while ((jsonLine = await sr.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(jsonLine)) continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(jsonLine);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;

                        var entryType = t.GetString();

                        if (entryType == "user" && string.IsNullOrEmpty(preview))
                        {
                            if (!root.TryGetProperty("message", out var msg)) continue;
                            if (!msg.TryGetProperty("content", out var content)) continue;

                            string candidate = "";
                            if (content.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in content.EnumerateArray())
                                {
                                    if (item.TryGetProperty("type", out var itemType) &&
                                        itemType.GetString() == "text" &&
                                        item.TryGetProperty("text", out var textEl))
                                    {
                                        candidate = textEl.GetString() ?? "";
                                        break;
                                    }
                                }
                            }
                            else if (content.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                candidate = content.GetString() ?? "";
                            }

                            candidate = StripLocalCommandCaveat(candidate).Trim();
                            if (string.IsNullOrEmpty(candidate)) continue;

                            preview = candidate.Length > 80 ? candidate.Substring(0, 80) + "…" : candidate;
                        }
                        else if (entryType == "assistant")
                        {
                            if (!root.TryGetProperty("message", out var msg)) continue;
                            if (!msg.TryGetProperty("usage", out var usage)) continue;

                            if (usage.TryGetProperty("input_tokens", out var inputTok))
                                tokenCount += inputTok.GetInt32();
                            if (usage.TryGetProperty("output_tokens", out var outputTok))
                                tokenCount += outputTok.GetInt32();
                        }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(preview))
                {
                    var sidecar = Path.Combine(Path.GetDirectoryName(file)!, $"{sessionId}.branch");
                    var isBranch = File.Exists(sidecar);
                    if (isBranch) preview = "↳ " + preview;
                    sessions.Add(new { id = sessionId, preview, date, tokens = tokenCount, isBranch });
                }
            }
        }

        var json = JsonSerializer.Serialize(new { type = "history", sessions });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private async Task HandleResumeSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (!Directory.Exists(claudeDir))
        {
            OutputLog.Warn("resume ignored: ~/.claude/projects not found");
            return;
        }

        var sourceFile = Directory.GetFiles(claudeDir, $"{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        if (sourceFile == null)
        {
            OutputLog.Warn($"resume ignored: session file {sessionId}.jsonl not found");
            return;
        }

        var msgs = new System.Collections.Generic.List<object>();

        try
        {
            using var fs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string? role = null;
                string? text = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var tEl)) continue;
                    var entryType = tEl.GetString();
                    if (entryType != "user" && entryType != "assistant") continue;
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content)) continue;

                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var itEl) && itEl.GetString() == "text" &&
                                item.TryGetProperty("text", out var txEl))
                            {
                                text = (text ?? "") + (txEl.GetString() ?? "");
                            }
                        }
                    }
                    else if (content.ValueKind == JsonValueKind.String)
                    {
                        text = content.GetString();
                    }

                    if (string.IsNullOrEmpty(text)) continue;
                    role = entryType;
                }
                catch { continue; }

                msgs.Add(new { role, text });
            }
        }
        catch (Exception ex)
        {
            OutputLog.Error($"resume read failed: {ex.Message}");
            return;
        }

        OutputLog.Info($"resume: loaded {msgs.Count} messages from {sessionId}");

        var json = JsonSerializer.Serialize(new
        {
            type = "branched",
            sessionId,
            messages = msgs
        });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private async Task HandleBranchAsync(int msgIndex)
    {
        var currentSessionId = _agentClient.CurrentSessionId;
        if (string.IsNullOrEmpty(currentSessionId))
        {
            OutputLog.Warn("branch ignored: no current session id");
            return;
        }

        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (!Directory.Exists(claudeDir))
        {
            OutputLog.Warn("branch ignored: ~/.claude/projects not found");
            return;
        }

        var sourceFile = Directory.GetFiles(claudeDir, $"{currentSessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        if (sourceFile == null)
        {
            OutputLog.Warn($"branch ignored: session file {currentSessionId}.jsonl not found");
            return;
        }

        var newSessionId = Guid.NewGuid().ToString();
        var newFile = Path.Combine(Path.GetDirectoryName(sourceFile)!, $"{newSessionId}.jsonl");

        var keptLines = new System.Collections.Generic.List<string>();
        var msgs = new System.Collections.Generic.List<object>();
        int visibleCount = 0;

        try
        {
            using var fs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string? role = null;
                string? text = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var tEl)) { keptLines.Add(line); continue; }
                    var entryType = tEl.GetString();
                    if (entryType != "user" && entryType != "assistant") { keptLines.Add(line); continue; }
                    if (!root.TryGetProperty("message", out var msg)) { keptLines.Add(line); continue; }
                    if (!msg.TryGetProperty("content", out var content)) { keptLines.Add(line); continue; }

                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var itEl) && itEl.GetString() == "text" &&
                                item.TryGetProperty("text", out var txEl))
                            {
                                text = (text ?? "") + (txEl.GetString() ?? "");
                            }
                        }
                    }
                    else if (content.ValueKind == JsonValueKind.String)
                    {
                        text = content.GetString();
                    }

                    if (string.IsNullOrEmpty(text)) { keptLines.Add(line); continue; }
                    role = entryType;
                }
                catch { keptLines.Add(line); continue; }

                var rewritten = RewriteSessionIdInLine(line, newSessionId);
                keptLines.Add(rewritten);
                msgs.Add(new { role, text });

                if (visibleCount == msgIndex)
                    break;
                visibleCount++;
            }
        }
        catch (Exception ex)
        {
            OutputLog.Error($"branch read failed: {ex.Message}");
            return;
        }

        try
        {
            File.WriteAllLines(newFile, keptLines);
            var sidecar = Path.Combine(Path.GetDirectoryName(sourceFile)!, $"{newSessionId}.branch");
            File.WriteAllText(sidecar, JsonSerializer.Serialize(new { parent = currentSessionId, msgIndex }));
        }
        catch (Exception ex)
        {
            OutputLog.Error($"branch write failed: {ex.Message}");
            return;
        }

        OutputLog.Info($"branched session {currentSessionId} → {newSessionId} at msgIndex {msgIndex} ({msgs.Count} messages kept)");

        await _agentClient.StopAsync();
        _agentClient.PendingResumeSessionId = newSessionId;

        var json = JsonSerializer.Serialize(new { type = "branched", sessionId = newSessionId, messages = msgs });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private static string StripLocalCommandCaveat(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Claude Code wraps slash-command output in <local-command-*>...</local-command-*> blocks
        // and <command-name>, <command-message>, <command-args>, <local-command-stdout> tags.
        // Strip them so the preview shows the user's actual following content (if any).
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<local-command-[a-z-]+>[\s\S]*?</local-command-[a-z-]+>|<command-[a-z-]+>[\s\S]*?</command-[a-z-]+>|<local-command-[a-z-]+>[\s\S]*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return stripped;
    }

    private static string RewriteSessionIdInLine(string line, string newSessionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return line;
            if (!root.TryGetProperty("sessionId", out _)) return line;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("sessionId"))
                        writer.WriteString("sessionId", newSessionId);
                    else
                        prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return line;
        }
    }

    private async Task HandleDeleteSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (Directory.Exists(claudeDir))
        {
            var files = Directory.GetFiles(claudeDir, $"{sessionId}.jsonl", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
                var sidecar = Path.Combine(Path.GetDirectoryName(file)!, $"{sessionId}.branch");
                try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { }
            }
        }

        var json = JsonSerializer.Serialize(new { type = "session-deleted", sessionId });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private async Task HandleGetDiffAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        var solutionPath = dte?.Solution?.FullName;
        var workDir = string.IsNullOrEmpty(solutionPath) ? null : Path.GetDirectoryName(solutionPath);

        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
        {
            var errJson = JsonSerializer.Serialize(new { type = "diff", stat = "", diff = "No solution open." });
            var disp = System.Windows.Application.Current.Dispatcher;
            disp.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(errJson));
            return;
        }

        var stat = await RunGitAsync(workDir, "diff --stat HEAD");
        var diff = await RunGitAsync(workDir, "diff HEAD");

        if (string.IsNullOrWhiteSpace(stat) && string.IsNullOrWhiteSpace(diff))
            stat = await RunGitAsync(workDir, "status --short");

        var json = JsonSerializer.Serialize(new { type = "diff", stat = stat.Trim(), diff = diff.Trim() });
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(json));
    }

    private static async Task<string> RunGitAsync(string workDir, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await Task.Run(() => proc.WaitForExit(5000));
            return output;
        }
        catch { return ""; }
    }

    private class WebChatMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("model")]
        public string Model { get; set; } = "claude-sonnet-4-6";

        [JsonPropertyName("effort")]
        public string? Effort { get; set; }

        [JsonPropertyName("permissionMode")]
        public string PermissionMode { get; set; } = "ask";

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; set; }

        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }

        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; set; }

        [JsonPropertyName("autoResume")]
        public bool AutoResume { get; set; }

        [JsonPropertyName("autoSave")]
        public string? AutoSave { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("sessionLimit")]
        public decimal? SessionLimit { get; set; }

        [JsonPropertyName("dailyLimit")]
        public decimal? DailyLimit { get; set; }

        [JsonPropertyName("block")]
        public bool Block { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("msgIndex")]
        public int MsgIndex { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("toolUseId")]
        public string? ToolUseId { get; set; }

        [JsonPropertyName("allow")]
        public bool Allow { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("allowSession")]
        public string? AllowSession { get; set; }
    }
}