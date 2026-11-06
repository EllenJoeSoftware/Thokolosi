using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Thokolosi.Core.Brain;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core;

/// <summary>A file or image the user attached to a message (drag-and-drop, snip, paste).</summary>
public record Attachment(string Path, bool IsImage);

/// <summary>
/// The agent loop: send the conversation to Claude, run whatever tools it asks for,
/// feed the results back, repeat until Claude is done.
/// </summary>
public class Agent
{
    readonly AppConfig _cfg;
    readonly ClaudeClient _client = new();
    readonly ToolRegistry _tools;
    readonly string? _historyPath;
    JsonArray _messages = new();
    JsonArray? _pendingResults; // tool results of the current step, kept so a Stop doesn't lose them

    /// <summary>Claude said something to the user.</summary>
    public event Action<string>? AssistantText;

    /// <summary>A step Thokolosi is taking (shown small in the chat, and logged).</summary>
    public event Action<string>? Activity;

    /// <summary>Ask the user to approve an action. Args: title, details. Return true to allow.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? ApprovalHandler { get; set; }

    /// <summary>Extra context for background runs (e.g. "This is a scheduled job").</summary>
    public string? RunContext { get; set; }

    public bool IsBusy { get; private set; }

    /// <param name="historyPath">Where to keep the conversation; null = in memory only (background jobs).</param>
    public Agent(AppConfig cfg, ToolRegistry tools, string? historyPath)
    {
        _cfg = cfg;
        _tools = tools;
        _historyPath = historyPath;
        LoadHistory();
    }

    public JsonArray Messages => _messages;

    public void NewConversation()
    {
        _messages = new JsonArray();
        _client.SessionId = ClaudeClient.NewSessionId();
        SaveHistory();
    }

    public async Task RunAsync(string userText, CancellationToken ct, IReadOnlyList<Attachment>? attachments = null)
    {
        // Budget cap
        var model = _cfg.Model;
        bool forceLocal = false;
        if (UsageTracker.OverCap)
        {
            switch (_cfg.Budget.AtCap)
            {
                case "pause": throw new InvalidOperationException($"This month's budget is used up ({UsageTracker.Summary()}). Raise it in Settings > Spending to continue.");
                case "local":
                    if (!_cfg.Local.Enabled) throw new InvalidOperationException($"This month's budget is used up ({UsageTracker.Summary()}) and the offline model isn't set up. Raise the budget or enable the offline model in Settings > Spending.");
                    forceLocal = true; break;
                case "cheaper": model = _cfg.CheapModel; break;
            }
        }
        var apiKey = _cfg.KeyFor(model);

        // "/playbook-name extra words" runs a saved playbook
        var m = Regex.Match(userText.Trim(), @"^/([\w-]+)\s*(.*)$", RegexOptions.Singleline);
        if (m.Success && PlaybookStore.Find(m.Groups[1].Value) is { } pb)
            userText = $"Run the playbook '{pb.Name}' (read it with the playbook tool and follow the steps).{(m.Groups[2].Value.Length > 0 ? " Details: " + m.Groups[2].Value : "")}";

        IsBusy = true;
        ActivityLog.Write("USER", userText);
        foreach (var a in attachments ?? Array.Empty<Attachment>()) AddUserContent(AttachmentBlock(a));
        AddUserContent(new JsonObject { ["type"] = "text", ["text"] = userText });
        var ctx = new ToolContext(_cfg);

        if (forceLocal)
        {
            try { await LocalReplyAsync("(budget cap reached)", ct); }
            finally { SaveHistory(); IsBusy = false; }
            return;
        }

        try
        {
            for (int i = 0; i < _cfg.MaxToolIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                StripOldImages();
                JsonObject resp;
                try { resp = await _client.SendAsync(apiKey, BuildRequest(model), ct); }
                catch (ClaudeApiException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest &&
                                                    ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase) && TrimHistory())
                {
                    Report("Conversation got long; dropped the oldest messages.");
                    resp = await _client.SendAsync(apiKey, BuildRequest(model), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && _cfg.Local.Enabled && IsConnectivityFailure(ex))
                {
                    // Claude unreachable (no internet / API outage): answer with the local model instead.
                    await LocalReplyAsync("(Claude unreachable: " + ex.Message + ")", ct);
                    break;
                }

                var stop = resp["stop_reason"]?.GetValue<string>();
                var content = resp["content"]!.DeepClone().AsArray();
                if (content.Count == 0)
                {
                    // An empty assistant turn would make every later request fail; don't store it.
                    if (stop != "tool_use" && stop != "pause_turn") break;
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = "(continuing)" });
                }
                _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                SaveHistory();

                foreach (var block in content)
                {
                    var type = block!["type"]?.GetValue<string>();
                    if (type == "text")
                    {
                        var text = block["text"]!.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            ActivityLog.Write("CLAUDE", text);
                            AssistantText?.Invoke(text);
                        }
                    }
                    else if (type == "server_tool_use")
                    {
                        Report("Searching the web: " + (block["input"]?["query"]?.GetValue<string>() ?? ""));
                    }
                }

                if (stop == "pause_turn") continue;          // server tool (web search) needs another round
                if (stop == "max_tokens") { AssistantText?.Invoke("(I hit my reply length limit - say \"continue\" if you want the rest.)"); break; }
                if (stop != "tool_use") break;              // end_turn: done

                var results = _pendingResults = new JsonArray();
                foreach (var block in content.Where(b => b!["type"]?.GetValue<string>() == "tool_use"))
                {
                    ct.ThrowIfCancellationRequested();
                    results.Add(await RunToolAsync(block!.AsObject(), ctx, ct));
                }
                _pendingResults = null;
                _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                SaveHistory();

                if (i == _cfg.MaxToolIterations - 1)
                    AssistantText?.Invoke($"(Stopped after {_cfg.MaxToolIterations} steps. Say \"continue\" to keep going.)");
            }
        }
        finally
        {
            RepairDanglingToolUse();
            SaveHistory();
            IsBusy = false;
        }
    }

    async Task<JsonObject> RunToolAsync(JsonObject block, ToolContext ctx, CancellationToken ct)
    {
        var id = block["id"]!.GetValue<string>();
        var name = block["name"]!.GetValue<string>();
        var input = block["input"] as JsonObject ?? new JsonObject();
        var tool = _tools.Find(name);

        JsonObject Result(ToolResult r) => new()
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id,
            ["content"] = r.Content,
            ["is_error"] = r.IsError
        };

        if (tool == null) return Result(ToolResult.Error($"Unknown tool '{name}'."));

        var description = tool.Describe(input);
        Report(description);

        bool dangerous = name == "run_powershell" && _cfg.AlwaysConfirmPatterns.Any(p =>
            !string.IsNullOrWhiteSpace(p) && input.Str("command").Contains(p, StringComparison.OrdinalIgnoreCase));

        if ((!_cfg.AutoMode && tool.NeedsApproval(input)) || dangerous)
        {
            var title = dangerous ? "Thokolosi wants to run a risky command" : "Thokolosi wants to do this";
            bool ok = ApprovalHandler != null && await ApprovalHandler(title, description, ct);
            if (!ok)
            {
                Report("Declined by you.");
                return Result(ToolResult.Error("The user declined this action. Ask them how they would like to proceed or try a different approach."));
            }
        }

        try
        {
            var r = await tool.RunAsync(input, ctx, ct);
            var first = r.Content.FirstOrDefault()?["text"]?.GetValue<string>() ?? "";
            ActivityLog.Write(r.IsError ? "ERROR" : "RESULT", Json.Truncate(first, 2000));
            return Result(r);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ActivityLog.Write("ERROR", ex.ToString());
            return Result(ToolResult.Error($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    void Report(string text)
    {
        ActivityLog.Write("ACTION", text);
        Activity?.Invoke(text);
    }

    static bool IsConnectivityFailure(Exception ex) =>
        ex is HttpRequestException || ex is TaskCanceledException ||
        (ex is ClaudeApiException c && c.Status is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.GatewayTimeout || (int?)(ex as ClaudeApiException)?.Status == 529);

    async Task LocalReplyAsync(string reason, CancellationToken ct)
    {
        Report($"Using the offline model ({_cfg.Local.Model}) {reason}");
        var system = "You are Thokolosi, a personal assistant on the user's Windows PC, currently running on a small OFFLINE model with NO tools: " +
                     "you cannot run commands, browse, or read files right now. Answer from your own knowledge and the conversation. " +
                     "If the request needs tools, say briefly that it will be done when the main model is back.\n\nMemory:\n" + MemoryStore.ForPrompt(3000);
        string text;
        try { text = await LocalModel.ChatAsync(_cfg.Local, system, _messages, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Claude is unreachable and the offline model failed too: " + ex.Message +
                                                " (Is Ollama running? See Settings > Spending & offline.)");
        }
        text = "(offline) " + text.Trim();
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } });
        AssistantText?.Invoke(text);
    }

    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    public static bool IsImageFile(string path) => ImageExt.Contains(Path.GetExtension(path));

    JsonObject AttachmentBlock(Attachment a)
    {
        if (a.IsImage && File.Exists(a.Path))
        {
            try
            {
                var (data, mime) = ImageForClaude(a.Path);
                return new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = Convert.ToBase64String(data) }
                };
            }
            catch (Exception ex) { ActivityLog.Write("ERROR", "Attachment image: " + ex.Message); }
        }
        var info = File.Exists(a.Path) ? $"{new FileInfo(a.Path).Length / 1024.0:0.#} KB" : Directory.Exists(a.Path) ? "folder" : "missing";
        return new JsonObject { ["type"] = "text", ["text"] = $"[Attached: {a.Path} ({info}). Read it with read_document / read_file / list_directory as appropriate.]" };
    }

    /// <summary>Downscale/re-encode so images fit Claude's limits (long edge 1568 px, a few MB).</summary>
    static (byte[] Data, string Mime) ImageForClaude(string path)
    {
        using var src = System.Drawing.Image.FromFile(path);
        double scale = Math.Min(1.0, 1568.0 / Math.Max(src.Width, src.Height));
        var len = new FileInfo(path).Length;
        // Use the real format, not the file extension (a JPEG saved as .png would be rejected by the API).
        var fmt = src.RawFormat;
        string? mime = fmt.Equals(System.Drawing.Imaging.ImageFormat.Png) ? "image/png"
                     : fmt.Equals(System.Drawing.Imaging.ImageFormat.Jpeg) ? "image/jpeg"
                     : fmt.Equals(System.Drawing.Imaging.ImageFormat.Gif) ? "image/gif" : null;
        if (scale >= 1.0 && len < 3_500_000 && mime != null)
            return (File.ReadAllBytes(path), mime);
        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        if (ms.Length < 3_500_000) return (ms.ToArray(), "image/png");
        ms.SetLength(0);
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
        return (ms.ToArray(), "image/jpeg");
    }

    JsonObject BuildRequest(string model)
    {
        var tools = new JsonArray();
        foreach (var t in _tools.Snapshot()) tools.Add(t.Definition.DeepClone());
        // Claude's server-side web search; other providers look things up with the browser tool instead.
        var openCode = OpenCodeGo.Handles(model);
        if (!openCode) tools.Add(new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 5 });
        // Cache the tool list + stable system prompt between calls (cheaper and faster).
        tools[^1]!["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        // The user model changes only every so often, so it lives in the cached block; only the clock and live screen info change per call.
        var system = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = StableSystemPrompt() + UserModelText(), ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } },
            new JsonObject { ["type"] = "text", ["text"] = $"Current local time: {DateTime.Now:dddd yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.DisplayName})." +
                LiveActivityText() + (RunContext != null ? "\n" + RunContext : "") +
                (openCode ? "\nThe web_search tool is not available with this model: for current information, use the browser tool (search a site such as duckduckgo.com) instead." : "") }
        };

        // Rolling cache point on the conversation: everything up to the latest message is read from cache on the next turn
        // instead of being re-billed at full price. (Breakpoints: tools, system, conversation = 3 of the 4 allowed.)
        var messages = _messages.DeepClone() as JsonArray ?? new JsonArray();
        if (messages.Count > 0 && messages[^1]?["content"] is JsonArray last && last.Count > 0 && last[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = _cfg.MaxTokens,
            ["system"] = system,
            ["tools"] = tools,
            ["messages"] = messages
        };
    }

    /// <summary>Set by the app: returns what the user is doing right now (from the activity watcher), or null when watching is off.</summary>
    public static Func<string?>? LiveContext { get; set; }

    static string LiveActivityText()
    {
        string? live = null;
        try { live = LiveContext?.Invoke(); } catch { }
        return string.IsNullOrWhiteSpace(live) ? "" :
            "\nLive activity on the user's PC (from the window watcher: app and window titles only, no content; refreshed on every message). " +
            "If the user asks what they are busy with or working on right now, answer immediately from this without calling any tool, " +
            "say it is based on window titles, and say so if they are away. It also lists every open window. " +
            "When the user wants to know what is INSIDE a window (what they are reading or watching, a chat, a document, a page), use the see_window tool " +
            "(text and/or a picture of that window) instead of guessing from the title. Do not mention this block otherwise.\n" + live;
    }

    /// <summary>The growing model of the user (kept out of the cached system prompt so it can change freely).</summary>
    static string UserModelText() =>
        "\n\n## What you have learned about the user (his model, built over time; use it to tailor help and suggest better ways; " +
        "the user_model tool has the full version)\n" + Profile.UserModel.ForPrompt();

    string StableSystemPrompt()
    {
        var user = Environment.UserName;
        var mode = _cfg.AutoMode
            ? "AUTO - act without asking first (a few risky commands still need approval)."
            : "ASK - the user approves each action that changes something. If they decline, adapt.";
        var display = _cfg.SilentMode
            ? "SILENT - commands run hidden and the browser is headless; the user only sees your chat replies."
            : "VISIBLE - the user can watch PowerShell windows and the browser as you work.";

        return $"""
            You are Thokolosi, a personal AI agent living in the system tray of {user}'s Windows PC ({Environment.MachineName}, {Environment.OSVersion}).
            You act on the user's behalf with full access to this computer through your tools.

            Environment:
            - User profile: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}
            - Your scratch workspace: {AppConfig.WorkDir}
            - Action mode: {mode}
            - Display mode: {display}

            Code folders: {string.Join(", ", _cfg.CodeFolders)}
            Meeting notes folder: {AppConfig.MeetingsDir}
            Activity timeline (what the user worked on, if watching is on): {AppConfig.ActivityDir}\YYYY-MM-DD.log

            How to work:
            - Do the task, don't just describe how. Use run_powershell for anything on the PC (files, apps, git, gh, winget installs, system info).
            - Commands must be non-interactive. Start GUI apps or long-running processes with Start-Process so they don't block.
            - Code: use search_files to find things, read_file to read, edit_file for precise changes, and git/gh through run_powershell. Run builds/tests after changing code. Work on a branch unless told otherwise.
            - Email: use the outlook tool for the work mailbox and calendar (desktop Outlook), the email tool for IMAP accounts, or mcp__* email tools if present.
            - Slack: use the slack tool (or mcp__* Slack tools if present).
            - Teams meetings: use the meetings tool (it joins through the Teams app and writes notes). For anything else in Teams or other desktop apps, use the desktop tool.
            - Use web_search for quick facts and current information. Use the browser tool to work inside websites; logins persist in your browser profile.
            - Use screenshot_screen when you need to see what's on the user's screen.
            - Tools named mcp__<server>__<tool> come from the user's own MCP servers; use them when they fit.
            - Before anything irreversible (deleting user data, sending emails or messages, posting publicly, purchases, changing security settings), confirm with the user in chat unless the job description below explicitly allows it.
            - If something fails, read the error, try a different approach, and only give up after a couple of sensible attempts.
            - Keep replies short and plain: what you did, and the result. No long preambles.

            - Memory: you have long-term memory (memory tool). When the user tells you something durable (a preference, a fact about their work,
              people, systems, accounts, how they like things done) or you learn a lesson that will matter next time, save it - briefly, without asking.
              Check memory before asking the user something you may already know. Never store passwords or keys.
            - Playbooks: saved procedures (playbook tool). If the user asks for something that matches a playbook, read it and follow it.
              When the user teaches you a routine ("whenever I say X, do Y", "this is how we deploy"), offer to save it as a playbook.
            - Documents: use read_document for PDF, Word, Excel and PowerPoint files (read_file is for plain text and code).
            - Stroke 5 News: the facebook tool posts or schedules to the Facebook Page; always show the post and get a yes first.
            - Farm/solar: the monitors tool has live and historical readings (battery SOC, PV, load, incubator temperature...).
            - Consulting admin: the timesheet tool aggregates app usage and git commits per day for timesheets and invoices.
            - Deleting: prefer recycle_files (recoverable) over Remove-Item. File changes made with write_file/edit_file can be undone with the undo tool.

            ## What you remember about the user (memory)
            {MemoryStore.ForPrompt()}

            ## Saved playbooks (run with the playbook tool; the user can type /name)
            {PlaybookStore.ForPrompt()}

            ## Your job (written by the user - follow it)
            {AppConfig.LoadJob()}
            """;
    }

    void AddUserContent(JsonObject block)
    {
        // The API needs user/assistant turns to alternate; merge into a trailing user turn if there is one.
        if (_messages.Count > 0 && _messages[^1]!["role"]!.GetValue<string>() == "user")
            _messages[^1]!["content"]!.AsArray().Add(block);
        else
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray { block } });
    }

    /// <summary>If a run was stopped mid-tool, add "cancelled" results so the conversation stays valid.</summary>
    void RepairDanglingToolUse()
    {
        if (_messages.Count == 0) return;
        var last = _messages[^1]!.AsObject();
        if (last["role"]!.GetValue<string>() != "assistant") return;
        var pending = last["content"]!.AsArray().Where(b => b!["type"]?.GetValue<string>() == "tool_use").ToList();
        if (pending.Count == 0) return;
        var results = new JsonArray();
        var done = _pendingResults ?? new JsonArray();
        _pendingResults = null;
        foreach (var b in pending)
        {
            var id = b!["id"]!.GetValue<string>();
            var real = done.FirstOrDefault(r => r?["tool_use_id"]?.GetValue<string>() == id);
            if (real != null) { results.Add(real.DeepClone()); continue; }
            results.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = id,
                ["content"] = "Stopped by the user before this ran.",
                ["is_error"] = true
            });
        }
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
    }

    /// <summary>Drop roughly the oldest half of the conversation, cutting only at a plain user message.</summary>
    bool TrimHistory()
    {
        int target = _messages.Count / 2;
        for (int i = Math.Max(1, target); i < _messages.Count; i++)
        {
            var m = _messages[i]!;
            if (m["role"]!.GetValue<string>() != "user" || m["content"] is not JsonArray c) continue;
            if (c.Any(b => b?["type"]?.GetValue<string>() == "tool_result")) continue;
            var keep = new JsonArray();
            for (int k = i; k < _messages.Count; k++) keep.Add(_messages[k]!.DeepClone());
            _messages = keep;
            SaveHistory();
            return true;
        }
        return false;
    }

    /// <summary>Screenshots and attached images are big; keep only the ones from the latest turn.</summary>
    void StripOldImages()
    {
        for (int m = 0; m < _messages.Count - 1; m++)
        {
            if (_messages[m]!["role"]!.GetValue<string>() != "user") continue;
            if (_messages[m]!["content"] is JsonArray top)
                for (int k = 0; k < top.Count; k++)
                    if (top[k]?["type"]?.GetValue<string>() == "image")
                        top[k] = new JsonObject { ["type"] = "text", ["text"] = "[image shared earlier - removed to save space]" };
            foreach (var block in _messages[m]!["content"] as JsonArray ?? new JsonArray())
            {
                if (block?["type"]?.GetValue<string>() != "tool_result" || block["content"] is not JsonArray inner) continue;
                for (int k = 0; k < inner.Count; k++)
                    if (inner[k]?["type"]?.GetValue<string>() == "image")
                        inner[k] = new JsonObject { ["type"] = "text", ["text"] = "[old screenshot removed to save space]" };
            }
        }
    }

    void LoadHistory()
    {
        try
        {
            if (_historyPath != null && File.Exists(_historyPath))
                _messages = JsonNode.Parse(File.ReadAllText(_historyPath)) as JsonArray ?? new JsonArray();
        }
        catch { _messages = new JsonArray(); }
        RepairDanglingToolUse();
    }

    void SaveHistory()
    {
        if (_historyPath == null) return;
        try { File.WriteAllText(_historyPath, _messages.ToJsonString()); } catch { }
    }
}
