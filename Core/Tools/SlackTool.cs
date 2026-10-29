using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

/// <summary>
/// Slack Web API. Works with a user token (xoxp-, acts as you, can search) or a bot token (xoxb-, acts as the app).
/// </summary>
public class SlackTool : ITool
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly Dictionary<string, string> _userNames = new();
    List<(string Id, string Name, bool Member)>? _channels;

    public string Name => "slack";

    public JsonObject Definition { get; } = Json.Def("slack",
        "Use the user's Slack workspace. Actions: whoami, channels (list), read (recent messages in a channel, or a thread with thread_ts), " +
        "search (user tokens only), join (a public channel), post (message to a channel/DM, optionally in a thread), dm (direct message a user by name, email or id). " +
        "Channels can be given as #name or id. Confirm with the user before posting unless their job description says otherwise.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["whoami", "channels", "read", "search", "join", "post", "dm"] },
            "channel": { "type": "string", "description": "#name or channel id" },
            "user": { "type": "string", "description": "dm: user id, @handle, display name or email" },
            "text": { "type": "string", "description": "post/dm: message text (Slack mrkdwn)" },
            "thread_ts": { "type": "string", "description": "read: thread to read. post: reply in this thread." },
            "query": { "type": "string", "description": "search query (Slack search syntax, e.g. 'invoice in:#finance after:2026-10-01')" },
            "limit": { "type": "integer", "description": "messages to return (default 20)" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "post" or "dm" or "join";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "post" => $"Slack: posting to {input.Str("channel")}\n\n{Json.Truncate(input.Str("text"), 800)}",
        "dm" => $"Slack: DM to {input.Str("user")}\n\n{Json.Truncate(input.Str("text"), 800)}",
        "read" => $"Slack: reading {input.Str("channel")}",
        "search" => $"Slack: searching '{input.Str("query")}'",
        "join" => $"Slack: joining {input.Str("channel")}",
        var a => $"Slack: {a}"
    };

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var token = ctx.Config.SlackToken;
        if (string.IsNullOrEmpty(token))
            return ToolResult.Error("No Slack token set. The user can add one in Settings > Integrations (or use the Slack MCP tools if available).");
        int limit = Math.Clamp(input.Int("limit", 20), 1, 200);

        switch (input.Str("action"))
        {
            case "whoami":
            {
                var r = await Call(token, "auth.test", new(), ct);
                return ToolResult.Text($"User: {r["user"]} ({r["user_id"]}) in workspace {r["team"]}. Token type: {(token.StartsWith("xoxb") ? "bot" : "user")}");
            }
            case "channels":
            {
                var chans = await Channels(token, ct, refresh: true);
                return ToolResult.Text(string.Join("\n", chans.Select(c => $"#{c.Name} ({c.Id}){(c.Member ? "" : " - not a member")}")));
            }
            case "read":
            {
                var ch = await ResolveChannel(token, input.Str("channel"), ct);
                var thread = input.Str("thread_ts");
                var r = string.IsNullOrEmpty(thread)
                    ? await Call(token, "conversations.history", new() { ["channel"] = ch, ["limit"] = limit.ToString() }, ct)
                    : await Call(token, "conversations.replies", new() { ["channel"] = ch, ["ts"] = thread, ["limit"] = limit.ToString() }, ct);
                var msgs = (r["messages"] as JsonArray ?? new JsonArray()).ToList();
                if (string.IsNullOrEmpty(thread)) msgs.Reverse(); // history is newest first
                var sb = new StringBuilder();
                foreach (var m in msgs)
                {
                    var ts = m!["ts"]?.GetValue<string>() ?? "";
                    var when = DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(ts, System.Globalization.CultureInfo.InvariantCulture)).LocalDateTime;
                    var who = await UserName(token, m["user"]?.GetValue<string>() ?? m["bot_id"]?.GetValue<string>() ?? "?", ct);
                    var replies = m["reply_count"]?.GetValue<int>() ?? 0;
                    sb.AppendLine($"[{when:MM-dd HH:mm}] {who}: {m["text"]?.GetValue<string>()}  (ts {ts}{(replies > 0 ? $", {replies} replies" : "")})");
                }
                return ToolResult.Text(Json.Truncate(sb.Length == 0 ? "(no messages)" : sb.ToString(), 40000));
            }
            case "search":
            {
                var r = await Call(token, "search.messages", new() { ["query"] = input.Str("query"), ["count"] = limit.ToString(), ["sort"] = "timestamp" }, ct);
                var sb = new StringBuilder();
                foreach (var m in r["messages"]?["matches"] as JsonArray ?? new JsonArray())
                    sb.AppendLine($"#{m!["channel"]?["name"]} | {m["username"]} | ts {m["ts"]}: {m["text"]}");
                return ToolResult.Text(sb.Length == 0 ? "No matches." : Json.Truncate(sb.ToString(), 40000));
            }
            case "join":
            {
                var ch = await ResolveChannel(token, input.Str("channel"), ct);
                await Call(token, "conversations.join", new() { ["channel"] = ch }, ct);
                _channels = null;
                return ToolResult.Text($"Joined {input.Str("channel")}.");
            }
            case "post":
            {
                var ch = await ResolveChannel(token, input.Str("channel"), ct);
                var p = new Dictionary<string, string> { ["channel"] = ch, ["text"] = input.Str("text") };
                if (!string.IsNullOrEmpty(input.Str("thread_ts"))) p["thread_ts"] = input.Str("thread_ts");
                var r = await Call(token, "chat.postMessage", p, ct);
                return ToolResult.Text($"Posted (ts {r["ts"]}).");
            }
            case "dm":
            {
                var uid = await ResolveUser(token, input.Str("user"), ct);
                var open = await Call(token, "conversations.open", new() { ["users"] = uid }, ct);
                var ch = open["channel"]!["id"]!.GetValue<string>();
                var r = await Call(token, "chat.postMessage", new() { ["channel"] = ch, ["text"] = input.Str("text") }, ct);
                return ToolResult.Text($"DM sent (ts {r["ts"]}).");
            }
            default:
                return ToolResult.Error("Unknown action.");
        }
    }

    static async Task<JsonObject> Call(string token, string method, Dictionary<string, string> args, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method)
        {
            Content = new FormUrlEncodedContent(args)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
        if (body["ok"]?.GetValue<bool>() != true)
        {
            var err = body["error"]?.GetValue<string>() ?? "unknown_error";
            var hint = err switch
            {
                "missing_scope" => $" (the token needs the scope: {body["needed"]})",
                "not_in_channel" => " (join the channel first)",
                "not_allowed_token_type" => " (this needs a user token, xoxp-)",
                _ => ""
            };
            throw new InvalidOperationException($"Slack {method} failed: {err}{hint}");
        }
        return body;
    }

    async Task<List<(string Id, string Name, bool Member)>> Channels(string token, CancellationToken ct, bool refresh = false)
    {
        if (_channels != null && !refresh) return _channels;
        var list = new List<(string, string, bool)>();
        string? cursor = null;
        do
        {
            var p = new Dictionary<string, string> { ["types"] = "public_channel,private_channel", ["exclude_archived"] = "true", ["limit"] = "500" };
            if (cursor != null) p["cursor"] = cursor;
            var r = await Call(token, "conversations.list", p, ct);
            foreach (var c in r["channels"] as JsonArray ?? new JsonArray())
                list.Add((c!["id"]!.GetValue<string>(), c["name"]?.GetValue<string>() ?? "", c["is_member"]?.GetValue<bool>() ?? false));
            cursor = r["response_metadata"]?["next_cursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor) && list.Count < 5000);
        return _channels = list;
    }

    async Task<string> ResolveChannel(string token, string channel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("channel is required");
        var name = channel.TrimStart('#');
        if (!channel.StartsWith('#') && name.Length >= 9 && name.All(char.IsLetterOrDigit) && name.ToUpperInvariant() == name) return name; // looks like an id
        var match = (await Channels(token, ct)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match.Id ?? throw new ArgumentException($"Channel '{channel}' not found.");
    }

    async Task<string> ResolveUser(string token, string user, CancellationToken ct)
    {
        user = user.Trim().TrimStart('@');
        if (user.Length >= 9 && (user.StartsWith('U') || user.StartsWith('W')) && user.ToUpperInvariant() == user) return user;
        if (user.Contains('@'))
        {
            var r = await Call(token, "users.lookupByEmail", new() { ["email"] = user }, ct);
            return r["user"]!["id"]!.GetValue<string>();
        }
        string? cursor = null;
        do
        {
            var p = new Dictionary<string, string> { ["limit"] = "500" };
            if (cursor != null) p["cursor"] = cursor;
            var r = await Call(token, "users.list", p, ct);
            foreach (var u in r["members"] as JsonArray ?? new JsonArray())
            {
                var names = new[] { u!["name"], u["real_name"], u["profile"]?["display_name"], u["profile"]?["real_name"] }
                    .Select(n => n?.GetValue<string>() ?? "");
                if (names.Any(n => n.Equals(user, StringComparison.OrdinalIgnoreCase)))
                    return u["id"]!.GetValue<string>();
            }
            cursor = r["response_metadata"]?["next_cursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));
        throw new ArgumentException($"Slack user '{user}' not found.");
    }

    async Task<string> UserName(string token, string id, CancellationToken ct)
    {
        if (_userNames.TryGetValue(id, out var n)) return n;
        if (!id.StartsWith('U') && !id.StartsWith('W')) return id;
        try
        {
            var r = await Call(token, "users.info", new() { ["user"] = id }, ct);
            var dn = r["user"]?["profile"]?["display_name"]?.GetValue<string>();
            n = string.IsNullOrEmpty(dn) ? r["user"]?["real_name"]?.GetValue<string>() ?? id : dn;
        }
        catch { n = id; }
        return _userNames[id] = n;
    }
}
