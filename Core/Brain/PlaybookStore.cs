using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Brain;

public record Playbook(string Name, string Description, string Steps, DateTime Updated);

/// <summary>
/// Saved procedures ("playbooks"): markdown files in %APPDATA%\Thokolosi\playbooks. Teach Thokolosi a routine once,
/// then run it by name ("/deploy-easypoultry", "run the morning brief", or from the phone).
/// </summary>
public static class PlaybookStore
{
    static readonly object Gate = new();
    public static string Dir => Path.Combine(AppConfig.Dir, "playbooks");
    public static event Action<Playbook>? Saved;
    public static event Action<string>? Deleted;

    public static string Slug(string name) => Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    public static List<Playbook> All()
    {
        EnsureSeeded();
        lock (Gate)
            return Directory.GetFiles(Dir, "*.md").Select(Parse).Where(p => p != null).Cast<Playbook>().OrderBy(p => p.Name).ToList();
    }

    public static Playbook? Find(string name)
    {
        var slug = Slug(name);
        return All().FirstOrDefault(p => p.Name == slug) ?? All().FirstOrDefault(p => p.Name.Contains(slug));
    }

    public static Playbook Save(string name, string description, string steps, DateTime? updated = null, bool raiseEvent = true)
    {
        var p = new Playbook(Slug(name), description.Trim(), steps.Trim(), updated ?? DateTime.Now);
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, p.Name + ".md");
            File.WriteAllText(path, $"---\nname: {p.Name}\ndescription: {p.Description.Replace("\n", " ")}\n---\n\n{p.Steps}\n");
            File.SetLastWriteTime(path, p.Updated);
        }
        if (raiseEvent) Saved?.Invoke(p);
        return p;
    }

    public static bool Delete(string name, bool raiseEvent = true)
    {
        var path = Path.Combine(Dir, Slug(name) + ".md");
        lock (Gate)
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
        }
        if (raiseEvent) Deleted?.Invoke(Slug(name));
        return true;
    }

    static Playbook? Parse(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var m = Regex.Match(text, @"^---\s*\n(.*?)\n---\s*\n?(.*)$", RegexOptions.Singleline);
            string name = Path.GetFileNameWithoutExtension(path), desc = "", steps = text;
            if (m.Success)
            {
                foreach (var line in m.Groups[1].Value.Split('\n'))
                {
                    var kv = line.Split(':', 2);
                    if (kv.Length < 2) continue;
                    if (kv[0].Trim() == "description") desc = kv[1].Trim();
                }
                steps = m.Groups[2].Value.Trim();
            }
            return new Playbook(name, desc, steps, File.GetLastWriteTime(path));
        }
        catch { return null; }
    }

    public static string ForPrompt()
    {
        var all = All();
        if (all.Count == 0) return "(none yet)";
        return string.Join("\n", all.Select(p => $"- {p.Name}: {p.Description}"));
    }

    static bool _seeded;

    /// <summary>First run: install starter playbooks for the user's domains.</summary>
    static void EnsureSeeded()
    {
        if (_seeded) return;
        _seeded = true;
        if (Directory.Exists(Dir)) return;
        Directory.CreateDirectory(Dir);
        foreach (var (name, desc, steps) in Starters)
            Save(name, desc, steps, raiseEvent: false);
    }

    static readonly (string, string, string)[] Starters =
    {
        ("morning-brief", "Daily start: unread email, Slack mentions, today's meetings, top 3 priorities",
         """
         1. Check unread email (outlook tool if available, else email tool / MCP email) from the last 24h. Group: needs reply / FYI / ignore.
         2. Check Slack for mentions and DMs since yesterday (slack tool or MCP).
         3. List today's meetings from the Outlook calendar (meetings upcoming, 14 hours).
         4. Check monitors (if any) for overnight alerts.
         5. Reply with a brief: max ~12 lines, then suggest the top 3 priorities. Don't send anything.
         """),
        ("stroke5-news-leads", "Stroke 5 News: find today's story leads for Aliwal North / Joe Gqabi / Eastern Cape",
         """
         1. web_search recent news (last 48h) for: Aliwal North; Walter Sisulu Local Municipality; Joe Gqabi District; Eastern Cape crime, service delivery, water, electricity, roads; Maletswai; Burgersdorp; Sterkspruit.
         2. Also check the SAPS Eastern Cape and municipality social pages via the browser if search is thin.
         3. Pick 5-8 leads that fit the pillars: municipal accountability, crime & safety, community voice, culture, human interest/alerts.
         4. For each: headline idea, why it matters locally, the angle, 2 people to contact, sources (links).
         5. Save to Documents\Thokolosi\Stroke5\leads-YYYY-MM-DD.md and summarise in chat.
         """),
        ("stroke5-edit-draft", "Stroke 5 News: edit a journalist's draft to house standard",
         """
         1. Ask for (or read) the draft file / pasted text.
         2. Edit for: clear lede (who/what/where/when), short paragraphs, local context, fairness (right of reply), attributed claims, no defamation risk, SA English spelling.
         3. Produce: headline (max 70 chars), 2 alternative headlines, standfirst, edited article, a list of facts to verify, and a Facebook post (max 60 words + 3 hashtags).
         4. Save next to the original as *-edited.md (or Documents\Thokolosi\Stroke5\drafts) and show a short summary of changes.
         """),
        ("stroke5-schedule-post", "Stroke 5 News: schedule an article to the Facebook page",
         """
         1. Get the article link/text and the time to publish (default: next 07:00 or 18:00 slot).
         2. Write the post: hook line, 2-3 sentences, link, 3 local hashtags (#AliwalNorth #Stroke5News + topic).
         3. Show the draft and the scheduled time; after the user confirms, use the facebook tool (post with schedule_at).
         """),
        ("stroke5-assign-stories", "Stroke 5 News: assign this week's stories to the journalists",
         """
         1. Load the latest leads file (Documents\Thokolosi\Stroke5) or run stroke5-news-leads first.
         2. Ask which journalists are available if not in memory (remember the team list once given).
         3. Match leads to journalists by pillar/area; give each: brief, angle, contacts, deadline.
         4. Draft the assignment messages (email/Slack/WhatsApp per memory) and show them; send only after confirmation.
         """),
        ("consulting-weekly-timesheet", "Consulting: build last week's timesheet from activity + git history",
         """
         1. Use the timesheet tool for last Monday-Sunday.
         2. Attribute time to clients/projects using window titles, repo names and memory (ask once if unclear, then remember the mapping).
         3. Round to 15 minutes, drop personal/away time, list tasks per day.
         4. Save Documents\Thokolosi\Consulting\timesheet-YYYY-Www.csv (date, client, project, hours, description) and show totals per client.
         """),
        ("consulting-invoice-draft", "Consulting: draft an invoice for a client from the timesheet",
         """
         1. Ask which client and period (default: last month). Get the rate from memory or ask once and remember it.
         2. Use timesheet data (or the saved CSVs) to total hours and list work items.
         3. Draft the invoice (company details from memory, VAT if registered, due in 30 days) as a Word/markdown file in Documents\Thokolosi\Consulting\invoices.
         4. Draft the covering email; don't send until confirmed.
         """),
        ("consulting-client-update", "Consulting: weekly status email to a client",
         """
         1. Gather the week's commits for the client's repos and timesheet notes.
         2. Write: done this week, next week, blockers/decisions needed. Plain, short, no jargon.
         3. Show the email draft; send via outlook/email only after confirmation.
         """),
        ("solar-daily-report", "Farm: daily solar + battery report from the monitors",
         """
         1. monitors history for the solar monitor over the last 24h.
         2. Report: min/max battery SOC, PV peak and estimated kWh, load peak, any alerts, grid availability.
         3. Flag problems (SOC below 20%, no PV during the day, faults) and suggest one action.
         4. Append a line to Documents\Thokolosi\Farm\solar-log.csv.
         """),
        ("farm-incubator-check", "Farm: check the incubator readings and hatch schedule",
         """
         1. monitors list / history for the incubator monitor (temperature, humidity).
         2. Compare with targets: 37.5 C (37.2-37.8), humidity 45-55% (days 1-18) and 65-75% (lockdown days 19-21). Use memory for the current batch's set date.
         3. Report status, day of incubation, next action (candling day 7 and 14, lockdown day 18), and anything out of range.
         """),
    };
}

public class PlaybookTool : ITool
{
    public string Name => "playbook";

    public JsonObject Definition { get; } = Json.Def("playbook",
        "Saved procedures the user can run by name. Actions: list, read (get the steps, then carry them out), save (create/update a playbook " +
        "when the user teaches you a routine or asks you to remember how to do something), delete. Write steps as a numbered list that a future you can follow.",
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "read", "save", "delete"] },
            "name": { "type": "string", "description": "short-kebab-case name" },
            "description": { "type": "string", "description": "save: one line saying what it does" },
            "steps": { "type": "string", "description": "save: numbered steps" }
          },
          "required": ["action"]
        }
        """);

    public bool NeedsApproval(JsonObject input) => input.Str("action") == "delete";
    public string Describe(JsonObject input) => $"Playbook: {input.Str("action")} {input.Str("name")}";

    public Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        var name = input.Str("name");
        switch (input.Str("action"))
        {
            case "read":
                var p = PlaybookStore.Find(name);
                return Task.FromResult(p == null ? ToolResult.Error($"No playbook '{name}'. Use list.") : ToolResult.Text($"# {p.Name}\n{p.Description}\n\n{p.Steps}"));
            case "save":
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(input.Str("steps"))) return Task.FromResult(ToolResult.Error("name and steps are required."));
                var saved = PlaybookStore.Save(name, input.Str("description"), input.Str("steps"));
                return Task.FromResult(ToolResult.Text($"Saved playbook '{saved.Name}'. The user can run it with /{saved.Name}."));
            case "delete":
                return Task.FromResult(ToolResult.Text(PlaybookStore.Delete(name) ? "Deleted." : "Not found."));
            default:
                var sb = new StringBuilder();
                foreach (var pb in PlaybookStore.All()) sb.AppendLine($"{pb.Name}: {pb.Description}");
                return Task.FromResult(ToolResult.Text(sb.Length == 0 ? "No playbooks yet." : sb.ToString()));
        }
    }
}
