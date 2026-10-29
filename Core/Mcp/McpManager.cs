using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Mcp;

/// <summary>
/// Starts the MCP servers listed in %APPDATA%\Thokolosi\mcp.json and exposes their tools to Claude.
/// On first run it copies the servers from Claude Desktop's config, so anything Claude Desktop can
/// use (e.g. your email/Slack/Hermes servers) Thokolosi can use too.
/// </summary>
public class McpManager(ToolRegistry registry) : IAsyncDisposable
{
    readonly List<IMcpClient> _clients = new();
    public List<string> Status { get; } = new();

    public async Task StartAllAsync(CancellationToken ct)
    {
        EnsureConfigFile();
        var servers = LoadServers();
        var tasks = servers.Where(s => !s.Disabled).Select(s => StartOneAsync(s, ct));
        await Task.WhenAll(tasks);
    }

    async Task StartOneAsync(McpServerConfig s, CancellationToken ct)
    {
        IMcpClient client = s.Url != null ? new McpHttpClient(s) : new McpStdioClient(s);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            await client.StartAsync(cts.Token);
            var tools = await client.ListToolsAsync(cts.Token);
            lock (_clients) _clients.Add(client);
            foreach (var t in tools) registry.Add(new McpTool(client, t));
            lock (Status) Status.Add($"{s.Name}: {tools.Count} tools");
            ActivityLog.Write("MCP", $"Connected to {s.Name} ({tools.Count} tools)");
        }
        catch (Exception ex)
        {
            lock (Status) Status.Add($"{s.Name}: failed ({ex.Message})");
            ActivityLog.Write("MCP", $"Could not start {s.Name}: {ex.Message}");
            await client.DisposeAsync();
        }
    }

    public static List<McpServerConfig> LoadServers()
    {
        var list = new List<McpServerConfig>();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(AppConfig.McpPath));
            if (root?["mcpServers"] is not JsonObject servers) return list;
            foreach (var (name, node) in servers)
            {
                if (node is not JsonObject o) continue;
                var s = new McpServerConfig
                {
                    Name = name,
                    Command = o["command"]?.GetValue<string>(),
                    Url = o["url"]?.GetValue<string>(),
                    Disabled = o["disabled"]?.GetValue<bool>() ?? false,
                };
                foreach (var a in o["args"] as JsonArray ?? new JsonArray()) s.Args.Add(a!.GetValue<string>());
                foreach (var (k, v) in o["env"] as JsonObject ?? new JsonObject()) s.Env[k] = v?.GetValue<string>() ?? "";
                foreach (var (k, v) in o["headers"] as JsonObject ?? new JsonObject()) s.Headers[k] = v?.GetValue<string>() ?? "";
                if (s.Command != null || s.Url != null) list.Add(s);
            }
        }
        catch (Exception ex) { ActivityLog.Write("MCP", "Could not read mcp.json: " + ex.Message); }
        return list;
    }

    /// <summary>Create mcp.json, importing Claude Desktop's servers if it's installed.</summary>
    public static void EnsureConfigFile()
    {
        if (File.Exists(AppConfig.McpPath)) return;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string> { Path.Combine(appData, "Claude", "claude_desktop_config.json") };
        try
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
                candidates.AddRange(Directory.GetDirectories(packages, "Claude_*")
                    .Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
        }
        catch { }

        JsonObject servers = new();
        foreach (var path in candidates.Where(File.Exists))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] is JsonObject found)
                {
                    servers = (JsonObject)found.DeepClone();
                    ActivityLog.Write("MCP", $"Imported {servers.Count} MCP servers from {path}");
                    break;
                }
            }
            catch { }
        }
        var root = new JsonObject
        {
            ["_help"] = "Same format as Claude Desktop's config. Add \"disabled\": true to skip a server. Restart Thokolosi after editing.",
            ["mcpServers"] = servers
        };
        Directory.CreateDirectory(AppConfig.Dir);
        File.WriteAllText(AppConfig.McpPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public async ValueTask DisposeAsync()
    {
        List<IMcpClient> copy;
        lock (_clients) { copy = _clients.ToList(); _clients.Clear(); }
        foreach (var c in copy) await c.DisposeAsync();
    }
}

/// <summary>Wraps one MCP server tool as a Thokolosi tool.</summary>
public class McpTool : ITool
{
    readonly IMcpClient _client;
    readonly McpToolInfo _info;

    public McpTool(IMcpClient client, McpToolInfo info)
    {
        _client = client;
        _info = info;
        Name = MakeName(client.ServerName, info.Name);
        Definition = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Json.Truncate($"[{client.ServerName} MCP] {info.Description}", 1000),
            ["input_schema"] = info.InputSchema.DeepClone()
        };
    }

    public string Name { get; }
    public JsonObject Definition { get; }

    static readonly Regex ReadVerb = new(@"^(list|get|read|search|find|fetch|query|lookup|describe|show|whoami|poll|count|view)$", RegexOptions.IgnoreCase);
    static readonly Regex WriteWord = new(@"^(send|post|create|update|delete|remove|write|set|mark|move|archive|reply|forward|add|edit|replace|upload|run|exec|execute|invite|join|leave|react|trash|spam|draft|and|or|then)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Skip approval only for tools that declare themselves read-only, or whose name starts with a read verb
    /// and has no write word anywhere (so get_or_create / search_and_replace / mark_read still ask).
    /// </summary>
    public bool NeedsApproval(JsonObject input)
    {
        if (_info.ReadOnly) return false;
        var parts = Regex.Split(_info.Name, @"[_\-.]|(?<=[a-z])(?=[A-Z])").Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0 || !ReadVerb.IsMatch(parts[0])) return true;
        return parts.Skip(1).Any(WriteWord.IsMatch);
    }

    public string Describe(JsonObject input) => $"{_client.ServerName}: {_info.Name} {Json.Truncate(input.ToJsonString(), 200)}";

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var res = await _client.CallToolAsync(_info.Name, input, ct);
        bool isError = res["isError"]?.GetValue<bool>() ?? false;
        var result = new ToolResult { IsError = isError };
        var text = new StringBuilder();
        foreach (var c in res["content"] as JsonArray ?? new JsonArray())
        {
            switch (c?["type"]?.GetValue<string>())
            {
                case "text":
                    text.AppendLine(c["text"]?.GetValue<string>());
                    break;
                case "image":
                    result.AddImage(Convert.FromBase64String(c["data"]!.GetValue<string>()), c["mimeType"]?.GetValue<string>() ?? "image/png");
                    break;
                case "resource":
                    text.AppendLine(c["resource"]?["text"]?.GetValue<string>() ?? c.ToJsonString());
                    break;
                default:
                    text.AppendLine(c?.ToJsonString());
                    break;
            }
        }
        if (res["structuredContent"] is JsonNode sc && text.Length == 0) text.Append(sc.ToJsonString());
        result.Content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = Json.Truncate(text.Length > 0 ? text.ToString() : "(no output)", 60000) });
        return result;
    }

    static string MakeName(string server, string tool)
    {
        static string Clean(string s) => Regex.Replace(s, "[^A-Za-z0-9_-]", "_");
        var name = $"mcp__{Clean(server)}__{Clean(tool)}";
        if (name.Length <= 64) return name;
        // Keep names unique when shortening: end with a short hash of the full name.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..6].ToLowerInvariant();
        return name[..57] + "_" + hash;
    }
}
