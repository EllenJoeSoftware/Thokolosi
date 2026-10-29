using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Thokolosi.Core.Tools;

namespace Thokolosi.Core.Meetings;

public interface INotifier
{
    void Notify(string title, string text, Action? onClick = null);
    Task RunBackgroundTaskAsync(string title, string prompt);
}

/// <summary>
/// Watches the Outlook calendar, joins Teams meetings through the desktop app, records the audio,
/// transcribes it locally with Whisper and writes meeting notes (summary, decisions, action items).
/// </summary>
public sealed class MeetingService : IDisposable
{
    readonly AppConfig _cfg;
    readonly INotifier _notifier;
    readonly ClaudeClient _claude = new();
    readonly System.Threading.Timer _timer;
    readonly HashSet<string> _handled = new();
    readonly SemaphoreSlim _tickGate = new(1, 1);
    readonly object _gate = new();

    ActiveMeeting? _current;
    AudioRecorder? _recorder;
    Task _processing = Task.CompletedTask;

    /// <summary>Transcription/summary work still running (awaited before the app quits).</summary>
    public Task PendingWork => _processing;

    public event Action? StateChanged;

    class ActiveMeeting
    {
        public string Subject = "Meeting";
        public DateTime Started = DateTime.Now;
        public DateTime? End;
        public bool ViaTeams;
        public int MissedChecks;
        public string BasePath = "";
    }

    public MeetingService(AppConfig cfg, INotifier notifier)
    {
        _cfg = cfg;
        _notifier = notifier;
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public bool IsRecording => _recorder?.IsRecording == true;

    public string Status
    {
        get
        {
            var m = _current;
            if (m == null) return _cfg.Meetings.AutoJoin ? "No meeting in progress. Auto-join is ON." : "No meeting in progress. Auto-join is off.";
            return $"In '{m.Subject}' since {m.Started:HH:mm}{(IsRecording ? ", taking notes (recording)" : "")}{(m.End.HasValue ? $", scheduled to end {m.End:HH:mm}" : "")}.";
        }
    }

    // ---------- public actions (used by the tool, tray menu and the timer) ----------

    public async Task<string> JoinAsync(string url, string subject, DateTime? end, bool takeNotes, CancellationToken ct = default)
    {
        ActiveMeeting meeting;
        lock (_gate)
        {
            if (_current != null) return "Already in a meeting: " + _current.Subject;
            // Claim the slot before the (slow) join so a second join can't start meanwhile.
            _current = meeting = new ActiveMeeting { Subject = subject, End = end, ViaTeams = true };
        }
        ActivityLog.Write("MEETING", $"Joining '{subject}' {url}");
        string result;
        try { result = await TeamsJoiner.JoinAsync(url, _cfg.Meetings.MuteOnJoin, ct); }
        catch
        {
            lock (_gate) if (_current == meeting) _current = null;
            throw;
        }
        meeting.Started = DateTime.Now;
        if (takeNotes)
        {
            try { lock (_gate) if (_current == meeting) StartRecording(meeting); }
            catch (Exception ex) { result += " But recording failed: " + ex.Message; takeNotes = false; }
        }
        StateChanged?.Invoke();
        _notifier.Notify("Thokolosi joined a meeting", $"{subject}\n{result}{(takeNotes ? "\nTaking notes." : "")}");
        return result + (takeNotes ? " Taking notes (recording + local transcription)." : "");
    }

    public string StartNotes(string subject, DateTime? end = null)
    {
        lock (_gate)
        {
            if (IsRecording) return "Already taking notes for: " + _current?.Subject;
            bool created = _current == null;
            _current ??= new ActiveMeeting { Subject = string.IsNullOrWhiteSpace(subject) ? "Meeting" : subject, End = end };
            try { StartRecording(_current); }
            catch (Exception ex)
            {
                if (created) _current = null;
                return "Couldn't start recording: " + ex.Message;
            }
        }
        StateChanged?.Invoke();
        return $"Taking notes for '{_current!.Subject}'. Say 'stop notes' (or use the tray menu) when it ends.";
    }

    public string StopNotes()
    {
        ActiveMeeting? m;
        AudioRecorder? rec;
        lock (_gate)
        {
            m = _current;
            rec = _recorder;
            _current = null;
            _recorder = null;
        }
        StateChanged?.Invoke();
        if (m == null) return "No meeting in progress.";
        if (rec == null) return $"Left '{m.Subject}' (no notes were being taken).";
        var previous = _processing;
        // Stop the capture off the UI thread (NAudio may post its stop event to the caller's context).
        _processing = Task.Run(async () =>
        {
            rec.Stop();
            await ProcessRecordingAsync(m, rec.SpeakerPath, rec.MicPath);
            await previous;
        });
        return $"Stopped recording '{m.Subject}'. Transcribing and writing notes in the background; you'll get a notification when they're ready.";
    }

    public async Task<string> LeaveAsync()
    {
        var r = await TeamsJoiner.LeaveAsync();
        return r + " " + StopNotes();
    }

    public async Task<List<CalendarEvent>> UpcomingAsync(int hours)
    {
        var events = await StaRunner.Run(() => Outlook.Events(DateTime.Now.AddMinutes(-30), DateTime.Now.AddHours(hours))).WaitAsync(TimeSpan.FromMinutes(2));
        return events.Where(e => e.End > DateTime.Now).OrderBy(e => e.Start).ToList();
    }

    // ---------- internals ----------

    void StartRecording(ActiveMeeting m)
    {
        var safe = Regex.Replace(m.Subject, @"[^\w\- ]", "").Trim();
        if (safe.Length > 60) safe = safe[..60];
        m.BasePath = Path.Combine(AppConfig.MeetingsDir, "audio", $"{DateTime.Now:yyyy-MM-dd HHmm} {safe}");
        var rec = new AudioRecorder();
        var includeMic = _cfg.Meetings.RecordMicrophone;
        // Create/start on a pool thread so NAudio doesn't bind its events to the UI thread.
        Task.Run(() => rec.Start(m.BasePath, includeMic)).GetAwaiter().GetResult();
        _recorder = rec;
        ActivityLog.Write("MEETING", "Recording started: " + m.BasePath);
    }

    async Task TickAsync()
    {
        if (!await _tickGate.WaitAsync(0)) return;
        try
        {
            var m = _current;
            if (m != null)
            {
                // Stop when the meeting is over: past its end time, or the Teams call window is gone.
                bool overTime = m.End.HasValue && DateTime.Now > m.End.Value.AddMinutes(15);
                if (m.ViaTeams && (DateTime.Now - m.Started).TotalMinutes > 2)
                {
                    bool inCall = await TeamsJoiner.InMeetingAsync();
                    m.MissedChecks = inCall ? 0 : m.MissedChecks + 1;
                }
                if (overTime || m.MissedChecks >= 3)
                {
                    ActivityLog.Write("MEETING", overTime ? "Meeting past end time; stopping." : "Teams call ended; stopping.");
                    StopNotes();
                }
                return;
            }

            if (!_cfg.Meetings.AutoJoin || !_cfg.OutlookEnabled) return;
            var lead = Math.Max(0, _cfg.Meetings.JoinMinutesBefore);
            var now = DateTime.Now;
            var events = await StaRunner.Run(() => Outlook.Events(now.AddHours(-1), now.AddMinutes(lead + 2))).WaitAsync(TimeSpan.FromMinutes(2));
            var filters = _cfg.Meetings.OnlySubjectsContaining.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var due = events.FirstOrDefault(e =>
                e.TeamsUrl != null &&
                !_handled.Contains(e.Id) &&
                e.Start.AddMinutes(-lead) <= now &&
                now < e.End.AddMinutes(-2) &&
                (filters.Count == 0 || filters.Any(f => e.Subject.Contains(f, StringComparison.OrdinalIgnoreCase))));
            if (due == null) return;
            _handled.Add(due.Id);
            await JoinAsync(due.TeamsUrl!, due.Subject, due.End, _cfg.Meetings.TakeNotes);
        }
        catch (Exception ex)
        {
            ActivityLog.Write("MEETING", "Tick error: " + ex.Message);
        }
        finally { _tickGate.Release(); }
    }

    async Task ProcessRecordingAsync(ActiveMeeting m, string? othersWav, string? meWav)
    {
        try
        {
            _notifier.Notify("Thokolosi", $"Transcribing '{m.Subject}'... this can take a few minutes.");
            var progress = new Progress<string>(s => ActivityLog.Write("MEETING", s));
            var lines = await Transcriber.TranscribeAsync(othersWav, meWav, _cfg.Meetings.WhisperModel, progress, CancellationToken.None);
            var transcript = Transcriber.Format(lines);

            string notes;
            if (string.IsNullOrWhiteSpace(transcript))
                notes = "_No speech was captured. Check that meeting audio played through this PC's default speakers/headset._";
            else if (string.IsNullOrEmpty(_cfg.ApiKey))
                notes = "_No Claude API key set, so no summary. Transcript below._";
            else
                notes = await SummariseAsync(m, transcript);

            Directory.CreateDirectory(AppConfig.MeetingsDir);
            var safe = Regex.Replace(m.Subject, @"[^\w\- ]", "").Trim();
            var path = Path.Combine(AppConfig.MeetingsDir, $"{m.Started:yyyy-MM-dd HHmm} {safe}.md");
            var md = new StringBuilder()
                .AppendLine($"# {m.Subject}")
                .AppendLine($"_{m.Started:dddd d MMMM yyyy, HH:mm} - {DateTime.Now:HH:mm}_")
                .AppendLine()
                .AppendLine(notes.Trim())
                .AppendLine()
                .AppendLine("---")
                .AppendLine("## Transcript")
                .AppendLine("_Automatic local transcript (Whisper). \"Me\" = this PC's microphone, \"Others\" = meeting audio._")
                .AppendLine()
                .AppendLine(transcript);
            await File.WriteAllTextAsync(path, md.ToString());
            ActivityLog.Write("MEETING", "Notes written: " + path);

            // Audio is large; keep only the notes.
            foreach (var f in new[] { othersWav, meWav }) try { if (f != null) File.Delete(f); } catch { }

            _notifier.Notify("Meeting notes ready", $"{m.Subject} - click to open.", () =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }));

            if (!string.IsNullOrWhiteSpace(_cfg.Meetings.AfterMeetingPrompt))
                await _notifier.RunBackgroundTaskAsync("After-meeting task",
                    $"{_cfg.Meetings.AfterMeetingPrompt}\n\nThe notes for the meeting '{m.Subject}' are in this file: {path}");
        }
        catch (Exception ex)
        {
            ActivityLog.Write("MEETING", "Notes failed: " + ex);
            _notifier.Notify("Meeting notes failed", ex.Message + (othersWav != null ? $"\nThe audio is still at {Path.GetDirectoryName(othersWav)}" : ""));
        }
    }

    async Task<string> SummariseAsync(ActiveMeeting m, string transcript)
    {
        var system = """
            You turn raw meeting transcripts into clear notes for the user. "Me" is the user; "Others" is everyone else
            (individual names may be mentioned in the conversation - use them when it's clear who said what).
            The transcript is automatic and may contain recognition errors; infer sensibly but never invent facts.
            Write markdown with these sections:
            ## Summary  (3-7 bullets)
            ## Decisions
            ## Action items  (checklist: "- [ ] Owner - task - due date if mentioned"; mark the user's own items clearly)
            ## Open questions / follow-ups
            Omit a section if there is nothing for it. Be concise.
            """;
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = $"Meeting: {m.Subject}\nDate: {m.Started:yyyy-MM-dd HH:mm}\n\nTranscript:\n{Json.Truncate(transcript, 400_000)}" }
        };
        return await _claude.CompleteAsync(_cfg.ApiKey!, _cfg.Model, system, content, 4000, CancellationToken.None);
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (IsRecording) StopNotes();
    }
}
