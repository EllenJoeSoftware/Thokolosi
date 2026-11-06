using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Thokolosi.Core;
using Thokolosi.Core.Mcp;
using Thokolosi.Core.Voice;

namespace Thokolosi;

public partial class SettingsWindow : Window
{
    bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        var cfg = App.Current.Config;

        // General
        ModelBox.Text = cfg.Model;
        AutoBox.IsChecked = cfg.AutoMode;
        SilentBox.IsChecked = cfg.SilentMode;
        StartupBox.IsChecked = cfg.StartWithWindows;
        CodeFoldersBox.Text = string.Join(Environment.NewLine, cfg.CodeFolders);
        KeyHint.Text = string.IsNullOrEmpty(cfg.ApiKey)
            ? "Required. Get a key at console.anthropic.com > API keys. Stored encrypted for this Windows user only."
            : "A key is saved. Leave blank to keep it, or paste a new one to replace it.";
        OpenCodeHint.Text = string.IsNullOrEmpty(cfg.OpenCodeApiKey)
            ? "For 'opencode-go/...' models. Get it at opencode.ai > your workspace > Go > API keys. Stored encrypted. Either key on its own is enough."
            : "An OpenCode Go key is saved. Leave blank to keep it, or paste a new one to replace it.";

        // Job
        JobBox.Text = AppConfig.LoadJob();

        // Email & Slack
        OutlookBox.IsChecked = cfg.OutlookEnabled;
        var acct = cfg.EmailAccounts.FirstOrDefault();
        if (acct != null)
        {
            EmailBox.Text = acct.Address;
            ImapHostBox.Text = acct.ImapHost; ImapPortBox.Text = acct.ImapPort.ToString();
            SmtpHostBox.Text = acct.SmtpHost; SmtpPortBox.Text = acct.SmtpPort.ToString();
            ProviderBox.SelectedIndex = acct.ImapHost switch
            {
                "imap.gmail.com" => 0, "outlook.office365.com" => 1, "imap.mail.yahoo.com" => 2, _ => 3
            };
        }
        else ProviderBox.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(cfg.SlackToken)) SlackHint.Text = "A Slack token is saved. Leave blank to keep it. " + SlackHint.Text;

        // Meetings
        var m = cfg.Meetings;
        AutoJoinBox.IsChecked = m.AutoJoin;
        TakeNotesBox.IsChecked = m.TakeNotes;
        MuteBox.IsChecked = m.MuteOnJoin;
        RecordMicBox.IsChecked = m.RecordMicrophone;
        LeadBox.Text = m.JoinMinutesBefore.ToString();
        SubjectFilterBox.Text = string.Join(", ", m.OnlySubjectsContaining);
        WhisperBox.Text = m.WhisperModel;
        SelectByText(WhisperBox, m.WhisperModel);
        AfterMeetingBox.Text = m.AfterMeetingPrompt;

        // Watch & schedule
        var w = cfg.Watcher;
        WatchBox.IsChecked = w.Enabled;
        WatchIntervalBox.Text = w.SuggestEveryMinutes.ToString();
        WatchShotsBox.IsChecked = w.UseScreenshots;
        WatchModelBox.Text = w.Model;
        ExcludeBox.Text = string.Join(Environment.NewLine, w.Exclude);
        SchedulesBox.Text = Scheduler.ToText(cfg.Schedules);

        // Voice
        var v = cfg.Voice;
        HotkeyBox.Text = v.Hotkey;
        WakeBox.IsChecked = v.WakeWord;
        WakePhraseBox.Text = v.WakePhrase;
        SelectByTag(SpeakBox, v.SpeakReplies);
        ConversationBox.IsChecked = v.Conversation;
        VoiceBox.Items.Add(new ComboBoxItem { Content = "(Windows default)", Tag = "" });
        foreach (var name in Speaker.Voices()) VoiceBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        SelectByTag(VoiceBox, v.VoiceName);
        RateSlider.Value = v.Rate;
        SelectByTag(LanguageBox, v.Language);
        SelectByTag(VoiceModelBox, v.WhisperModel);
        ThresholdSlider.Value = v.MicThreshold;

        // Phone
        var ph = cfg.Phone;
        PhoneBox.IsChecked = ph.Enabled;
        FbApiKeyBox.Text = ph.ApiKey;
        FbDbUrlBox.Text = ph.DatabaseUrl;
        FbProjectBox.Text = ph.ProjectId;
        ApprovalTimeoutBox.Text = ph.ApprovalTimeoutMinutes.ToString();
        RefreshPhones();

        // Spending & offline
        SpendText.Text = Core.Brain.UsageTracker.Summary();
        BudgetBox.Text = cfg.Budget.MonthlyUsd.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SelectByTag(AtCapBox, cfg.Budget.AtCap);
        CheapModelBox.Text = cfg.CheapModel;
        LocalBox.IsChecked = cfg.Local.Enabled;
        LocalUrlBox.Text = cfg.Local.Url;
        LocalModelBox.Text = cfg.Local.Model;

        // Business
        FbPageIdBox.Text = cfg.Facebook.PageId;
        if (!string.IsNullOrEmpty(cfg.Facebook.PageAccessToken)) FbHint.Text = "A Page token is saved. " + FbHint.Text;
        MsgBox.IsChecked = cfg.Messaging.Enabled;
        MsgServerBox.Text = cfg.Messaging.McpServer;
        MsgAllowBox.Text = string.Join(Environment.NewLine, cfg.Messaging.AllowedSenders);
        MonitorsStatusBox.Text = MonitorStatusText();
        UpdateBox.IsChecked = cfg.Update.Enabled;
        AutoUpdateBox.IsChecked = cfg.Update.AutoInstall;
        UpdateRepoBox.Text = cfg.Update.Repo;
        if (!string.IsNullOrEmpty(cfg.Update.Token)) UpdateHint.Text = "A token is saved. " + UpdateHint.Text;
        VersionText.Text = $"This is version {Core.Updater.Current.ToString(3)}";
        SnipKeyBox.Text = cfg.Hotkeys.Snip;
        ClipKeyBox.Text = cfg.Hotkeys.Clipboard;

        // MCP
        McpBox.IsChecked = cfg.McpEnabled;
        var status = App.Current.Mcp?.Status;
        string statusText = "";
        if (status != null) lock (status) statusText = string.Join(Environment.NewLine, status);
        McpStatusBox.Text = statusText.Length > 0 ? statusText
            : File.Exists(AppConfig.McpPath) ? "No servers connected (yet). Servers start in the background after launch." : "No mcp.json yet.";

        // Passwords
        RefreshVault();

        _loading = false;
    }

    // ---------- Passwords (saved immediately, encrypted with Windows DPAPI) ----------

    void RefreshVault(string? select = null)
    {
        VaultList.ItemsSource = Vault.Load().Select(e => e.Name).ToList();
        if (select != null) VaultList.SelectedItem = select;
    }

    void VaultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VaultList.SelectedItem is not string name) return;
        var entry = Vault.Find(name);
        if (entry == null) return;
        VaultNameBox.Text = entry.Name;
        VaultUserBox.Text = entry.Username;
        VaultNotesBox.Text = entry.Notes;
        VaultPwBox.Password = "";
        VaultPwHint.Text = "A password is saved for this entry. Leave blank to keep it, or type a new one to replace it.";
    }

    void VaultNew_Click(object sender, RoutedEventArgs e)
    {
        VaultList.SelectedItem = null;
        VaultNameBox.Text = VaultUserBox.Text = VaultNotesBox.Text = "";
        VaultPwBox.Password = "";
        VaultPwHint.Text = "Required for a new entry. For an existing entry leave it blank to keep the saved password.";
        VaultStatus.Text = "";
    }

    void VaultSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = VaultNameBox.Text.Trim();
            Vault.Set(name, VaultUserBox.Text, VaultPwBox.Password, VaultNotesBox.Text);
            VaultPwBox.Password = "";
            RefreshVault(name);
            VaultStatus.Text = $"Saved '{name}' (encrypted).";
        }
        catch (Exception ex) { VaultStatus.Text = "Not saved: " + ex.Message; }
    }

    void VaultDelete_Click(object sender, RoutedEventArgs e)
    {
        var name = VaultNameBox.Text.Trim();
        if (name.Length == 0 || Vault.Find(name) == null) { VaultStatus.Text = "Select a saved entry first."; return; }
        if (MessageBox.Show($"Delete the saved password '{name}'?", "Thokolosi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Vault.Remove(name);
        VaultNew_Click(sender, e);
        RefreshVault();
        VaultStatus.Text = $"Deleted '{name}'.";
    }

    static void SelectByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    static string SelectedTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        using var s = new Speaker();
        s.Configure(SelectedTag(VoiceBox, ""), (int)RateSlider.Value);
        s.Speak("Hi, this is Thokolosi. How can I help?");
        // Keep the test synthesizer alive until it has finished speaking.
        var wait = s.WaitUntilDoneAsync();
        var frame = new System.Windows.Threading.DispatcherFrame();
        wait.ContinueWith(_ => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    static void SelectByText(ComboBox box, string text)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Content == text) { box.SelectedItem = item; return; }
    }

    void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading && !string.IsNullOrEmpty(ImapHostBox.Text)) return;
        (string imap, int ip, string smtp, int sp)? preset = ProviderBox.SelectedIndex switch
        {
            0 => ("imap.gmail.com", 993, "smtp.gmail.com", 465),
            1 => ("outlook.office365.com", 993, "smtp-mail.outlook.com", 587),
            2 => ("imap.mail.yahoo.com", 993, "smtp.mail.yahoo.com", 465),
            _ => null
        };
        if (preset is { } p)
        {
            ImapHostBox.Text = p.imap; ImapPortBox.Text = p.ip.ToString();
            SmtpHostBox.Text = p.smtp; SmtpPortBox.Text = p.sp.ToString();
        }
    }

    static List<string> Lines(string text, params char[] seps) =>
        text.Split(seps.Length > 0 ? seps : new[] { '\n' }).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var app = App.Current;
        var cfg = app.Config;

        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) cfg.ApiKey = ApiKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(OpenCodeKeyBox.Password)) cfg.OpenCodeApiKey = OpenCodeKeyBox.Password.Trim();
        var model = string.IsNullOrWhiteSpace(ModelBox.Text) ? "claude-haiku-5-5" : ModelBox.Text.Trim();
        if (!cfg.HasKeyFor(model))
        {
            MessageBox.Show(this, Core.OpenCodeGo.Handles(model)
                    ? $"{model} runs on OpenCode Go. Paste your OpenCode Go API key, or pick a Claude model."
                    : "Thokolosi needs a Claude API key for Claude models. Paste one, or add an OpenCode Go key and pick an 'opencode-go/...' model.",
                "Thokolosi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        cfg.Model = model;
        cfg.StartWithWindows = StartupBox.IsChecked == true;
        cfg.CodeFolders = Lines(CodeFoldersBox.Text, '\n');

        cfg.OutlookEnabled = OutlookBox.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            var acct = cfg.EmailAccounts.FirstOrDefault();
            if (acct == null) { acct = new EmailAccount(); cfg.EmailAccounts.Insert(0, acct); }
            acct.Address = EmailBox.Text.Trim();
            acct.Username = acct.Address;
            acct.Name = acct.Address;
            acct.ImapHost = ImapHostBox.Text.Trim();
            acct.ImapPort = int.TryParse(ImapPortBox.Text, out var ip) ? ip : 993;
            acct.SmtpHost = SmtpHostBox.Text.Trim();
            acct.SmtpPort = int.TryParse(SmtpPortBox.Text, out var sp) ? sp : 465;
            if (!string.IsNullOrWhiteSpace(EmailPasswordBox.Password)) acct.Password = EmailPasswordBox.Password.Replace(" ", "");
        }
        else if (cfg.EmailAccounts.Count > 0 && MessageBox.Show(this, "Remove the saved email account?", "Thokolosi", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            cfg.EmailAccounts.RemoveAt(0);
        }
        if (!string.IsNullOrWhiteSpace(SlackBox.Password)) cfg.SlackToken = SlackBox.Password;

        var m = cfg.Meetings;
        m.AutoJoin = AutoJoinBox.IsChecked == true;
        m.TakeNotes = TakeNotesBox.IsChecked == true;
        m.MuteOnJoin = MuteBox.IsChecked == true;
        m.RecordMicrophone = RecordMicBox.IsChecked == true;
        m.JoinMinutesBefore = int.TryParse(LeadBox.Text, out var lead) ? Math.Clamp(lead, 0, 15) : 1;
        m.OnlySubjectsContaining = Lines(SubjectFilterBox.Text, ',');
        m.WhisperModel = string.IsNullOrWhiteSpace(WhisperBox.Text) ? "base" : WhisperBox.Text.Trim();
        m.AfterMeetingPrompt = AfterMeetingBox.Text.Trim();

        var w = cfg.Watcher;
        w.Enabled = WatchBox.IsChecked == true;
        w.SuggestEveryMinutes = int.TryParse(WatchIntervalBox.Text, out var iv) ? Math.Clamp(iv, 3, 240) : 15;
        w.UseScreenshots = WatchShotsBox.IsChecked == true;
        w.Model = string.IsNullOrWhiteSpace(WatchModelBox.Text) ? "claude-haiku-5-5" : WatchModelBox.Text.Trim();
        w.Exclude = Lines(ExcludeBox.Text, '\n');
        cfg.Schedules = Scheduler.FromText(SchedulesBox.Text, cfg.Schedules);

        cfg.McpEnabled = McpBox.IsChecked == true;

        var ph = cfg.Phone;
        ph.Enabled = PhoneBox.IsChecked == true;
        ph.ApiKey = FbApiKeyBox.Text.Trim();
        ph.DatabaseUrl = FbDbUrlBox.Text.Trim();
        ph.ProjectId = FbProjectBox.Text.Trim();
        ph.ApprovalTimeoutMinutes = int.TryParse(ApprovalTimeoutBox.Text, out var ato) ? Math.Clamp(ato, 1, 120) : 10;

        cfg.Budget.MonthlyUsd = double.TryParse(BudgetBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bud) ? Math.Max(0, bud) : 0;
        cfg.Budget.AtCap = SelectedTag(AtCapBox, "cheaper");
        cfg.CheapModel = string.IsNullOrWhiteSpace(CheapModelBox.Text) ? "claude-haiku-5-5" : CheapModelBox.Text.Trim();
        cfg.Local.Enabled = LocalBox.IsChecked == true;
        cfg.Local.Url = string.IsNullOrWhiteSpace(LocalUrlBox.Text) ? "http://localhost:11434" : LocalUrlBox.Text.Trim();
        cfg.Local.Model = string.IsNullOrWhiteSpace(LocalModelBox.Text) ? "llama3.2" : LocalModelBox.Text.Trim();

        cfg.Facebook.PageId = FbPageIdBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(FbTokenBox.Password)) cfg.Facebook.PageAccessToken = FbTokenBox.Password.Trim();
        cfg.Messaging.Enabled = MsgBox.IsChecked == true;
        cfg.Messaging.McpServer = string.IsNullOrWhiteSpace(MsgServerBox.Text) ? "hermes" : MsgServerBox.Text.Trim();
        cfg.Messaging.AllowedSenders = Lines(MsgAllowBox.Text, '\n');
        cfg.Update.Enabled = UpdateBox.IsChecked == true;
        cfg.Update.AutoInstall = AutoUpdateBox.IsChecked == true;
        cfg.Update.Repo = string.IsNullOrWhiteSpace(UpdateRepoBox.Text) ? "EllenJoeSoftware/thokolosi-mobile" : UpdateRepoBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(UpdateTokenBox.Password)) cfg.Update.Token = UpdateTokenBox.Password.Trim();
        cfg.Hotkeys.Snip = SnipKeyBox.Text.Trim();
        cfg.Hotkeys.Clipboard = ClipKeyBox.Text.Trim();

        var v = cfg.Voice;
        v.Hotkey = HotkeyBox.Text.Trim();
        v.WakeWord = WakeBox.IsChecked == true;
        v.WakePhrase = string.IsNullOrWhiteSpace(WakePhraseBox.Text) ? "Hey Thokolosi" : WakePhraseBox.Text.Trim();
        v.SpeakReplies = SelectedTag(SpeakBox, "voice");
        v.Conversation = ConversationBox.IsChecked == true;
        v.VoiceName = SelectedTag(VoiceBox, "");
        v.Rate = (int)RateSlider.Value;
        v.Language = SelectedTag(LanguageBox, "en");
        v.WhisperModel = SelectedTag(VoiceModelBox, "base");
        v.MicThreshold = Math.Round(ThresholdSlider.Value, 4);

        cfg.Save();
        app.SetAutoMode(AutoBox.IsChecked == true);
        app.SetSilentMode(SilentBox.IsChecked == true);
        app.ApplyStartWithWindows();
        AppConfig.SaveJob(JobBox.Text);
        Close();
        if (!app.Chat.IsVisible) app.Chat.ShowNearTray();
    }

    // ---------- phone ----------

    void RefreshPhones()
    {
        var link = App.Current.Phone;
        PhonesList.Items.Clear();
        if (link == null)
            PhoneStatusText.Text = App.Current.Config.Phone.Configured ? "Not connected (enable it and Save)." : "Fill in the Firebase details and Save.";
        else if (!link.Connected)
            PhoneStatusText.Text = "Not connected: " + (link.LastError ?? "starting...");
        else
        {
            PhoneStatusText.Text = $"Connected. This PC's id: {link.PcUid}";
            foreach (var p in link.Phones) PhonesList.Items.Add(new ListBoxItem { Content = $"{p.Name}  (paired {p.PairedAt:yyyy-MM-dd})", Tag = p.Uid });
            if (link.Phones.Count == 0) PhonesList.Items.Add(new ListBoxItem { Content = "No phones paired yet", IsEnabled = false });
        }
    }

    void Pair_Click(object sender, RoutedEventArgs e) => App.Current.ShowPairWindow();

    async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (PhonesList.SelectedItem is not ListBoxItem { Tag: string uid } || App.Current.Phone == null) return;
        try { await App.Current.Phone.UnpairAsync(uid); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Thokolosi"); }
        await Task.Delay(800);
        RefreshPhones();
    }

    // ---------- spending / offline ----------

    /// <summary>Replace the opencode-go entries in the model pickers with the gateway's current list.</summary>
    async void LoadGoModels_Click(object sender, RoutedEventArgs e)
    {
        var btn = (System.Windows.Controls.Button)sender;
        btn.IsEnabled = false;
        try
        {
            var models = await Core.OpenCodeGo.UsableModelsAsync(CancellationToken.None);
            foreach (var box in new[] { ModelBox, CheapModelBox, WatchModelBox })
            {
                var text = box.Text;
                foreach (var old in box.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                             .Where(i => (i.Content as string)?.StartsWith(Core.OpenCodeGo.Prefix) == true).ToList())
                    box.Items.Remove(old);
                foreach (var m in models) box.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = m });
                box.Text = text;
            }
            MessageBox.Show(this, $"{models.Count} OpenCode Go models with tool support are now in the model lists.", "Thokolosi");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't load the OpenCode Go model list: " + ex.Message, "Thokolosi"); }
        finally { btn.IsEnabled = true; }
    }

    void OpenUsage_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Path.Combine(AppConfig.Dir, "usage"));

    async void TestLocal_Click(object sender, RoutedEventArgs e)
    {
        LocalTestText.Text = "Testing...";
        var s = new Core.Brain.LocalModelSettings { Enabled = true, Url = LocalUrlBox.Text.Trim(), Model = LocalModelBox.Text.Trim() };
        if (!await Core.Brain.LocalModel.AvailableAsync(s)) { LocalTestText.Text = "Ollama isn't reachable at that URL."; return; }
        try
        {
            var reply = await Core.Brain.LocalModel.ChatAsync(s, "Reply in five words or fewer.", new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = new System.Text.Json.Nodes.JsonArray { new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = "Say hello." } } }
            }, CancellationToken.None);
            LocalTestText.Text = "Works: " + reply.Trim();
        }
        catch (Exception ex) { LocalTestText.Text = ex.Message; }
    }

    // ---------- business ----------

    void OpenMemory_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Core.Brain.MemoryStore.Path)) Core.Brain.MemoryStore.Add("Thokolosi memory file created.", "system");
        Notepad(Core.Brain.MemoryStore.Path);
    }

    void OpenPlaybooks_Click(object sender, RoutedEventArgs e)
    {
        Core.Brain.PlaybookStore.All();
        App.OpenFolder(Core.Brain.PlaybookStore.Dir);
    }

    static string MonitorStatusText()
    {
        var cfg = App.Current.Config;
        if (cfg.Monitors.Count == 0) return "No monitors yet.";
        var status = ExtraTools.Monitors?.Status();
        return string.Join(Environment.NewLine, cfg.Monitors.Select(m =>
        {
            var st = status?.FirstOrDefault(s => s.Name == m.Name);
            if (st == null) return $"{m.Name} ({m.Kind}){(m.Enabled ? "" : " - off")}: starting";
            var vals = string.Join(", ", st.Values.Take(5).Select(v => $"{v.Name} {v.Raw}{v.Unit}"));
            var state = st.LastError != null ? "error: " + st.LastError : st.Connected ? "connected" : "not connected";
            return $"{m.Name} ({m.Kind}{(m.Enabled ? "" : ", off")}): {state}{(vals.Length > 0 ? " - " + vals : "")}";
        }));
    }

    void AddSolar_Click(object sender, RoutedEventArgs e)
    {
        var host = SolarHostBox.Text.Trim();
        if (host.Length == 0) { MessageBox.Show(this, "Enter the SolarAssistant host name or IP (e.g. 192.168.1.50).", "Thokolosi"); return; }
        var cfg = App.Current.Config;
        cfg.Monitors.RemoveAll(m => m.Name == "solar");
        var preset = Core.Monitors.MonitorSettings.SolarAssistantPreset(host);
        preset.Name = "solar";
        cfg.Monitors.Add(preset);
        cfg.Save();
        ExtraTools.ReloadMonitors();
        MonitorsStatusBox.Text = MonitorStatusText();
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(UpdateTokenBox.Password)) App.Current.Config.Update.Token = UpdateTokenBox.Password.Trim();
        App.Current.Config.Update.Repo = UpdateRepoBox.Text.Trim();
        await App.Current.CheckForUpdatesAsync(manual: true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    void OpenData_Click(object sender, RoutedEventArgs e) => App.OpenFolder(AppConfig.Dir);
    void OpenNotes_Click(object sender, RoutedEventArgs e) => App.OpenFolder(AppConfig.MeetingsDir);
    void EditConfig_Click(object sender, RoutedEventArgs e) => Notepad(AppConfig.ConfigPath);

    void EditMcp_Click(object sender, RoutedEventArgs e)
    {
        McpManager.EnsureConfigFile();
        Notepad(AppConfig.McpPath);
    }

    void ReimportMcp_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppConfig.McpPath)) File.Copy(AppConfig.McpPath, AppConfig.McpPath + ".bak", true);
        File.Delete(AppConfig.McpPath);
        McpManager.EnsureConfigFile();
        var n = McpManager.LoadServers().Count;
        McpStatusBox.Text = $"Imported {n} server(s). Restart Thokolosi to connect. (Previous file saved as mcp.json.bak)";
    }

    static void Notepad(string path) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
}
