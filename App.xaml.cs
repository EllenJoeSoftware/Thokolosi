using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Thokolosi.Core;
using Thokolosi.Core.Brain;
using Thokolosi.Core.Mcp;
using Thokolosi.Core.Meetings;
using Thokolosi.Core.Remote;
using Thokolosi.Core.Tools;
using Thokolosi.Core.Voice;
using Thokolosi.Core.Watch;
using WinForms = System.Windows.Forms;

namespace Thokolosi;

public partial class App : Application, INotifier
{
    static Mutex? _single;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem? _autoItem, _silentItem, _watchItem, _autoJoinItem, _notesItem, _spendItem, _phoneItem;
    Action? _balloonAction;

    public AppConfig Config { get; private set; } = null!;
    public ToolRegistry Tools { get; } = new();
    public Agent Agent { get; private set; } = null!;
    public MainWindow Chat { get; private set; } = null!;
    public McpManager Mcp { get; private set; } = null!;
    public MeetingService Meetings { get; private set; } = null!;
    public ActivityWatcher Watcher { get; private set; } = null!;
    public PhoneLink? Phone { get; private set; }
    Scheduler? _scheduler;
    KanbanPoller? _kanban;
    public Thokolosi.Core.Profile.UserModelBuilder? ProfileBuilder { get; private set; }
    MessagingBridge? _messaging;
    BrowserTool? _browser;
    readonly List<GlobalHotkey> _hotkeys = new();
    System.Threading.Timer? _updateTimer;
    public Speaker Speaker { get; } = new();
    public WakeWordListener WakeWord { get; } = new();

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "Thokolosi.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            ActivityLog.Write("CRASH", ex.Exception.ToString());
            MessageBox.Show(ex.Exception.Message, "Thokolosi error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { ActivityLog.Write("ERROR", ex.Exception.ToString()); ex.SetObserved(); };

        Config = AppConfig.Load();
        ApplyStartWithWindows();
        UsageTracker.Config = Config;
        ClaudeClient.OpenCodeKey = () => Config.OpenCodeApiKey;
        UsageTracker.Alert += (t, m) => Notify(t, m);
        UsageTracker.Changed += () => PublishInfo();

        Meetings = new MeetingService(Config, this);
        RegisterTools();
        Agent = new Agent(Config, Tools, AppConfig.HistoryPath);

        Watcher = new ActivityWatcher(Config);
        Thokolosi.Core.Agent.LiveContext = () => Watcher.Snapshot();   // "what am I busy with?" is answerable instantly
        ProfileBuilder = new Thokolosi.Core.Profile.UserModelBuilder(Config, () => Watcher.Snapshot());   // learns about the user from day one
        Watcher.Suggested += s => Dispatcher.Invoke(() =>
        {
            // Pop-ups vanish (and Focus Assist hides them), so also keep the suggestion in the chat.
            Chat.AddExternal($"Suggestion: {s.Title} - {s.Message}\nTask: {s.Prompt}");
            Notify("Thokolosi suggests: " + s.Title, s.Message + "\nClick to let me do it.", () => { Chat.ShowNearTray(); Chat.Prefill(s.Prompt); });
        });

        _scheduler = new Scheduler(Config, job => RunBackgroundTaskAsync(job.Name, job.Prompt));
        _kanban = new KanbanPoller((title, prompt) => RunBackgroundTaskCoreAsync(title, prompt, trackOnBoard: false));   // checks the Easy Kanban board every 5s
        _messaging = new MessagingBridge(Config, Tools, (title, prompt) => Dispatcher.Invoke(() => Chat.SubmitRemoteAsync(prompt, "message", title)));

        Speaker.Configure(Config.Voice.VoiceName, Config.Voice.Rate);
        Chat = new MainWindow();
        Meetings.StateChanged += () => Dispatcher.Invoke(() => { Chat.RefreshModes(); RefreshTrayChecks(); });
        CreateTray();
        WakeWord.Heard += rest => Dispatcher.BeginInvoke(() => Chat.OnWakeWord(rest));
        WakeWord.IsBlocked = () => Speaker.IsSpeaking || Meetings.IsRecording;
        ApplyHotkeysAndVoice(showErrors: false);

        // Mirror the main chat to paired phones.
        Agent.AssistantText += t => Phone?.Post("reply", new JsonObject { ["text"] = t });
        Agent.Activity += t => Phone?.Post("activity", new JsonObject { ["text"] = t.Length > 400 ? t[..400] + "..." : t });
        PlaybookStore.Saved += p => _ = Phone?.PublishPlaybookAsync(p.Name, p.Description, p.Steps, new DateTimeOffset(p.Updated).ToUnixTimeMilliseconds());
        PlaybookStore.Deleted += n => _ = Phone?.DeletePlaybookAsync(n);

        Mcp = new McpManager(Tools);
        if (Config.McpEnabled) _ = Mcp.StartAllAsync(CancellationToken.None);
        _ = StartPhoneLinkAsync();

        _updateTimer = new System.Threading.Timer(_ => _ = CheckForUpdatesAsync(manual: false), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
        if (e.Args.Contains("--updated")) Notify("Thokolosi updated", $"Now running version {Updater.Current.ToString(3)}.");

        if (!Config.HasAnyKey) OpenSettings();
        else if (!e.Args.Contains("--tray")) Chat.ShowNearTray();
    }

    void RegisterTools()
    {
        _browser = new BrowserTool();
        foreach (var t in new ITool[]
        {
            new PowerShellTool(), new ReadFileTool(), new WriteFileTool(), new EditFileTool(), new ListDirectoryTool(), new SearchFilesTool(),
            _browser, new ScreenshotTool(), new DesktopTool(),
            new OutlookTool(), new EmailTool(), new SlackTool(), new MeetingTool(Meetings),
            new MemoryTool(), new PlaybookTool(), new SendToPhoneTool(() => Phone), new SecretsTool(), new SeeWindowTool(), new UserModelTool(),
        })
            Tools.Add(t);
        ExtraTools.Register(this);
    }

    // ---------- phone link ----------

    public async Task StartPhoneLinkAsync()
    {
        if (Phone != null) { await Phone.DisposeAsync(); Phone = null; }
        if (!Config.Phone.Enabled || !Config.Phone.Configured) return;
        var link = new PhoneLink(Config);
        link.MessageReceived += (text, voice, from) => Dispatcher.Invoke(() => Chat.SubmitRemoteAsync(text, "phone", from));
        link.ApprovalAnswered += (id, allow, by) => ResolveApproval(id, allow, by);
        link.StopRequested += () => Dispatcher.Invoke(() => Chat.StopFromRemote());
        link.ModeChangeRequested += (auto, silent) => Dispatcher.Invoke(() =>
        {
            if (auto.HasValue) SetAutoMode(auto.Value);
            if (silent.HasValue) SetSilentMode(silent.Value);
        });
        link.NewChatRequested += () => Dispatcher.Invoke(() => Chat.NewChatFromRemote());
        link.PlaybookRequested += name => Dispatcher.Invoke(() => Chat.SubmitRemoteAsync("/" + PlaybookStore.Slug(name), "phone", "phone"));
        link.UndoRequested += id => Dispatcher.Invoke(() => UndoFromRemote(id));
        link.PhonePaired += p => Notify("Phone paired", $"{p.Name} can now chat with Thokolosi and approve actions.");
        link.PhonesChanged += () => Dispatcher.BeginInvoke(RefreshTrayChecks);
        link.JobChangedRemotely += text => Dispatcher.Invoke(() =>
        {
            AppConfig.SaveJob(text);
            Notify("Thokolosi", "Job description updated from your phone.");
        });
        link.PlaybooksChangedRemotely += SyncPlaybooksFromPhone;
        link.FileReceived += (path, caption, from) => Dispatcher.Invoke(() =>
        {
            var name = System.IO.Path.GetFileName(path);
            if (caption.Trim().Length > 0)
            {
                // A file with a request ("summarise this", "print it") goes to the agent like a drag-and-drop attachment.
                _ = Chat.SubmitRemoteAsync($"{caption}\n\n(Sent from {from}; saved at {path})", "phone", from,
                    new[] { new Attachment(path, Agent.IsImageFile(path)) });
            }
            else
            {
                Chat.AddExternal($"Received {name} from {from}. Saved to {path}");
                Notify("File from " + from, $"{name} saved in Downloads\\Thokolosi. Click to show it.", () => ShowInExplorer(path));
            }
        });
        Phone = link;
        await link.StartAsync();
        if (link.Connected)
        {
            // Merge playbooks by last update: push only what's newer here; newer phone edits arrive via the config stream.
            JsonObject remote;
            try { remote = await link.GetPlaybooksAsync(); } catch { remote = new JsonObject(); }
            foreach (var p in PlaybookStore.All())
            {
                var local = new DateTimeOffset(p.Updated).ToUnixTimeMilliseconds();
                var r = remote[p.Name];
                var rt = r?["updatedAt"] is JsonValue v && v.TryGetValue<long>(out var ms) ? ms : 0;
                if (r == null || rt < local) await link.PublishPlaybookAsync(p.Name, p.Description, p.Steps, local);
                lock (_published) _published.Add(p.Name);
            }
            PublishInfo();
        }
        Dispatcher.Invoke(RefreshTrayChecks);
    }

    readonly HashSet<string> _published = new();

    void SyncPlaybooksFromPhone(JsonObject remote)
    {
        // Deleted on the phone: it was published from here before, and now it's gone remotely.
        List<string> gone;
        lock (_published) gone = _published.Where(n => remote[n] == null).ToList();
        foreach (var n in gone)
        {
            PlaybookStore.Delete(n, raiseEvent: false);
            lock (_published) _published.Remove(n);
        }
        foreach (var (key, node) in remote)
        {
            if (node is not JsonObject o || o["by"]?.GetValue<string>() == "pc") continue;
            var name = o["name"]?.GetValue<string>() ?? key;
            var updated = o["updatedAt"] is JsonValue v && v.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.Now;
            var local = PlaybookStore.Find(name);
            if (local != null && local.Name == PlaybookStore.Slug(name) && local.Updated >= updated) continue;
            var saved = PlaybookStore.Save(name, o["description"]?.GetValue<string>() ?? "", o["steps"]?.GetValue<string>() ?? "", updated, raiseEvent: false);
            lock (_published) _published.Add(saved.Name);
            _ = Phone?.PublishPlaybookAsync(saved.Name, saved.Description, saved.Steps, new DateTimeOffset(saved.Updated).ToUnixTimeMilliseconds());
        }
    }

    void UndoFromRemote(string? id)
    {
        var result = ExtraTools.UndoLast(id);
        Phone?.Post("status", new JsonObject { ["text"] = result });
        Chat.AddExternal("> " + result);
    }

    /// <summary>Push busy/status/modes/spend to /info so phones show the PC's state.</summary>
    public void PublishInfo(bool? busy = null, string? status = null, int? queue = null)
    {
        var p = Phone;
        if (p == null || !p.Connected) return;
        var info = new JsonObject
        {
            ["auto"] = Config.AutoMode,
            ["silent"] = Config.SilentMode,
            ["spendMonth"] = Math.Round(UsageTracker.MonthUsd, 2),
            ["spendCurrency"] = "USD",
        };
        if (busy.HasValue) info["busy"] = busy.Value;
        if (status != null) info["status"] = status;
        if (queue.HasValue) info["queue"] = queue.Value;
        _ = p.UpdateInfoAsync(info);
    }

    public void ShowPairWindow()
    {
        if (!Config.Phone.Configured)
        {
            MessageBox.Show("Add your Firebase project details in Settings > Phone first.", "Thokolosi");
            OpenSettings();
            return;
        }
        if (Phone == null || !Phone.Connected)
        {
            MessageBox.Show("The phone link isn't connected: " + (Phone?.LastError ?? "starting...") + "\n\nCheck Settings > Phone and your internet connection.", "Thokolosi");
            return;
        }
        new PairWindow(Phone).Show();
    }

    // ---------- approvals (PC window + phone, first answer wins) ----------

    readonly Dictionary<string, TaskCompletionSource<(bool Allow, string By)>> _approvals = new();

    public async Task<bool> RequestApprovalAsync(string title, string details, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvals) _approvals[id] = tcs;
        var phones = Phone is { Connected: true } && Phone.Phones.Count > 0;
        var timeout = TimeSpan.FromMinutes(Math.Max(1, Config.Phone.ApprovalTimeoutMinutes));
        if (phones)
            Phone!.Post("approval_request", new JsonObject
            {
                ["approvalId"] = id, ["title"] = title, ["details"] = details.Length > 3000 ? details[..3000] + "..." : details,
                ["expires"] = DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds()
            });

        ApprovalWindow? win = null;
        await Dispatcher.InvokeAsync(() =>
        {
            win = new ApprovalWindow(title, details, phones);
            win.Answered += allow => tcs.TrySetResult((allow, "pc"));
            win.Show();
            win.Activate();
        });
        if (phones) _ = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult((false, "timeout")));
        using var reg = ct.Register(() => tcs.TrySetResult((false, "stopped"))); // Stop pressed while waiting

        var (allow, by) = await tcs.Task;
        lock (_approvals) _approvals.Remove(id);
        await Dispatcher.InvokeAsync(() => win?.Dismiss());
        if (phones) Phone?.Post("approval_resolved", new JsonObject { ["approvalId"] = id, ["allow"] = allow, ["by"] = by });
        if (by != "pc") Chat.AddExternal($"> {(allow ? "Allowed" : "Denied")} by {by}");
        ct.ThrowIfCancellationRequested();
        return allow;
    }

    void ResolveApproval(string id, bool allow, string by)
    {
        TaskCompletionSource<(bool, string)>? tcs;
        lock (_approvals) _approvals.TryGetValue(id, out tcs);
        tcs?.TrySetResult((allow, by));
    }

    // ---------- tray ----------

    void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/thokolosi.ico"))!.Stream;
        _tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Thokolosi",
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Chat.Toggle(); };
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonAction; _balloonAction = null; a?.Invoke(); };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Thokolosi", null, (_, _) => Chat.ShowNearTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _autoItem = new WinForms.ToolStripMenuItem("Auto mode (act without asking)") { CheckOnClick = true };
        _autoItem.Click += (_, _) => SetAutoMode(_autoItem.Checked);
        _silentItem = new WinForms.ToolStripMenuItem("Silent mode (work invisibly)") { CheckOnClick = true };
        _silentItem.Click += (_, _) => SetSilentMode(_silentItem.Checked);
        _watchItem = new WinForms.ToolStripMenuItem("Watch && suggest") { CheckOnClick = true };
        _watchItem.Click += (_, _) => { Config.Watcher.Enabled = _watchItem.Checked; Config.Save(); };
        _autoJoinItem = new WinForms.ToolStripMenuItem("Auto-join Teams meetings") { CheckOnClick = true };
        _autoJoinItem.Click += (_, _) => { Config.Meetings.AutoJoin = _autoJoinItem.Checked; Config.Save(); };
        menu.Items.AddRange(new WinForms.ToolStripItem[] { _autoItem, _silentItem, _watchItem, _autoJoinItem });
        menu.Items.Add("Suggest something now", null, async (_, _) =>
        {
            Notify("Thokolosi", "Looking at what you're doing...");
            await Task.Run(() => Watcher.ReviewNowAsync());
            if (!Watcher.LastResult.Contains("suggested:")) Notify("Thokolosi", Watcher.LastResult);
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());

        menu.Items.Add("Talk to Thokolosi", null, (_, _) => Chat.ToggleListening());
        menu.Items.Add("Send a file to my phone...", null, async (_, _) => await SendFileToPhoneAsync());
        menu.Items.Add("Snip screen", null, async (_, _) => await SnipAsync());
        menu.Items.Add("Clipboard actions", null, (_, _) => ShowClipboardMenu());
        _notesItem = new WinForms.ToolStripMenuItem("Take meeting notes now");
        _notesItem.Click += (_, _) =>
        {
            var msg = Meetings.IsRecording ? Meetings.StopNotes() : Meetings.StartNotes("Meeting " + DateTime.Now.ToString("HH:mm"));
            Notify("Thokolosi", msg);
        };
        menu.Items.Add(_notesItem);
        menu.Items.Add("Undo last file change", null, (_, _) => { var r = ExtraTools.UndoLast(null); Notify("Thokolosi", r); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _phoneItem = new WinForms.ToolStripMenuItem("Pair a phone...");
        _phoneItem.Click += (_, _) => ShowPairWindow();
        menu.Items.Add(_phoneItem);
        menu.Items.Add("Meeting notes folder", null, (_, _) => OpenFolder(AppConfig.MeetingsDir));
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Open activity log", null, (_, _) => OpenFolder(AppConfig.LogDir));
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        _spendItem = new WinForms.ToolStripMenuItem("") { Enabled = false };
        menu.Items.Add(_spendItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit Thokolosi", null, async (_, _) => await QuitAsync());
        menu.Opening += (_, _) => RefreshTrayChecks();
        _tray.ContextMenuStrip = menu;
        RefreshTrayChecks();
    }

    void RefreshTrayChecks()
    {
        if (_autoItem == null) return;
        _autoItem.Checked = Config.AutoMode;
        _silentItem!.Checked = Config.SilentMode;
        _watchItem!.Checked = Config.Watcher.Enabled;
        _watchItem.ToolTipText = "Last review: " + Watcher.LastResult;
        _autoJoinItem!.Checked = Config.Meetings.AutoJoin;
        _notesItem!.Text = Meetings.IsRecording ? "Stop meeting notes" : "Take meeting notes now";
        _spendItem!.Text = "Spend: " + UsageTracker.Summary();
        var n = Phone?.Phones.Count ?? 0;
        _phoneItem!.Text = n > 0 ? $"Pair a phone... ({n} paired)" : "Pair a phone...";
        if (_tray != null) _tray.Text = Meetings.IsRecording ? "Thokolosi - taking meeting notes" : "Thokolosi";
    }

    public void SetAutoMode(bool on)
    {
        Config.AutoMode = on;
        Config.Save();
        RefreshTrayChecks();
        Chat.RefreshModes();
        PublishInfo();
    }

    public void SetSilentMode(bool on)
    {
        Config.SilentMode = on;
        Config.Save();
        RefreshTrayChecks();
        Chat.RefreshModes();
        PublishInfo();
    }

    // ---------- snipping & clipboard ----------

    public async Task SnipAsync()
    {
        var wasVisible = Chat.IsVisible;
        if (wasVisible) Chat.Hide();
        await Task.Delay(200); // let the chat window disappear before capturing
        var path = await SnipWindow.SnipAsync();
        Chat.ShowNearTray();
        if (path != null) Chat.AddAttachment(path, isImage: true, focusPrompt: "What do you want me to do with this snip?");
    }

    void ShowClipboardMenu()
    {
        string? text = null;
        System.Windows.Media.Imaging.BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
            else if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
        }
        catch { }

        var menu = new WinForms.ContextMenuStrip();
        if (!string.IsNullOrWhiteSpace(text))
        {
            menu.Items.Add(new WinForms.ToolStripMenuItem($"Clipboard: \"{Shorten(text, 40)}\"") { Enabled = false });
            foreach (var action in ClipboardActions.Text)
            {
                var a = action;
                menu.Items.Add(a.Label, null, async (_, _) => await RunClipboardActionAsync(a, text));
            }
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Ask Thokolosi about it...", null, (_, _) => { Chat.ShowNearTray(); Chat.Prefill("\n\n---\n" + text); });
        }
        else if (image != null)
        {
            menu.Items.Add("Ask Thokolosi about this image...", null, (_, _) =>
            {
                var path = SaveClipboardImage(image);
                Chat.ShowNearTray();
                Chat.AddAttachment(path, true, "What should I do with this image?");
            });
            menu.Items.Add("Extract the text (copy it)", null, async (_, _) =>
            {
                var path = SaveClipboardImage(image);
                await Chat.SubmitRemoteAsync("Extract all the text from the attached image exactly, then put it on the clipboard with PowerShell Set-Clipboard. Reply with just 'Copied' and the first line.",
                    "pc", "clipboard", new[] { new Attachment(path, true) });
            });
        }
        else menu.Items.Add(new WinForms.ToolStripMenuItem("The clipboard is empty - copy some text first") { Enabled = false });
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(menu.Dispose);
        menu.Show(WinForms.Cursor.Position);
        // A menu opened from a global hotkey must be foreground, or it won't close on an outside click.
        Native.SetForegroundWindow(menu.Handle);
    }

    async Task RunClipboardActionAsync(ClipboardActions.Action action, string text)
    {
        Notify("Thokolosi", $"{action.Label.Replace("&&", "&")}...");
        try
        {
            var result = await Task.Run(() => ClipboardActions.RunAsync(Config, action, text, CancellationToken.None));
            Clipboard.SetText(result);
            Notify("Copied to clipboard", Shorten(result, 200) + "\nPaste it with Ctrl+V.");
            Chat.AddExternal($"> Clipboard - {action.Label.Replace("&&", "&")}:\n{result}");
        }
        catch (Exception ex) { Notify("Clipboard action failed", ex.Message); }
    }

    static string SaveClipboardImage(System.Windows.Media.Imaging.BitmapSource img)
    {
        var dir = Path.Combine(Path.GetTempPath(), "thokolosi");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"clip-{DateTime.Now:HHmmss}.png");
        using var fs = File.Create(path);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
        enc.Save(fs);
        return path;
    }

    static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > n ? s[..n] + "..." : s;
    }

    // ---------- INotifier ----------

    public void Notify(string title, string text, Action? onClick = null)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            _balloonAction = onClick;
            _tray.ShowBalloonTip(8000, title, text.Length > 240 ? text[..240] + "..." : text, WinForms.ToolTipIcon.None);
        });
        Phone?.Post("notify", new JsonObject { ["title"] = title, ["text"] = text });
    }

    static void ShowInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    /// <summary>Pick a file and send it to the paired phone(s).</summary>
    public async Task SendFileToPhoneAsync(string? path = null)
    {
        if (Phone is not { Connected: true }) { Notify("Thokolosi", "The phone link isn't connected. Check Settings > Phone."); return; }
        if (Phone.Phones.Count == 0) { Notify("Thokolosi", "No phone is paired yet. Use Settings > Phone > Pair a phone."); return; }
        if (path == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Send a file to your phone", CheckFileExists = true };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        var name = System.IO.Path.GetFileName(path);
        try
        {
            Notify("Thokolosi", $"Sending {name} to your phone...");
            await Task.Run(() => Phone.SendFileAsync(path, null, null, CancellationToken.None));
            Chat.AddExternal($"Sent {name} to your phone.");
            Notify("Thokolosi", $"{name} is on your phone.");
        }
        catch (Exception ex) { Notify("Couldn't send " + name, ex.Message); }
    }

    /// <summary>Runs a prompt with a fresh, separate agent (scheduled jobs, after-meeting tasks) and reports back.</summary>
    public Task RunBackgroundTaskAsync(string title, string prompt) => RunBackgroundTaskCoreAsync(title, prompt, trackOnBoard: true);

    async Task RunBackgroundTaskCoreAsync(string title, string prompt, bool trackOnBoard)
    {
        if (!Config.HasKeyFor(Config.Model)) return;
        var agent = new Agent(Config, Tools, historyPath: null)
        {
            RunContext = $"This is a background task ('{title}') started automatically, not a live chat. The user is not watching; " +
                         "do the work, then finish with a short summary of the result for a notification." +
                         (trackOnBoard
                            ? " Easy Kanban tracking (only if the easy-kanban tools are available, otherwise skip silently): at the start use list_boards, then create_card on board 'Agent Operations' " +
                              $"in the In Progress column titled '{title}' with label 'scheduled' and a one-line description; use log_progress at milestones; at the end call complete_card with a short Markdown result " +
                              "(moveTo done for routine runs, review if Wycliffe needs to look at something or decide). If you fail, call fail_card. Never use claim_next_card for this."
                            : ""),
            ApprovalHandler = (t, d, c) => RequestApprovalAsync(t, d, c),
        };
        string? last = null;
        agent.AssistantText += t => last = t;
        try
        {
            ActivityLog.Write("TASK", $"Background task '{title}' started");
            await Task.Run(() => agent.RunAsync(prompt, CancellationToken.None));
            var summary = last ?? "Done.";
            Dispatcher.Invoke(() => Chat.AddExternal($"[{title}] {summary}"));
            Notify($"Thokolosi: {title}", summary, () => Chat.ShowNearTray());
        }
        catch (Exception ex)
        {
            ActivityLog.Write("TASK", $"'{title}' failed: {ex}");
            Notify($"Thokolosi: {title} failed", ex.Message);
        }
    }

    // ---------- updates ----------

    int _updating;

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!Config.Update.Enabled && !manual) return;
        if (Interlocked.Exchange(ref _updating, 1) == 1) return;
        try
        {
            Config.Update.LastCheck = DateTime.Now;
            Config.Save();
            var info = await Updater.CheckAsync(Config.Update, CancellationToken.None);
            if (info == null)
            {
                if (manual) Notify("Thokolosi is up to date", $"Version {Updater.Current.ToString(3)}.");
                return;
            }
            if (Config.Update.AutoInstall && !Agent.IsBusy && !Meetings.IsRecording)
            {
                await Dispatcher.InvokeAsync(() => InstallUpdateAsync(info)).Task.Unwrap();
                return;
            }
            Notify($"Thokolosi {info.Tag} is available", "Click to update now (takes about a minute).", () => _ = Dispatcher.InvokeAsync(() => InstallUpdateAsync(info)));
        }
        catch (Exception ex)
        {
            ActivityLog.Write("UPDATE", ex.Message);
            if (manual) Notify("Update check failed", ex.Message);
        }
        finally { Interlocked.Exchange(ref _updating, 0); }
    }

    async Task InstallUpdateAsync(UpdateInfo info)
    {
        if (Agent.IsBusy || Meetings.IsRecording)
        {
            Notify("Update postponed", "Thokolosi is busy; it will offer the update again later.");
            return;
        }
        try
        {
            Notify("Updating Thokolosi", $"Downloading {info.Tag}...");
            await Updater.InstallAsync(Config.Update, info, CancellationToken.None);
            await QuitAsync(askAboutMeeting: false);
        }
        catch (Exception ex) { Notify("Update failed", ex.Message); }
    }

    // ---------- misc ----------

    /// <summary>(Re)apply global shortcuts, wake word and voice after startup or a settings change.</summary>
    public void ApplyHotkeysAndVoice(bool showErrors)
    {
        var v = Config.Voice;
        Speaker.Configure(v.VoiceName, v.Rate);

        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        var hwnd = new WindowInteropHelper(Chat).EnsureHandle();
        void Reg(string combo, Action action)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            var hk = GlobalHotkey.Register(hwnd, combo, action, out var err);
            if (hk != null) _hotkeys.Add(hk);
            if (err != null) { ActivityLog.Write("HOTKEY", err); if (showErrors) Notify("Thokolosi shortcut", err); }
        }
        Reg(v.Hotkey, () => Chat.ToggleListening());
        Reg(Config.Hotkeys.Snip, () => _ = SnipAsync());
        Reg(Config.Hotkeys.Clipboard, ShowClipboardMenu);

        WakeWord.Stop();
        if (v.WakeWord)
        {
            var err = WakeWord.Start(v.WakePhrase, v.MicThreshold, v.WhisperModel, v.Language);
            if (err != null) { ActivityLog.Write("VOICE", err); if (showErrors) Notify("Thokolosi voice", err); }
        }
    }

    public void OpenSettings()
    {
        var phoneBefore = (Config.Phone.Enabled, Config.Phone.ApiKey, Config.Phone.DatabaseUrl);
        var w = new SettingsWindow();
        w.ShowDialog();
        ApplyHotkeysAndVoice(showErrors: true);
        if ((Config.Phone.Enabled, Config.Phone.ApiKey, Config.Phone.DatabaseUrl) != phoneBefore) _ = StartPhoneLinkAsync();
        _ = Phone?.PublishJobAsync(AppConfig.LoadJob());
        Chat.RefreshModes();
        RefreshTrayChecks();
        PublishInfo();
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public void ApplyStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (Config.StartWithWindows && exe != null) key.SetValue("Thokolosi", $"\"{exe}\" --tray");
            else key.DeleteValue("Thokolosi", throwOnMissingValue: false);
        }
        catch (Exception ex) { ActivityLog.Write("ERROR", "Start with Windows: " + ex.Message); }
    }

    async Task QuitAsync(bool askAboutMeeting = true)
    {
        if (Meetings.IsRecording && (!askAboutMeeting ||
            MessageBox.Show("Thokolosi is taking meeting notes. Stop and write the notes before quitting?", "Thokolosi",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))
        {
            Meetings.StopNotes();
        }
        if (!Meetings.PendingWork.IsCompleted)
        {
            Notify("Thokolosi", "Finishing meeting notes before closing; this can take a few minutes.");
            if (_tray != null) _tray.Text = "Thokolosi - finishing meeting notes...";
            await Task.WhenAny(Meetings.PendingWork, Task.Delay(TimeSpan.FromMinutes(20)));
        }
        try { _updateTimer?.Dispose(); _scheduler?.Dispose(); _kanban?.Dispose(); ProfileBuilder?.Dispose(); _messaging?.Dispose(); Watcher.Dispose(); Meetings.Dispose(); } catch { }
        try { foreach (var h in _hotkeys) h.Dispose(); WakeWord.Dispose(); Speaker.Dispose(); } catch { }
        try { ExtraTools.Shutdown(); } catch { }
        try { if (Phone != null) await Phone.DisposeAsync(); } catch { }
        try { if (_browser != null) await _browser.CloseAsync(); } catch { }
        try { await Mcp.DisposeAsync(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }
}
