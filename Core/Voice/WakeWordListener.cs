using System.IO;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Thokolosi.Core.Meetings;

namespace Thokolosi.Core.Voice;

/// <summary>
/// Always-on wake phrase detection ("Hey Thokolosi"). It watches the mic level and, only when there's a short
/// burst of speech, transcribes that snippet on this PC with Whisper and checks for the assistant's name.
/// Whisper copes with an unusual word like "Thokolosi" far better than the built-in Windows recognizer.
/// You can also say the request in one go: "Hey Thokolosi, what's on my calendar?".
/// Nothing is recorded or kept unless the wake word is heard.
/// </summary>
public sealed class WakeWordListener : IDisposable
{
    WasapiCapture? _capture;
    volatile bool _paused;
    int _busy;                       // a snippet is being transcribed
    DateTime _lastFired = DateTime.MinValue;
    string[] _targets = Array.Empty<string>();
    double _threshold;
    string _model = "base", _language = "en";

    // segmenting state (capture thread only)
    readonly List<byte[]> _preroll = new();
    MemoryStream? _segment;
    DateTime _segStart, _lastVoice;
    WaveFormat? _fmt;

    /// <summary>Fired with the rest of the sentence after the wake word (null if you only said the wake word).</summary>
    public event Action<string?>? Heard;

    /// <summary>Return true to ignore speech right now (e.g. while Thokolosi is talking).</summary>
    public Func<bool>? IsBlocked { get; set; }

    public bool IsRunning => _capture != null;

    /// <returns>null on success, otherwise why it couldn't start.</returns>
    public string? Start(string phrase, double threshold, string whisperModel, string language)
    {
        Stop();
        _threshold = Math.Max(0.004, threshold);
        _model = whisperModel;
        _language = language;
        var name = Regex.Replace(phrase.ToLowerInvariant(), @"\b(hey|hi|ok|okay|yo|hello)\b", " ").Trim();
        _targets = new[] { name, "thokolosi", "tokolosi", "tokoloshe", "tikoloshe", "thokoloshe", "tokolosh", "thokolosh", "tikolosi" }
            .Select(Normalize).Where(t => t.Length >= 4).Distinct().ToArray();
        try
        {
            // Create on a pool thread so NAudio doesn't bind to the UI thread.
            Task.Run(() =>
            {
                var cap = new WasapiCapture { ShareMode = AudioClientShareMode.Shared };
                _fmt = cap.WaveFormat;
                cap.DataAvailable += OnData;
                cap.StartRecording();
                _capture = cap;
            }).GetAwaiter().GetResult();
            ActivityLog.Write("VOICE", $"Wake word listening for '{phrase}'");
            return null;
        }
        catch (Exception ex)
        {
            Stop();
            return "Wake word couldn't use the microphone: " + ex.Message;
        }
    }

    // Only flags here: the segment buffers belong to the capture thread, which clears them when it sees the flag.
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    void ResetSegment()
    {
        _segment?.Dispose();
        _segment = null;
        _preroll.Clear();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (_fmt == null) return;
        if (_paused || IsBlocked?.Invoke() == true) { if (_segment != null || _preroll.Count > 0) ResetSegment(); return; }

        var chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
        var rms = Rms(chunk, _fmt);
        var now = DateTime.UtcNow;

        if (_segment == null)
        {
            // keep ~0.4 s before speech starts so the first syllable isn't clipped
            _preroll.Add(chunk);
            long keep = _fmt.AverageBytesPerSecond * 4 / 10, total = _preroll.Sum(c => (long)c.Length);
            while (_preroll.Count > 1 && total - _preroll[0].Length > keep) { total -= _preroll[0].Length; _preroll.RemoveAt(0); }
            if (rms < _threshold) return;
            _segment = new MemoryStream();
            foreach (var c in _preroll) _segment.Write(c);
            _preroll.Clear();
            _segStart = _lastVoice = now;
            return;
        }

        _segment.Write(chunk);
        if (rms >= _threshold) _lastVoice = now;
        bool ended = (now - _lastVoice).TotalSeconds > 0.8;
        bool tooLong = (now - _segStart).TotalSeconds > 9;
        if (!ended && !tooLong) return;

        var seconds = (double)_segment.Length / _fmt.AverageBytesPerSecond;
        var data = _segment.ToArray();
        ResetSegment();
        // Wake phrase + short request fits in ~9 s; skip long monologues and tiny clicks.
        if (seconds < 0.5 || tooLong) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // still checking the previous snippet
        var fmt = _fmt;
        _ = Task.Run(() => CheckAsync(data, fmt));
    }

    async Task CheckAsync(byte[] pcm, WaveFormat fmt)
    {
        var wav = Path.Combine(Path.GetTempPath(), "thokolosi", $"wake-{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            using (var w = new WaveFileWriter(wav, fmt)) w.Write(pcm, 0, pcm.Length);
            var text = await Transcriber.TranscribeSpeechAsync(wav, _model, _language, null, CancellationToken.None);
            if (_paused || string.IsNullOrWhiteSpace(text)) return;

            var (found, rest) = Match(text);
            if (!found || (DateTime.Now - _lastFired).TotalSeconds < 3) return;
            _lastFired = DateTime.Now;
            ActivityLog.Write("VOICE", $"Wake word heard: \"{text}\"");
            Heard?.Invoke(string.IsNullOrWhiteSpace(rest) ? null : rest);
        }
        catch (Exception ex) { ActivityLog.Write("VOICE", "Wake check failed: " + ex.Message); }
        finally
        {
            try { File.Delete(wav); } catch { }
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Find the assistant's name in the snippet (fuzzy, since speech-to-text spells it many ways).</summary>
    (bool Found, string? Remainder) Match(string text)
    {
        var words = Regex.Matches(text, @"[\p{L}']+").Select(m => m.Value).ToList();
        for (int i = 0; i < words.Count; i++)
        {
            // single word, or two words run together ("toko loshi")
            foreach (var (candidate, used) in new[] { (words[i], 1), (i + 1 < words.Count ? words[i] + words[i + 1] : "", 2) })
            {
                var c = Normalize(candidate);
                if (c.Length < 4) continue;
                if (_targets.Any(t => Similar(c, t)))
                {
                    var after = string.Join(" ", words.Skip(i + used));
                    // keep the original punctuation-free tail as the request
                    return (true, after.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 ? after : null);
                }
            }
        }
        return (false, null);
    }

    static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z]", "").Replace("h", "");

    static bool Similar(string a, string b)
    {
        if (a == b) return true;
        int d = Levenshtein(a, b);
        return d <= Math.Max(1, (int)Math.Round(b.Length * 0.3));
    }

    static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }

    static double Rms(byte[] buf, WaveFormat fmt)
    {
        double sum = 0; int n = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
            for (int i = 0; i + 4 <= buf.Length; i += 4) { double s = BitConverter.ToSingle(buf, i); sum += s * s; n++; }
        else if (fmt.BitsPerSample == 16)
            for (int i = 0; i + 2 <= buf.Length; i += 2) { double s = BitConverter.ToInt16(buf, i) / 32768.0; sum += s * s; n++; }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }

    public void Dispose() => Stop();
}
