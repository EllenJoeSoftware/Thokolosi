using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Mcp;

/// <summary>One MCP server entry, same shape as Claude Desktop's claude_desktop_config.json.</summary>
public class McpServerConfig
{
    public string Name { get; set; } = "";
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool Disabled { get; set; }
}

public record McpToolInfo(string Name, string Description, JsonObject InputSchema, bool ReadOnly);

public interface IMcpClient : IAsyncDisposable
{
    string ServerName { get; }
    Task StartAsync(CancellationToken ct);
    Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct);
    Task<JsonObject> CallToolAsync(string name, JsonObject args, CancellationToken ct);
}

/// <summary>Shared JSON-RPC bits for both transports.</summary>
public abstract class McpClientBase : IMcpClient
{
    public string ServerName { get; }
    protected McpClientBase(string name) => ServerName = name;

    protected abstract Task<JsonNode?> RequestAsync(string method, JsonObject? @params, CancellationToken ct, TimeSpan? timeout = null);
    protected abstract Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct);
    public abstract ValueTask DisposeAsync();
    protected virtual Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Thokolosi", ["version"] = "0.2.0" }
        }, ct, TimeSpan.FromSeconds(60));
        await NotifyAsync("notifications/initialized", null, ct);
    }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var list = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var p = new JsonObject();
            if (cursor != null) p["cursor"] = cursor;
            var res = await RequestAsync("tools/list", p, ct);
            foreach (var t in res?["tools"] as JsonArray ?? new JsonArray())
            {
                if (t is null) continue;
                var schema = t["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject();
                schema["type"] ??= "object";
                bool ro = t["annotations"]?["readOnlyHint"]?.GetValue<bool>() ?? false;
                list.Add(new McpToolInfo(t["name"]!.GetValue<string>(), t["description"]?.GetValue<string>() ?? "", schema, ro));
            }
            cursor = res?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));
        return list;
    }

    public async Task<JsonObject> CallToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        var res = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args }, ct, TimeSpan.FromMinutes(5));
        return res as JsonObject ?? new JsonObject();
    }

    protected static JsonNode? ResultOrThrow(JsonNode msg)
    {
        if (msg["error"] is JsonNode err)
            throw new InvalidOperationException("MCP error: " + (err["message"]?.GetValue<string>() ?? err.ToJsonString()));
        return msg["result"];
    }
}

/// <summary>MCP over stdin/stdout of a child process (the common local-server setup).</summary>
public class McpStdioClient : McpClientBase
{
    readonly McpServerConfig _cfg;
    Process? _proc;
    int _nextId;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);

    public McpStdioClient(McpServerConfig cfg) : base(cfg.Name) => _cfg = cfg;

    protected override Task ConnectAsync(CancellationToken ct)
    {
        var cmd = Environment.ExpandEnvironmentVariables(_cfg.Command!);
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var args = _cfg.Args.Select(Environment.ExpandEnvironmentVariables).ToList();
        // npx, uvx, node scripts etc. are often .cmd shims, so route anything that isn't an .exe through cmd.exe
        if (cmd.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = cmd;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = "cmd.exe";
            // /s + outer quotes: cmd strips exactly the outer pair and keeps every inner quoted part intact.
            psi.Arguments = "/d /s /c \"" + string.Join(" ", new[] { cmd }.Concat(args).Select(QuoteForCmd)) + "\"";
        }
        foreach (var (k, v) in _cfg.Env) psi.Environment[k] = Environment.ExpandEnvironmentVariables(v);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {cmd}");
        _ = Task.Run(ReadLoop);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _proc.StandardError.ReadLineAsync() is { } line)
                    ActivityLog.Write("MCP-ERR", $"[{ServerName}] {line}");
            }
            catch { }
        });
        return Task.CompletedTask;
    }

    /// <summary>Quote one argument for cmd.exe: wrap in quotes and neutralise cmd metacharacters.</summary>
    static string QuoteForCmd(string arg)
    {
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes);
            backslashes = 0;
            // % is expanded by cmd even inside quotes; ^ escaping doesn't work inside quotes, so break out briefly.
            if (c == '%') sb.Append("\"^%\"");
            else sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    async Task ReadLoop()
    {
        try
        {
            while (_proc != null && await _proc.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; } // servers sometimes print logs to stdout
                if (msg is null) continue;

                var hasId = msg["id"] is JsonValue;
                var method = msg["method"]?.GetValue<string>();
                if (method != null && hasId)
                {
                    // Request from server to us (ping, roots/list, ...)
                    var reply = method == "ping"
                        ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["result"] = new JsonObject() }
                        : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" } };
                    await WriteAsync(reply, CancellationToken.None);
                    continue;
                }
                if (hasId && int.TryParse(msg["id"]!.ToString(), out var id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(msg);
            }
        }
        catch { }
        foreach (var kv in _pending) kv.Value.TrySetException(new IOException($"MCP server '{ServerName}' exited."));
    }

    async Task WriteAsync(JsonObject msg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _proc!.StandardInput.WriteLineAsync(msg.ToJsonString());
            await _proc.StandardInput.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    protected override async Task<JsonNode?> RequestAsync(string method, JsonObject? @params, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params != null) msg["params"] = @params;
        await WriteAsync(msg, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        try { return ResultOrThrow(await tcs.Task); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP server '{ServerName}' did not answer {method} in time.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    protected override Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params != null) msg["params"] = @params;
        return WriteAsync(msg, ct);
    }

    public override ValueTask DisposeAsync()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>MCP over Streamable HTTP (remote servers with a "url").</summary>
public class McpHttpClient : McpClientBase
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };
    readonly McpServerConfig _cfg;
    string? _session;
    int _nextId;

    public McpHttpClient(McpServerConfig cfg) : base(cfg.Name) => _cfg = cfg;

    HttpRequestMessage Build(JsonObject msg)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (_session != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        foreach (var (k, v) in _cfg.Headers) req.Headers.TryAddWithoutValidation(k, Environment.ExpandEnvironmentVariables(v));
        req.Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    protected override async Task<JsonNode?> RequestAsync(string method, JsonObject? @params, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params != null) msg["params"] = @params;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var resp = await Http.SendAsync(Build(msg), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var s)) _session = s.FirstOrDefault();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP server '{ServerName}' HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(cts.Token)}");

        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("event-stream"))
        {
            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.StartsWith("data:")) { data.Append(line[5..].TrimStart()); continue; }
                if (line.Length == 0 && data.Length > 0)
                {
                    var node = JsonNode.Parse(data.ToString());
                    data.Clear();
                    if (node?["id"]?.ToString() == id.ToString()) return ResultOrThrow(node);
                }
            }
            throw new IOException($"MCP server '{ServerName}' closed the stream without answering.");
        }
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token))!;
        return ResultOrThrow(body);
    }

    protected override async Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params != null) msg["params"] = @params;
        using var resp = await Http.SendAsync(Build(msg), ct);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
