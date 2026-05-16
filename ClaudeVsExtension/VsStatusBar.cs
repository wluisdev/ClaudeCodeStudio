using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVsExtension;

internal static class VsStatusBar
{
    private static IVsStatusbar? GetBar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
    }

#pragma warning disable VSTHRD010
    public static void ShowThinking() => Post("⟳ Claude thinking…");
#pragma warning restore VSTHRD010

    public static void Clear()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var bar = GetBar();
            if (bar == null) return;
            bar.IsFrozen(out var frozen);
            if (frozen != 0) bar.FreezeOutput(0);
            bar.Clear();
        });
    }

    private static void Post(string text)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var bar = GetBar();
            if (bar == null) return;
            bar.IsFrozen(out var frozen);
            if (frozen != 0) bar.FreezeOutput(0);
            bar.SetText(text);
        });
    }
}
