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
    private FileSystemWatcher? _claudeJsonWatcher;
    private bool _lastSignedInStatus;
    private System.Threading.Timer? _claudeJsonDebounceTimer;
    private FileSystemWatcher? _trustedWorkspacesWatcher;
    private System.Threading.Timer? _trustedWorkspacesDebounceTimer;

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
        // The webview tracks visibility for the caption attention markers
        // (V13): "done" only makes sense while hidden, and clears on return.
        if (Browser?.CoreWebView2 != null)
        {
            try
            {
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "visibility-changed", visible = (bool)e.NewValue }));
            }
            catch { /* webview tearing down */ }
        }

        if ((bool)e.NewValue && Browser?.CoreWebView2 != null)
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(FocusTextarea));

            // Defensive refresh: covers the case where the user finished
            // `claude login`/`logout` in an external terminal while the tool
            // window was hidden, and the FileSystemWatcher missed the event
            // (CLI write patterns vary; safer to also re-poll on focus return).
            _ = Task.Run(() => { try { SendAccountInfo(); } catch { } });
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
            StartClaudeJsonWatcher();  // seeds _lastSignedInStatus before first SendAccountInfo
            StartTrustedWorkspacesWatcher();
            SendAccountInfo();
            SendCwdInfo();
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

    private void SendAccountInfo()
    {
        try
        {
            // File reads are safe on any thread.
            var info = ReadAccountInfo();
            var nowSignedIn = IsSignedIn();
            var prevSignedIn = _lastSignedInStatus;
            _lastSignedInStatus = nowSignedIn;

            // Browser is a WPF control — touching it from a threadpool thread
            // (watcher debounce timer, IsVisibleChanged Task.Run) throws
            // "calling thread cannot access this object". Marshal everything
            // that touches Browser to the UI thread.
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
            {
                if (Browser?.CoreWebView2 == null) return;
                Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(info));
                if (nowSignedIn && !prevSignedIn)
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "claude-login-completed" }));
                }
            });
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"account info read failed: {ex.Message}");
        }
    }

    public void SendCwdInfo()
    {
        SendCwdInfoCore(retryAttempt: 0);
    }

    public void SendMcpTrustCheck(string? workingDir)
    {
        try
        {
            if (Browser?.CoreWebView2 == null) return;
            var pending = CollectUntrustedMcpServers(workingDir);
            if (pending.Count == 0) return;
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "mcp-trust-required", servers = pending })));
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"mcp trust scan (boot) failed: {ex.Message}");
        }
    }

    // Open Folder in VS populates IVsSolution.GetSolutionInfo asynchronously, sometimes
    // after the 1500ms ScheduleReset delay has elapsed. When the initial resolve returns
    // null but we're not at the entry point (NavigationCompleted on a fresh tool window),
    // schedule a couple of retries with backoff so the cwdbar/trust gate catches up.
    private void SendCwdInfoCore(int retryAttempt)
    {
        try
        {
            if (Browser?.CoreWebView2 == null) return;

            string? cwd = null;
#pragma warning disable VSTHRD010
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                cwd = ResolveWorkspaceCwd(dte);
            }
            catch { }
#pragma warning restore VSTHRD010

            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "cwd-info", path = cwd })));

            // Trust gate: if a workspace is open and not yet trusted, prompt the
            // user before the agent does anything in it. Skipped when there's no
            // workspace (cwd null) — UserProfile fallback isn't covered by trust.
            var workspaceTrusted = string.IsNullOrEmpty(cwd) || Trust.TrustedWorkspacesStore.IsTrusted(cwd);
            if (!workspaceTrusted)
            {
                var parent = TryGetParent(cwd!);
                var parentIsBlocked = IsBlockedRoot(parent);
                var riskWarning = GetRiskWarning(cwd);
                dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "trust-required", path = cwd, parent, parentIsBlocked, riskWarning })));
            }
            else
            {
                dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "workspace-trusted", path = cwd })));

                // Only scan MCPs once the workspace is past the gate — chaining
                // both modals at once would be confusing.
                SendMcpTrustCheck(cwd);
            }

            // Retry if we got nothing — Open Folder may still be initializing.
            // Two retries (3s then 5s) cover slow disks without spamming.
            if (cwd == null && retryAttempt < 2)
            {
                var nextDelayMs = retryAttempt == 0 ? 3000 : 5000;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(nextDelayMs);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => SendCwdInfoCore(retryAttempt + 1));
                });
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"cwd info send failed: {ex.Message}");
        }
    }

    private static string? TryGetParent(string path)
    {
        try
        {
            var p = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(p) ? null : p;
        }
        catch { return null; }
    }

    // Exact match against %USERPROFILE%. Subfolders (e.g. C:\Users\was\source\repos\Foo)
    // are real workspaces and go through the normal trust flow — trusting the root
    // would wide-trust everything under home due to prefix matching in IsTrusted.
    // Used by: the no-workspace send block, and IsBlockedRoot below.
    private static bool IsHomeDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return PathEquals(path, home);
    }

    // Drive root like "C:\" / "D:\". Trusting here wide-trusts the entire drive
    // (Windows, Program Files, every future folder).
    private static bool IsDriveRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(path!);
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) && PathEquals(full, root);
        }
        catch { return false; }
    }

    // Parent of %USERPROFILE%, usually "C:\Users". Trusting here covers every
    // user profile on the machine.
    private static bool IsUsersContainer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return false;
            var parent = Directory.GetParent(home)?.FullName;
            return PathEquals(path, parent);
        }
        catch { return false; }
    }

    // Used to suppress the "Trust parent" button — escalating to any of these
    // roots via the parent shortcut would re-introduce the wide-trust trap.
    private static bool IsBlockedRoot(string? path) =>
        IsHomeDirectory(path) || IsDriveRoot(path) || IsUsersContainer(path);

    // Returns a key describing why a path is high-risk, or null when it's a
    // regular folder. Used by the UI to render a contextual warning banner
    // inside the trust modal (home is blocked upstream, so it's not surfaced
    // here — the user gets the no-workspace card instead).
    private static string? GetRiskWarning(string? path)
    {
        if (IsDriveRoot(path)) return "drive-root";
        if (IsUsersContainer(path)) return "users-container";
        return null;
    }

    private static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a!).TrimEnd('\\', '/'),
                Path.GetFullPath(b!).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<object> CollectUntrustedMcpServers(string? projectDir)
    {
        var pending = new List<object>();
        try
        {
            CollectFromScope(Mcp.McpScope.User, null, pending);
            if (!string.IsNullOrEmpty(projectDir))
                CollectFromScope(Mcp.McpScope.Project, projectDir, pending);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"mcp trust scan failed: {ex.Message}");
        }
        return pending;
    }

    private static void CollectFromScope(Mcp.McpScope scope, string? projectDir, List<object> pending)
    {
        List<Mcp.McpServer> servers;
        try { servers = Mcp.McpConfigStore.Load(scope, projectDir); }
        catch { return; }

        var projectPath = scope == Mcp.McpScope.Project ? projectDir : null;

        foreach (var s in servers)
        {
            if (s.Disabled) continue;
            var hash = Trust.McpServerHash.Compute(s);
            if (Trust.TrustedMcpServersStore.IsTrusted(s.Name, scope, hash, projectPath)) continue;

            pending.Add(new
            {
                name = s.Name,
                scope = scope == Mcp.McpScope.Project ? "project" : "user",
                transport = s.Transport.ToString().ToLowerInvariant(),
                summary = Trust.McpServerHash.ShortSummary(s),
                hash,
                projectPath
            });
        }
    }

    private static string ClaudeJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude.json");

    // Lightweight check used by the pre-flight gate. Mirrors ReadAccountInfo's
    // signed-in determination (presence of oauthAccount with any identity field)
    // but avoids allocating the full payload.
    private static bool IsSignedIn()
    {
        var path = ClaudeJsonPath;
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("oauthAccount", out var acct) ||
                acct.ValueKind != JsonValueKind.Object)
                return false;
            return TryGetString(acct, "organizationName") != null
                || TryGetString(acct, "emailAddress") != null
                || TryGetString(acct, "email") != null
                || TryGetString(acct, "displayName") != null;
        }
        catch { return false; }
    }

    private static object ReadAccountInfo()
    {
        var path = ClaudeJsonPath;

        if (!File.Exists(path))
            return new { type = "account-info", signedIn = false };

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? organizationName = null;
            string? email = null;
            string? accountDisplayName = null;

            if (root.TryGetProperty("oauthAccount", out var acct) && acct.ValueKind == JsonValueKind.Object)
            {
                organizationName = TryGetString(acct, "organizationName");
                email = TryGetString(acct, "emailAddress") ?? TryGetString(acct, "email");
                accountDisplayName = TryGetString(acct, "displayName");
            }

            // No oauthAccount object — treat as signed out (CLI hasn't logged in yet).
            if (organizationName == null && email == null && accountDisplayName == null)
                return new { type = "account-info", signedIn = false };

            // Personal Claude Pro accounts use email-like org names; drop those so
            // the cascade in JS can fall back to a real display name.
            if (organizationName != null && organizationName.Contains("@"))
                organizationName = null;

            // Plan comes from oauthAccount.organizationType (e.g. "claude_pro",
            // "claude_max_5x"). Map known values; capitalize anything new as a
            // fallback so a future plan still shows something readable.
            var orgType = TryGetString(acct, "organizationType");
            var plan = MapPlan(orgType);

            return new
            {
                type = "account-info",
                signedIn = true,
                organizationName,
                accountDisplayName,
                email,
                plan
            };
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"~/.claude.json parse failed: {ex.Message}");
            return new { type = "account-info", signedIn = false };
        }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static bool TryGetBool(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    private static string MapPlan(string? orgType)
    {
        if (string.IsNullOrEmpty(orgType)) return "Free";
        switch (orgType)
        {
            case "claude_pro": return "Pro";
            case "claude_max": return "Max";
            case "claude_max_5x": return "Max 5x";
            case "claude_max_20x": return "Max 20x";
            case "claude_team": return "Team";
            case "claude_enterprise": return "Enterprise";
            case "claude_free": return "Free";
        }
        // Unknown: strip "claude_" prefix and Title-Case underscores.
        var s = orgType!.StartsWith("claude_", StringComparison.OrdinalIgnoreCase)
            ? orgType.Substring(7)
            : orgType;
        var parts = s.Split('_');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        }
        return string.Join(" ", parts);
    }

    private void AgentToolWindowControl_Unloaded(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        VSColorTheme.ThemeChanged -= OnVsThemeChanged;
        // Intentionally NOT calling StopClaudeJsonWatcher() — Unloaded fires on
        // tool window tab switches, but Loaded is guarded by _initialized so the
        // watcher would never restart. Keep it alive for the control's lifetime.
    }

    // Watches ~/.claude.json so the titlebar/auth state updates when the user
    // signs in (or out) via an external `claude login` / `claude logout`. The
    // file gets multiple writes per save, so we debounce ~500ms before re-reading.
    private void StartClaudeJsonWatcher()
    {
        try
        {
            if (_claudeJsonWatcher != null) return;

            var dir = Path.GetDirectoryName(ClaudeJsonPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _lastSignedInStatus = IsSignedIn();

            // Watch the whole user-profile dir without a Filter — the CLI may
            // write via temp+rename, delete+recreate, or in-place, and filter
            // patterns on dot-prefixed names have been unreliable. We re-check
            // the FullPath inside the handler instead.
            _claudeJsonWatcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.FileName
                    | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            var targetPath = ClaudeJsonPath;
            FileSystemEventHandler onChange = (_, args) =>
            {
                if (string.Equals(args.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    DebouncedReloadAccountInfo();
            };
            _claudeJsonWatcher.Changed += onChange;
            _claudeJsonWatcher.Created += onChange;
            _claudeJsonWatcher.Deleted += onChange;
            _claudeJsonWatcher.Renamed += (_, args) =>
            {
                if (string.Equals(args.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args.OldFullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    DebouncedReloadAccountInfo();
            };
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"~/.claude.json watcher setup failed: {ex.Message}");
        }
    }

    private void StopClaudeJsonWatcher()
    {
        try
        {
            _claudeJsonWatcher?.Dispose();
            _claudeJsonWatcher = null;
            _claudeJsonDebounceTimer?.Dispose();
            _claudeJsonDebounceTimer = null;
        }
        catch { }
    }

    private void DebouncedReloadAccountInfo()
    {
        try
        {
            _claudeJsonDebounceTimer?.Dispose();
            _claudeJsonDebounceTimer = new System.Threading.Timer(
                _ => { try { SendAccountInfo(); } catch { } },
                null, 500, System.Threading.Timeout.Infinite);
        }
        catch { }
    }

    private static string TrustedWorkspacesJsonPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeStudio",
        "trusted-workspaces.json");

    // Watches %APPDATA%/ClaudeStudio/trusted-workspaces.json so the trust gate
    // refreshes when the file is edited externally (another VS instance, manual
    // edit, sync tool). Mirrors StartClaudeJsonWatcher 1:1 — same long-lived
    // pattern, same broad NotifyFilter, same FullPath equality check.
    private void StartTrustedWorkspacesWatcher()
    {
        try
        {
            if (_trustedWorkspacesWatcher != null) return;

            var dir = Path.GetDirectoryName(TrustedWorkspacesJsonPath);
            if (string.IsNullOrEmpty(dir)) return;

            // Directory only gets created on first Trust() call; create it now so
            // the watcher can attach even before the user has trusted anything.
            try { Directory.CreateDirectory(dir!); } catch { }
            if (!Directory.Exists(dir)) return;

            _trustedWorkspacesWatcher = new FileSystemWatcher(dir!)
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.FileName
                    | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            var targetPath = TrustedWorkspacesJsonPath;
            FileSystemEventHandler onChange = (_, args) =>
            {
                if (string.Equals(args.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    DebouncedReloadTrustGate();
            };
            _trustedWorkspacesWatcher.Changed += onChange;
            _trustedWorkspacesWatcher.Created += onChange;
            _trustedWorkspacesWatcher.Deleted += onChange;
            _trustedWorkspacesWatcher.Renamed += (_, args) =>
            {
                if (string.Equals(args.FullPath, targetPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args.OldFullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    DebouncedReloadTrustGate();
            };
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"trusted-workspaces watcher setup failed: {ex.Message}");
        }
    }

    // Called from McpToolWindowControl (and potentially other VS tool windows) to
    // surface the trusted workspaces modal from outside the chat. Trust is global
    // across all Claude usage, so it deserves access points outside the agent tab.
    public void ShowTrustedWorkspacesModal()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
            {
                if (Browser?.CoreWebView2 == null) return;
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "open-trusted-workspaces-modal" }));
            });
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"open trusted-workspaces modal failed: {ex.Message}");
        }
    }

    private void SendTrustedWorkspacesList()
    {
        try
        {
            var paths = Trust.TrustedWorkspacesStore.GetAll()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            dispatcher.Invoke(() =>
            {
                if (Browser?.CoreWebView2 == null) return;
                Browser.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(new { type = "trusted-workspaces-list", paths }));
            });
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"trusted-workspaces list send failed: {ex.Message}");
        }
    }

    private void DebouncedReloadTrustGate()
    {
        try
        {
            _trustedWorkspacesDebounceTimer?.Dispose();
            _trustedWorkspacesDebounceTimer = new System.Threading.Timer(
                _ =>
                {
                    // SendCwdInfo touches DTE (Package.GetGlobalService) — must run
                    // on the UI thread. Dispatch from the threadpool callback.
                    // SendTrustedWorkspacesList keeps the management modal in sync
                    // if it's currently open (cheap no-op if it isn't).
                    try
                    {
                        var dispatcher = System.Windows.Application.Current.Dispatcher;
                        dispatcher.InvokeAsync(() =>
                        {
                            try { SendCwdInfo(); } catch { }
                            try { SendTrustedWorkspacesList(); } catch { }
                        });
                    }
                    catch { }
                },
                null, 500, System.Threading.Timeout.Infinite);
        }
        catch { }
    }

    // Opens a visible cmd window running `claude login` so the user can complete
    // the OAuth flow. cmd /K keeps the window open after the command exits so
    // any error message is readable. The watcher picks up the ~/.claude.json
    // update and posts claude-login-completed when oauthAccount appears.
    private void StartClaudeLogin()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/K \"claude login\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            Process.Start(psi);

            if (Browser?.CoreWebView2 != null)
            {
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                dispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "claude-login-started" })));
            }
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"claude login spawn failed: {ex.Message}");
        }
    }

    // Opens a visible cmd window running the documented npm install for Claude
    // Code. cmd /K keeps the window open so the user can read the result (and any
    // error if npm isn't present). After it finishes they re-send their message;
    // the agent re-runs FindClaudeExe on the next request. A VS restart may be
    // needed for a freshly-updated PATH to be visible to the agent.
    private void StartClaudeInstall()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/K \"npm install -g @anthropic-ai/claude-code\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"claude install spawn failed: {ex.Message}");
        }
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

            if (request.Type == "refresh-cwd")
            {
                SendCwdInfo();
                return;
            }

            if (request.Type == "start-claude-login")
            {
                StartClaudeLogin();
                return;
            }

            if (request.Type == "start-claude-install")
            {
                StartClaudeInstall();
                return;
            }

            if (request.Type == "trust-workspace")
            {
                if (!string.IsNullOrWhiteSpace(request.Path))
                {
                    Trust.TrustedWorkspacesStore.Trust(request.Path);
                    OutputLog.Info($"workspace trusted: {request.Path}");
                    var trustDispatcher = System.Windows.Application.Current.Dispatcher;
                    trustDispatcher.Invoke(() =>
                        Browser.CoreWebView2.PostWebMessageAsJson(
                            JsonSerializer.Serialize(new { type = "workspace-trusted", path = request.Path })));
                    // Workspace just cleared the gate — surface any pending MCP servers now.
                    SendMcpTrustCheck(currentSolutionDir);
                }
                return;
            }

            if (request.Type == "untrust-workspace")
            {
                OutputLog.Info($"workspace untrusted (declined): {request.Path}");
                var untrustDispatcher = System.Windows.Application.Current.Dispatcher;
                untrustDispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "workspace-untrusted", path = request.Path })));
                return;
            }

            if (request.Type == "get-trusted-workspaces")
            {
                SendTrustedWorkspacesList();
                return;
            }

            if (request.Type == "remove-trusted-workspace")
            {
                if (!string.IsNullOrWhiteSpace(request.Path))
                {
                    Trust.TrustedWorkspacesStore.Untrust(request.Path);
                    OutputLog.Info($"workspace removed from trusted list: {request.Path}");
                    SendTrustedWorkspacesList();
                    // If the removed entry was covering the active cwd, the trust
                    // gate needs to re-evaluate so the modal can reappear.
                    SendCwdInfo();
                }
                return;
            }

            if (request.Type == "trust-mcp-servers")
            {
                if (request.Servers != null)
                {
                    foreach (var entry in request.Servers)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Hash)) continue;
                        var scope = string.Equals(entry.Scope, "project", StringComparison.OrdinalIgnoreCase)
                            ? Mcp.McpScope.Project
                            : Mcp.McpScope.User;

                        if (string.Equals(entry.Action, "skip", StringComparison.OrdinalIgnoreCase))
                        {
                            Trust.TrustedMcpServersStore.SkipForSession(entry.Name, scope, entry.Hash, entry.ProjectPath);
                            OutputLog.Info($"mcp skipped for session: {entry.Scope}/{entry.Name}");
                        }
                        else
                        {
                            Trust.TrustedMcpServersStore.Trust(entry.Name, scope, entry.Hash, entry.ProjectPath);
                            OutputLog.Info($"mcp trusted: {entry.Scope}/{entry.Name}");
                        }
                    }
                }
                var mcpTrustDispatcher = System.Windows.Application.Current.Dispatcher;
                mcpTrustDispatcher.Invoke(() =>
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "mcp-trust-completed" })));
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

            if (request.Type == "ask-answer")
            {
                if (string.IsNullOrEmpty(request.ToolUseId))
                {
                    OutputLog.Warn("ask-answer missing toolUseId");
                    return;
                }
                await _agentClient.SendAskAnswerAsync(request.ToolUseId, request.Answers ?? "", request.Dismissed);
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
                await OpenFileInEditorAsync(request.Path, request.StartLine ?? 0, request.EndLine ?? 0,
                    _lastWorkingDir ?? currentSolutionDir);
                return;
            }

            if (request.Type == "open-vs-diff")
            {
                await HandleOpenVsDiffAsync(request.Path ?? "", request.ToolName ?? "");
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

            if (request.Type == "rewind")
            {
                await HandleRewindAsync(request.MsgIndex, request.DryRun);
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

            // Pre-flight auth check: if ~/.claude.json has no oauthAccount, the
            // agent will spawn and fail with a generic stream error. Surface an
            // actionable CTA inline instead of letting that happen.
            if (!IsSignedIn())
            {
                var authDispatcher = System.Windows.Application.Current.Dispatcher;
                authDispatcher.Invoke(() =>
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "auth-required" }));
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "stream-done" }));
                });
                return;
            }

            // No-workspace block: when ResolveCwd falls through to UserProfile, there's
            // no real workspace open. Showing the trust modal here would let the user
            // accidentally wide-trust their entire home (prefix match in IsTrusted).
            // Block with an inline message that nudges them to open a folder/solution.
            if (IsHomeDirectory(workingDir))
            {
                var noWorkspaceDispatcher = System.Windows.Application.Current.Dispatcher;
                noWorkspaceDispatcher.Invoke(() =>
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "no-workspace", path = workingDir }));
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "stream-done" }));
                });
                return;
            }

            // Trust gate (defense-in-depth — the boot-time prompt already covers the
            // common case, but a stale UI / cleared settings could let an untrusted
            // send slip through). Block before spawning the agent.
            if (!string.IsNullOrEmpty(workingDir) && !Trust.TrustedWorkspacesStore.IsTrusted(workingDir))
            {
                var parent = TryGetParent(workingDir!);
                var parentIsBlocked = IsBlockedRoot(parent);
                var riskWarning = GetRiskWarning(workingDir);
                var trustGateDispatcher = System.Windows.Application.Current.Dispatcher;
                trustGateDispatcher.Invoke(() =>
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "trust-required", path = workingDir, parent, parentIsBlocked, riskWarning }));
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "stream-done" }));
                });
                return;
            }

            // MCP trust gate — surface enabled servers the user hasn't approved (or
            // whose payload changed) before spawning the agent. The user marks which
            // ones to trust; sending again proceeds normally.
            var pendingMcp = CollectUntrustedMcpServers(workingDir);
            if (pendingMcp.Count > 0)
            {
                var mcpGateDispatcher = System.Windows.Application.Current.Dispatcher;
                mcpGateDispatcher.Invoke(() =>
                {
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "mcp-trust-required", servers = pendingMcp }));
                    Browser.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "stream-done" }));
                });
                return;
            }

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

            // User-configurable claude settings (V7). Booleans default to true
            // when the webview omits them (older payloads).
            _agentClient.ClaudeSettings = new ClaudeStudioShared.ClaudeSettings
            {
                CoAuthoredBy = request.CoAuthoredBy ?? true,
                CleanupPeriodDays = request.CleanupPeriodDays,
                AutoCompact = request.AutoCompact ?? true
            };

            // IDE selection context (V11): when the user has text highlighted in
            // the active editor, attach it to the outgoing message in
            // <ide_selection> tags. Only what claude receives changes — the user
            // bubble was already rendered from the typed text.
            var ideSelection = await CaptureIdeSelectionAsync(workingDir);

            var dispatcher = System.Windows.Application.Current.Dispatcher;

            await _agentClient.AskStreamingAsync(request.Text + ideSelection, request.Model, request.Effort, request.PermissionMode,
                chunk => dispatcher.Invoke(() =>
                {
                    const string notFound = "CLAUDE_NOT_FOUND::";
                    if (chunk != null && chunk.StartsWith(notFound, StringComparison.Ordinal))
                        Browser.CoreWebView2.PostWebMessageAsJson(
                            JsonSerializer.Serialize(new { type = "claude-not-found", detail = chunk.Substring(notFound.Length) }));
                    else
                        Browser.CoreWebView2.PostWebMessageAsJson(
                            JsonSerializer.Serialize(new { type = "chunk", text = chunk }));
                }),
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
                        JsonSerializer.Serialize(new { type = "permission_request", tool, input, id, cwd }))),
                onDiagnosticsRequest: (filePath, requestId) => _ = HandleDiagnosticsRequestAsync(filePath, requestId));

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
                // Open Folder mode: Solution.FullName is the folder itself, not a
                // .sln file — applying Path.GetDirectoryName would jump up one level.
                if (Directory.Exists(solutionPath))
                    return solutionPath;
                if (File.Exists(solutionPath))
                {
                    var dir = Path.GetDirectoryName(solutionPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        return dir;
                }
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
        // 1. Loaded .sln or Open Folder (most common case)
        try
        {
            var solutionPath = dte?.Solution?.FullName;
            if (!string.IsNullOrEmpty(solutionPath))
            {
                // Open Folder mode: Solution.FullName is the folder itself, not a
                // .sln file — applying Path.GetDirectoryName would jump up one level.
                if (Directory.Exists(solutionPath))
                {
                    OutputLog.Info($"cwd resolved via Solution.FullName (folder): {solutionPath}");
                    return solutionPath;
                }
                if (File.Exists(solutionPath))
                {
                    var dir = Path.GetDirectoryName(solutionPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        OutputLog.Info($"cwd resolved via Solution.FullName: {dir}");
                        return dir;
                    }
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

        SendCwdInfo();
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

    // Opens a file in the VS editor, optionally navigating to a 1-based line
    // range (V11 file-links). Relative paths — claude's markdown links are
    // relative to its working directory per the appended system prompt —
    // resolve against the agent cwd, falling back to the solution directory.
    private async Task OpenFileInEditorAsync(string? path, int startLine, int endLine, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var candidate = path!.Replace('/', '\\');
        if (!System.IO.Path.IsPathRooted(candidate))
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            candidate = System.IO.Path.Combine(baseDir, candidate);
        }
        if (!File.Exists(candidate))
        {
            OutputLog.Warn($"open-file: not found: {candidate}");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (dte == null) return;
        try
        {
            dte.ItemOperations.OpenFile(candidate);
            if (startLine > 0 && dte.ActiveDocument?.Selection is EnvDTE.TextSelection sel)
            {
                sel.MoveToLineAndOffset(startLine, 1);
                if (endLine > startLine)
                    sel.MoveToLineAndOffset(endLine, 1, true);
                sel.EndOfLine(true);
            }
        }
        catch (Exception ex) { OutputLog.Warn($"open-file: navigate failed: {ex.Message}"); }
    }

    // Captures the active editor selection (if any) wrapped in <ide_selection>
    // tags for appending to the outgoing user message (V11). The appended
    // system prompt tells the model what the tag means; the webview already
    // rendered the user bubble from the typed text, so the UI stays clean.
    private async Task<string> CaptureIdeSelectionAsync(string? baseDir)
    {
        const int maxChars = 8000;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var doc = dte?.ActiveDocument;
            var selection = doc?.Selection as EnvDTE.TextSelection;
            var code = selection?.Text;
            if (string.IsNullOrWhiteSpace(code))
                return "";

            var filePath = doc?.FullName ?? "";
            var startLine = selection?.TopLine ?? 0;
            var endLine = selection?.BottomLine ?? 0;

            var displayPath = (!string.IsNullOrEmpty(baseDir) && filePath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                ? filePath.Substring(baseDir!.Length).TrimStart('\\', '/')
                : filePath;

            if (code!.Length > maxChars)
                code = code.Substring(0, maxChars) + "\n… [selection truncated]";

            var lineInfo = startLine == endLine ? $"line {startLine}" : $"lines {startLine}-{endLine}";
            return $"\n\n<ide_selection>The user selected the following from {displayPath} ({lineInfo}):\n{code.TrimEnd('\r', '\n')}\n</ide_selection>";
        }
        catch { return ""; }
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

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lang = GetLanguageId(ext);
        if (string.IsNullOrEmpty(lang))
        {
            // Extension-less or unmapped files — fall back to filename heuristics
            // (Dockerfile, Makefile, etc.) so the fence still gets a language ID.
            var fname = Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(fname))
            {
                if (fname.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
                    fname.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
                    lang = "dockerfile";
                else if (fname.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
                         fname.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase))
                    lang = "makefile";
                else if (fname.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                    lang = "cmake";
            }
        }
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
        ".java" => "java", ".kt" => "kotlin", ".scala" => "scala",
        ".cpp" or ".cc" or ".cxx" => "cpp", ".c" => "c", ".h" => "c", ".hpp" => "cpp",
        ".go" => "go", ".rs" => "rust", ".swift" => "swift",
        ".rb" => "ruby", ".php" => "php", ".lua" => "lua", ".dart" => "dart",
        ".r" => "r", ".m" or ".mm" => "objectivec",
        ".html" or ".htm" => "html", ".css" => "css",
        ".scss" => "scss", ".sass" => "sass", ".less" => "less",
        ".xml" or ".xaml" => "xml", ".json" => "json",
        ".yaml" or ".yml" => "yaml", ".toml" => "toml",
        ".sql" => "sql",
        ".sh" or ".bash" => "bash", ".zsh" => "zsh", ".fish" => "fish",
        ".ps1" or ".psm1" => "powershell",
        ".bat" or ".cmd" => "batch",
        ".md" => "markdown", ".rst" => "rst", ".tex" => "latex",
        ".ex" or ".exs" => "elixir", ".erl" => "erlang",
        ".zig" => "zig", ".nim" => "nim", ".v" => "v",
        ".clj" or ".cljs" => "clojure", ".elm" => "elm",
        ".hs" => "haskell", ".ml" or ".mli" => "ocaml",
        ".pl" or ".pm" => "perl", ".groovy" => "groovy",
        ".dockerfile" => "dockerfile", ".gradle" => "groovy",
        ".tf" => "terraform", ".graphql" or ".gql" => "graphql",
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

    // Native file rewind. msgIndex here is the UI's *user-message ordinal* (the
    // Nth user message), which maps 1:1 to the user entries in the JSONL — unlike
    // the mixed user+assistant index used by branching, which drifts when an
    // assistant turn produces multiple text entries. We collect the uuids of user
    // entries that carry text (real messages, not tool_result entries) and index
    // into them. dryRun previews the diff stats; otherwise files are reverted.
    private async Task HandleRewindAsync(int msgIndex, bool dryRun)
    {
        void PostRewindError(string m)
        {
            var d = System.Windows.Application.Current.Dispatcher;
            d.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "rewind-error", message = m })));
        }

        var currentSessionId = _agentClient.CurrentSessionId;
        if (string.IsNullOrEmpty(currentSessionId)) { PostRewindError("No active session."); return; }

        var claudeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var sourceFile = Directory.Exists(claudeDir)
            ? Directory.GetFiles(claudeDir, $"{currentSessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (sourceFile == null) { PostRewindError("Session transcript not found."); return; }

        var userUuids = new System.Collections.Generic.List<string>();
        try
        {
            using var fs = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var tEl) || tEl.GetString() != "user") continue;
                    if (!root.TryGetProperty("message", out var msg)) continue;
                    if (!msg.TryGetProperty("content", out var content)) continue;

                    // Real user messages carry text; tool_result entries (also
                    // type:"user") carry a tool_result block with no text → skip.
                    string? text = null;
                    if (content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in content.EnumerateArray())
                            if (item.TryGetProperty("type", out var itEl) && itEl.GetString() == "text" &&
                                item.TryGetProperty("text", out var txEl))
                                text = (text ?? "") + (txEl.GetString() ?? "");
                    }
                    else if (content.ValueKind == JsonValueKind.String)
                    {
                        text = content.GetString();
                    }
                    if (string.IsNullOrEmpty(text)) continue;

                    if (root.TryGetProperty("uuid", out var uEl) && uEl.GetString() is { } u)
                        userUuids.Add(u);
                }
                catch { continue; }
            }
        }
        catch (Exception ex)
        {
            OutputLog.Error($"rewind walk failed: {ex.Message}");
            PostRewindError("Could not read transcript.");
            return;
        }

        if (msgIndex < 0 || msgIndex >= userUuids.Count)
        {
            OutputLog.Warn($"rewind: user ordinal {msgIndex} out of range (found {userUuids.Count} user messages)");
            PostRewindError("Could not locate that message in the transcript.");
            return;
        }
        var uuid = userUuids[msgIndex];

        var (resultJson, error) = await _agentClient.RewindAsync(uuid!, dryRun);
        if (error != null || resultJson == null)
        {
            PostRewindError(error ?? "Rewind failed.");
            return;
        }

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        dispatcher.Invoke(() => Browser.CoreWebView2.PostWebMessageAsJson(
            JsonSerializer.Serialize(new
            {
                type = dryRun ? "rewind-preview" : "rewind-done",
                msgIndex,
                result = JsonSerializer.Deserialize<JsonElement>(resultJson)
            })));
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

    // Opens a native VS side-by-side diff tab for an edited file: left = the git
    // HEAD baseline (reusing GetGitBaselineAsync), right = the current on-disk
    // content. Read-only review — both sides are written to throwaway temp files
    // that VS deletes on close (the *FileIsTemporary flags). New/untracked files
    // get an empty left side (renders as all-added). Never throws — failures just
    // log and no tab opens.
    private async Task HandleOpenVsDiffAsync(string filePath, string toolName)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                OutputLog.Warn($"open-vs-diff: file not found: {filePath}");
                return;
            }

            var current = await Task.Run(() => File.ReadAllText(filePath));
            var (baseline, baselineErr) = await GetGitBaselineAsync(filePath);
            // No git baseline (new file / untracked / no repo) → empty left side.
            baseline ??= "";

            var fileName = Path.GetFileName(filePath);
            var diffDir = Path.Combine(Path.GetTempPath(), "ClaudeStudio", "diffs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(diffDir);
            // Keep the original extension on both sides so VS picks the right
            // language service for syntax highlighting.
            var leftPath = Path.Combine(diffDir, "BASE_" + fileName);
            var rightPath = Path.Combine(diffDir, fileName);
            File.WriteAllText(leftPath, baseline, new UTF8Encoding(false));
            File.WriteAllText(rightPath, current, new UTF8Encoding(false));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService diffService)
            {
                OutputLog.Warn("open-vs-diff: IVsDifferenceService unavailable");
                return;
            }

            var tool = string.IsNullOrEmpty(toolName) ? "Diff" : toolName;
            var caption = $"{tool}: {fileName}";
            var leftLabel = string.IsNullOrEmpty(baselineErr) ? "HEAD (baseline)" : "(no baseline)";
            var grfDiffOptions = (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary
                                      | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);

            diffService.OpenComparisonWindow2(
                leftPath, rightPath,
                caption, filePath,
                leftLabel, "Current",
                fileName, null, grfDiffOptions);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"open-vs-diff failed: {ex.Message}");
        }
    }

    // Handles a diagnostics_request from the agent's PostToolUse hook: give VS a
    // moment to re-analyze the just-edited file, read its Error List entries, and
    // send them back so the (blocked) hook can surface them to claude. Never
    // throws — on any failure the agent's hook times out and just adds no context.
    private async Task HandleDiagnosticsRequestAsync(string filePath, string requestId)
    {
        try
        {
            // The edit just hit disk; the language service needs a beat to refresh
            // the Error List. Stay well under the agent's 8s hook timeout.
            await Task.Delay(900);
            var text = await Diagnostics.ErrorListReader.ReadForFileAsync(filePath);
            await _agentClient.SendDiagnosticsResponseAsync(requestId, text);
        }
        catch (Exception ex)
        {
            OutputLog.Warn($"diagnostics request handling failed: {ex.Message}");
            try { await _agentClient.SendDiagnosticsResponseAsync(requestId, ""); } catch { }
        }
    }

    private class WebChatMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("model")]
        public string Model { get; set; } = "claude-sonnet-5";

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

        // 1-based line range for open-file navigation (V11 file-links).
        [JsonPropertyName("startLine")]
        public int? StartLine { get; set; }

        [JsonPropertyName("endLine")]
        public int? EndLine { get; set; }

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

        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; }

        // V7 claude settings (nullable so an absent field means "use default").
        [JsonPropertyName("coAuthoredBy")]
        public bool? CoAuthoredBy { get; set; }

        [JsonPropertyName("cleanupPeriodDays")]
        public int? CleanupPeriodDays { get; set; }

        [JsonPropertyName("autoCompact")]
        public bool? AutoCompact { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("toolUseId")]
        public string? ToolUseId { get; set; }

        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; }

        [JsonPropertyName("answers")]
        public string? Answers { get; set; }

        [JsonPropertyName("dismissed")]
        public bool Dismissed { get; set; }

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

        [JsonPropertyName("servers")]
        public List<McpTrustEntry>? Servers { get; set; }
    }

    private class McpTrustEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "user";

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = "trust";  // "trust" | "skip"
    }
}