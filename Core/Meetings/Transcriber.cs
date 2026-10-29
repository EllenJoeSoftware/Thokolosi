using System.IO;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace Thokolosi.Core.Meetings;

public record TranscriptLine(TimeSpan Start, string Speaker, string Text);

/// <summary>
/// Local, free speech-to-text with Whisper (whisper.cpp). The model downloads once (~140 MB for "base")
/// into %APPDATA%\Thokolosi\models. Nothing leaves the PC during transcription.
/// </summary>
public static class Transcriber
{
    public static async Task<List<TranscriptLine>> TranscribeAsync(string? othersWav, string? meWav, string modelName, IProgress<string>? progress, CancellationToken ct)
    {
        var modelPath = await EnsureModelAsync(modelName, progress, ct);
        using var factory = WhisperFactory.FromPath(modelPath);
        var lines = new List<TranscriptLine>();
        foreach (var (wav, speaker) in new[] { (othersWav, "Others"), (meWav, "Me") })
        {
            if (wav == null || !File.Exists(wav) || new FileInfo(wav).Length < 50_000) continue;
            progress?.Report($"Transcribing {speaker.ToLowerInvariant()} audio...");
            var pcm = ConvertTo16kMono(wav);
            try
            {
                using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
                using var fs = File.OpenRead(pcm);
                await foreach (var seg in processor.ProcessAsync(fs, ct))
                {
                    var text = seg.Text.Trim();
                    if (text.Length == 0 || IsNoise(text)) continue;
                    lines.Add(new TranscriptLine(seg.Start, speaker, text));
                }
            }
            finally { try { File.Delete(pcm); } catch { } }
        }
        return lines.OrderBy(l => l.Start).ToList();
    }

    static WhisperFactory? _voiceFactory;
    static string? _voiceModelPath;
    static readonly SemaphoreSlim _voiceGate = new(1, 1);

    /// <summary>Quick transcription of a short voice command. Keeps the model loaded between calls for speed.</summary>
    public static async Task<string> TranscribeSpeechAsync(string wav, string modelName, string language, IProgress<string>? progress, CancellationToken ct)
    {
        var modelPath = await EnsureModelAsync(modelName, progress, ct);
        await _voiceGate.WaitAsync(ct);
        try
        {
            if (_voiceFactory == null || _voiceModelPath != modelPath)
            {
                _voiceFactory?.Dispose();
                _voiceFactory = WhisperFactory.FromPath(modelPath);
                _voiceModelPath = modelPath;
            }
            var pcm = ConvertTo16kMono(wav);
            try
            {
                using var processor = _voiceFactory.CreateBuilder()
                    .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                    .WithPrompt("Thokolosi") // helps Whisper spell the assistant's name
                    .Build();
                using var fs = File.OpenRead(pcm);
                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(fs, ct))
                {
                    var t = seg.Text.Trim();
                    if (t.Length > 0 && !IsNoise(t)) sb.Append(t).Append(' ');
                }
                return sb.ToString().Trim();
            }
            finally { try { File.Delete(pcm); } catch { } }
        }
        finally { _voiceGate.Release(); }
    }

    public static string Format(IEnumerable<TranscriptLine> lines)
    {
        var sb = new StringBuilder();
        string? last = null;
        foreach (var l in lines)
        {
            if (l.Speaker != last) { sb.Append($"\n[{l.Start:hh\\:mm\\:ss}] {l.Speaker}: "); last = l.Speaker; }
            sb.Append(l.Text).Append(' ');
        }
        return sb.ToString().Trim();
    }

    // Whisper hallucinates these on silence.
    static bool IsNoise(string t) =>
        t is "[BLANK_AUDIO]" or "(silence)" or "[Music]" or "[MUSIC]" or "(music)" or "[ Silence ]" || t.StartsWith("[") && t.EndsWith("]") && t.Length < 25;

    static string ConvertTo16kMono(string wav)
    {
        var outPath = Path.ChangeExtension(wav, ".16k.wav");
        using var reader = new AudioFileReader(wav);
        ISampleProvider sp = reader;
        if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new DownmixToMono(sp);
        if (sp.WaveFormat.SampleRate != 16000) sp = new WdlResamplingSampleProvider(sp, 16000);
        WaveFileWriter.CreateWaveFile16(outPath, sp);
        return outPath;
    }

    public static async Task<string> EnsureModelAsync(string modelName, IProgress<string>? progress, CancellationToken ct)
    {
        var type = modelName.ToLowerInvariant() switch
        {
            "tiny" => GgmlType.Tiny,
            "small" => GgmlType.Small,
            "medium" => GgmlType.Medium,
            "large" => GgmlType.LargeV3,
            _ => GgmlType.Base
        };
        Directory.CreateDirectory(AppConfig.ModelsDir);
        var path = Path.Combine(AppConfig.ModelsDir, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return path;

        progress?.Report($"Downloading the Whisper '{modelName}' speech model (one time)...");
        var tmp = path + ".download";
        await using (var model = await WhisperGgmlDownloader.GetGgmlModelAsync(type, QuantizationType.NoQuantization, ct))
        await using (var file = File.Create(tmp))
            await model.CopyToAsync(file, ct);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    sealed class DownmixToMono(ISampleProvider src) : ISampleProvider
    {
        readonly int _ch = src.WaveFormat.Channels;
        float[] _buf = Array.Empty<float>();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int need = count * _ch;
            if (_buf.Length < need) _buf = new float[need];
            int read = src.Read(_buf, 0, need);
            int frames = read / _ch;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _ch; c++) sum += _buf[f * _ch + c];
                buffer[offset + f] = sum / _ch;
            }
            return frames;
        }
    }
}
