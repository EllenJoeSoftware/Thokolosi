using System.Text;
using System.Text.Json.Nodes;
using Thokolosi.Core.Monitors;

namespace Thokolosi.Core.Tools;

/// <summary>Read-only access to background monitors (solar inverter, incubator...).</summary>
public class MonitorsTool(MonitorService service) : ITool
{
    public string Name => "monitors";

    public JsonObject Definition { get; } = Json.Def("monitors",
        "Background monitors (e.g. solar inverter via SolarAssistant, egg incubator). Actions: list (status, latest values, active alerts), " +
        "history (name, hours: min/avg/max per value and a compact series), check_now (poll now; optional name).",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "history", "check_now"] },
            "name": { "type": "string", "description": "monitor name" },
            "hours": { "type": "number", "description": "history: hours back (default 24)" },
            "points": { "type": "integer", "description": "history: series points per value (default 24)" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;

    public string Describe(JsonObject input) => input.Str("action") switch
    {
        "history" => $"Monitors: history of {input.Str("name")}",
        "check_now" => $"Monitors: checking {(input.Str("name") is { Length: > 0 } n ? n : "all")} now",
        _ => "Monitors: status"
    };

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            switch (input.Str("action"))
            {
                case "list":
                    return ToolResult.Text(Format(service.Status()));
                case "check_now":
                    return ToolResult.Text(Format(await service.CheckNowAsync(input.Str("name"), ct)));
                case "history":
                {
                    var name = input.Str("name");
                    if (string.IsNullOrWhiteSpace(name)) return ToolResult.Error("name is required.");
                    double hours = 24;
                    if (input["hours"] is JsonNode h && double.TryParse(h.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var hv)) hours = hv;
                    return ToolResult.Text(Json.Truncate(service.History(name, hours, input.Int("points", 24)), 30000));
                }
                default:
                    return ToolResult.Error("Unknown action.");
            }
        }
        catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
    }

    static string Format(IReadOnlyList<MonitorStatus> list)
    {
        if (list.Count == 0) return "No monitors configured. The user can add them in Settings (e.g. the SolarAssistant preset).";
        var sb = new StringBuilder();
        var now = DateTime.Now;
        foreach (var m in list)
        {
            var state = !m.Enabled ? "disabled" : m.Connected ? "ok" : "NOT CONNECTED";
            sb.AppendLine($"## {m.Name} ({m.Kind}, {state}; last data {(m.LastData is DateTime d ? Ago(now - d) : "never")})");
            if (m.LastError != null) sb.AppendLine($"  error: {m.LastError}");
            foreach (var v in m.Values)
            {
                if (v.Time == DateTime.MinValue) { sb.AppendLine($"  {v.Name}: (no data)"); continue; }
                var val = v.Number is double n ? MonitorService.Fmt(n) : v.Raw;
                sb.AppendLine($"  {v.Name}: {val}{v.Unit} ({Ago(now - v.Time)})");
            }
            foreach (var r in m.Rules)
            {
                var bits = new List<string>();
                if (r.Error != null) bits.Add("INVALID: " + r.Error);
                if (r.Active) bits.Add("ALERTING");
                if (r.LastAlert is DateTime la) bits.Add($"last alert {la:MM-dd HH:mm}");
                sb.AppendLine($"  rule '{r.When}'{(bits.Count > 0 ? " - " + string.Join(", ", bits) : "")}");
            }
        }
        return Json.Truncate(sb.ToString(), 20000);
    }

    static string Ago(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{Math.Max(0, (int)t.TotalSeconds)}s ago" : t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} min ago" :
        t.TotalHours < 48 ? $"{t.TotalHours:0.#} h ago" : $"{t.TotalDays:0} days ago";
}
