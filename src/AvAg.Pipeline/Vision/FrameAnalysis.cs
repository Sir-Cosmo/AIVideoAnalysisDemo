using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Vision;

public sealed record StateChange(double TimeS, double Magnitude, double Confidence, BBox Region);

/// <summary>
/// Detects UI state changes by block-wise frame differencing. A change is a local maximum of the
/// mean absolute difference above <see cref="Threshold"/>; its region is the bounding box of the changed blocks.
/// Scene cuts (nearly the whole frame changes) are reported separately via <see cref="IsSceneCut"/>.
/// </summary>
public sealed class StateChangeDetector
{
    public int BlockSize { get; init; } = 8;
    public double BlockThreshold { get; init; } = 12.0;   // mean abs diff per block (0..255)
    public double Threshold { get; init; } = 0.0015;      // fraction of changed blocks to count as event
    public double SceneCutFraction { get; init; } = 0.6;

    public List<StateChange> Detect(IReadOnlyList<GrayFrame> frames)
    {
        var changes = new List<StateChange>();
        if (frames.Count < 2) return changes;
        var scores = new double[frames.Count];
        var regions = new BBox?[frames.Count];
        for (int i = 1; i < frames.Count; i++)
            (scores[i], regions[i]) = BlockDiff(frames[i - 1], frames[i]);

        for (int i = 1; i < frames.Count; i++)
        {
            if (scores[i] < Threshold) continue;
            bool localMax = (i == 1 || scores[i] >= scores[i - 1]) && (i == frames.Count - 1 || scores[i] > scores[i + 1]);
            if (!localMax) continue;
            double conf = Math.Clamp(scores[i] / (Threshold * 20), 0.3, 0.98);
            changes.Add(new StateChange(frames[i].PtsS, scores[i], conf, regions[i] ?? new BBox(0, 0, frames[i].Width, frames[i].Height)));
        }
        return changes;
    }

    public bool IsSceneCut(StateChange c) => c.Magnitude >= SceneCutFraction;

    private (double fraction, BBox? region) BlockDiff(GrayFrame a, GrayFrame b)
    {
        int bs = BlockSize, bw = a.Width / bs, bh = a.Height / bs;
        int changed = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                long sum = 0;
                for (int y = by * bs; y < by * bs + bs; y++)
                {
                    int row = y * a.Width;
                    for (int x = bx * bs; x < bx * bs + bs; x++)
                        sum += Math.Abs(a.Pixels[row + x] - b.Pixels[row + x]);
                }
                if (sum / (double)(bs * bs) >= BlockThreshold)
                {
                    changed++;
                    minX = Math.Min(minX, bx * bs); minY = Math.Min(minY, by * bs);
                    maxX = Math.Max(maxX, bx * bs + bs); maxY = Math.Max(maxY, by * bs + bs);
                }
            }
        if (changed == 0) return (0, null);
        return ((double)changed / (bw * bh), new BBox(minX, minY, maxX, maxY));
    }
}

public sealed record CursorSample(double TimeS, int X, int Y, double Confidence);

/// <summary>
/// Cursor tracker. If a cursor template (gray, e.g. decoded from a PNG via FFmpeg) is supplied, it does
/// normalised SAD template matching with a local search window around the previous position (full-frame
/// search when lost). Without a template it falls back to detecting the small moving blob between frames,
/// which works well on screen recordings where the cursor is the only small thing moving.
/// </summary>
public sealed class CursorTracker
{
    public GrayFrame? Template { get; init; }
    public int SearchRadius { get; init; } = 120;
    public double MaxTemplateError { get; init; } = 18.0;   // mean abs diff accepted as a match
    public int MaxBlobSize { get; init; } = 48;             // px, anything larger is a UI change, not the cursor
    public int MinBlobPixels { get; init; } = 4;
    public int DiffThreshold { get; init; } = 40;

    public List<CursorSample> Track(IReadOnlyList<GrayFrame> frames) =>
        Template is not null ? TrackByTemplate(frames) : TrackByMotion(frames);

    private List<CursorSample> TrackByTemplate(IReadOnlyList<GrayFrame> frames)
    {
        var samples = new List<CursorSample>();
        var tpl = Template!;
        (int x, int y)? last = null;
        foreach (var f in frames)
        {
            int x0 = 0, y0 = 0, x1 = f.Width - tpl.Width, y1 = f.Height - tpl.Height;
            if (last is { } l)
            {
                x0 = Math.Max(0, l.x - SearchRadius); y0 = Math.Max(0, l.y - SearchRadius);
                x1 = Math.Min(x1, l.x + SearchRadius); y1 = Math.Min(y1, l.y + SearchRadius);
            }
            var (bx, by, err) = BestMatch(f, tpl, x0, y0, x1, y1, 2);
            if (err > MaxTemplateError && last is not null)
                (bx, by, err) = BestMatch(f, tpl, 0, 0, f.Width - tpl.Width, f.Height - tpl.Height, 3); // re-acquire
            if (err <= MaxTemplateError)
            {
                last = (bx, by);
                // Hot-spot: assume the pointer tip is the template's top-left corner.
                samples.Add(new CursorSample(f.PtsS, bx, by, Math.Clamp(1.0 - err / MaxTemplateError, 0.2, 0.98)));
            }
            else last = null;
        }
        return samples;
    }

    private static (int x, int y, double err) BestMatch(GrayFrame f, GrayFrame tpl, int x0, int y0, int x1, int y1, int step)
    {
        double best = double.MaxValue; int bx = -1, by = -1;
        for (int y = y0; y <= y1; y += step)
            for (int x = x0; x <= x1; x += step)
            {
                double e = Sad(f, tpl, x, y, best);
                if (e < best) { best = e; bx = x; by = y; }
            }
        // refine at step 1 around the best coarse hit
        if (step > 1 && bx >= 0)
            for (int y = Math.Max(y0, by - step); y <= Math.Min(y1, by + step); y++)
                for (int x = Math.Max(x0, bx - step); x <= Math.Min(x1, bx + step); x++)
                {
                    double e = Sad(f, tpl, x, y, best);
                    if (e < best) { best = e; bx = x; by = y; }
                }
        return (bx, by, best);
    }

    private static double Sad(GrayFrame f, GrayFrame tpl, int ox, int oy, double abortAbove)
    {
        long sum = 0; int n = tpl.Width * tpl.Height; long abort = (long)(abortAbove * n);
        for (int y = 0; y < tpl.Height; y++)
        {
            int frow = (oy + y) * f.Width + ox, trow = y * tpl.Width;
            for (int x = 0; x < tpl.Width; x++)
                sum += Math.Abs(f.Pixels[frow + x] - tpl.Pixels[trow + x]);
            if (sum > abort) return double.MaxValue;
        }
        return (double)sum / n;
    }

    private List<CursorSample> TrackByMotion(IReadOnlyList<GrayFrame> frames)
    {
        var samples = new List<CursorSample>();
        (int x, int y)? last = null;
        for (int i = 1; i < frames.Count; i++)
        {
            var a = frames[i - 1]; var b = frames[i];
            // Pixels that are bright in b and differ from a → candidate new cursor position (cursor is usually light with dark outline or vice versa).
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0; long sx = 0, sy = 0;
            for (int y = 0; y < b.Height; y++)
            {
                int row = y * b.Width;
                for (int x = 0; x < b.Width; x++)
                {
                    int d = b.Pixels[row + x] - a.Pixels[row + x];
                    if (d >= DiffThreshold)
                    {
                        count++; sx += x; sy += y;
                        if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                    }
                }
            }
            if (count >= MinBlobPixels && (maxX - minX) <= MaxBlobSize && (maxY - minY) <= MaxBlobSize)
            {
                // Use the top-left of the appeared blob as the pointer tip (arrow cursors point to their top-left).
                last = (minX, minY);
                samples.Add(new CursorSample(b.PtsS, minX, minY, 0.75));
            }
            else if (last is { } l && count < MinBlobPixels)
            {
                // Cursor is stationary: carry the last position forward with slightly lower confidence.
                samples.Add(new CursorSample(b.PtsS, l.x, l.y, 0.7));
            }
        }
        return samples;
    }
}

/// <summary>
/// Combines cursor dwell + UI state change into click candidates (<see cref="VisualEvent"/>s).
/// A click candidate is emitted when a state change occurs within <see cref="MaxChangeDelayS"/> after a
/// moment where the cursor is (nearly) stationary. The change region does not need to contain the cursor
/// (a click on "Save" may change the whole view), but if it does, confidence is boosted.
/// </summary>
public sealed class ClickCandidateDetector
{
    public double MaxChangeDelayS { get; init; } = 0.6;
    public double DwellSpeedPxPerS { get; init; } = 60;
    public double MinDwellS { get; init; } = 0.12;
    public double SceneCutFraction { get; init; } = 0.6;
    public double ScaleX { get; init; } = 1.0;   // analysis frame → source pixel scale
    public double ScaleY { get; init; } = 1.0;

    /// <summary>Changes no larger than this (in analysis pixels) that contain a *moving* cursor are the cursor itself, not UI.</summary>
    public int CursorRegionMaxPx { get; init; } = 96;
    /// <summary>Cursor displacement (analysis px) below which the cursor counts as resting.</summary>
    public double CursorStillTolerancePx { get; init; } = 2.0;

    public List<VisualEvent> Detect(IReadOnlyList<CursorSample> cursor, IReadOnlyList<StateChange> changes)
    {
        var events = new List<VisualEvent>();
        int n = 0;
        var ordered = cursor.OrderBy(c => c.TimeS).ToList();

        foreach (var ch in changes)
        {
            if (ch.Magnitude >= SceneCutFraction && !ordered.Any()) continue;
            if (IsCursorMotion(ch, ordered)) continue;
            // Find cursor samples just before the change.
            var before = ordered.Where(c => c.TimeS <= ch.TimeS + 1e-6 && c.TimeS >= ch.TimeS - MaxChangeDelayS - MinDwellS).ToList();
            if (before.Count == 0)
            {
                // State change without any visible cursor: a real UI change, but the click point is not observable.
                events.Add(new VisualEvent
                {
                    Id = $"vis_{n++:D4}", Action = ActionType.Click, TimeS = ch.TimeS, StartS = ch.TimeS - 0.05, EndS = ch.TimeS + 0.1,
                    Point = null, ChangedRegion = Scale(ch.Region), DetectorConfidence = 0.5 * ch.Confidence, StateChangeConfidence = ch.Confidence,
                    Evidence = { new Evidence(EvidenceSource.FrameStateChange, ch.Confidence) },
                });
                continue;
            }

            var lastSample = before[^1];
            double dwell = DwellDuration(before, lastSample);
            bool dwelled = dwell >= MinDwellS;
            var point = new Point2D((int)Math.Round(lastSample.X * ScaleX), (int)Math.Round(lastSample.Y * ScaleY));
            bool inside = Scale(ch.Region).Contains(point);
            double detConf = Math.Clamp(0.5 * lastSample.Confidence + 0.3 * (dwelled ? 1 : 0.4) + 0.2 * (inside ? 1 : 0.5), 0, 1);

            events.Add(new VisualEvent
            {
                Id = $"vis_{n++:D4}", Action = ActionType.Click,
                TimeS = ch.TimeS,                       // the click is observed through its effect; refine with higher-fps re-decode
                StartS = Math.Max(0, lastSample.TimeS - dwell), EndS = ch.TimeS + 0.1,
                Point = point, ChangedRegion = Scale(ch.Region),
                DetectorConfidence = detConf, StateChangeConfidence = ch.Confidence,
                PointSource = EvidenceSource.CursorTracker,
                Evidence = { new Evidence(EvidenceSource.CursorTracker, lastSample.Confidence), new Evidence(EvidenceSource.FrameStateChange, ch.Confidence) },
            });
        }
        return events;
    }

    /// <summary>
    /// A small change region that contains the cursor while the cursor is moving faster than the dwell speed
    /// is the cursor's own motion. A change while the cursor rests (even if it contains the cursor) is a real UI change.
    /// </summary>
    private bool IsCursorMotion(StateChange ch, List<CursorSample> ordered)
    {
        if (Math.Max(ch.Region.Width, ch.Region.Height) > CursorRegionMaxPx) return false;
        var at = ordered.Where(c => Math.Abs(c.TimeS - ch.TimeS) <= 0.11).OrderBy(c => Math.Abs(c.TimeS - ch.TimeS)).FirstOrDefault();
        if (at is null) return false;
        var prev = ordered.LastOrDefault(c => c.TimeS < at.TimeS - 1e-6);
        if (prev is null) return true; // first appearance of the cursor = motion
        // Displacement, not speed: at low analysis fps a moving cursor can look "slow" in px/s.
        double moved = Math.Sqrt(Math.Pow(at.X - prev.X, 2) + Math.Pow(at.Y - prev.Y, 2));
        var grown = new BBox(ch.Region.X1 - 8, ch.Region.Y1 - 8, ch.Region.X2 + 8, ch.Region.Y2 + 8);
        return moved > CursorStillTolerancePx && grown.Contains(new Point2D(at.X, at.Y));
    }

    private double DwellDuration(List<CursorSample> before, CursorSample last)
    {
        double start = last.TimeS;
        for (int i = before.Count - 2; i >= 0; i--)
        {
            var s = before[i];
            double dt = last.TimeS - s.TimeS;
            double dist = Math.Sqrt(Math.Pow(s.X - last.X, 2) + Math.Pow(s.Y - last.Y, 2));
            if (dt > 0 && dist / dt > DwellSpeedPxPerS) break;
            start = s.TimeS;
        }
        return last.TimeS - start;
    }

    private BBox Scale(BBox b) => new((int)(b.X1 * ScaleX), (int)(b.Y1 * ScaleY), (int)(b.X2 * ScaleX), (int)(b.Y2 * ScaleY));
}
