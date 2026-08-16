using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace ClaudeStudioExtension
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class AgentToolWindowCommand
    {
        public const int CommandId = 0x0100;
        public const int FocusCommandId = 0x0101;
        public const int SendSelectionCommandId = 0x0102;
        public const int SendFileCommandId = 0x0103;
        public const int UsageWindowCommandId = 0x0104;
        public const int McpWindowCommandId = 0x0105;

        // Editor right-click submenu actions (issue #6). Each sends the current
        // selection prefixed with a fixed instruction so the common asks don't
        // need the prompt typed out.
        public const int EditorExplainCommandId = 0x0106;
        public const int EditorSummaryCommandId = 0x0107;
        public const int EditorCommentsCommandId = 0x0108;
        public const int EditorUnitTestsCommandId = 0x0109;
        public const int EditorRefactorCommandId = 0x010A;
        public const int EditorSecurityCheckCommandId = 0x010B;

        // Instruction prefixes for the editor actions above. Kept directive (and
        // em-dash-free) so the model has clear intent without the user typing it.
        private const string ExplainPrompt = "Explain what this code does, step by step:";
        private const string SummaryPrompt = "Add documentation summary comments to this code (XML `<summary>` doc comments for C#, or the language's equivalent). Return the complete updated code:";
        private const string CommentsPrompt = "Add clear inline comments explaining this code. Return the complete updated code:";
        private const string UnitTestsPrompt = "Write unit tests for this code. Match the test framework this project already uses if you can tell which one:";
        private const string RefactorPrompt = "Suggest and apply refactorings that improve this code's readability and performance without changing its behavior. Return the complete updated code:";
        private const string SecurityCheckPrompt = "Review this code for security issues and suggest fixes:";

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
            // Called by InitializeAsync after SwitchToMainThreadAsync, so the whole
            // constructor (AddCommand, the editor-action registrations) is on the UI thread.
            ThreadHelper.ThrowIfNotOnUIThread();
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

            var sendFileCmdID = new CommandID(CommandSet, SendFileCommandId);
            var sendFileItem = new OleMenuCommand(this.ExecuteSendFile, sendFileCmdID);
            sendFileItem.BeforeQueryStatus += OnSendFileQueryStatus;
            commandService.AddCommand(sendFileItem);

            var usageCmdID = new CommandID(CommandSet, UsageWindowCommandId);
            var usageItem = new MenuCommand(this.ExecuteShowUsage, usageCmdID);
            commandService.AddCommand(usageItem);

            var mcpCmdID = new CommandID(CommandSet, McpWindowCommandId);
            var mcpItem = new MenuCommand(this.ExecuteShowMcp, mcpCmdID);
            commandService.AddCommand(mcpItem);

            // Editor submenu actions (issue #6): same selection-gated visibility
            // as Send Selection, each carrying its own fixed instruction prefix.
            AddEditorAction(commandService, EditorExplainCommandId, ExplainPrompt);
            AddEditorAction(commandService, EditorSummaryCommandId, SummaryPrompt);
            AddEditorAction(commandService, EditorCommentsCommandId, CommentsPrompt);
            AddEditorAction(commandService, EditorUnitTestsCommandId, UnitTestsPrompt);
            AddEditorAction(commandService, EditorRefactorCommandId, RefactorPrompt);
            AddEditorAction(commandService, EditorSecurityCheckCommandId, SecurityCheckPrompt);
        }

        private void AddEditorAction(OleMenuCommandService commandService, int commandId, string instruction)
        {
            var id = new CommandID(CommandSet, commandId);
            var item = new OleMenuCommand((s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); ExecuteEditorAction(instruction); }, id);
            // Reuse the Send Selection gate: always visible, enabled only when the
            // active document has a non-empty text selection.
            item.BeforeQueryStatus += OnSendSelectionQueryStatus;
            commandService.AddCommand(item);
        }

        private void ExecuteEditorAction(string instruction)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ToolWindowPane window = this.package.FindToolWindow(typeof(AgentToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                if (ActiveControl != null)
                    await ActiveControl.SendEditorActionAsync(instruction);
            });
        }

        private void ExecuteShowMcp(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ToolWindowPane window = this.package.FindToolWindow(typeof(Mcp.McpToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            if (window?.Content is Mcp.McpToolWindowControl ctrl)
                ctrl.Refresh();
        }

        private void ExecuteShowUsage(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ToolWindowPane window = this.package.FindToolWindow(typeof(Usage.UsageToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            if (window?.Content is Usage.UsageToolWindowControl ctrl)
                ctrl.Refresh();
        }

        private static string? GetSelectedSolutionExplorerFile()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var items = dte?.SelectedItems;
                if (items == null || items.Count == 0) return null;

                foreach (EnvDTE.SelectedItem item in items)
                {
                    var pi = item.ProjectItem;
                    if (pi != null && pi.FileCount > 0)
                    {
                        var path = pi.FileNames[1];
                        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                            return path;
                    }
                }
            }
            catch { }
            return null;
        }

        private void OnSendFileQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmd = (OleMenuCommand)sender;
            cmd.Visible = true;
            cmd.Enabled = GetSelectedSolutionExplorerFile() != null;
        }

        private void ExecuteSendFile(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var path = GetSelectedSolutionExplorerFile();
            if (path == null) return;

            ToolWindowPane window = this.package.FindToolWindow(typeof(AgentToolWindow), 0, true);
            if (window?.Frame is IVsWindowFrame frame)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());

            ActiveControl?.InsertFileReference(path);
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
            ActiveControl?.FocusTextarea();
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
