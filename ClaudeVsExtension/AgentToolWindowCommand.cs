using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVsExtension
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class AgentToolWindowCommand
    {
        public const int CommandId = 0x0100;
        public const int FocusCommandId = 0x0101;
        public const int SendSelectionCommandId = 0x0102;

        public static readonly Guid CommandSet = new("dd63979a-7c8a-4d0c-b2f7-321ba5b6d8d2");

        public static AgentToolWindowControl? ActiveControl { get; set; }

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="AgentToolWindowCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private AgentToolWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);

            var focusCommandID = new CommandID(CommandSet, FocusCommandId);
            var focusItem = new MenuCommand(this.ExecuteFocus, focusCommandID);
            commandService.AddCommand(focusItem);

            var sendSelCmdID = new CommandID(CommandSet, SendSelectionCommandId);
            var sendSelItem = new OleMenuCommand(this.ExecuteSendSelection, sendSelCmdID);
            sendSelItem.BeforeQueryStatus += OnSendSelectionQueryStatus;
            commandService.AddCommand(sendSelItem);
        }

        private void OnSendSelectionQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmd = (OleMenuCommand)sender;
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var sel = dte?.ActiveDocument?.Selection as EnvDTE.TextSelection;
                cmd.Visible = true;
                cmd.Enabled = sel != null && !string.IsNullOrEmpty(sel.Text);
            }
            catch
            {
                cmd.Visible = true;
                cmd.Enabled = false;
            }
        }

        private void ExecuteSendSelection(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ToolWindowPane window = this.package.FindToolWindow(typeof(AgentToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (ActiveControl != null)
                    await ActiveControl.SendActiveSelectionAsync();
            });
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static AgentToolWindowCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in AgentToolWindowCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new AgentToolWindowCommand(package, commandService);
        }

        /// <summary>
        /// Shows the tool window when the menu item is clicked.
        /// </summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Get the instance number 0 of this tool window. This window is single instance so this instance
            // is actually the only one.
            // The last flag is set to true so that if the tool window does not exists it will be created.
            ToolWindowPane window = this.package.FindToolWindow(typeof(AgentToolWindow), 0, true);
            if ((null == window) || (null == window.Frame))
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }

        private void ExecuteFocus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ToolWindowPane window = this.package.FindToolWindow(typeof(AgentToolWindow), 0, true);
            if (window?.Frame is not IVsWindowFrame frame) return;

            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            ActiveControl?.FocusTextarea();
        }
    }
}
