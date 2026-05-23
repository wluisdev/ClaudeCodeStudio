using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    private static bool _tempCleanupDone;

    public AgentToolWindowControl()
    {
        InitializeComponent();

        Loaded += AgentToolWindowControl_Loaded;
        Unloaded += AgentToolWindowControl_Unloaded;
        IsVisibleChanged += AgentToolWindowControl_IsVisibleChanged;

        AgentToolWindowCommand.ActiveControl = this;

        // One-shot cleanup of stale pasted images/files. Runs once per VS
        // session in the background — these files accumulate in %TEMP%/ClaudeStudio
        // without expiry (every Ctrl+V of an image or binary file drops one),
        // so screenshots of Snipping Tool at ~300KB each can pile up to MB
        // over weeks. 7 days is a comfortable retention.
        if (!_tempCleanupDone)
        {
            _tempCleanupDone = true;
            _ = Task.Run(() => CleanupOldTempFiles(TimeSpan.FromDays(7)));
        }
    }

    private static void CleanupOldTempFiles(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(_tempDir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(_tempDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* file locked or already gone — ignore */ }
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"temp cleanup failed: {ex.Message}");
        }
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
                currentSolutionDir = ResolveCwd(dteForCwd);

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
                // For the history filter we use ResolveWorkspaceCwd (no ActiveDocument
                // / UserProfile fallback) so "no workspace" cleanly means "show all"
                // instead of accidentally filtering by a random doc folder.
                string? workspaceForHistory = null;
                try
                {
                    var dteHist = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                    workspaceForHistory = ResolveWorkspaceCwd(dteHist);
                }
                catch { }
                await HandleGetHistoryAsync(workspaceForHistory, request.ShowAll);
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

            if (request.Type == "request-git-baseline")
            {
                var reqId = request.RequestId ?? "";
                var filePath = request.Path ?? "";
                var (content, err) = await GetGitBaselineAsync(filePath);
                var gitDispatcher = System.Windows.Application.Current.Dispatcher;
                gitDispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "git-baseline-response", requestId = reqId, content, error = err })));
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
                onPermissionRequest: (tool, input, id, cwd) => dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "permission_request", tool, input, id, cwd }))));

            VsStatusBar.Clear();

            dispatcher.Invoke(() =>
            {
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "stream-done" }));
                // Refresh Usage now — at this point claude.exe has flushed the
                // assistant turn to JSONL, so the gated count (assistantTurns > 0)
                // picks up the new session. Doing this on `session` (init) fires
                // too early, before any turn has been written.
                Usage.UsageToolWindowControl.RefreshIfOpen();
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
    /// <summary>
    /// Workspace-only resolver: returns the path of a loaded .sln or Open Folder,
    /// or null. Does NOT fall through to ActiveDocument or UserProfile — those
    /// are agent-cwd fallbacks, not "current workspace" signals. Used by features
    /// that filter by project (history dropdown, Usage panel) so null cleanly
    /// means "no current workspace → show all".
    /// </summary>
    public static string? ResolveWorkspaceCwd(EnvDTE.DTE? dte)
    {
        try
        {
            var solutionPath = dte?.Solution?.FullName;
            if (!string.IsNullOrEmpty(solutionPath))
            {
                var dir = Path.GetDirectoryName(solutionPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return dir;
            }
        }
        catch { }

        try
        {
            var vsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            if (vsSolution != null &&
                vsSolution.GetSolutionInfo(out var solutionDir, out _, out _) == 0 &&
                !string.IsNullOrEmpty(solutionDir) &&
                Directory.Exists(solutionDir))
                return solutionDir;
        }
        catch { }

        return null;
    }

    private static string? ResolveCwd(EnvDTE.DTE? dte)
    {
        // 1. Loaded .sln (most common case)
        try
        {
            var solutionPath = dte?.Solution?.FullName;
            if (!string.IsNullOrEmpty(solutionPath))
            {
                var dir = Path.GetDirectoryName(solutionPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    OutputLog.Info($"cwd resolved via Solution.FullName: {dir}");
                    return dir;
                }
            }
        }
        catch { }

        // 2. Open Folder mode — VS exposes the folder via IVsSolution.GetSolutionInfo
        try
        {
            var vsSolution = Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            if (vsSolution != null &&
                vsSolution.GetSolutionInfo(out var solutionDir, out _, out _) == 0 &&
                !string.IsNullOrEmpty(solutionDir) &&
                Directory.Exists(solutionDir))
            {
                OutputLog.Info($"cwd resolved via IVsSolution (Open Folder): {solutionDir}");
                return solutionDir;
            }
        }
        catch { }

        // 3. Active document's directory
        try
        {
            var activeDocPath = dte?.ActiveDocument?.Path;
            if (!string.IsNullOrEmpty(activeDocPath) && Directory.Exists(activeDocPath))
            {
                OutputLog.Info($"cwd resolved via ActiveDocument: {activeDocPath}");
                return activeDocPath;
            }
        }
        catch { }

        // 4. Last resort: user profile (always writable, never the binary dir)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile) && Directory.Exists(userProfile))
        {
            OutputLog.Info($"cwd resolved via UserProfile fallback: {userProfile}");
            return userProfile;
        }

        return null;
    }
#pragma warning restore VSTHRD010

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

        // Solution-driven reset: also clear the chat UI so the user doesn't see
        // messages from the previous workspace. Without this the agent is killed
        // but the WebView keeps showing the old conversation.
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            dispatcher?.Invoke(() => Browser?.CoreWebView2?.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "reset-chat" })));
        }
        catch { }
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

                string? returnPath = null;
                if (isBinary)
                {
                    // Copy into our temp dir so the path falls under the --add-dir
                    // whitelist passed to claude.exe. Without this, pasted image
                    // files referenced from Pictures/Downloads/etc. fail the
                    // workspace boundary check and Read is denied.
                    try
                    {
                        Directory.CreateDirectory(_tempDir);
                        var destFilename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{filename}";
                        var destPath = Path.Combine(_tempDir, destFilename);
                        File.Copy(filePath, destPath, overwrite: true);
                        returnPath = destPath;
                    }
                    catch (Exception ex)
                    {
                        OutputLog.Warn($"failed to stage pasted file in temp: {ex.Message}");
                        returnPath = filePath; // fallback: claude may still refuse
                    }
                }
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

    private static string EncodeClaudeProjectPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return sb.ToString();
    }

    private async Task HandleGetHistoryAsync(string? workspaceDir, bool showAll)
    {
        var rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        var sessions = new System.Collections.Generic.List<object>();
        var scope = "all";
        var workspaceSubdir = "";

        IEnumerable<string> files = Array.Empty<string>();

        if (Directory.Exists(rootDir))
        {
            if (!showAll && !string.IsNullOrEmpty(workspaceDir))
            {
                workspaceSubdir = Path.Combine(rootDir, EncodeClaudeProjectPath(workspaceDir!));
                if (Directory.Exists(workspaceSubdir))
                {
                    files = Directory.GetFiles(workspaceSubdir, "*.jsonl", SearchOption.TopDirectoryOnly);
                    scope = "workspace";
                }
                // If the encoded folder doesn't exist yet, we leave files empty so the
                // UI can show "no sessions in this workspace" without falling back silently
            }
            else
            {
                // Match UsageReader.ReadAll's enumeration shape: iterate project
                // subdirectories then their direct .jsonl children only. Using
                // AllDirectories picked up nested files (sub-agent transcripts,
                // stray backups) that /usage doesn't count.
                files = Directory
                    .EnumerateDirectories(rootDir)
                    .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly));
            }

            // Parse files in parallel — each JSONL is independent, IO-bound.
            // Order by mtime afterwards so the UI list stays newest-first.
            var orderedFiles = files.OrderByDescending(File.GetLastWriteTime).ToArray();

            var parsed = await Task.WhenAll(orderedFiles.Select(file => Task.Run(() =>
            {
                var sessionId = Path.GetFileNameWithoutExtension(file);
                var lastWrite = File.GetLastWriteTime(file);
                var date = lastWrite.ToString("dd/MM/yyyy HH:mm");
                var preview = "";
                var tokenCount = 0;
                var messageCount = 0;
                var assistantTurns = 0; // gates the session (matches UsageReader logic)

                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    string? jsonLine;
                    while ((jsonLine = sr.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(jsonLine)) continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(jsonLine);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("type", out var t)) continue;

                        var entryType = t.GetString();

                        if (entryType == "user")
                        {
                            messageCount++;

                            if (string.IsNullOrEmpty(preview))
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
                                if (!string.IsNullOrEmpty(candidate))
                                    preview = candidate.Length > 80 ? candidate.Substring(0, 80) + "…" : candidate;
                            }
                        }
                        else if (entryType == "assistant")
                        {
                            messageCount++;

                            if (!root.TryGetProperty("message", out var msg)) continue;

                            // Skip <synthetic> assistant entries (quota warnings, injected
                            // errors). Same gating UsageReader uses so session counts agree.
                            if (msg.TryGetProperty("model", out var modelEl))
                            {
                                var m = modelEl.GetString();
                                if (!string.IsNullOrEmpty(m) && m!.StartsWith("<")) continue;
                            }

                            if (!msg.TryGetProperty("usage", out var usage)) continue;

                            assistantTurns++;

                            if (usage.TryGetProperty("input_tokens", out var inputTok))
                                tokenCount += inputTok.GetInt32();
                            if (usage.TryGetProperty("output_tokens", out var outputTok))
                                tokenCount += outputTok.GetInt32();
                        }
                    }
                }
                catch { }

                return (file, sessionId, preview, date, lastWrite, tokenCount, messageCount, assistantTurns);
            })));

            foreach (var entry in parsed.OrderByDescending(e => e.lastWrite))
            {
                // Match /usage's gating: a session needs at least one real assistant turn.
                // Sessions aborted before any API response are skipped from the list.
                if (string.IsNullOrEmpty(entry.preview) || entry.assistantTurns == 0) continue;
                var sidecar = Path.Combine(Path.GetDirectoryName(entry.file)!, $"{entry.sessionId}.branch");
                var isBranch = File.Exists(sidecar);
                var preview = isBranch ? "↳ " + entry.preview : entry.preview;
                sessions.Add(new { id = entry.sessionId, preview, date = entry.date, tokens = entry.tokenCount, messages = entry.messageCount, isBranch });
            }
        }

        string? workspaceName = null;
        if (scope == "workspace" && !string.IsNullOrEmpty(workspaceDir))
        {
            try { workspaceName = Path.GetFileName(workspaceDir!.TrimEnd('\\', '/')); }
            catch { }
        }

        var json = JsonSerializer.Serialize(new { type = "history", sessions, scope, workspace = workspaceName });
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
        // Wrap everything so we always post SOMETHING back to the WebView —
        // otherwise the "Running git diff…" loader sits there forever on any
        // exception path.
        string stat = "";
        string diff = "";

        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var solutionPath = dte?.Solution?.FullName;
            var workDir = string.IsNullOrEmpty(solutionPath) ? null : Path.GetDirectoryName(solutionPath);

            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            {
                diff = "No solution open.";
            }
            else if (!Directory.Exists(Path.Combine(workDir, ".git")) &&
                     !await IsInsideGitWorktreeAsync(workDir))
            {
                // Not a git repository at all (no .git folder anywhere up the tree).
                // VS's own Git panel shows "git not in use" in this case. We just
                // surface a friendly message instead of running git commands that
                // all fail silently.
                stat = "Not a git repository.";
                diff = "This project is not under git version control.\n\nInitialize a repository in VS to enable diffs.";
            }
            else
            {
                stat = await RunGitAsync(workDir, "diff --stat HEAD");
                diff = await RunGitAsync(workDir, "diff HEAD");

                // Repo sem commits ainda → diff HEAD vazio. Sintetiza "tudo novo".
                if (string.IsNullOrWhiteSpace(stat) && string.IsNullOrWhiteSpace(diff))
                {
                    var hasHead = !string.IsNullOrWhiteSpace(
                        await RunGitAsync(workDir, "rev-parse --verify HEAD"));

                    if (!hasHead)
                    {
                        var allFiles = await RunGitAsync(workDir, "ls-files --others --cached --exclude-standard");
                        var paths = allFiles.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                        var statSb = new StringBuilder();
                        var diffSb = new StringBuilder();
                        int totalLines = 0;

                        foreach (var rel in paths)
                        {
                            try
                            {
                                var full = Path.Combine(workDir, rel);
                                if (!File.Exists(full)) continue;
                                var info = new FileInfo(full);
                                if (info.Length > 256 * 1024) continue;
                                var content = File.ReadAllText(full, Encoding.UTF8);
                                var lines = content.Split('\n');
                                var insertCount = lines.Length;
                                totalLines += insertCount;
                                statSb.AppendLine($" {rel} | {insertCount} +");
                                diffSb.AppendLine($"diff --git a/{rel} b/{rel}");
                                diffSb.AppendLine("new file mode 100644");
                                diffSb.AppendLine("--- /dev/null");
                                diffSb.AppendLine($"+++ b/{rel}");
                                foreach (var line in lines)
                                    diffSb.AppendLine("+" + line.TrimEnd('\r'));
                            }
                            catch { }
                        }

                        if (statSb.Length > 0)
                        {
                            statSb.AppendLine($" {paths.Length} files (no initial commit yet), {totalLines} insertions(+)");
                            stat = statSb.ToString();
                            diff = diffSb.ToString();
                        }
                        else
                        {
                            stat = "Repository initialized but empty.";
                            diff = "No tracked or untracked files to show.";
                        }
                    }
                    else
                    {
                        stat = await RunGitAsync(workDir, "status --short");
                        if (string.IsNullOrWhiteSpace(stat))
                            diff = "No changes since the last commit.";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"HandleGetDiffAsync failed: {ex.Message}");
            diff = $"Error computing diff: {ex.Message}";
        }
        finally
        {
            var json = JsonSerializer.Serialize(new { type = "diff", stat = (stat ?? "").Trim(), diff = (diff ?? "").Trim() });
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                dispatcher?.Invoke(() => Browser?.CoreWebView2?.PostWebMessageAsJson(json));
            }
            catch (Exception ex)
            {
                OutputLog.Warn($"diff post failed: {ex.Message}");
            }
        }
    }

    private static async Task<bool> IsInsideGitWorktreeAsync(string workDir)
    {
        // git emits "true" / "false" / nothing depending on context.
        var result = await RunGitAsync(workDir, "rev-parse --is-inside-work-tree");
        return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
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
                CreateNoWindow = true,
                // git emits UTF-8 by default; without this hint the host process
                // would decode with the OS ANSI codepage and mangle non-ASCII
                // bytes (e.g. "Aplicação" → "AplicaÃ§Ã£o").
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await Task.Run(() => proc.WaitForExit(5000));
            return output;
        }
        catch { return ""; }
    }

    private static async Task<(string? content, string? error)> GetGitBaselineAsync(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return (null, "file not found");

            // Walk up to find .git
            var dir = Path.GetDirectoryName(filePath);
            string? repoRoot = null;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    repoRoot = dir;
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
            if (repoRoot == null) return (null, "not a git repo");

            var rel = filePath.Substring(repoRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
            var psi = new ProcessStartInfo("git", $"show HEAD:\"{rel}\"")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => proc.WaitForExit(2000));
            if (!exited) { try { proc.Kill(); } catch { } return (null, "git timeout"); }
            if (proc.ExitCode != 0)
                return (null, string.IsNullOrWhiteSpace(err) ? "git failed" : err.Trim());
            return (output, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
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

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        [JsonPropertyName("showAll")]
        public bool ShowAll { get; set; }
    }
}