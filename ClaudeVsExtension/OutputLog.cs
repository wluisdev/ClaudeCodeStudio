using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVsExtension;

internal static class OutputLog
{
    private static readonly Guid PaneGuid = new("c0a2a8c1-7e1f-4b8a-9d33-cd3a911f7bb6");
    private const string PaneName = "Claude VS";

    private static IVsOutputWindowPane? _pane;

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Internally switches to UI thread before touching VS services.")]
    public static void Info(string message) => Enqueue("INFO ", message);

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Internally switches to UI thread before touching VS services.")]
    public static void Warn(string message) => Enqueue("WARN ", message);

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Internally switches to UI thread before touching VS services.")]
    public static void Error(string message) => Enqueue("ERROR", message);

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Lambda runs on UI thread via SwitchToMainThreadAsync.")]
    private static void Enqueue(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}{Environment.NewLine}";
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            WriteOnUIThread(line);
        });
    }

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Caller already switched to UI thread via SwitchToMainThreadAsync.")]
    private static void WriteOnUIThread(string line)
    {
        try
        {
            if (_pane == null)
            {
                if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow window)
                    return;
                var paneGuid = PaneGuid;
                if (ErrorHandler.Failed(window.GetPane(ref paneGuid, out _pane)) || _pane == null)
                {
                    window.CreatePane(ref paneGuid, PaneName, fInitVisible: 1, fClearWithSolution: 0);
                    window.GetPane(ref paneGuid, out _pane);
                }
            }
            _pane?.OutputStringThreadSafe(line);
        }
        catch { /* never throw from logger */ }
    }
}
