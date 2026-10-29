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

    async Task ReviewAsync()
    {
        if (!_cfg.Watcher.Enabled || string.IsNullOrEmpty(_cfg.ApiKey)) return;
        if ((DateTime.Now - _lastReview).TotalMinutes < Math.Max(3, _cfg.Watcher.SuggestEveryMinutes)) return;
        if (Native.IdleSeconds() > 120) return; // only suggest while the user is actually working
        if (Interlocked.Exchange(ref _reviewing, 1) == 1) return;
        try
        {
            var since = _lastReview;
            _lastReview = DateTime.Now;
            List<Span> recent;
            lock (_gate)
            {
                recent = _spans.Where(s => s.End >= since).ToList();
                if (_open != null) recent.Add(_open);
            }
            var active = recent.Where(s => s.App != "(away)").ToList();
            if (active.Sum(s => (s.End - s.Start).TotalMinutes) < 2) return;

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
                Be selective: only suggest when it is concrete and clearly useful. Most of the time the right answer is no suggestion.
                Reply with ONLY a JSON object, either {"suggest": false} or
                {"suggest": true, "title": "<= 6 words", "message": "one sentence for the user", "prompt": "a complete instruction Thokolosi can execute"}.

                The user's job description for Thokolosi:
                {{AppConfig.LoadJob()}}
                """;

            var reply = await _claude.CompleteAsync(_cfg.ApiKey!, _cfg.Watcher.Model, system, content, 600, CancellationToken.None);
            var json = ExtractJson(reply);
            if (json?["suggest"]?.GetValue<bool>() != true) return;

            var sug = new Suggestion(
                json["title"]?.GetValue<string>() ?? "Suggestion",
                json["message"]?.GetValue<string>() ?? "",
                json["prompt"]?.GetValue<string>() ?? "");
            if (sug.Prompt.Length == 0) return;
            _recentSuggestions.Enqueue(sug.Title);
            while (_recentSuggestions.Count > 10) _recentSuggestions.Dequeue();
            ActivityLog.Write("SUGGEST", $"{sug.Title}: {sug.Message}");
            Suggested?.Invoke(sug);
        }
        catch (Exception ex) { ActivityLog.Write("SUGGEST", "Review failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _reviewing, 0); }
    }

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
