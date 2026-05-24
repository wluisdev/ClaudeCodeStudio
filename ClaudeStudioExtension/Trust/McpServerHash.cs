using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudeStudioExtension.Mcp;

namespace ClaudeStudioExtension.Trust;

/// <summary>
/// Stable SHA256 of an MCP server's payload, excluding env/header values so
/// secrets never reach the trust store. Re-running on the same server config
/// returns the same hash; renaming, changing the command/url/args, or adding
/// a new env/header key invalidates the previously trusted hash.
/// </summary>
public static class McpServerHash
{
    public static string Compute(McpServer server)
    {
        var sb = new StringBuilder();
        sb.Append(server.Transport).Append('\0');
        sb.Append(server.Command ?? "").Append('\0');
        sb.Append(string.Join("\0", server.Args ?? Enumerable.Empty<string>())).Append('\0');
        sb.Append(server.Url ?? "").Append('\0');

        var envKeys = (server.Env?.Keys ?? Enumerable.Empty<string>())
            .Select(k => k.ToLowerInvariant())
            .OrderBy(k => k, StringComparer.Ordinal);
        sb.Append(string.Join("\0", envKeys)).Append('\0');

        var headerKeys = (server.Headers?.Keys ?? Enumerable.Empty<string>())
            .Select(k => k.ToLowerInvariant())
            .OrderBy(k => k, StringComparer.Ordinal);
        sb.Append(string.Join("\0", headerKeys));

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(bytes);
    }

    public static string ShortSummary(McpServer server)
    {
        if (server.Transport == McpTransport.Stdio)
        {
            var cmd = (server.Command ?? "").Trim();
            var args = string.Join(" ", server.Args ?? Enumerable.Empty<string>());
            var full = string.IsNullOrEmpty(args) ? cmd : $"{cmd} {args}";
            return Truncate(full, 80);
        }
        return Truncate(server.Url ?? "", 80);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
