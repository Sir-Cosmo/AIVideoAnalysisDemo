using System.Globalization;
using System.Text;
using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Articles;

/// <summary>
/// Picks and extracts the screenshot of each solution step with FFmpeg (CPU only). The planned moments come from
/// <see cref="ArticleBuilder.PlaceScreenshots"/>; this class refines them by looking at the video and then renders the
/// JPEGs, drawing the click marker where a click was observed.
/// </summary>
public sealed class ArticleScreenshotService
{
    private readonly FfmpegService _ff;
    public ArticleScreenshotService(FfmpegService ff) => _ff = ff;
    /// <summary>ffmpeg processes run at the same time (each one seeks and decodes on its own).</summary>
    private static int Parallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    public int ScreenshotWidth { get; init; } = 1280;
    /// <summary>Mean absolute difference (0–255) below which two thumbnails count as "the same screen".</summary>
    public double SameScreenThreshold { get; init; } = 2.5;

    /// <summary>
    /// For steps without an observed click, looks at small thumbnails of the step's own part of the video (2 fps, 160 px)
    /// and picks the frame that is settled (barely changes in the next half second, so menus have finished opening) and
    /// differs most from the previous step's screenshot. If every candidate looks like the previous screenshot, the step
    /// gets no picture instead of a repeated one.
    /// </summary>
    public async Task<string> RefineTimesAsync(WikiArticle m, string videoPath, CancellationToken ct)
    {
        // The choice per step depends on the step before, but the frames do not: decode every step's span in parallel first.
        var spans = new List<GrayFrame>[m.Steps.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, m.Steps.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (i, token) =>
        {
            var s = m.Steps[i];
            spans[i] = s.PointXyPx is not null && s.ScreenshotS is { } fixedT
                ? await _ff.DecodeGrayFramesAsync(videoPath, 2, fixedT, fixedT + 0.5, scaleWidth: 160, ct: token)
                : await _ff.DecodeGrayFramesAsync(videoPath, 2, s.TimeS, Math.Min(Math.Max(s.EndS, s.TimeS + 1.0) + 1.0, s.TimeS + 15.0), scaleWidth: 160, ct: token);
        });

        GrayFrame? prev = null;
        int moved = 0, dropped = 0;
        for (int k = 0; k < m.Steps.Count; k++)
        {
            var s = m.Steps[k];
            var frames = spans[k];
            if (s.PointXyPx is not null && s.ScreenshotS is not null)
            {
                prev = frames.FirstOrDefault() ?? prev;
                continue;
            }
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

    /// <summary>
    /// Up to <paramref name="max"/> frames that show what happened on the supporter's screen, for a vision model:
    /// the video is scanned at 1 fps as thumbnails; a frame qualifies when the screen has just changed and then
    /// settled, or when a click was observed; near-duplicates are dropped; the biggest changes win when there are too
    /// many. Returned in time order as 1280-px JPEGs labelled "Bild n (mm:ss)". <paramref name="thumbnails"/> (1 fps,
    /// 160 px, from the analysis) save decoding the whole video again.
    /// </summary>
    public async Task<IReadOnlyList<PromptImage>> KeyframesAsync(string videoPath, EventGraph graph, CancellationToken ct, int max = 12,
                                                                IReadOnlyList<GrayFrame>? thumbnails = null)
    {
        var thumbs = thumbnails is { Count: > 0 } ? thumbnails : await _ff.DecodeGrayFramesAsync(videoPath, 1, scaleWidth: 160, ct: ct);
        if (thumbs.Count == 0) return [];
        var clicks = graph.Events.Where(e => e.GroundingStatus is GroundingStatus.Observed or GroundingStatus.Tracked).Select(e => e.Temporal.PeakS).ToList();

        // Score each second: how much it differs from the frame before, settled (little change to the next one), clicks first.
        var candidates = new List<(GrayFrame Frame, double Score)> { (thumbs[0], 50) };
        for (int i = 1; i < thumbs.Count; i++)
        {
            double change = Diff(thumbs[i], thumbs[i - 1]);
            double unsettled = i + 1 < thumbs.Count ? Diff(thumbs[i], thumbs[i + 1]) : 0;
            bool click = clicks.Any(c => Math.Abs(c - thumbs[i].PtsS) <= 0.75);
            if (change >= SameScreenThreshold && unsettled < SameScreenThreshold * 2 || click)
                candidates.Add((thumbs[i], change + (click ? 100 : 0)));
        }
        var chosen = new List<GrayFrame>();
        foreach (var (frame, _) in candidates.OrderByDescending(c => c.Score))
        {
            if (chosen.Count >= max) break;
            if (chosen.All(c => Diff(c, frame) >= SameScreenThreshold && Math.Abs(c.PtsS - frame.PtsS) >= 2)) chosen.Add(frame);
        }

        var ordered = chosen.OrderBy(f => f.PtsS).ToList();
        var jpegs = new byte[]?[ordered.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, ordered.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (i, token) =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"avag_frame_{Guid.NewGuid():N}.jpg");
            try
            {
                var (_, _, code) = await FfmpegService.RunAsync(_ff.FfmpegPath,
                    ["-y", "-loglevel", "error", "-ss", ordered[i].PtsS.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", $"scale='min({ScreenshotWidth},iw)':-2", "-q:v", "5", tmp], null, token);
                if (code == 0 && File.Exists(tmp)) jpegs[i] = await File.ReadAllBytesAsync(tmp, token);
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        });
        var images = new List<PromptImage>();
        for (int i = 0; i < ordered.Count; i++)
            if (jpegs[i] is { } jpeg) images.Add(new PromptImage($"Bild {images.Count + 1} ({ArticleRenderer.Ts(ordered[i].PtsS)})", jpeg));
        return images;
    }

    /// <summary>Extracts one JPEG per step that has a screenshot moment; returns a message per failed frame.</summary>
    public async Task<List<string>> ExtractAsync(WikiArticle m, string videoPath, VideoInfo v, CancellationToken ct)
    {
        int w = Math.Min(ScreenshotWidth, v.WidthPx > 0 ? v.WidthPx : ScreenshotWidth);
        int h = v.WidthPx > 0 ? (int)Math.Round(v.HeightPx * (double)w / v.WidthPx / 2) * 2 : 0;
        var problems = new string?[m.Steps.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, m.Steps.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (i, token) =>
        {
            var s = m.Steps[i];
            if (s.ScreenshotS is not { } t) return;
            t = Math.Clamp(t, 0, Math.Max(0, v.DurationS - 0.1));
            var tmp = Path.Combine(Path.GetTempPath(), $"avag_article_{Guid.NewGuid():N}.jpg");
            try
            {
                var (_, err, code) = await FfmpegService.RunAsync(_ff.FfmpegPath,
                    ["-y", "-loglevel", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", Filter(s, v, w), "-q:v", "4", tmp], null, token);
                if (code != 0 || !File.Exists(tmp)) { problems[i] = $"screenshot at {t:0.0}s failed: {err.Trim()}"; return; }
                s.ScreenshotJpeg = await File.ReadAllBytesAsync(tmp, token);
                s.ScreenshotS = t; s.ScreenshotWidth = w; s.ScreenshotHeight = h;
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        });
        return problems.OfType<string>().ToList();
    }

    /// <summary>FFmpeg filter: red boxes around the observed click (and its UI element), then scale to the output width.</summary>
    private static string Filter(ArticleStep s, VideoInfo v, int width)
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
