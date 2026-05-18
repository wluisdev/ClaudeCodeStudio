using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeStudioExtension.Usage;

[Guid("a2b4c1e6-9d8f-4f2a-9e8b-1234567890ab")]
public class UsageToolWindow : ToolWindowPane
{
    public UsageToolWindow() : base(null)
    {
        this.Caption = "Claude Code Studio Usage";
        this.Content = new UsageToolWindowControl();
    }
}
