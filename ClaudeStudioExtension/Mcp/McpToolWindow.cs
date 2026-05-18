using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace ClaudeStudioExtension.Mcp;

[Guid("c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
public class McpToolWindow : ToolWindowPane
{
    public McpToolWindow() : base(null)
    {
        this.Caption = "Claude Code Studio MCP Servers";
        this.BitmapImageMoniker = KnownMonikers.Extension;
        this.Content = new McpToolWindowControl();
    }
}
