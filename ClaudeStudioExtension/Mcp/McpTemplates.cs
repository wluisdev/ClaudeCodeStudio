using System.Collections.Generic;

namespace ClaudeStudioExtension.Mcp;

public class McpTemplate
{
    public string Label { get; }
    public McpServer Server { get; }
    public List<string> EnvHints { get; }

    public McpTemplate(string label, McpServer server, List<string>? envHints = null)
    {
        Label = label;
        Server = server;
        EnvHints = envHints ?? new List<string>();
    }
}

public static class McpTemplates
{
    public static readonly List<McpTemplate> All = new()
    {
        new McpTemplate("(custom)", new McpServer()),

        new McpTemplate("filesystem", new McpServer
        {
            Name = "filesystem",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-filesystem", "C:\\path\\to\\allowed\\dir" },
        }),

        new McpTemplate("github", new McpServer
        {
            Name = "github",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-github" },
            Env = new() { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "" },
        }, new() { "GITHUB_PERSONAL_ACCESS_TOKEN" }),

        new McpTemplate("postgres", new McpServer
        {
            Name = "postgres",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-postgres", "postgresql://user:pass@localhost/dbname" },
        }),

        new McpTemplate("brave-search", new McpServer
        {
            Name = "brave-search",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-brave-search" },
            Env = new() { ["BRAVE_API_KEY"] = "" },
        }, new() { "BRAVE_API_KEY" }),

        new McpTemplate("sequential-thinking", new McpServer
        {
            Name = "sequential-thinking",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-sequential-thinking" },
        }),

        new McpTemplate("fetch", new McpServer
        {
            Name = "fetch",
            Transport = McpTransport.Stdio,
            Command = "uvx",
            Args = new() { "mcp-server-fetch" },
        }),

        new McpTemplate("memory", new McpServer
        {
            Name = "memory",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-memory" },
        }),

        new McpTemplate("puppeteer", new McpServer
        {
            Name = "puppeteer",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Args = new() { "-y", "@modelcontextprotocol/server-puppeteer" },
        }),
    };

    private static readonly Dictionary<string, string> ArgMarkers = new()
    {
        ["@modelcontextprotocol/server-filesystem"] = "filesystem",
        ["@modelcontextprotocol/server-github"] = "github",
        ["@modelcontextprotocol/server-postgres"] = "postgres",
        ["@modelcontextprotocol/server-brave-search"] = "brave-search",
        ["@modelcontextprotocol/server-sequential-thinking"] = "sequential-thinking",
        ["mcp-server-fetch"] = "fetch",
        ["@modelcontextprotocol/server-memory"] = "memory",
        ["@modelcontextprotocol/server-puppeteer"] = "puppeteer",
    };

    public static int DetectIndex(McpServer s)
    {
        if (s.Transport != McpTransport.Stdio) return 0;
        foreach (var arg in s.Args)
        {
            if (ArgMarkers.TryGetValue(arg, out var label))
            {
                for (int i = 0; i < All.Count; i++)
                    if (All[i].Label == label) return i;
            }
        }
        return 0;
    }
}
