using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVsExtension
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(AgentToolWindow))]
    [Guid(PackageGuidString)]
    public sealed class ClaudeVsExtensionPackage : AsyncPackage
    {
        /// <summary>
        /// ClaudeVsExtensionPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "36a4617b-b537-4a28-bdd4-a18067f26ea2";

        private uint _solutionEventsCookie;
        private SolutionEventsHandler? _solutionEventsHandler;

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
        /// <param name="progress">A provider for progress updates.</param>
        /// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await AgentToolWindowCommand.InitializeAsync(this);

            if (await GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution)
            {
                _solutionEventsHandler = new SolutionEventsHandler();
                solution.AdviseSolutionEvents(_solutionEventsHandler, out _solutionEventsCookie);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _solutionEventsCookie != 0)
            {
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (GetService(typeof(SVsSolution)) is IVsSolution solution)
                        solution.UnadviseSolutionEvents(_solutionEventsCookie);
                });
                _solutionEventsCookie = 0;
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
