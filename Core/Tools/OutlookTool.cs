using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Thokolosi.Core.Tools;

public class OutlookTool : ITool
{
    public string Name => "outlook";

    public JsonObject Definition { get; } = Json.Def("outlook",
        "Use the desktop Outlook app on this PC (the user's work Microsoft 365 mailbox and calendar). " +
        "Actions: list (inbox/sent), search, read (by id), send, draft, reply (by id), calendar (events in a date range, including Teams meeting links). " +
        "Confirm with the user before 'send' or 'reply' unless their job description says otherwise.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "search", "read", "send", "draft", "reply", "calendar"] },
            "folder": { "type": "string", "enum": ["inbox", "sent"], "description": "Default inbox." },
            "unread_only": { "type": "boolean" },
            "limit": { "type": "integer", "description": "Default 20." },
            "query": { "type": "string", "description": "search: text in subject, body or sender name." },
            "id": { "type": "string", "description": "read/reply: message id from list/search." },
            "mark_read": { "type": "boolean", "description": "read: also mark it as read." },
            "to": { "type": "string" },
            "cc": { "type": "string" },
            "subject": { "type": "string" },
            "body": { "type": "string" },
            "attachments": { "type": "array", "items": { "type": "string" } },
            "from_date": { "type": "string", "description": "calendar: start date yyyy-MM-dd (default today)." },
            "days": { "type": "integer", "description": "calendar: number of days (default 1)." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "send" or "reply" or "draft";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "send" or "draft" => $"Outlook: {input.Str("action")} '{input.Str("subject")}' to {input.Str("to")}\n\n{Json.Truncate(input.Str("body"), 800)}",
        "reply" => $"Outlook: replying\n\n{Json.Truncate(input.Str("body"), 800)}",
        "search" => $"Outlook: searching '{input.Str("query")}'",
        var a => $"Outlook: {a}"
    };

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        if (!ctx.Config.OutlookEnabled) return Task.FromResult(ToolResult.Error("Outlook integration is switched off in Settings."));
        // Outlook COM is happiest on its own STA thread.
        // Timeout guards against Outlook showing a hidden "a program is trying to access..." security prompt.
        return StaRunner.Run(() => Execute(input)).WaitAsync(TimeSpan.FromMinutes(3), ct);
    }

    static ToolResult Execute(JsonObject input)
    {
        int limit = Math.Clamp(input.Int("limit", 20), 1, 100);
        switch (input.Str("action"))
        {
            case "list":
            case "search":
            {
                var mails = Outlook.ListMail(input.Str("folder", "inbox"), limit, input.Bool("unread_only"), input.Str("action") == "search" ? input.Str("query") : null);
                if (mails.Count == 0) return ToolResult.Text("(no messages)");
                var sb = new StringBuilder();
                foreach (var m in mails)
                {
                    sb.AppendLine($"id {m.Id}\n  {m.Received:yyyy-MM-dd HH:mm} | {m.From} | {m.Subject}{(m.Unread ? " [UNREAD]" : "")}");
                    if (!string.IsNullOrWhiteSpace(m.Preview)) sb.AppendLine("  " + m.Preview.Trim());
                }
                return ToolResult.Text(sb.ToString());
            }
            case "read":
                return ToolResult.Text(Json.Truncate(Outlook.ReadMail(input.Str("id"), input.Bool("mark_read")), 30000));
            case "send":
            case "draft":
            case "reply":
            {
                var atts = (input["attachments"] as JsonArray ?? new JsonArray()).Select(a => Environment.ExpandEnvironmentVariables(a!.GetValue<string>())).ToList();
                var missing = atts.FirstOrDefault(a => !File.Exists(a));
                if (missing != null) return ToolResult.Error($"Attachment not found: {missing}");
                var r = Outlook.Send(input.Str("to"), input.Str("cc"), input.Str("subject"), input.Str("body"), atts,
                    input.Str("action") == "draft", input.Str("action") == "reply" ? input.Str("id") : null);
                return ToolResult.Text(r);
            }
            case "calendar":
            {
                var from = DateTime.TryParse(input.Str("from_date"), out var d) ? d.Date : DateTime.Today;
                var days = Math.Clamp(input.Int("days", 1), 1, 60);
                var events = Outlook.Events(from, from.AddDays(days));
                if (events.Count == 0) return ToolResult.Text("No events.");
                var sb = new StringBuilder();
                foreach (var e in events.OrderBy(e => e.Start))
                    sb.AppendLine($"{e.Start:ddd yyyy-MM-dd HH:mm}-{e.End:HH:mm} | {e.Subject} | {e.Organizer}{(e.TeamsUrl != null ? "\n  Teams: " + e.TeamsUrl : "")}{(string.IsNullOrWhiteSpace(e.Location) ? "" : "\n  Location: " + e.Location)}");
                return ToolResult.Text(sb.ToString());
            }
            default:
                return ToolResult.Error("Unknown action.");
        }
    }
}

/// <summary>Runs a function on a fresh STA thread (needed for COM apps like Outlook and for UI Automation).</summary>
public static class StaRunner
{
    public static Task<T> Run<T>(Func<T> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(fn()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
