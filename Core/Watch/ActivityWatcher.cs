using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Watch;

public record Suggestion(string Title, string Message, string Prompt);

/// <summary>
/// Keeps a timeline of which app/window you're using (no keystrokes, no content), and every few minutes asks
/// a cheap Claude model whether there's something Thokolosi could do to help or automate. Suggestions arrive
/// as tray notifications; clicking one puts the task in the chat box for you to send.
/// The timeline is also written to %APPDATA%\Thokolosi\activity\YYYY-MM-DD.log so you can ask "what did I work on today?".
/// </summary>
public sealed class ActivityWatcher : IDisposable
{
    record Span(DateTime Start, DateTime End, string App, string Title);

    readonly AppConfig _cfg;
    readonly ClaudeClient _claude = new();
    readonly System.Threading.Timer _sampler;
    readonly System.Threading.Timer _reviewer;
    readonly List<Span> _spans = new();
    readonly Queue<string> _recentSuggestions = new();
    readonly object _gate = new();
    Span? _open;
    DateTime _lastReview = DateTime.Now;
    int _reviewing;

    public event Action<Suggestion>? Suggested;

    public ActivityWatcher(AppConfig cfg)
    {
        _cfg = cfg;
        _sampler = new System.Threading.Timer(_ => Sample(), null, 3000, 5000);
        _reviewer = new System.Threading.Timer(_ => _ = ReviewAsync(), null, 60_000, 60_000);
        _lastReview = DateTime.Now.AddMinutes(-(Math.Max(2, cfg.Watcher.SuggestEveryMinutes) - 2)); // first review ~2 min after start
    }

    bool Excluded(string app, string title) =>
        _cfg.Watcher.Exclude.Any(x => !string.IsNullOrWhiteSpace(x) &&
            (app.Contains(x, StringComparison.OrdinalIgnoreCase) || title.Contains(x, StringComparison.OrdinalIgnoreCase)));

    void Sample()
    {
        if (!_cfg.Watcher.Enabled) return;
        try
        {
            var h = Native.GetForegroundWindow();
            var app = Native.ProcessName(h);
            var title = Native.WindowTitle(h);
            if (Native.IdleSeconds() > 300) { app = "(away)"; title = ""; }
            else if (Excluded(app, title)) title = "(private)";
            if (app == "Thokolosi") return;

            lock (_gate)
            {
                var now = DateTime.Now;
                if (_open != null && _open.App == app && _open.Title == title)
                {
                    _open = _open with { End = now };
                    return;
                }
                if (_open != null) Close(_open);
                _open = new Span(now, now, app, title);
            }
        }
        catch { }
    }

    void Close(Span s)
    {
        if ((s.End - s.Start).TotalSeconds < 4) return;
        _spans.Add(s);
        if (_spans.Count > 2000) _spans.RemoveRange(0, 500);
        try
        {
            Directory.CreateDirectory(AppConfig.ActivityDir);
            File.AppendAllText(Path.Combine(AppConfig.ActivityDir, $"{s.Start:yyyy-MM-dd}.log"),
                $"{s.Start:HH:mm:ss}-{s.End:HH:mm:ss}  {s.App,-18} {s.Title}\n");
        }
        catch { }
    }

    /// <summary>
    /// A short, always-ready description of what the user is doing right now (from window titles only, no content),
    /// added to every agent request so "what am I busy with?" can be answered instantly without any tool call.
    /// Returns null when watching is off. Private/excluded windows show as "(private)".
    /// </summary>
    public string? Snapshot()
    {
        if (!_cfg.Watcher.Enabled) return null;
        try
        {
            var now = DateTime.Now;
            double idle = Native.IdleSeconds();
            var sb = new StringBuilder();
            lock (_gate)
            {
                if (_open == null) sb.Append("Right now: no activity recorded yet since Thokolosi started. ");
                else if (_open.App == "(away)") sb.Append($"Right now: away from the PC (idle about {Math.Max(idle / 60, (now - _open.Start).TotalMinutes):0} min). ");
                else
                {
                    var t = string.IsNullOrWhiteSpace(_open.Title) ? "(no window title)" : _open.Title;
                    if (t.Length > 140) t = t[..140] + "...";
                    sb.Append($"Right now: {_open.App}: {t} (in this window {Math.Max(0, (now - _open.Start).TotalMinutes):0} min, since {_open.Start:HH:mm}; idle {idle:0}s). ");
                }

                var since = now.AddMinutes(-60);
                var recent = _spans.Where(s => s.End >= since && s.App != "(away)").ToList();
                if (_open != null && _open.App != "(away)") recent.Add(_open);
                if (recent.Count > 0)
                {
                    var totals = recent.GroupBy(s => s.App)
                        .Select(g => (App: g.Key, Min: g.Sum(s => (s.End - s.Start).TotalMinutes)))
                        .Where(x => x.Min >= 1).OrderByDescending(x => x.Min).Take(6)
                        .Select(x => $"{x.App} {x.Min:0} min");
                    var totalsList = totals.ToList();
                    if (totalsList.Count > 0) sb.Append("Last 60 min by app: " + string.Join(", ", totalsList) + ". ");
                    var titles = recent.Where(s => !string.IsNullOrWhiteSpace(s.Title) && (s.End - s.Start).TotalSeconds >= 20)
                        .OrderByDescending(s => s.End).Select(s => $"{s.End:HH:mm} {s.App}: {(s.Title.Length > 80 ? s.Title[..80] + "..." : s.Title)}")
                        .Distinct().Take(6);
                    var titlesList = titles.ToList();
                    if (titlesList.Count > 0) sb.Append("Recent windows (newest first): " + string.Join(" | ", titlesList) + ".");
                }
            }
            // Every open window (titles only), so the whole desktop is known at a glance. Private/excluded ones are masked.
            try
            {
                var wins = OpenWindows.List().Where(w => w.App != "Thokolosi").ToList();
                if (wins.Count > 0)
                {
                    string Fmt(OpenWindows.Win w)
                    {
                        var t = Excluded(w.App, w.Title) ? "(private)" : (w.Title.Length > 60 ? w.Title[..60] + "..." : w.Title);
                        return $"{w.App}: {t}{(w.Minimized ? " [min]" : "")}";
                    }
                    sb.Append($" Open windows ({wins.Count}, front to back): " + string.Join(" | ", wins.Take(30).Select(Fmt)) + (wins.Count > 30 ? " | ..." : "") + ".");
                }
            }
            catch { }
            return sb.ToString().Trim();
        }
        catch { return null; }
    }

    /// <summary>Review right away (tray "Suggest something now"), ignoring the interval and idle checks.</summary>
    public Task ReviewNowAsync() => ReviewAsync(force: true);

    /// <summary>Last review outcome, for the tray tooltip / troubleshooting.</summary>
    public string LastResult { get; private set; } = "No review yet";

    async Task ReviewAsync(bool force = false)
    {
        if (!_cfg.Watcher.Enabled && !force) return;
        var model = string.IsNullOrWhiteSpace(_cfg.Watcher.Model) ? _cfg.Model : _cfg.Watcher.Model;
        if (!_cfg.HasKeyFor(model)) { if (force) Report($"No API key set for {model}."); return; }
        if (!force)
        {
            if ((DateTime.Now - _lastReview).TotalMinutes < Math.Max(2, _cfg.Watcher.SuggestEveryMinutes)) return;
            if (Native.IdleSeconds() > 120) return; // only suggest while the user is actually working
        }
        if (Interlocked.Exchange(ref _reviewing, 1) == 1) return;
        try
        {
            // Look back over the review window (at least 10 minutes) so a quiet stretch still has context.
            var since = force ? DateTime.Now.AddMinutes(-30)
                              : DateTime.Now.AddMinutes(-Math.Max(10, _cfg.Watcher.SuggestEveryMinutes * 2));
            _lastReview = DateTime.Now;
            List<Span> recent;
            lock (_gate)
            {
                recent = _spans.Where(s => s.End >= since).ToList();
                if (_open != null) recent.Add(_open);
            }
            var active = recent.Where(s => s.App != "(away)").ToList();
            if (!force && active.Sum(s => (s.End - s.Start).TotalMinutes) < 1) { Report("Skipped: under a minute of activity."); return; }

            var timeline = new StringBuilder();
            foreach (var s in active.TakeLast(80))
                timeline.AppendLine($"{s.Start:HH:mm:ss} ({(s.End - s.Start).TotalSeconds:0}s) {s.App}: {s.Title}");
            // Window switching summary helps spot repetitive back-and-forth.
            var totals = active.GroupBy(s => s.App).Select(g => $"{g.Key}: {g.Sum(s => (s.End - s.Start).TotalMinutes):0.#} min, {g.Count()} switches");

            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] =
                    $"Time now: {DateTime.Now:ddd HH:mm}\n\nApp totals since {since:HH:mm}:\n{string.Join("\n", totals)}\n\nWindow timeline:\n{timeline}\n\n" +
                    $"Suggestions already made recently (don't repeat): {string.Join("; ", _recentSuggestions)}" }
            };

            var fg = Native.GetForegroundWindow();
            if (_cfg.Watcher.UseScreenshots && !Excluded(Native.ProcessName(fg), Native.WindowTitle(fg)))
            {
                var jpg = ScreenshotTool.Capture(1024);
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpg) }
                });
            }

            var system = $$"""
                You are the "watch and suggest" part of Thokolosi, a personal AI agent on the user's Windows PC. Thokolosi can run
                PowerShell, edit files and code, use git, browse the web, read/send email (Outlook/IMAP), use Slack, control desktop apps,
                join Teams meetings and take notes, and run scheduled jobs.
                Look at what the user has been doing and decide whether Thokolosi could save them real time right now: automate a
                repetitive sequence, finish a tedious step, find information they seem to be hunting for, fix an error on screen, or set up a schedule.
                Good suggestions are specific to what is on screen: summarise the long article/video/thread they're reading, draft the reply
                to the email they're looking at, fix the error in their terminal or build log, automate a repetitive back-and-forth between
                two apps, pull together information they keep searching for, or set up a recurring job for something they do often.
                Use the user's job description below to judge what matters to them. Suggest whenever there is a concrete, useful action;
                stay quiet only when nothing on screen gives Thokolosi a real job (idle browsing with nothing actionable, or something
                you already suggested).
                Reply with ONLY a JSON object (no prose, no code fence), either
                {"suggest": false, "reason": "a few words"} or
                {"suggest": true, "title": "<= 6 words", "message": "one sentence for the user", "prompt": "a complete instruction Thokolosi can execute"}.

                The user's job description for Thokolosi:
                {{AppConfig.LoadJob()}}
                """;

            var reply = await _claude.CompleteAsync(_cfg.KeyFor(model), model, system, content, 600, CancellationToken.None);
            var json = ExtractJson(reply) as JsonObject;
            if (json == null) { Report("Unreadable reply: " + Trim(reply)); return; }
            if (!IsTrue(json["suggest"])) { Report("No suggestion: " + (Str(json["reason"]) ?? "nothing actionable")); return; }

            var sug = new Suggestion(Str(json["title"]) ?? "Suggestion", Str(json["message"]) ?? "", Str(json["prompt"]) ?? "");
            if (sug.Prompt.Length == 0) { Report("Suggestion had no task: " + Trim(reply)); return; }
            LastResult = $"{DateTime.Now:HH:mm} suggested: {sug.Title}";
            _recentSuggestions.Enqueue(sug.Title);
            while (_recentSuggestions.Count > 10) _recentSuggestions.Dequeue();
            ActivityLog.Write("SUGGEST", $"{sug.Title}: {sug.Message}");
            Suggested?.Invoke(sug);
        }
        catch (Exception ex) { Report("Review failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _reviewing, 0); }
    }

    void Report(string text)
    {
        LastResult = $"{DateTime.Now:HH:mm} {text}";
        ActivityLog.Write("WATCH", text);
    }

    static string Trim(string s) => s.Length > 200 ? s[..200] + "..." : s;

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    static bool IsTrue(JsonNode? n) => n is JsonValue v &&
        ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase)));

    static JsonNode? ExtractJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(s[a..(b + 1)]); } catch { return null; }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _reviewer.Dispose();
        lock (_gate) if (_open != null) Close(_open);
    }
}


