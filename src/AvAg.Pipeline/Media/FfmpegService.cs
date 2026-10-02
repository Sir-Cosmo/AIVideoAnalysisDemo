using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvAg.Core;

namespace AvAg.Pipeline.Media;

/// <summary>One decoded grayscale frame with its real presentation timestamp (not frame_index / nominal_fps).</summary>
public sealed class GrayFrame
{
    public required int Index { get; init; }
    public required double PtsS { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; } // row-major, Width*Height
    public byte this[int x, int y] => Pixels[y * Width + x];
}

public sealed class FfmpegService
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";

    private static readonly Regex PtsRx = new(@"pts_time:(?<t>[0-9.\-e]+)", RegexOptions.Compiled);
    // Probing is repeated for every decode of the same file: cache by path, size and modification time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), VideoInfo> _probes = new();

    public async Task<VideoInfo> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        var fi = new FileInfo(videoPath);
        var key = (fi.FullName, fi.Exists ? fi.Length : -1, fi.Exists ? fi.LastWriteTimeUtc : default);
        if (_probes.TryGetValue(key, out var cached)) return cached;
        var info = await ProbeUncachedAsync(videoPath, ct);
        if (fi.Exists) _probes[key] = info;
        return info;
    }

    private async Task<VideoInfo> ProbeUncachedAsync(string videoPath, CancellationToken ct)
    {
        var (stdout, _, code) = await RunAsync(FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", videoPath], null, ct);
        if (code != 0) throw new InvalidOperationException($"ffprobe failed for {videoPath}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var video = root.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
        int w = video.GetProperty("width").GetInt32();
        int h = video.GetProperty("height").GetInt32();
        double fps = ParseRate(video.GetProperty("r_frame_rate").GetString() ?? "0/1");
        double dur = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
        return new VideoInfo(Path.GetFileNameWithoutExtension(videoPath), dur, w, h, fps);
    }

    public async Task<bool> HasAudioAsync(string videoPath, CancellationToken ct = default)
    {
        var (stdout, _, _) = await RunAsync(FfprobePath, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_type", "-of", "csv=p=0", videoPath], null, ct);
        return stdout.Contains("audio");
    }

    /// <summary>PCM 16-bit, mono, 16 kHz WAV for ASR, preserving the media time origin.</summary>
    public async Task<string> ExtractAudioAsync(string videoPath, string outWavPath, CancellationToken ct = default)
    {
        var (_, stderr, code) = await RunAsync(FfmpegPath,
            ["-y", "-loglevel", "error", "-i", videoPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outWavPath], null, ct);
        if (code != 0) throw new InvalidOperationException($"ffmpeg audio extraction failed: {stderr}");
        return outWavPath;
    }

    /// <summary>Compressed mono MP3 for upload to a cloud speech service (16 kHz, <paramref name="kbps"/> kbit/s:
    /// 32 kbit/s ≈ 14 MB per hour, well under the usual 25 MB request limit).</summary>
    public async Task<string> EncodeMp3Async(string audioPath, string outMp3Path, int kbps = 32, CancellationToken ct = default,
                                             double? startS = null, double? endS = null)
    {
        var args = new List<string> { "-y", "-loglevel", "error" };
        if (startS is { } s) args.AddRange(["-ss", F(s)]);
        if (endS is { } e) args.AddRange(["-to", F(e)]);
        args.AddRange(["-i", audioPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", $"{kbps}k", outMp3Path]);
        var (_, stderr, code) = await RunAsync(FfmpegPath, args, null, ct);
        if (code != 0) throw new InvalidOperationException($"ffmpeg mp3 encoding failed: {stderr}");
        return outMp3Path;
    }

    private static readonly Regex SilenceRx = new(@"silence_(?<k>start|end): (?<t>[0-9.]+)", RegexOptions.Compiled);

    /// <summary>Pauses in speech (quieter than <paramref name="noiseDb"/> for at least <paramref name="minS"/> seconds).</summary>
    public async Task<List<(double StartS, double EndS)>> DetectSilencesAsync(string audioPath, double noiseDb = -35, double minS = 0.5, CancellationToken ct = default)
    {
        var (_, stderr, _) = await RunAsync(FfmpegPath,
            ["-hide_banner", "-nostats", "-i", audioPath, "-af", $"silencedetect=noise={F(noiseDb)}dB:d={F(minS)}", "-f", "null", "-"], null, ct);
        var result = new List<(double, double)>();
        double? start = null;
        foreach (Match m in SilenceRx.Matches(stderr))
        {
            double t = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["k"].Value == "start") start = t;
            else if (start is { } s0) { result.Add((s0, t)); start = null; }
        }
        return result;
    }

    /// <summary>Single frame at a time as PNG (for sidecar UI parsing / OCR).</summary>
    public async Task<string> ExtractFramePngAsync(string videoPath, double timeS, string outPngPath, CancellationToken ct = default)
    {
        var (_, stderr, code) = await RunAsync(FfmpegPath,
            ["-y", "-loglevel", "error", "-ss", F(timeS), "-i", videoPath, "-frames:v", "1", outPngPath], null, ct);
        if (code != 0) throw new InvalidOperationException($"ffmpeg frame extraction failed: {stderr}");
        return outPngPath;
    }

    /// <summary>
    /// Decode grayscale frames at <paramref name="fps"/> between <paramref name="startS"/> and <paramref name="endS"/>
    /// (null = whole video), optionally scaled to <paramref name="scaleWidth"/>. Real PTS values are parsed from the
    /// showinfo filter so variable-frame-rate sources keep a correct time base. Holds every frame in memory – for long
    /// passes use <see cref="StreamGrayFramesAsync"/>.
    /// </summary>
    public async Task<List<GrayFrame>> DecodeGrayFramesAsync(string videoPath, double fps, double? startS = null, double? endS = null,
                                                             int? scaleWidth = null, CancellationToken ct = default)
    {
        var frames = new List<GrayFrame>();
        await StreamGrayFramesAsync(videoPath, fps, frames.Add, startS, endS, scaleWidth, ct);
        return frames;
    }

    /// <summary>
    /// Like <see cref="DecodeGrayFramesAsync"/>, but hands every frame to <paramref name="onFrame"/> as soon as it is
    /// decoded instead of keeping them all: memory stays constant however long the video is, and the analysis runs while
    /// ffmpeg decodes the next frames. Each frame has its own pixel buffer, so it may be kept.
    /// </summary>
    public async Task StreamGrayFramesAsync(string videoPath, double fps, Action<GrayFrame> onFrame, double? startS = null, double? endS = null,
                                            int? scaleWidth = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-loglevel", "info", "-nostats", "-hide_banner" };
        if (startS is { } s) args.AddRange(["-ss", F(s)]);
        if (endS is { } e) args.AddRange(["-to", F(e)]);
        // -copyts keeps absolute timestamps after -ss so pts_time stays on the media time base.
        if (startS is not null) args.Add("-copyts");
        args.AddRange(["-i", videoPath]);
        var vf = $"fps={F(fps)}";
        if (scaleWidth is { } sw) vf += $",scale={sw}:-2:flags=area";
        vf += ",showinfo";
        args.AddRange(["-vf", vf, "-an", "-f", "rawvideo", "-pix_fmt", "gray", "-"]);

        var info = await ProbeAsync(videoPath, ct);
        int w = info.WidthPx, h = info.HeightPx;
        if (scaleWidth is { } sw2) { h = (int)Math.Round((double)h * sw2 / w / 2) * 2; w = sw2; }

        var psi = new ProcessStartInfo(FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ } });

        // showinfo writes a frame's PTS to stderr before the frame reaches stdout, so frame i waits for PTS i.
        var pts = System.Threading.Channels.Channel.CreateUnbounded<double>();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync(ct)) is not null)
                {
                    var m = PtsRx.Match(line);
                    if (m.Success && double.TryParse(m.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        pts.Writer.TryWrite(t);
                }
            }
            finally { pts.Writer.TryComplete(); }
        }, ct);

        int frameBytes = w * h, idx = 0;
        var stdout = proc.StandardOutput.BaseStream;
        while (true)
        {
            var buf = new byte[frameBytes];
            int read = 0;
            while (read < frameBytes)
            {
                int n = await stdout.ReadAsync(buf.AsMemory(read, frameBytes - read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read < frameBytes) break;
            // Fall back to index/fps if the PTS could not be parsed.
            double t = await pts.Reader.WaitToReadAsync(ct) && pts.Reader.TryRead(out var p) ? p : (startS ?? 0) + idx / fps;
            onFrame(new GrayFrame { Index = idx++, PtsS = t, Width = w, Height = h, Pixels = buf });
        }
        await proc.WaitForExitAsync(ct);
        await stderrTask;
    }

    public static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(string exe, IEnumerable<string> args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (await so, await se, p.ExitCode);
    }

    private static double ParseRate(string r)
    {
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                              && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
