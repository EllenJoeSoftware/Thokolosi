using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thokolosi.Core.Tools;

/// <summary>
/// Aggregates the activity timeline (%APPDATA%\Thokolosi\activity\YYYY-MM-DD.log) and the user's git commits
/// per day, so the model can attribute time to clients/projects for invoicing.
/// </summary>
public class TimesheetTool(Func<List<string>> codeFolders) : ITool
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "packages", ".venv", "venv", "__pycache__", ".next", "Pods", "AppData" };

    static readonly Regex LineRx = new(@"^(\d{2}:\d{2}:\d{2})-(\d{2}:\d{2}:\d{2})  (.*)$", RegexOptions.Compiled);

    record Commit(DateTime Time, string Hash, string Subject);

    public string Name => "timesheet";

    public JsonObject Definition { get; } = Json.Def("timesheet",
        "Timesheet data for a date range: per day, active minutes per app with top window titles (from the activity timeline, " +
        "needs the activity watcher on), plus the user's git commits per repo in the code folders. Use it to attribute time to clients/projects.",
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "yyyy-MM-dd (default today)" },
            "to": { "type": "string", "description": "yyyy-MM-dd inclusive (default = from)" },
            "titles_per_app": { "type": "integer", "description": "top window titles per app per day (default 6)" },
            "include_commits": { "type": "boolean", "description": "default true" }
          }
        }
        """);

    public bool NeedsApproval(JsonObject input) => false;

    public string Describe(JsonObject input) =>
        $"Timesheet {(input.Str("from") is { Length: > 0 } f ? f : "today")}{(input.Str("to") is { Length: > 0 } t ? " to " + t : "")}";

    public async Task<ToolResult> RunAsync(JsonObject input, ToolContext ctx, CancellationToken ct)
    {
        if (!TryDate(input.Str("from"), DateTime.Today, out var from)) return ToolResult.Error("from must be yyyy-MM-dd.");
        if (!TryDate(input.Str("to"), from, out var to)) return ToolResult.Error("to must be yyyy-MM-dd.");
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > 62) return ToolResult.Error("Range too long (max 62 days). Split it up.");
        int titlesPerApp = Math.Clamp(input.Int("titles_per_app", 6), 0, 30);

        var commitsTask = input.Bool("include_commits", true)
            ? Task.Run(() => CollectCommits(from, to, ct), ct)
            : Task.FromResult(new List<(string Repo, string Path, List<Commit> Commits)>());

        var sb = new StringBuilder();
        var totalApps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double totalActive = 0;
        int daysWithActivity = 0;
        var perDay = new Dictionary<DateTime, StringBuilder>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var spans = ReadDay(day);
            var d = new StringBuilder();
            perDay[day] = d;
            if (spans.Count == 0) continue;
            daysWithActivity++;
            double active = spans.Sum(s => s.Minutes);
            totalActive += active;
            var first = spans.Min(s => s.Start); var last = spans.Max(s => s.End);
            d.AppendLine($"Active {Hm(active)} between {first:HH:mm} and {last:HH:mm}");
            foreach (var app in spans.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(s => s.Minutes)))
            {
                var mins = app.Sum(s => s.Minutes);
                totalApps[app.Key] = totalApps.GetValueOrDefault(app.Key) + mins;
                if (mins < 1) continue;
                d.Append($"  {app.Key} {Hm(mins)}");
                var suffix = CommonSuffix(app.Select(s => s.Title));
                var titles = app.Where(s => s.Title.Length > 0)
                    .GroupBy(s => NormalizeTitle(s.Title, suffix), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Title: g.Key, Min: g.Sum(s => s.Minutes)))
                    .Where(t => t.Title.Length > 0 && t.Min >= 1)
                    .OrderByDescending(t => t.Min).Take(titlesPerApp).ToList();
                d.AppendLine(titles.Count == 0 ? "" : ": " + string.Join(" | ", titles.Select(t => $"{t.Title} ({Hm(t.Min)})")));
            }
        }

        List<(string Repo, string Path, List<Commit> Commits)> repos;
        try { repos = await commitsTask; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { repos = new(); sb.AppendLine($"(git scan failed: {ex.Message})"); }

        int totalCommits = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var d = perDay[day];
            var dayCommits = repos.Select(r => (r.Repo, r.Path, C: r.Commits.Where(c => c.Time.Date == day).OrderBy(c => c.Time).ToList()))
                .Where(r => r.C.Count > 0).ToList();
            if (d.Length == 0 && dayCommits.Count == 0) continue;
            sb.AppendLine($"## {day:ddd yyyy-MM-dd}");
            sb.Append(d.Length == 0 ? "No activity timeline for this day.\n" : d.ToString());
            if (dayCommits.Count > 0)
            {
                sb.AppendLine("  Commits:");
                foreach (var r in dayCommits)
                {
                    totalCommits += r.C.Count;
                    sb.AppendLine($"    {r.Repo} ({r.C.Count}): " + string.Join("; ", r.C.Select(c => $"{c.Time:HH:mm} {c.Subject}")));
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine($"## Total {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        sb.AppendLine($"Active {Hm(totalActive)} over {daysWithActivity} day(s) with timeline data.");
        if (totalApps.Count > 0)
            sb.AppendLine("By app: " + string.Join(", ", totalApps.Where(kv => kv.Value >= 1).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {Hm(kv.Value)}")));
        if (repos.Count > 0)
            sb.AppendLine($"Commits: {totalCommits} - " + string.Join(", ", repos.Where(r => r.Commits.Count > 0).OrderByDescending(r => r.Commits.Count)
                .Select(r => $"{r.Repo} {r.Commits.Count} ({r.Path})")));
        if (daysWithActivity == 0)
            sb.AppendLine($"No activity logs found in {AppConfig.ActivityDir}. The activity watcher (Settings > Watch) must be on to record time.");
        return ToolResult.Text(Json.Truncate(sb.ToString(), 60000));
    }

    static bool TryDate(string s, DateTime fallback, out DateTime d)
    {
        s = s.Trim();
        if (s.Length == 0) { d = fallback.Date; return true; }
        var ok = DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        d = d.Date;
        return ok;
    }

    record Span(DateTime Start, DateTime End, string App, string Title)
    {
        public double Minutes => Math.Max(0, (End - Start).TotalMinutes);
    }

    static List<Span> ReadDay(DateTime day)
    {
        var list = new List<Span>();
        var path = Path.Combine(AppConfig.ActivityDir, $"{day:yyyy-MM-dd}.log");
        if (!File.Exists(path)) return list;
        IEnumerable<string> lines;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            lines = sr.ReadToEnd().Split('\n');
        }
        catch { return list; }
        foreach (var raw in lines)
        {
            var m = LineRx.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            // Written as "{app,-18} {title}": the app is padded to 18 chars, longer names just run on to the first space.
            var rest = m.Groups[3].Value;
            string app, title;
            if (rest.Length >= 19 && rest[18] == ' ' && !rest[..18].TrimEnd().Contains(' '))
            {
                app = rest[..18].Trim();
                title = rest[19..].Trim();
            }
            else
            {
                var sp = rest.IndexOf(' ');
                app = (sp < 0 ? rest : rest[..sp]).Trim();
                title = sp < 0 ? "" : rest[(sp + 1)..].Trim();
            }
            if (app.Length == 0) app = "(unknown)";
            if (app == "(away)" || title == "(private)") continue;
            if (!TimeSpan.TryParseExact(m.Groups[1].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var a) ||
                !TimeSpan.TryParseExact(m.Groups[2].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var b)) continue;
            var start = day + a;
            var end = day + b;
            if (end < start) end = day.AddDays(1); // span ran past midnight: count it up to midnight
            list.Add(new Span(start, end, app, title));
        }
        return list;
    }

    /// <summary>The " - App Name" suffix most titles of an app share (e.g. " - Google Chrome"), so it can be stripped.</summary>
    static string? CommonSuffix(IEnumerable<string> titles)
    {
        var all = titles.Where(t => t.Length > 0).ToList();
        if (all.Count == 0) return null;
        var best = all.Select(LastSegment).Where(s => s != null).GroupBy(s => s!).OrderByDescending(g => g.Count()).FirstOrDefault();
        return best != null && best.Count() * 2 >= all.Count ? best.Key : null;
    }

    static readonly string[] Separators = { " - ", " — ", " – ", " | " };

    static string? LastSegment(string title)
    {
        int idx = -1, len = 0;
        foreach (var sep in Separators)
        {
            var i = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (i > idx) { idx = i; len = sep.Length; }
        }
        return idx > 0 ? title[idx..] : null;
    }

    static readonly Regex Noise = new(@"^(\(\d+\)\s*|[●•*]\s*)+|\s*\(\d+\)$", RegexOptions.Compiled);

    static string NormalizeTitle(string title, string? suffix)
    {
        var t = title.Trim();
        if (suffix != null && t.EndsWith(suffix, StringComparison.Ordinal) && t.Length > suffix.Length) t = t[..^suffix.Length];
        t = Noise.Replace(t, "").Trim();
        return t.Length > 90 ? t[..87] + "..." : t;
    }

    static string Hm(double minutes)
    {
        var m = (int)Math.Round(minutes);
        return m >= 60 ? $"{m / 60}h{m % 60:00}m" : $"{m}m";
    }

    // ---------------- git ----------------

    List<(string Repo, string Path, List<Commit> Commits)> CollectCommits(DateTime from, DateTime to, CancellationToken ct)
    {
        var repos = new List<string>();
        foreach (var root in (codeFolders() ?? new()).Select(Environment.ExpandEnvironmentVariables).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(root)) FindRepos(root, 0, repos);

        var result = new List<(string, string, List<Commit>)>();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        var gate = new object();
        Parallel.ForEach(repos.Distinct(StringComparer.OrdinalIgnoreCase), opts, repo =>
        {
            var email = Git(repo, "config user.email").Trim();
            if (email.Length == 0) return;
            var since = from.ToString("yyyy-MM-dd 00:00:00", CultureInfo.InvariantCulture);
            var until = to.AddDays(1).ToString("yyyy-MM-dd 00:00:00", CultureInfo.InvariantCulture);
            var log = Git(repo, $"log --all --no-merges --author=\"{email.Replace("\"", "")}\" --since=\"{since}\" --until=\"{until}\" " +
                                "--date=format-local:%Y-%m-%dT%H:%M:%S --pretty=format:%ad%x09%h%x09%s");
            var commits = new List<Commit>();
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t', 3);
                if (parts.Length < 3 || !DateTime.TryParseExact(parts[0], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) continue;
                if (t.Date < from || t.Date > to) continue;
                var subject = parts[2].Length > 120 ? parts[2][..117] + "..." : parts[2];
                commits.Add(new Commit(t, parts[1], subject));
            }
            if (commits.Count == 0) return;
            lock (gate) result.Add((Path.GetFileName(repo.TrimEnd('\\', '/')), repo, commits.DistinctBy(c => c.Hash).ToList()));
        });
        return result.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void FindRepos(string dir, int depth, List<string> repos)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                repos.Add(dir);
                return; // don't descend into a repo (submodules/nested repos are rare here)
            }
            if (depth >= 3) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                FindRepos(sub, depth + 1, repos);
            }
        }
        catch { /* access denied etc. */ }
    }

    static string Git(string repo, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{repo}\" {args}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var errTask = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } return ""; }
            return outTask.Result;
        }
        catch { return ""; } // git not installed or not a repo
    }
}
