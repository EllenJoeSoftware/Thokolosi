using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Thokolosi.Core.Watch;

namespace Thokolosi.Core.Profile;

/// <summary>
/// A growing, human-readable model of the user, kept at %APPDATA%\Thokolosi\profile\model.md.
/// It starts from a seed on every new install and is refined by <see cref="UserModelBuilder"/> from chats and the
/// window/activity watcher. The user can read it, edit it, or wipe it (tool: user_model).
/// </summary>
public static class UserModel
{
    static readonly object Gate = new();

    public static string Dir => Path.Combine(AppConfig.Dir, "profile");
    public static string Path_ => Path.Combine(Dir, "model.md");
    public static string StatePath => Path.Combine(Dir, "state.json");

    public const string Seed = """
        # Model of Wycliffe (built by Thokolosi)

        Status: just started. Nothing confirmed yet. It grows from chats and what is open on screen (window titles, and text when asked).

        ## Who he is
        (not yet learned)

        ## Work and projects
        (not yet learned)

        ## Tools, accounts and sites (names only, never passwords or keys)
        (not yet learned)

        ## Routines and habits
        (not yet learned)

        ## Preferences and how he likes things done
        - Short, plain replies; approve before anything is sent, posted, paid or deployed.

        ## Better ways to do things (suggestions, newest first)
        (none yet)

        ## Never store
        Passwords, keys, card or bank details, children's identifying details, or the content of other people's messages.
        """;

    public static string Read()
    {
        lock (Gate) return File.Exists(Path_) ? File.ReadAllText(Path_) : Seed;
    }

    public static void Write(string markdown)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, markdown, new UTF8Encoding(false));
            File.Move(tmp, Path_, overwrite: true);
        }
    }

    /// <summary>Keeps the previous version as model.previous.md before each change, so nothing learned is lost.</summary>
    public static void Backup()
    {
        lock (Gate)
        {
            try { if (File.Exists(Path_)) File.Copy(Path_, System.IO.Path.Combine(Dir, "model.previous.md"), overwrite: true); } catch { }
        }
    }

    /// <summary>Wipes the model back to the seed (the user asked for it). Returns false if there was nothing saved.</summary>
    public static bool Forget()
    {
        lock (Gate)
        {
            bool had = File.Exists(Path_);
            if (had) File.Delete(Path_);
            if (File.Exists(StatePath)) File.Delete(StatePath);
            return had;
        }
    }

    /// <summary>What the agent sees each turn (kept short; the full file is always readable with the user_model tool).</summary>
    public static string ForPrompt()
    {
        var t = Read();
        return t.Length > 4000 ? t[..4000] + "\n...(longer; use the user_model tool to read all of it)" : t;
    }

    public static string? LastUpdated()
    {
        try { return File.Exists(Path_) ? File.GetLastWriteTime(Path_).ToString("yyyy-MM-dd HH:mm") : null; }
        catch { return null; }
    }

    internal static string ReadState()
    {
        try { return File.Exists(StatePath) ? File.ReadAllText(StatePath) : "{}"; } catch { return "{}"; }
    }

    internal static void WriteState(string json)
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(StatePath, json, new UTF8Encoding(false)); } catch { }
    }
}

/// <summary>
/// Refines the user model in the background. Every interval (and about 2 minutes after the app starts, so a new install
/// starts learning at once) it gives the cheap model the current model, what is open on screen, and the latest chat, and
/// asks for the updated model. If nothing has changed since the last run, it does nothing (no cost).
/// </summary>
public sealed class UserModelBuilder : IDisposable
{
    const int MaxModelChars = 7000;
    readonly AppConfig _cfg;
    readonly Func<string?> _liveSnapshot;
    readonly ClaudeClient _claude = new();
    readonly System.Threading.Timer _timer;
    int _busy;

    public UserModelBuilder(AppConfig cfg, Func<string?> liveSnapshot)
    {
        _cfg = cfg;
        _liveSnapshot = liveSnapshot;
        var every = TimeSpan.FromMinutes(Math.Max(10, cfg.Profile.IntervalMinutes));
        _timer = new System.Threading.Timer(_ => _ = RunAsync(), null, TimeSpan.FromMinutes(2), every);
        ActivityLog.Write("PROFILE", cfg.Profile.Enabled ? $"Building the user model every {every.TotalMinutes:0} min." : "User model building is off (Profile.Enabled = false).");
    }

    public async Task RunAsync(bool force = false)
    {
        if (!_cfg.Profile.Enabled && !force) return;
        var model = string.IsNullOrWhiteSpace(_cfg.Profile.Model) ? _cfg.Model : _cfg.Profile.Model;
        if (!_cfg.HasKeyFor(model)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var live = _liveSnapshot() ?? "(watching is off, so no live screen information)";
            var chat = RecentChat(6000);
            var current = UserModel.Read();

            // Only spend tokens when something new has happened.
            var fingerprint = Hash(live + "\n" + chat);
            var state = JsonNode.Parse(UserModel.ReadState())?.AsObject() ?? new JsonObject();
            if (!force && state["fingerprint"]?.GetValue<string>() == fingerprint) return;

            var system = """
                You maintain a private, practical model of the user (Wycliffe) for his personal AI agent, Thokolosi, on his Windows PC.
                You receive: the current model (Markdown), what is open on screen right now (app names and window titles, which the
                user chose to share), and the latest chat with the agent. Update the model with NEW facts about who he is, his work,
                projects, tools and sites, routines and habits, and how he likes things done. Also add 1 to 3 concrete, specific
                suggestions for doing things better or faster, based on what you see him doing. Put each suggestion under
                "Better ways to do things" with the date, and remove suggestions that are stale or already done.
                Rules:
                - Keep every existing fact and every section (including "Never store") unless the new evidence clearly contradicts it.
                  Your reply must be the whole model, not a summary. Mark uncertain things with "(guess)".
                - Never record passwords, keys, card or bank details, children's identifying details, addresses of family, or the
                  content of other people's messages. Describe activity in general terms.
                - Do not identify people from faces. Use names only when they appear as text the user is working with and are needed.
                - Do not copy the screen section or any "Open on screen" text into the model; describe lasting habits instead.
                - If nothing new is worth recording, reply with exactly: NO_CHANGE
                - Otherwise reply with ONLY the full updated Markdown model, starting with "# Model of Wycliffe", at most 6000 characters.
                """;
            var user = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] =
                    $"Current time: {DateTime.Now:ddd yyyy-MM-dd HH:mm}\n\n## Current model\n{current}\n\n## Open on screen now\n{live}\n\n## Latest chat with the agent\n{chat}" }
            };

            var reply = (await _claude.CompleteAsync(_cfg.KeyFor(model), model, system, user, 2000, CancellationToken.None, "profile")).Trim();
            state["fingerprint"] = fingerprint;
            state["checked"] = DateTime.Now.ToString("s");
            if (reply.StartsWith("NO_CHANGE", StringComparison.OrdinalIgnoreCase) || reply.Length < 200 || !reply.StartsWith("# "))
            {
                UserModel.WriteState(state.ToJsonString());
                ActivityLog.Write("PROFILE", "No new information to add.");
                return;
            }
            // The live screen section changes every minute; it must never be saved as part of the model.
            var cut = reply.IndexOf("\n## Open on screen", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) reply = reply[..cut].TrimEnd();
            if (reply.Length > MaxModelChars) reply = reply[..MaxModelChars];
            // Safety net: never let an update throw away what was learned before. Keep the old model if the new one
            // loses a lot of text or drops a section that was there.
            var previous = UserModel.Read();
            bool dropsSections = previous.Contains("## Never store") && !reply.Contains("## Never store");
            if (dropsSections || reply.Length < previous.Length * 0.8)
            {
                ActivityLog.Write("PROFILE", $"Update rejected: it would shrink the model from {previous.Length} to {reply.Length} characters. Kept the previous version.");
                UserModel.WriteState(state.ToJsonString());
                return;
            }
            UserModel.Backup();
            UserModel.Write(reply);
            state["updated"] = DateTime.Now.ToString("s");
            UserModel.WriteState(state.ToJsonString());
            ActivityLog.Write("PROFILE", $"User model updated ({reply.Length} characters).");
        }
        catch (Exception ex) { ActivityLog.Write("PROFILE", "Update failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>The last few text messages of the main chat (user and assistant), newest last, capped.</summary>
    static string RecentChat(int max)
    {
        try
        {
            if (!File.Exists(AppConfig.HistoryPath)) return "(no chat yet)";
            var arr = JsonNode.Parse(File.ReadAllText(AppConfig.HistoryPath)) as JsonArray;
            if (arr == null) return "(no chat yet)";
            var lines = new List<string>();
            foreach (var m in arr.TakeLast(16))
            {
                var role = m?["role"]?.GetValue<string>() ?? "";
                var content = m?["content"];
                if (content is JsonValue v && v.TryGetValue<string>(out var s)) lines.Add($"{role}: {s}");
                else if (content is JsonArray blocks)
                    foreach (var b in blocks)
                        if (b?["type"]?.GetValue<string>() == "text") lines.Add($"{role}: {b["text"]?.GetValue<string>()}");
            }
            var text = string.Join("\n", lines);
            return text.Length > max ? text[^max..] : (text.Length == 0 ? "(no chat yet)" : text);
        }
        catch { return "(chat could not be read)"; }
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public void Dispose() => _timer.Dispose();
}


