using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Thokolosi.Core.Meetings;

/// <summary>
/// Records what you hear (WASAPI loopback = the other people in the meeting) and, optionally, your microphone,
/// into two time-aligned WAV files so the transcript can label "Me" vs "Others".
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    Track? _speaker, _mic;
    public string? SpeakerPath => _speaker?.Path;
    public string? MicPath => _mic?.Path;
    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }

    public void Start(string basePath, bool includeMic)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        StartedAt = DateTime.Now;
        _speaker = new Track(new WasapiLoopbackCapture(), basePath + ".others.wav", fillSilence: true);
        if (includeMic)
        {
            try { _mic = new Track(new WasapiCapture(), basePath + ".me.wav", fillSilence: false); }
            catch (Exception ex) { ActivityLog.Write("MEETING", "No microphone capture: " + ex.Message); }
        }
        _speaker.Start();
        IsRecording = true;
        if (_mic != null)
        {
            try { _mic.Start(); }
            catch (Exception ex)
            {
                // e.g. microphone blocked in Windows privacy settings: keep recording the meeting audio only.
                ActivityLog.Write("MEETING", "Microphone recording failed: " + ex.Message);
                try { _mic.Stop(); } catch { }
                try { File.Delete(_mic.Path); } catch { }
                _mic = null;
            }
        }
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        try { _speaker?.Stop(); } catch (Exception ex) { ActivityLog.Write("MEETING", "Stop error: " + ex.Message); }
        try { _mic?.Stop(); } catch (Exception ex) { ActivityLog.Write("MEETING", "Stop error: " + ex.Message); }
    }

    public void Dispose() => Stop();

    sealed class Track
    {
        readonly IWaveIn _capture;
        readonly WaveFileWriter _writer;
        readonly bool _fillSilence;
        readonly Stopwatch _clock = new();
        readonly object _gate = new();
        readonly System.Threading.Timer? _gapTimer;
        long _written;
        bool _stopped;
        readonly TaskCompletionSource _done = new();
        public string Path { get; }

        public Track(IWaveIn capture, string path, bool fillSilence)
        {
            _capture = capture;
            Path = path;
            _fillSilence = fillSilence;
            _writer = new WaveFileWriter(path, capture.WaveFormat);
            _capture.DataAvailable += (_, e) =>
            {
                lock (_gate)
                {
                    if (_stopped) return;
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _written += e.BytesRecorded;
                }
            };
            _capture.RecordingStopped += (_, _) => _done.TrySetResult();
            // Loopback capture delivers nothing while the PC is silent; pad with silence to keep the timeline real.
            if (fillSilence) _gapTimer = new System.Threading.Timer(_ => PadSilence(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _clock.Start();
            _capture.StartRecording();
            _gapTimer?.Change(500, 500);
        }

        void PadSilence()
        {
            lock (_gate)
            {
                if (_stopped || !_fillSilence) return;
                var fmt = _writer.WaveFormat;
                long expected = (long)(_clock.Elapsed.TotalSeconds * fmt.AverageBytesPerSecond);
                long gap = expected - _written;
                // Only pad real gaps (> 300 ms), aligned to whole frames.
                if (gap < fmt.AverageBytesPerSecond * 3 / 10) return;
                gap -= gap % fmt.BlockAlign;
                var zeros = new byte[Math.Min(gap, fmt.AverageBytesPerSecond)];
                while (gap > 0)
                {
                    int n = (int)Math.Min(gap, zeros.Length);
                    _writer.Write(zeros, 0, n);
                    _written += n;
                    gap -= n;
                }
            }
        }

        public void Stop()
        {
            lock (_gate) { if (_stopped) return; }
            _gapTimer?.Dispose();
            try { _capture.StopRecording(); _done.Task.Wait(3000); } catch { }
            lock (_gate)
            {
                _stopped = true;
                _writer.Dispose();
            }
            _capture.Dispose();
        }
    }
}
