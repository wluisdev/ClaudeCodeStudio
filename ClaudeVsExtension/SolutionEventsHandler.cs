using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVsExtension;

public class SolutionEventsHandler : IVsSolutionEvents
{
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ResetActiveControlSession();
        RefreshMcpControl();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved)
    {
        ResetActiveControlSession();
        RefreshMcpControl();
        return VSConstants.S_OK;
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

    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
}
