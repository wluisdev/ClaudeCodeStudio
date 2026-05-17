using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeVsExtension.Mcp;

[Guid("c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
public class McpToolWindow : ToolWindowPane
{
    public McpToolWindow() : base(null)
    {
        this.Caption = "Claude VS MCP Servers";
        this.Content = new McpToolWindowControl();
    }
}
