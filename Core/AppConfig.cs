using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thokolosi.Core;

/// <summary>
/// Settings stored in %APPDATA%\Thokolosi\config.json. Secrets (API key, passwords, tokens) are
/// encrypted with Windows DPAPI, so only the same Windows user on the same PC can read them.
/// </summary>
public class AppConfig
{
    public string? ApiKeyProtected { get; set; }
    public string Model { get; set; } = "claude-haiku-5-5";
    public int MaxTokens { get; set; } = 8192;

    /// <summary>Auto = act without asking. Off (Ask) = user approves every action that changes something.</summary>
    public bool AutoMode { get; set; } = false;

    /// <summary>Silent = hidden command windows + headless browser. Off (Visible) = you can watch.</summary>
    public bool SilentMode { get; set; } = false;

    public bool StartWithWindows { get; set; } = true;
    public int MaxToolIterations { get; set; } = 40;

    /// <summary>Folders with your code/repos. Mentioned to Claude so it knows where to look.</summary>
    public List<string> CodeFolders { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos")
    };

    /// <summary>
    /// Safety net: PowerShell commands containing any of these always need your approval, even in Auto mode.
    /// Clear the list if you want no exceptions.
    /// </summary>
    public List<string> AlwaysConfirmPatterns { get; set; } = new()
    {
        "Format-Volume", "Clear-Disk", "Initialize-Disk", "diskpart", "bcdedit", "cipher /w",
        "Remove-Item -Recurse", "rm -r", "rd /s", "rmdir /s", "del /s",
        "Stop-Computer", "Restart-Computer", "shutdown", "reg delete", "Remove-ItemProperty -Path HKLM",
        "git push --force", "git push -f", "git reset --hard"
    };

    // ---- integrations ----
    public List<EmailAccount> EmailAccounts { get; set; } = new();
    public bool OutlookEnabled { get; set; } = true;
    public string? SlackTokenProtected { get; set; }
    public string? OpenCodeKeyProtected { get; set; }
    public bool McpEnabled { get; set; } = true;
    public MeetingSettings Meetings { get; set; } = new();
    public WatcherSettings Watcher { get; set; } = new();
    public ProfileSettings Profile { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public PhoneSettings Phone { get; set; } = new();

    /// <summary>Model used when the budget cap says "cheaper", and for quick jobs.</summary>
    public string CheapModel { get; set; } = "claude-haiku-5-5";
    public Brain.BudgetSettings Budget { get; set; } = new();
    public Brain.LocalModelSettings Local { get; set; } = new();
    public RemoteChatSettings Messaging { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public Tools.FacebookSettings Facebook { get; set; } = new();
    public List<Monitors.MonitorSettings> Monitors { get; set; } = new();
    public List<ScheduledJob> Schedules { get; set; } = new();

    // ---- paths ----
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thokolosi");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string JobPath => Path.Combine(Dir, "job.md");
    public static string McpPath => Path.Combine(Dir, "mcp.json");
    public static string HistoryPath => Path.Combine(Dir, "history.json");
    public static string LogDir => Path.Combine(Dir, "logs");
    public static string BrowserProfileDir => Path.Combine(Dir, "browser-profile");
    public static string WorkDir => Path.Combine(Dir, "workspace");
    public static string ModelsDir => Path.Combine(Dir, "models");
    public static string MeetingsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Thokolosi", "Meetings");
    public static string ActivityDir => Path.Combine(Dir, "activity");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            var k = Secret.Unprotect(ApiKeyProtected);
            if (!string.IsNullOrEmpty(k)) return k;
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }
        set => ApiKeyProtected = Secret.Protect(value);
    }

    [JsonIgnore]
    public string? SlackToken
    {
        get => Secret.Unprotect(SlackTokenProtected) ?? NullIfEmpty(Environment.GetEnvironmentVariable("SLACK_TOKEN"));
        set => SlackTokenProtected = Secret.Protect(value);
    }

    /// <summary>OpenCode Go key (opencode.ai > Go > API keys), for models named "opencode-go/...".</summary>
    [JsonIgnore]
    public string? OpenCodeApiKey
    {
        get => Secret.Unprotect(OpenCodeKeyProtected) ?? NullIfEmpty(Environment.GetEnvironmentVariable("OPENCODE_API_KEY"));
        set => OpenCodeKeyProtected = Secret.Protect(value);
    }

    /// <summary>True if a key exists for the provider that serves this model.</summary>
    public bool HasKeyFor(string model) =>
        !string.IsNullOrEmpty(OpenCodeGo.Handles(model) ? OpenCodeApiKey : ApiKey);

    /// <summary>The key to pass for this model, or a clear error naming the missing one.</summary>
    public string KeyFor(string model) => OpenCodeGo.Handles(model)
        ? OpenCodeApiKey ?? throw new InvalidOperationException("No OpenCode Go API key set. Open Settings from the tray icon and paste it under General.")
        : ApiKey ?? throw new InvalidOperationException("No Claude API key set. Open Settings from the tray icon and paste your key.");

    /// <summary>At least one provider is set up.</summary>
    [JsonIgnore] public bool HasAnyKey => !string.IsNullOrEmpty(ApiKey) || !string.IsNullOrEmpty(OpenCodeApiKey);

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static readonly object SaveGate = new();

    public static AppConfig Load()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(WorkDir);
        AppConfig cfg;
        try
        {
            cfg = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig()
                : new AppConfig();
        }
        catch { cfg = new AppConfig(); }
        cfg.Save(); // writes any new settings added in this version
        if (!File.Exists(JobPath)) File.WriteAllText(JobPath, DefaultJob);
        return cfg;
    }

    public void Save()
    {
        lock (SaveGate)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
    }

    public static string LoadJob() => File.Exists(JobPath) ? File.ReadAllText(JobPath) : DefaultJob;
    public static void SaveJob(string text) { Directory.CreateDirectory(Dir); File.WriteAllText(JobPath, text); }

    public const string DefaultJob = """
        # Thokolosi - job description

        Tell Thokolosi who it works for and what its job is on this PC.
        It reads this file before every task. Edit it from the tray icon > Settings.

        ## Who I work for
        - Name:
        - Role / business:
        - Timezone: Africa/Johannesburg

        ## My job on this PC
        - Help with day-to-day computer tasks: files, apps, installs, troubleshooting.
        - Research things on the web and give short, sourced answers.
        - Work with code and git repos in my code folders.
        - Keep on top of my email and Slack when asked; summarise what needs my attention.
        - Take notes in my Teams meetings and list action items.

        ## Rules
        - Ask before sending any email or message on my behalf.
        - Ask before deleting anything outside the Thokolosi workspace folder.
        - Never push to main/master without asking.
        - Never make purchases or payments.
        """;
}

public class EmailAccount
{
    public string Name { get; set; } = "Main";
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string? PasswordProtected { get; set; }

    /// <summary>Plain password for an outgoing connection; never empty (connect fails with a clear message instead).</summary>
    public string RequirePassword() => Password is { Length: > 0 } p ? p : throw new InvalidOperationException($"No password saved for {Address} (Settings > Email & Slack).");

    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 465;

    [JsonIgnore]
    public string? Password { get => Secret.Unprotect(PasswordProtected); set => PasswordProtected = Secret.Protect(value); }
}

public class MeetingSettings
{
    /// <summary>Watch the Outlook calendar and join Teams meetings automatically.</summary>
    public bool AutoJoin { get; set; } = false;

    /// <summary>Record + transcribe + summarise meetings it joins.</summary>
    public bool TakeNotes { get; set; } = true;

    /// <summary>Join this many minutes before the start time.</summary>
    public int JoinMinutesBefore { get; set; } = 1;

    /// <summary>Turn mic and camera off before joining.</summary>
    public bool MuteOnJoin { get; set; } = true;

    /// <summary>Also record your microphone (otherwise only other people's audio is captured).</summary>
    public bool RecordMicrophone { get; set; } = true;

    /// <summary>Whisper model for local transcription: tiny, base, small, medium (bigger = better + slower).</summary>
    public string WhisperModel { get; set; } = "base";

    /// <summary>Only auto-join meetings whose subject contains one of these (empty = all Teams meetings).</summary>
    public List<string> OnlySubjectsContaining { get; set; } = new();

    /// <summary>Optional instruction Thokolosi carries out after notes are written, e.g. "Post the action items to #team on Slack".</summary>
    public string AfterMeetingPrompt { get; set; } = "";
}

/// <summary>Building the user model (Core\Profile). On by default on every install; set Enabled to false to stop.</summary>
public class ProfileSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>How often the model is refreshed when something new has happened.</summary>
    public int IntervalMinutes { get; set; } = 30;
    /// <summary>Model used to refine the model. Empty = same as the main model (Settings > General).</summary>
    public string Model { get; set; } = "";
}

public class WatcherSettings
{
    /// <summary>Watch which apps/windows you use and suggest ways to help.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often (minutes) to review recent activity and maybe suggest something.</summary>
    public int SuggestEveryMinutes { get; set; } = 15;

    /// <summary>Include a low-res screenshot in the review.</summary>
    public bool UseScreenshots { get; set; } = true;

    /// <summary>Model for this background job. Empty = same as the main model (Settings > General).</summary>
    public string Model { get; set; } = "";

    /// <summary>Never screenshot or log these apps or window titles (case-insensitive substring).</summary>
    public List<string> Exclude { get; set; } = new()
    {
        "KeePass", "1Password", "Bitwarden", "LastPass", "password", "bank", "FNB", "Capitec", "Absa", "Standard Bank", "Nedbank", "InPrivate", "Incognito"
    };
}

public class RemoteChatSettings
{
    /// <summary>Let people message Thokolosi through Hermes (WhatsApp/Telegram/Discord bridge) and have it act.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Name of the Hermes MCP server in mcp.json.</summary>
    public string McpServer { get; set; } = "hermes";
    /// <summary>Only messages whose sender/chat contains one of these (phone number, username, chat id) are acted on.</summary>
    public List<string> AllowedSenders { get; set; } = new();
    public long Cursor { get; set; }
}

public class UpdateSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>GitHub repo that publishes releases with Thokolosi-win-x64.zip.</summary>
    public string Repo { get; set; } = "EllenJoeSoftware/thokolosi-mobile";
    /// <summary>Install updates automatically (otherwise ask with a notification).</summary>
    public bool AutoInstall { get; set; } = false;
    /// <summary>Read-only GitHub token (needed while the repo is private).</summary>
    public string? TokenProtected { get; set; }
    [JsonIgnore] public string? Token { get => Secret.Unprotect(TokenProtected); set => TokenProtected = Secret.Protect(value); }
    public DateTime LastCheck { get; set; }
}

public class HotkeySettings
{
    public string Snip { get; set; } = "Ctrl+Alt+S";
    public string Clipboard { get; set; } = "Ctrl+Alt+C";
}

public class PhoneSettings
{
    public bool Enabled { get; set; } = true;
    // Firebase project identifiers (not secrets). Set in Settings > Phone.
    public string ApiKey { get; set; } = "AIzaSyCChfsVaXicAN7OIwo9q9PyJT8by3aTXOQ";
    public string DatabaseUrl { get; set; } = "https://thokolosi-default-rtdb.europe-west1.firebasedatabase.app";
    public string ProjectId { get; set; } = "thokolosi";
    // This PC's anonymous Firebase identity (kept so pairings survive restarts).
    public string? PcUid { get; set; }
    public string? RefreshTokenProtected { get; set; }
    [JsonIgnore] public string? RefreshToken { get => Secret.Unprotect(RefreshTokenProtected); set => RefreshTokenProtected = Secret.Protect(value); }
    /// <summary>How long a phone approval request waits before it counts as "No".</summary>
    public int ApprovalTimeoutMinutes { get; set; } = 10;
    [JsonIgnore] public bool Configured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(DatabaseUrl);
}

public class VoiceSettings
{
    /// <summary>When to read replies aloud: "voice" (only when I spoke the request), "always", or "never".</summary>
    public string SpeakReplies { get; set; } = "voice";

    /// <summary>Hands-free: after a spoken reply, listen again for a follow-up (stops when you say nothing).</summary>
    public bool Conversation { get; set; } = false;

    /// <summary>Global push-to-talk shortcut, e.g. "Ctrl+Alt+Space". Empty = none.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Listen for the wake phrase (uses the Windows speech recognizer, offline).</summary>
    public bool WakeWord { get; set; } = false;
    public string WakePhrase { get; set; } = "Hey Thokolosi";

    /// <summary>Windows voice name (blank = default) and speed -10..10.</summary>
    public string VoiceName { get; set; } = "";
    public int Rate { get; set; } = 1;

    /// <summary>Whisper model for voice commands (tiny = fastest, base = good, small = most accurate).</summary>
    public string WhisperModel { get; set; } = "base";

    /// <summary>Language code you speak (en, af, zu, xh, st...) or "auto".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Mic level that counts as speech; raise it in noisy rooms.</summary>
    public double MicThreshold { get; set; } = 0.015;
}

public class ScheduledJob
{
    public string Name { get; set; } = "";
    /// <summary>24h local time, e.g. "07:30".</summary>
    public string Time { get; set; } = "08:00";
    /// <summary>Days: "daily", "weekdays", or comma list like "Mon,Wed,Fri".</summary>
    public string Days { get; set; } = "weekdays";
    public string Prompt { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime? LastRun { get; set; }
}
