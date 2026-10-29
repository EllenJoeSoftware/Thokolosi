using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Thokolosi.Core.Voice;

/// <summary>
/// Records one spoken request from the default microphone and stops by itself when you stop talking
/// (simple energy-based voice activity detection).
/// </summary>
public static class VoiceRecorder
{
    /// <param name="threshold">RMS level counted as speech (0.005 = very sensitive, 0.04 = only loud speech).</param>
    /// <param name="level">Optional live mic level 0..1 for a UI meter.</param>
    /// <returns>Path to a WAV file, or null if no speech was heard.</returns>
    public static Task<string?> RecordAsync(double threshold, CancellationToken ct, Action<float>? level = null,
        double silenceSeconds = 1.3, double maxSeconds = 60, double waitForSpeechSeconds = 8) =>
        // Run on a pool thread so NAudio doesn't tie its events to the UI thread.
        Task.Run(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "thokolosi", $"voice-{Guid.NewGuid():N}.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var capture = new WasapiCapture { ShareMode = AudioClientShareMode.Shared };
            var fmt = capture.WaveFormat;
            var writer = new WaveFileWriter(path, fmt);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = DateTime.UtcNow;
            var lastVoice = DateTime.UtcNow;
            bool heard = false, stopping = false;
            object gate = new();

            capture.DataAvailable += (_, e) =>
            {
                lock (gate)
                {
                    if (stopping) return;
                    writer.Write(e.Buffer, 0, e.BytesRecorded);
                }
                var rms = Rms(e.Buffer, e.BytesRecorded, fmt);
                level?.Invoke((float)Math.Min(1.0, rms * 8));
                var now = DateTime.UtcNow;
                if (rms >= threshold) { heard = true; lastVoice = now; }

                bool finished = heard
                    ? (now - lastVoice).TotalSeconds >= silenceSeconds
                    : (now - start).TotalSeconds >= waitForSpeechSeconds;
                if (finished || (now - start).TotalSeconds >= maxSeconds)
                {
                    lock (gate) stopping = true;
                    capture.StopRecording();
                }
            };
            capture.RecordingStopped += (_, _) => done.TrySetResult();

            capture.StartRecording();
            using (ct.Register(() => { try { capture.StopRecording(); } catch { } }))
                await done.Task;

            lock (gate) { stopping = true; writer.Dispose(); }
            if (!heard)
            {
                try { File.Delete(path); } catch { }
                return null;
            }
            return path;
        });

    static double Rms(byte[] buf, int count, WaveFormat fmt)
    {
        double sum = 0;
        int n = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32))
        {
            for (int i = 0; i + 4 <= count; i += 4) { double s = BitConverter.ToSingle(buf, i); sum += s * s; n++; }
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= count; i += 2) { double s = BitConverter.ToInt16(buf, i) / 32768.0; sum += s * s; n++; }
        }
        else if (fmt.BitsPerSample == 24)
        {
            for (int i = 0; i + 3 <= count; i += 3) { int v = (buf[i] << 8 | buf[i + 1] << 16 | buf[i + 2] << 24) >> 8; double s = v / 8388608.0; sum += s * s; n++; }
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}
