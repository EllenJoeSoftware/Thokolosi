using System.Text.Json.Nodes;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Remote;

/// <summary>
/// Control Thokolosi by WhatsApp/Telegram/Discord through the Hermes MCP server: polls Hermes for new messages and
/// hands messages from allowed senders to the agent, which replies through Hermes' messages_send tool.
/// </summary>
public sealed class MessagingBridge : IDisposable
{
    const string ReplyPrefix = "\U0001F916 Thokolosi:";
    readonly AppConfig _cfg;
    readonly ToolRegistry _tools;
    readonly Func<string, string, Task> _runTask; // (title, prompt)
    readonly System.Threading.Timer _timer;
    int _busy;
    bool _warned;

    public MessagingBridge(AppConfig cfg, ToolRegistry tools, Func<string, string, Task> runTask)
    {
        _cfg = cfg;
        _tools = tools;
        _runTask = runTask;
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, 15_000, 10_000);
    }

    async Task PollAsync()
    {
        var s = _cfg.Messaging;
        if (!s.Enabled || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var poll = _tools.Find($"mcp__{s.McpServer}__events_poll");
            if (poll == null)
            {
                if (!_warned) { _warned = true; ActivityLog.Write("MSG", $"Messaging is on but the '{s.McpServer}' MCP server (events_poll) isn't connected."); }
                return;
            }
            if (s.AllowedSenders.Count == 0)
            {
                if (!_warned) { _warned = true; ActivityLog.Write("MSG", "Messaging is on but no allowed senders are set - ignoring all messages for safety."); }
                return;
            }
            _warned = false;

            var result = await poll.RunAsync(new JsonObject { ["after_cursor"] = s.Cursor, ["limit"] = 20 }, new ToolContext(_cfg), CancellationToken.None);
            var text = result.Content.FirstOrDefault()?["text"]?.GetValue<string>() ?? "";
            var json = TryParse(text);
            if (json == null) return;

            var next = FindLong(json, "next_cursor") ?? FindLong(json, "cursor");
            var events = (json["events"] as JsonArray) ?? (json as JsonArray) ?? new JsonArray();
            var firstRun = s.Cursor == 0;
            if (next.HasValue && next.Value != s.Cursor) { s.Cursor = next.Value; _cfg.Save(); }
            if (firstRun) return; // don't act on old history the first time

            foreach (var e in events)
            {
                if (e is not JsonObject ev) continue;
                var type = Str(ev, "type", "event_type", "kind") ?? "";
                if (!type.Contains("message", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = ev.ToJsonString();
                var body = Str(ev, "text", "content", "message", "body") ?? Str(ev["message"] as JsonObject, "text", "content", "body") ?? "";
                if (string.IsNullOrWhiteSpace(body) || body.StartsWith(ReplyPrefix)) continue;           // our own replies
                if (ev["is_self"]?.GetValue<bool>() == true || ev["from_me"]?.GetValue<bool>() == true) continue;
                var dir = Str(ev, "direction") ?? "";
                if (dir.Equals("outgoing", StringComparison.OrdinalIgnoreCase) || dir.Equals("outbound", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsAllowed(ev, s.AllowedSenders)) continue; // checks sender fields only, never the message text

                var session = Str(ev, "session_key", "session", "conversation") ?? "";
                var platform = Str(ev, "platform") ?? "";
                var sender = Str(ev, "sender_name", "sender", "from", "author", "user") ?? "the sender";
                ActivityLog.Write("MSG", $"Message from {sender} via {platform}: {body}");
                var prompt =
                    $"A message arrived via {(platform.Length > 0 ? platform : "the messaging bridge")} from {sender}:\n\n\"{body}\"\n\n" +
                    $"Treat it as a request from the user. Do it with your tools. Then reply to them with the {s.McpServer} messages_send tool " +
                    $"(look up the right target with conversation_get/conversations_list using session_key '{session}' if needed). " +
                    $"Start the reply with \"{ReplyPrefix}\" and keep it short. Raw event for reference: {Json.Truncate(raw, 1500)}";
                await _runTask($"Message from {sender}", prompt);
            }
        }
        catch (Exception ex) { ActivityLog.Write("MSG", "Poll failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static readonly string[] SenderKeys = { "sender", "sender_id", "sender_name", "from", "from_id", "author", "author_id", "user", "user_id", "username", "phone", "chat_id" };

    /// <summary>True only if one of the event's sender fields matches an allowed sender (exact, or same phone number).</summary>
    static bool IsAllowed(JsonObject ev, List<string> allowed)
    {
        var candidates = new List<string>();
        void Collect(JsonObject? o)
        {
            if (o == null) return;
            foreach (var k in SenderKeys)
            {
                if (o[k] is JsonValue v) candidates.Add(v.ToString());
                else if (o[k] is JsonObject nested) Collect(nested);
            }
        }
        Collect(ev);
        Collect(ev["sender"] as JsonObject);
        Collect(ev["message"] as JsonObject);
        static string Digits(string x) => new(x.Where(char.IsDigit).ToArray());
        foreach (var a in allowed.Select(a => a.Trim()).Where(a => a.Length > 0))
            foreach (var c in candidates.Select(c => c.Trim()))
            {
                if (c.Equals(a, StringComparison.OrdinalIgnoreCase) || c.Equals("@" + a.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) return true;
                var da = Digits(a);
                var dc = Digits(c);
                // phone numbers: "+27 82 123 4567", "27821234567@s.whatsapp.net", "whatsapp:+27821234567" all match
                if (da.Length >= 9 && dc.Length >= 9 && (dc.EndsWith(da[^9..]) && da.EndsWith(dc[^9..]))) return true;
            }
        return false;
    }

    static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch
        {
            int a = text.IndexOfAny(new[] { '{', '[' });
            if (a < 0) return null;
            try { return JsonNode.Parse(text[a..]); } catch { return null; }
        }
    }

    static string? Str(JsonObject? o, params string[] keys)
    {
        if (o == null) return null;
        foreach (var k in keys)
            if (o[k] is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToJsonString();
            }
        return null;
    }

    static long? FindLong(JsonNode n, string key) =>
        n[key] is JsonValue v && (v.TryGetValue<long>(out var l) || long.TryParse(v.ToString(), out l)) ? l : null;

    public void Dispose() => _timer.Dispose();
}
