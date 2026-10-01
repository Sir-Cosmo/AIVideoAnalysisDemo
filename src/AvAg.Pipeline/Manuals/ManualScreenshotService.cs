using System.Globalization;
using System.Text;
using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Manuals;

/// <summary>
/// Picks and extracts the screenshot of each manual step with FFmpeg (CPU only). The planned moments come from
/// <see cref="ManualBuilder.PlaceScreenshots"/>; this class refines them by looking at the video and then renders the
/// JPEGs, drawing the click marker where a click was observed.
/// </summary>
public sealed class ManualScreenshotService
{
    private readonly FfmpegService _ff;
    public ManualScreenshotService(FfmpegService ff) => _ff = ff;

    public int ScreenshotWidth { get; init; } = 1280;
    /// <summary>Mean absolute difference (0–255) below which two thumbnails count as "the same screen".</summary>
    public double SameScreenThreshold { get; init; } = 2.5;

    /// <summary>
    /// For steps without an observed click, looks at small thumbnails of the step's own part of the video (2 fps, 160 px)
    /// and picks the frame that is settled (barely changes in the next half second, so menus have finished opening) and
    /// differs most from the previous step's screenshot. If every candidate looks like the previous screenshot, the step
    /// gets no picture instead of a repeated one.
    /// </summary>
    public async Task<string> RefineTimesAsync(Manual m, string videoPath, CancellationToken ct)
    {
        GrayFrame? prev = null;
        int moved = 0, dropped = 0;
        foreach (var s in m.Steps)
        {
            if (s.PointXyPx is not null && s.ScreenshotS is { } fixedT)
            {
                prev = (await _ff.DecodeGrayFramesAsync(videoPath, 2, fixedT, fixedT + 0.5, scaleWidth: 160, ct: ct)).FirstOrDefault() ?? prev;
                continue;
            }
            double from = s.TimeS, to = Math.Min(Math.Max(s.EndS, s.TimeS + 1.0) + 1.0, s.TimeS + 15.0);
            var frames = await _ff.DecodeGrayFramesAsync(videoPath, 2, from, to, scaleWidth: 160, ct: ct);
            if (frames.Count == 0) continue;

            double bestScore = double.NegativeInfinity, bestNovelty = 0;
            GrayFrame? best = null;
            for (int i = 0; i < frames.Count; i++)
            {
                double unsettled = i + 1 < frames.Count ? Diff(frames[i], frames[i + 1]) : 0;
                double novelty = prev is null ? 255 : Diff(frames[i], prev);
                // Prefer frames near the spoken instruction when everything else is equal.
                double nearSpoken = s.ScreenshotS is { } t0 ? -0.05 * Math.Abs(frames[i].PtsS - t0) : 0;
                double score = Math.Min(novelty, 40) - 2.0 * unsettled + nearSpoken;
                if (score > bestScore) { bestScore = score; best = frames[i]; bestNovelty = novelty; }
            }
            if (best is null) continue;
            if (prev is not null && bestNovelty < SameScreenThreshold) { s.ScreenshotS = null; dropped++; continue; }
            if (s.ScreenshotS is not { } old || Math.Abs(old - best.PtsS) > 0.25) moved++;
            s.ScreenshotS = best.PtsS;
            prev = best;
        }
        return $"screenshots – {moved} moved to a settled, distinct frame, {dropped} skipped (same screen as the step before)";
    }

    /// <summary>Extracts one JPEG per step that has a screenshot moment; returns a message per failed frame.</summary>
    public async Task<List<string>> ExtractAsync(Manual m, string videoPath, VideoInfo v, CancellationToken ct)
    {
        var problems = new List<string>();
        int w = Math.Min(ScreenshotWidth, v.WidthPx > 0 ? v.WidthPx : ScreenshotWidth);
        int h = v.WidthPx > 0 ? (int)Math.Round(v.HeightPx * (double)w / v.WidthPx / 2) * 2 : 0;
        foreach (var s in m.Steps)
        {
            if (s.ScreenshotS is not { } t) continue;
            t = Math.Clamp(t, 0, Math.Max(0, v.DurationS - 0.1));
            var tmp = Path.Combine(Path.GetTempPath(), $"avag_manual_{Guid.NewGuid():N}.jpg");
            try
            {
                var (_, err, code) = await FfmpegService.RunAsync(_ff.FfmpegPath,
                    ["-y", "-loglevel", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", Filter(s, v, w), "-q:v", "4", tmp], null, ct);
                if (code != 0 || !File.Exists(tmp)) { problems.Add($"screenshot at {t:0.0}s failed: {err.Trim()}"); continue; }
                s.ScreenshotJpeg = await File.ReadAllBytesAsync(tmp, ct);
                s.ScreenshotS = t; s.ScreenshotWidth = w; s.ScreenshotHeight = h;
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
        return problems;
    }

    /// <summary>FFmpeg filter: red boxes around the observed click (and its UI element), then scale to the output width.</summary>
    private static string Filter(ManualStep s, VideoInfo v, int width)
    {
        var vf = new StringBuilder();
        int stroke = Math.Max(3, v.WidthPx / 400);
        if (s.BboxXyxyPx is { Length: 4 } b)
            vf.Append(CultureInfo.InvariantCulture, $"drawbox=x={b[0] - 4}:y={b[1] - 4}:w={b[2] - b[0] + 8}:h={b[3] - b[1] + 8}:color=0xE8336B@0.95:t={stroke},");
        if (s.PointXyPx is { Length: 2 } p)
        {
            int r = Math.Max(18, v.WidthPx / 70);
            vf.Append(CultureInfo.InvariantCulture, $"drawbox=x={p[0] - r}:y={p[1] - r}:w={2 * r}:h={2 * r}:color=0xE8336B@0.95:t={stroke},");
        }
        vf.Append(CultureInfo.InvariantCulture, $"scale={width}:-2");
        return vf.ToString();
    }

    private static double Diff(GrayFrame a, GrayFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i++) sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return (double)sum / a.Pixels.Length;
    }
}
