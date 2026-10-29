using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Thokolosi.Core;
using Thokolosi.Core.Meetings;
using Thokolosi.Core.Voice;

namespace Thokolosi;

public record ChatItem(string Role, string Text);

public partial class MainWindow : Window
{
    readonly ObservableCollection<ChatItem> _items = new();
    CancellationTokenSource? _cts;
    CancellationTokenSource? _listenCts;
    static readonly Regex StopWords = new(@"^(stop|cancel|stop (that|it|everything)|cancel (that|it)|never ?mind|abort)\W*$", RegexOptions.IgnoreCase);
    volatile bool _voiceTurn;
    App AppRef => App.Current;

    const string VoiceRunContext =
        "The user spoke this request out loud and will hear your reply read aloud by a text-to-speech voice. " +
        "Answer in short, natural spoken sentences: no markdown, no tables, no bullet lists, no code blocks unless they asked for code. " +
        "Do the work with your tools as usual; just keep what you say brief.";

    public MainWindow()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _items;

        var agent = AppRef.Agent;
        agent.AssistantText += t =>
        {
            Dispatcher.Invoke(() => Add("assistant", t));
            var mode = AppRef.Config.Voice.SpeakReplies;
            if (mode == "always" || (mode == "voice" && _voiceTurn)) AppRef.Speaker.Speak(t);
        };
        AppRef.Speaker.SpeakingChanged += speaking => Dispatcher.BeginInvoke(() =>
        {
            if (speaking) StatusText.Text = "Speaking... (press the mic to interrupt)";
            else if (_cts == null && _listenCts == null) StatusText.Text = IdleStatus();
        });
        agent.Activity += t => Dispatcher.Invoke(() => Add("activity", "> " + (t.Length > 300 ? t[..300] + "..." : t)));
        agent.ApprovalHandler = AskApprovalAsync;

        LoadPreviousChat();
        RefreshModes();
        Deactivated += (_, _) => { /* stay open; hide with the button or tray icon */ };
    }

    // ---------- window placement ----------

    public void ShowNearTray()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - Height - 12;
        Show();
        Activate();
        InputBox.Focus();
    }

    public void Toggle()
    {
        if (IsVisible) Hide(); else ShowNearTray();
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // closing just hides to the tray
        Hide();
    }

    // ---------- modes ----------

    public void RefreshModes()
    {
        var cfg = AppRef.Config;
        AutoToggle.IsChecked = cfg.AutoMode;
        AutoToggle.Content = cfg.AutoMode ? "Auto: acts on its own" : "Ask: I approve actions";
        SilentToggle.IsChecked = cfg.SilentMode;
        SilentToggle.Content = cfg.SilentMode ? "Silent: works invisibly" : "Visible: I can watch";
        ConversationToggle.IsChecked = cfg.Voice.Conversation;
        ConversationToggle.Content = cfg.Voice.Conversation ? "Voice chat: on" : "Voice chat: off";
        if (_cts == null && _listenCts == null) StatusText.Text = IdleStatus();
    }

    string IdleStatus() => AppRef.Meetings?.IsRecording == true ? "\u25CF Taking meeting notes" : "Ready";

    void ConversationToggle_Click(object sender, RoutedEventArgs e)
    {
        AppRef.Config.Voice.Conversation = ConversationToggle.IsChecked == true;
        AppRef.Config.Save();
        RefreshModes();
    }

    // ---------- voice ----------

    void Mic_Click(object sender, RoutedEventArgs e) => ToggleListening();

    /// <summary>Mic button / global hotkey / wake word: start listening, or stop and send what was said.</summary>
    public void ToggleListening()
    {
        if (!IsVisible) ShowNearTray();
        if (_listenCts != null) { _listenCts.Cancel(); return; }        // second press = done talking
        if (AppRef.Speaker.IsSpeaking) AppRef.Speaker.Stop();            // interrupt the reply
        _ = ListenAndSendAsync(followUp: false);
    }

    /// <summary>Wake phrase heard. If the request came in the same breath ("Hey Thokolosi, open Outlook"), run it; else listen.</summary>
    public void OnWakeWord(string? request)
    {
        if (!IsVisible) ShowNearTray();
        AppRef.Speaker.Stop();
        if (!string.IsNullOrWhiteSpace(request))
        {
            System.Media.SystemSounds.Asterisk.Play();
            _ = HandleSpokenAsync(request);
        }
        else if (_listenCts == null)
        {
            _ = ListenAndSendAsync(followUp: false);
        }
    }

    async Task ListenAndSendAsync(bool followUp)
    {
        if (_listenCts != null) return;
        var v = AppRef.Config.Voice;
        AppRef.Speaker.Stop();
        AppRef.WakeWord.Pause();
        _listenCts = new CancellationTokenSource();
        SetListening(true);
        if (!followUp) System.Media.SystemSounds.Asterisk.Play();

        string? wav = null;
        try
        {
            wav = await VoiceRecorder.RecordAsync(v.MicThreshold, _listenCts.Token, waitForSpeechSeconds: followUp ? 6 : 8);
        }
        catch (Exception ex)
        {
            Add("error", "Microphone problem: " + ex.Message + "\nCheck Windows Settings > Privacy & security > Microphone.");
        }
        finally
        {
            _listenCts.Dispose();
            _listenCts = null;
            SetListening(false);
            AppRef.WakeWord.Resume();
        }

        if (wav == null)
        {
            StatusText.Text = _cts != null ? "Working..." : followUp ? IdleStatus() : "Didn't hear anything";
            return;
        }

        string text;
        try
        {
            StatusText.Text = "Transcribing...";
            text = await Transcriber.TranscribeSpeechAsync(wav, v.WhisperModel, v.Language,
                new Progress<string>(s => StatusText.Text = s), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Add("error", "Couldn't transcribe: " + ex.Message);
            StatusText.Text = _cts != null ? "Working..." : IdleStatus();
            return;
        }
        finally { try { File.Delete(wav); } catch { } }

        await HandleSpokenAsync(text);
    }

    /// <summary>Spoken text: "stop"/"cancel" interrupts the current task; anything else is sent (or queued if busy).</summary>
    async Task HandleSpokenAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) { StatusText.Text = _cts != null ? "Working..." : IdleStatus(); return; }
        if (StopWords.IsMatch(text))
        {
            if (_cts != null) StopCurrent(clearQueue: true);
            else StatusText.Text = IdleStatus();
            return;
        }
        if (_cts == null && Regex.IsMatch(text, @"^(nothing|that'?s all|thanks?,? that'?s it|no thanks?)\W*$", RegexOptions.IgnoreCase))
        {
            StatusText.Text = IdleStatus();
            return;
        }
        await SendAsync(text, voice: true);
    }

    void StopCurrent(bool clearQueue)
    {
        if (clearQueue && _requests.Count > 0)
        {
            Add("activity", $"> Cleared {_requests.Count} queued request(s).");
            _requests.Clear();
        }
        _cts?.Cancel();
        AppRef.Speaker.Stop();
    }

    void SetListening(bool on)
    {
        MicButton.Background = on ? new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x50)) : Brushes.Transparent;
        MicButton.Foreground = on ? Brushes.White : (Brush)FindResource("Muted");
        MicButton.ToolTip = on ? "Listening - click when you're done" : "Talk to Thokolosi (" + AppRef.Config.Voice.Hotkey + ")";
        if (on) StatusText.Text = _cts != null ? "\u25CF Listening (still working - I'll queue this)..." : "\u25CF Listening... speak now";
        else if (_cts != null) StatusText.Text = "Working...";
    }

    /// <summary>Put a suggested task in the input box for the user to review and send.</summary>
    public void Prefill(string prompt)
    {
        InputBox.Text = prompt;
        InputBox.CaretIndex = prompt.Length;
        InputBox.Focus();
    }

    /// <summary>Show a message from a background task in the chat (not part of the conversation history).</summary>
    public void AddExternal(string text)
    {
        if (Dispatcher.CheckAccess()) Add("activity", text);
        else Dispatcher.BeginInvoke(() => Add("activity", text));
    }

    void AutoToggle_Click(object sender, RoutedEventArgs e) => AppRef.SetAutoMode(AutoToggle.IsChecked == true);
    void SilentToggle_Click(object sender, RoutedEventArgs e) => AppRef.SetSilentMode(SilentToggle.IsChecked == true);

    void Settings_Click(object sender, RoutedEventArgs e) => AppRef.OpenSettings();
    void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    void NewChat_Click(object sender, RoutedEventArgs e) => NewChatFromRemote();

    public void NewChatFromRemote()
    {
        if (AppRef.Agent.IsBusy) { Add("activity", "> Can't start a new chat while working - press Stop first."); return; }
        AppRef.Agent.NewConversation();
        _items.Clear();
        _attachments.Clear();
        RefreshAttachments();
        Add("assistant", "New chat. What should I do?");
        AppRef.Phone?.Post("status", new System.Text.Json.Nodes.JsonObject { ["text"] = "New chat started" });
    }

    // ---------- attachments (drag-and-drop, paste, snip) ----------

    readonly List<Attachment> _attachments = new();

    public void AddAttachment(string path, bool isImage, string? focusPrompt = null)
    {
        if (_attachments.Any(a => a.Path == path)) return;
        _attachments.Add(new Attachment(path, isImage));
        RefreshAttachments();
        if (!IsVisible) ShowNearTray();
        if (focusPrompt != null && InputBox.Text.Length == 0) Placeholder.Text = focusPrompt;
        InputBox.Focus();
    }

    void RefreshAttachments()
    {
        AttachStrip.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AttachText.Text = _attachments.Count == 0 ? "" :
            "\U0001F4CE " + string.Join(", ", _attachments.Select(a => Path.GetFileName(a.Path)));
        if (_attachments.Count == 0) Placeholder.Text = "Ask Thokolosi to do something...";
    }

    void ClearAttachments_Click(object sender, RoutedEventArgs e) { _attachments.Clear(); RefreshAttachments(); }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files) AddAttachment(f, Agent.IsImageFile(f));
        if (InputBox.Text.Length == 0) Placeholder.Text = "What should I do with " + (files.Length == 1 ? "this?" : "these?");
    }

    void InputBox_Paste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                var dir = Path.Combine(Path.GetTempPath(), "thokolosi");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"paste-{DateTime.Now:HHmmss}.png");
                using (var fs = File.Create(path))
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    enc.Save(fs);
                }
                AddAttachment(path, true);
                e.Handled = true;
            }
            else if (Clipboard.ContainsFileDropList())
            {
                foreach (var f in Clipboard.GetFileDropList().Cast<string>()) AddAttachment(f, Agent.IsImageFile(f));
                e.Handled = true;
            }
        }
        catch { }
    }

    // ---------- sending ----------

    record Request(string Text, bool Voice, string Source, List<Attachment>? Attachments);
    readonly Queue<Request> _requests = new();

    void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Send_Click(sender, e);
        }
    }

    void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_cts != null) SendButton.Content = InputBox.Text.Trim().Length == 0 ? "Stop" : "Queue";
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        // While working: an empty box means "Stop"; typed text is queued to run next.
        if (_cts != null && text.Length == 0 && _attachments.Count == 0) { StopCurrent(clearQueue: false); return; }
        if (text.Length == 0 && _attachments.Count == 0) return;
        if (text.Length == 0) text = "Look at the attached file(s) and tell me what's useful, or ask what I want done.";
        InputBox.Clear();
        var atts = _attachments.ToList();
        _attachments.Clear();
        RefreshAttachments();
        await SendAsync(text, voice: false, "pc", atts);
    }

    /// <summary>A request from the phone, a messaging app, the clipboard menu or a playbook button.</summary>
    public Task SubmitRemoteAsync(string text, string source, string from, IReadOnlyList<Attachment>? attachments = null)
    {
        Add("activity", $"> From {from}:");
        return SendAsync(text, voice: false, source, attachments?.ToList());
    }

    public void StopFromRemote() => StopCurrent(clearQueue: true);

    Task SendAsync(string text, bool voice) => SendAsync(text, voice, voice ? "voice" : "pc", null);

    async Task SendAsync(string text, bool voice, string source, List<Attachment>? attachments)
    {
        var shown = (voice ? "\U0001F399 " : source == "phone" ? "\U0001F4F1 " : "") + text +
                    (attachments is { Count: > 0 } ? "\n\U0001F4CE " + string.Join(", ", attachments.Select(a => Path.GetFileName(a.Path))) : "");
        Add("user", shown);
        AppRef.Phone?.Post("user", new System.Text.Json.Nodes.JsonObject { ["text"] = text, ["source"] = source });
        var req = new Request(text, voice, source, attachments);
        if (_cts != null)
        {
            _requests.Enqueue(req);
            Add("activity", $"> Queued - I'll do this next ({_requests.Count} waiting). Say or press Stop to cancel the current task.");
            AppRef.PublishInfo(queue: _requests.Count);
            return;
        }
        await RunAsync(req);

        // Work through anything that was queued while busy.
        while (_cts == null && _requests.Count > 0)
        {
            var next = _requests.Dequeue();
            Add("activity", "> Next: " + (next.Text.Length > 80 ? next.Text[..80] + "..." : next.Text));
            await RunAsync(next);
        }
    }

    async Task RunAsync(Request req)
    {
        var voice = req.Voice;
        _voiceTurn = voice;
        AppRef.Agent.RunContext = voice ? VoiceRunContext
            : req.Source == "phone" ? "This request came from the user's phone (Thokolosi mobile app). Keep replies compact - they're read on a small screen."
            : null;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        string? lastReply = null;
        void Capture(string t) => lastReply = t;
        AppRef.Agent.AssistantText += Capture;
        try
        {
            await Task.Run(() => AppRef.Agent.RunAsync(req.Text, _cts.Token, req.Attachments));
            if (!IsVisible && lastReply != null && req.Source != "phone") AppRef.Notify("Thokolosi finished", lastReply);
        }
        catch (OperationCanceledException)
        {
            Add("activity", "> Stopped.");
            AppRef.Phone?.Post("status", new System.Text.Json.Nodes.JsonObject { ["text"] = "Stopped" });
        }
        catch (Exception ex)
        {
            ActivityLog.Write("ERROR", ex.ToString());
            Add("error", ex.Message);
            AppRef.Phone?.Post("error", new System.Text.Json.Nodes.JsonObject { ["text"] = ex.Message });
            if (!IsVisible) AppRef.Notify("Thokolosi hit a problem", ex.Message);
        }
        finally
        {
            AppRef.Agent.AssistantText -= Capture;
            AppRef.Agent.RunContext = null;
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }

        // Hands-free conversation: once the reply has been spoken, listen for a follow-up.
        var v = AppRef.Config.Voice;
        if (voice && v.Conversation && v.SpeakReplies != "never" && _requests.Count == 0)
        {
            await AppRef.Speaker.WaitUntilDoneAsync();
            await Task.Delay(300);
            if (_cts == null && _listenCts == null) await ListenAndSendAsync(followUp: true);
        }
    }

    void SetBusy(bool busy)
    {
        SendButton.Content = busy && InputBox.Text.Trim().Length == 0 ? "Stop" : busy ? "Queue" : "Send";
        StatusText.Text = busy ? "Working..." : (AppRef.Meetings.IsRecording ? "\u25CF Taking meeting notes" : "Ready");
        AppRef.PublishInfo(busy: busy, status: StatusText.Text, queue: _requests.Count);
    }

    public Task<bool> AskApprovalAsync(string title, string details, CancellationToken ct) => AppRef.RequestApprovalAsync(title, details, ct);

    // ---------- chat list ----------

    void Add(string role, string text)
    {
        _items.Add(new ChatItem(role, text.Trim()));
        Scroller.ScrollToEnd();
    }

    void LoadPreviousChat()
    {
        foreach (var msg in AppRef.Agent.Messages)
        {
            var role = msg?["role"]?.GetValue<string>();
            if (msg?["content"] is not JsonArray blocks) continue;
            foreach (var b in blocks)
            {
                if (b?["type"]?.GetValue<string>() != "text") continue;
                var t = b["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(t)) _items.Add(new ChatItem(role == "user" ? "user" : "assistant", t.Trim()));
            }
        }
        if (_items.Count == 0)
            _items.Add(new ChatItem("assistant", "Hi, I'm Thokolosi. Tell me what to do on this PC: run commands, find files, browse the web, work in your repos. Set my job in Settings."));
    }
}
