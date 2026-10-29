using System.Globalization;

namespace Thokolosi.Core;

/// <summary>Runs saved prompts at set times ("every weekday at 07:30, summarise my unread email and Slack").</summary>
public sealed class Scheduler : IDisposable
{
    readonly AppConfig _cfg;
    readonly Func<ScheduledJob, Task> _run;
    readonly System.Threading.Timer _timer;
    int _busy;

    public Scheduler(AppConfig cfg, Func<ScheduledJob, Task> run)
    {
        _cfg = cfg;
        _run = run;
        _timer = new System.Threading.Timer(_ => Tick(), null, 30_000, 30_000);
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            foreach (var job in _cfg.Schedules.ToList())
            {
                if (!job.Enabled || string.IsNullOrWhiteSpace(job.Prompt)) continue;
                if (!TimeSpan.TryParse(job.Time, CultureInfo.InvariantCulture, out var at)) continue;
                var due = now.Date + at;
                if (now < due || now > due.AddHours(2)) continue;           // run within 2h of the time (covers a late PC start)
                if (job.LastRun.HasValue && job.LastRun.Value >= due) continue;
                if (!DayMatches(job.Days, now.DayOfWeek)) continue;

                job.LastRun = now;
                _cfg.Save();
                ActivityLog.Write("SCHEDULE", $"Running '{job.Name}'");
                _ = _run(job);
            }
        }
        catch (Exception ex) { ActivityLog.Write("SCHEDULE", "Error: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public static bool DayMatches(string days, DayOfWeek d)
    {
        days = (days ?? "daily").Trim().ToLowerInvariant();
        if (days is "" or "daily" or "every day" or "everyday") return true;
        if (days == "weekdays") return d is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        if (days == "weekends") return d is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var abbr = d.ToString()[..3].ToLowerInvariant();
        return days.Split(',', ' ', ';').Any(p => p.Trim().StartsWith(abbr));
    }

    /// <summary>Text format used in Settings: one job per line "07:30 | weekdays | Name | prompt".</summary>
    public static string ToText(IEnumerable<ScheduledJob> jobs) =>
        string.Join(Environment.NewLine, jobs.Select(j => $"{(j.Enabled ? "" : "# ")}{j.Time} | {j.Days} | {j.Name} | {j.Prompt}"));

    public static List<ScheduledJob> FromText(string text, IEnumerable<ScheduledJob> existing)
    {
        var old = existing.ToDictionary(j => j.Name + "|" + j.Prompt, j => j);
        var list = new List<ScheduledJob>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            bool enabled = !line.StartsWith('#');
            line = line.TrimStart('#').Trim();
            var parts = line.Split('|', 4);
            if (parts.Length < 4) continue;
            var job = new ScheduledJob
            {
                Time = parts[0].Trim(), Days = parts[1].Trim(), Name = parts[2].Trim(), Prompt = parts[3].Trim(), Enabled = enabled
            };
            if (old.TryGetValue(job.Name + "|" + job.Prompt, out var prev)) job.LastRun = prev.LastRun;
            list.Add(job);
        }
        return list;
    }

    public void Dispose() => _timer.Dispose();
}
