using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace Thokolosi.Core.Voice;

/// <summary>Reads replies aloud with the Windows voices installed on this PC (offline).</summary>
public sealed class Speaker : IDisposable
{
    readonly SpeechSynthesizer _synth = new();
    int _pending;
    TaskCompletionSource _idle = CompletedTcs();

    public event Action<bool>? SpeakingChanged;

    public Speaker()
    {
        try { _synth.SetOutputToDefaultAudioDevice(); } catch (Exception ex) { ActivityLog.Write("VOICE", "No audio output: " + ex.Message); }
        _synth.SpeakCompleted += (_, _) =>
        {
            if (Interlocked.Decrement(ref _pending) <= 0)
            {
                Interlocked.Exchange(ref _pending, 0);
                _idle.TrySetResult();
                SpeakingChanged?.Invoke(false);
            }
        };
    }

    public bool IsSpeaking => Volatile.Read(ref _pending) > 0;

    public static List<string> Voices()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return new List<string>(); }
    }

    public void Configure(string voiceName, int rate)
    {
        try { if (!string.IsNullOrWhiteSpace(voiceName)) _synth.SelectVoice(voiceName); } catch { /* voice not installed */ }
        _synth.Rate = Math.Clamp(rate, -10, 10);
    }

    public void Speak(string text)
    {
        var clean = Clean(text);
        if (clean.Length == 0) return;
        if (Interlocked.Increment(ref _pending) == 1)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SpeakingChanged?.Invoke(true);
        }
        try { _synth.SpeakAsync(clean); }
        catch (Exception ex)
        {
            ActivityLog.Write("VOICE", "Speak failed: " + ex.Message);
            if (Interlocked.Decrement(ref _pending) <= 0) { Interlocked.Exchange(ref _pending, 0); _idle.TrySetResult(); SpeakingChanged?.Invoke(false); }
        }
    }

    /// <summary>Stop talking immediately (e.g. when the user starts speaking).</summary>
    public void Stop()
    {
        try { _synth.SpeakAsyncCancelAll(); } catch { }
    }

    public Task WaitUntilDoneAsync() => IsSpeaking ? _idle.Task : Task.CompletedTask;

    /// <summary>Turn markdown/chat text into something pleasant to hear.</summary>
    public static string Clean(string text)
    {
        var s = Regex.Replace(text, "```.*?```", " (I've put the code in the chat.) ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");              // [label](url) -> label
        s = Regex.Replace(s, @"https?://\S+", "the link in the chat");
        s = Regex.Replace(s, @"[*_`#>|~]+", " ");
        s = Regex.Replace(s, @"^\s*[-+]\s+", "", RegexOptions.Multiline);   // bullets
        s = Regex.Replace(s, @"[A-Za-z]:\\[^\s]+", "the file path in the chat");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        // Long answers: read the start, the rest is on screen.
        if (s.Length > 900)
        {
            var cut = s.LastIndexOf('.', 900);
            s = s[..(cut > 300 ? cut + 1 : 900)] + " The rest is in the chat.";
        }
        return s;
    }

    static TaskCompletionSource CompletedTcs()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Dispose()
    {
        Stop();
        _synth.Dispose();
    }
}
