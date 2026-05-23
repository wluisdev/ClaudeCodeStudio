using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ClaudeStudioExtension.Mcp;

public enum McpScope { Project, User }

public enum McpTransport { Stdio, Http, Sse }

public class McpServer
{
    public string Name { get; set; } = "";
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool Disabled { get; set; }

    // Forward-compat bucket: any per-server JSON properties we don't recognize
    // (e.g. future MCP options like timeoutMs, autoStart, etc.) are captured
    // here on Load and emitted verbatim on Save so they survive roundtrip
    // through this binary.
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    public McpServer Clone() => new()
    {
        Name = Name,
        Transport = Transport,
        Command = Command,
        Args = new List<string>(Args),
        Env = new Dictionary<string, string>(Env),
        Url = Url,
        Headers = new Dictionary<string, string>(Headers),
        Disabled = Disabled,
        ExtraFields = ExtraFields == null ? null : new Dictionary<string, JsonElement>(ExtraFields),
    };
}

public static class McpConfigStore
{
    public static string GetPath(McpScope scope, string? projectDir)
    {
        if (scope == McpScope.User)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        if (string.IsNullOrEmpty(projectDir))
            throw new InvalidOperationException("Project scope requires a project directory");
        return Path.Combine(projectDir, ".mcp.json");
    }

    public static List<McpServer> Load(McpScope scope, string? projectDir)
    {
        var path = GetPath(scope, projectDir);
        if (!File.Exists(path)) return new List<McpServer>();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new List<McpServer>();

        using var doc = JsonDocument.Parse(text);
        var list = new List<McpServer>();
        LoadSection(doc, "mcpServers", disabled: false, list);
        LoadSection(doc, "_disabledMcpServers", disabled: true, list);
        return list;
    }

    private static void LoadSection(JsonDocument doc, string key, bool disabled, List<McpServer> list)
    {
        if (!doc.RootElement.TryGetProperty(key, out var serversEl) ||
            serversEl.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in serversEl.EnumerateObject())
        {
            var s = ParseServer(prop.Name, prop.Value);
            s.Disabled = disabled;
            var existing = list.FirstOrDefault(x => x.Name == s.Name);
            if (existing != null) list.Remove(existing);
            list.Add(s);
        }
    }

    // Known per-server JSON keys we map to typed McpServer properties. Anything
    // outside this set lands in McpServer.ExtraFields for forward-compat
    // roundtrip preservation.
    private static readonly HashSet<string> _knownServerKeys = new(StringComparer.Ordinal)
    {
        "type", "url", "command", "args", "env", "headers"
    };

    private static McpServer ParseServer(string name, JsonElement el)
    {
        var s = new McpServer { Name = name };

        string? type = null;
        if (el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            type = typeEl.GetString();

        if (el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            s.Url = urlEl.GetString() ?? "";

        bool hasUrl = !string.IsNullOrEmpty(s.Url);

        if (type == "sse") s.Transport = McpTransport.Sse;
        else if (type == "http" || type == "streamable-http" || (hasUrl && type == null)) s.Transport = McpTransport.Http;
        else s.Transport = McpTransport.Stdio;

        if (el.TryGetProperty("command", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
            s.Command = cmdEl.GetString() ?? "";

        if (el.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
            foreach (var a in argsEl.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String) s.Args.Add(a.GetString() ?? "");

        if (el.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
            foreach (var p in envEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) s.Env[p.Name] = p.Value.GetString() ?? "";

        if (el.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
            foreach (var p in hdrEl.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) s.Headers[p.Name] = p.Value.GetString() ?? "";

        // Capture any unrecognized server-level properties so they survive
        // a Save without being silently dropped.
        foreach (var prop in el.EnumerateObject())
        {
            if (_knownServerKeys.Contains(prop.Name)) continue;
            s.ExtraFields ??= new Dictionary<string, JsonElement>();
            s.ExtraFields[prop.Name] = prop.Value.Clone();
        }

        return s;
    }

    public static void Save(McpScope scope, string? projectDir, List<McpServer> servers)
    {
        var path = GetPath(scope, projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (scope == McpScope.Project)
        {
            WriteProjectFile(path, servers);
        }
        else
        {
            WriteUserFile(path, servers);
        }
    }

    private static void WriteProjectFile(string path, List<McpServer> servers)
    {
        var hasDisabled = servers.Any(x => x.Disabled);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WritePropertyName("mcpServers");
            WriteServersFiltered(w, servers, disabled: false);
            if (hasDisabled)
            {
                w.WritePropertyName("_disabledMcpServers");
                WriteServersFiltered(w, servers, disabled: true);
            }
            w.WriteEndObject();
        }
        AtomicWrite(path, ms.ToArray());
    }

    private static void WriteUserFile(string path, List<McpServer> servers)
    {
        Dictionary<string, JsonElement>? existing = null;
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    existing = new Dictionary<string, JsonElement>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        existing[prop.Name] = prop.Value.Clone();
                }
            }
        }

        var hasDisabled = servers.Any(x => x.Disabled);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            bool wroteServers = false, wroteDisabled = false;
            if (existing != null)
            {
                foreach (var kv in existing)
                {
                    if (kv.Key == "mcpServers")
                    {
                        w.WritePropertyName("mcpServers");
                        WriteServersFiltered(w, servers, disabled: false);
                        wroteServers = true;
                    }
                    else if (kv.Key == "_disabledMcpServers")
                    {
                        if (hasDisabled)
                        {
                            w.WritePropertyName("_disabledMcpServers");
                            WriteServersFiltered(w, servers, disabled: true);
                        }
                        wroteDisabled = true;
                    }
                    else
                    {
                        w.WritePropertyName(kv.Key);
                        kv.Value.WriteTo(w);
                    }
                }
            }
            if (!wroteServers)
            {
                w.WritePropertyName("mcpServers");
                WriteServersFiltered(w, servers, disabled: false);
            }
            if (!wroteDisabled && hasDisabled)
            {
                w.WritePropertyName("_disabledMcpServers");
                WriteServersFiltered(w, servers, disabled: true);
            }
            w.WriteEndObject();
        }
        AtomicWrite(path, ms.ToArray());
    }

    private static void WriteServersFiltered(Utf8JsonWriter w, List<McpServer> servers, bool disabled)
    {
        w.WriteStartObject();
        foreach (var s in servers.Where(x => x.Disabled == disabled))
        {
            w.WritePropertyName(s.Name);
            WriteServerBody(w, s);
        }
        w.WriteEndObject();
    }

    private static void WriteServerBody(Utf8JsonWriter w, McpServer s)
    {
        w.WriteStartObject();
        if (s.Transport == McpTransport.Stdio)
        {
            w.WriteString("type", "stdio");
            w.WriteString("command", s.Command);
            w.WriteStartArray("args");
            foreach (var a in s.Args) w.WriteStringValue(a);
            w.WriteEndArray();
            if (s.Env.Count > 0)
            {
                w.WriteStartObject("env");
                foreach (var kv in s.Env) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
        }
        else
        {
            w.WriteString("type", s.Transport == McpTransport.Sse ? "sse" : "http");
            w.WriteString("url", s.Url);
            if (s.Headers.Count > 0)
            {
                w.WriteStartObject("headers");
                foreach (var kv in s.Headers) w.WriteString(kv.Key, kv.Value);
                w.WriteEndObject();
            }
        }
        // Re-emit unknown per-server fields captured on Parse so the on-disk
        // file keeps any options a newer version (or hand-edits) introduced.
        if (s.ExtraFields != null)
        {
            foreach (var kv in s.ExtraFields)
            {
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
        }
        w.WriteEndObject();
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path))
        {
            var bak = path + ".bak";
            try { File.Replace(tmp, path, bak); File.Delete(bak); }
            catch
            {
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required";
        if (name.Length > 64) return "Name too long";
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return "Name can only contain letters, digits, '-' and '_'";
        return "";
    }
}
