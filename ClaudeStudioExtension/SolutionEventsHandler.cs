using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeStudioExtension;

public class SolutionEventsHandler : IVsSolutionEvents, IVsSolutionEvents4, IVsSolutionEvents7
{
    // ── IVsSolutionEvents ─────────────────────────────────────────
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ScheduleReset();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved)
    {
        ScheduleReset();
        return VSConstants.S_OK;
    }

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    // ── IVsSolutionEvents4 ────────────────────────────────────────
    // Project rename / change parent — kept for completeness, not used here.
    public int OnAfterRenameProject(IVsHierarchy pHierarchy) => VSConstants.S_OK;
    public int OnQueryChangeProjectParent(IVsHierarchy pHierarchy, IVsHierarchy pNewParentHier, ref int pfCancel) => VSConstants.S_OK;
    public int OnAfterChangeProjectParent(IVsHierarchy pHierarchy) => VSConstants.S_OK;
    public int OnAfterAsynchOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnAfterMergeSolution(object pUnkReserved) => VSConstants.S_OK;

    // ── IVsSolutionEvents7 ────────────────────────────────────────
    // Covers "Open Folder" mode (VS 2017+). OnAfterOpenSolution does NOT fire
    // reliably for folder open, so we explicitly hook the folder events here.
    public void OnAfterOpenFolder(string folderPath) => ScheduleReset();
    public void OnBeforeCloseFolder(string folderPath) { }
    public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
    public void OnAfterCloseFolder(string folderPath) => ScheduleReset();
    public void OnAfterLoadAllDeferredProjects() { }

    /// <summary>
    /// Schedule a reset off the UI thread with a delay so VS has time to
    /// finalize the new workspace state (Solution.FullName, IVsSolution.GetSolutionInfo).
    /// Close+Open sequences (close one .sln then open another) need a longer
    /// window than the bare 500ms used originally — VS finishes the close fast
    /// but the open's Solution.FullName isn't always populated by 500ms.
    /// </summary>
    private static void ScheduleReset()
    {
#pragma warning disable VSSDK007, VSTHRD110
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await Task.Delay(1500);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ResetActiveControlSession();
            RefreshMcpControl();
            Usage.UsageToolWindowControl.RefreshIfOpen();
        });
#pragma warning restore VSSDK007, VSTHRD110
    }

    private static void ResetActiveControlSession()
    {
        var control = AgentToolWindowCommand.ActiveControl;
        if (control == null) return;

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await control.ResetSessionAsync();
        });
    }

    private static void RefreshMcpControl()
    {
        var mcp = Mcp.McpToolWindowControl.ActiveControl;
        if (mcp == null) return;
        mcp.Dispatcher.BeginInvoke(new Action(() => mcp.Refresh()));
    }
}
