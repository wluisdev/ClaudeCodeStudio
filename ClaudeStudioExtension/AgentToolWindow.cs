using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClaudeStudioExtension
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// <para>
    /// Do not try to fix issues/2 (Home/End navigating the tab group) from here. WPF's
    /// <c>TabControl</c> claims those keys inside its own <c>OnKeyDown</c> and the shell's docking
    /// wells derive from it, so this pane never sees them — a <c>PreProcessMessage</c> override was
    /// tried, instrumented, and never called once. Marking the routed event handled from a
    /// <c>TabControl</c> class handler does stop the tab switch, but WPF then reports the input as
    /// consumed and the WebView2 never gets the key, which also breaks the floating panel that used
    /// to work. Both were measured and reverted; the remaining known option is patching
    /// <c>TabControl.OnKeyDown</c> itself, which skips the navigation without consuming the event.
    /// </para>
    /// </remarks>
    [Guid("ecbedd02-1fe6-4af5-8053-cd3a911f7bb5")]
    public class AgentToolWindow : ToolWindowPane
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AgentToolWindow"/> class.
        /// </summary>
        public AgentToolWindow() : base(null)
        {
            this.Caption = "Claude Code Studio Chat";

            // This is the user control hosted by the tool window; Note that, even if this class implements IDisposable,
            // we are not calling Dispose on this object. This is because ToolWindowPane calls Dispose on
            // the object returned by the Content property.
            this.Content = new AgentToolWindowControl();
        }
    }
}
