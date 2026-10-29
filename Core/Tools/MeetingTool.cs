using System.Text;
using System.Text.Json.Nodes;
using Thokolosi.Core.Meetings;

namespace Thokolosi.Core.Tools;

public class MeetingTool(MeetingService meetings) : ITool
{
    public string Name => "meetings";

    public JsonObject Definition { get; } = Json.Def("meetings",
        "Teams meetings and meeting notes. Actions: upcoming (meetings from the Outlook calendar with Teams links), " +
        "join (join a Teams meeting in the Teams app by url, or 'next' for the next one, and take notes), " +
        "start_notes (record + transcribe whatever meeting/call is happening now), stop_notes (finish and write notes), " +
        "leave (leave the Teams call and finish notes), status. Notes are saved as markdown in Documents\\Thokolosi\\Meetings.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["upcoming", "join", "start_notes", "stop_notes", "leave", "status"] },
            "url": { "type": "string", "description": "join: Teams meeting link, or 'next'." },
            "subject": { "type": "string", "description": "join/start_notes: meeting name for the notes." },
            "take_notes": { "type": "boolean", "description": "join: record and write notes (default true)." },
            "hours": { "type": "integer", "description": "upcoming: look ahead this many hours (default 12)." }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") is "join" or "start_notes" or "leave";

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "join" => $"Meetings: joining {input.Str("subject", input.Str("url"))} in Teams{(input.Bool("take_notes", true) ? " and taking notes (records meeting audio)" : "")}",
        "start_notes" => $"Meetings: start recording notes for '{input.Str("subject")}'",
        var a => $"Meetings: {a}"
    };

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        switch (input.Str("action"))
        {
            case "status":
                return ToolResult.Text(meetings.Status);
            case "upcoming":
            {
                var events = await meetings.UpcomingAsync(Math.Clamp(input.Int("hours", 12), 1, 24 * 14));
                if (events.Count == 0) return ToolResult.Text("No upcoming meetings.");
                var sb = new StringBuilder();
                foreach (var e in events)
                    sb.AppendLine($"{e.Start:ddd HH:mm}-{e.End:HH:mm} {e.Subject}{(e.TeamsUrl != null ? "\n  " + e.TeamsUrl : " (no Teams link)")}");
                return ToolResult.Text(sb.ToString());
            }
            case "join":
            {
                var url = input.Str("url");
                var subject = input.Str("subject", "Teams meeting");
                DateTime? end = null;
                if (string.IsNullOrWhiteSpace(url) || url.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    var next = (await meetings.UpcomingAsync(12)).FirstOrDefault(e => e.TeamsUrl != null);
                    if (next == null) return ToolResult.Error("No upcoming Teams meeting found in the Outlook calendar.");
                    url = next.TeamsUrl!;
                    subject = next.Subject;
                    end = next.End;
                }
                return ToolResult.Text(await meetings.JoinAsync(url, subject, end, input.Bool("take_notes", true), ct));
            }
            case "start_notes":
                return ToolResult.Text(meetings.StartNotes(input.Str("subject", "Meeting")));
            case "stop_notes":
                return ToolResult.Text(meetings.StopNotes());
            case "leave":
                return ToolResult.Text(await meetings.LeaveAsync());
            default:
                return ToolResult.Error("Unknown action.");
        }
    }
}
