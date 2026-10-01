namespace AvAg.Core;

/// <summary>Ground truth annotation for one action event (internal dataset format from the design document).</summary>
public sealed class GroundTruthEvent
{
    public required string VideoId { get; set; }
    public string? Speaker { get; set; }
    public required string ActionUtterance { get; set; }
    public double UtteranceStartS { get; set; }
    public double UtteranceEndS { get; set; }
    public required double EventTimeS { get; set; }
    public double? EventStartS { get; set; }
    public double? EventEndS { get; set; }
    public required ActionType EventType { get; set; }
    public int[]? Point { get; set; }          // cursor/finger point
    public int[]? TargetBbox { get; set; }     // [x1,y1,x2,y2]
    public string? UiTargetText { get; set; }
    public bool Observable { get; set; } = true;
}

public sealed class GroundTruthFile
{
    public required string VideoId { get; set; }
    public List<Word> Words { get; set; } = new();
    public List<GroundTruthEvent> Events { get; set; } = new();
}

public static class Stats
{
    public static double Mean(IReadOnlyList<double> xs) => xs.Count == 0 ? double.NaN : xs.Average();
    public static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return double.NaN;
        var s = xs.OrderBy(x => x).ToArray();
        double rank = p / 100.0 * (s.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return s[lo] + (s[hi] - s[lo]) * (rank - lo);
    }
}

public static class AsrMetrics
{
    /// <summary>Word Error Rate = (S+D+I)/N via Levenshtein over normalised tokens.</summary>
    public static double Wer(IEnumerable<string> reference, IEnumerable<string> hypothesis)
    {
        var r = reference.Select(Norm).ToArray();
        var h = hypothesis.Select(Norm).ToArray();
        if (r.Length == 0) return h.Length == 0 ? 0 : 1;
        return (double)TextMetrics.Levenshtein(r, h) / r.Length;
    }

    /// <summary>MAE / median / P90 / P95 of |t̂ − t| over aligned words (matched by order after Levenshtein alignment on text).</summary>
    public static (double Mae, double Median, double P90, double P95, int N) WordTimeError(IReadOnlyList<Word> reference, IReadOnlyList<Word> hypothesis)
    {
        var errs = new List<double>();
        // Simple monotone matching on identical normalised tokens.
        int j = 0;
        foreach (var rw in reference)
        {
            for (int k = j; k < Math.Min(hypothesis.Count, j + 5); k++)
            {
                if (Norm(hypothesis[k].Text) == Norm(rw.Text))
                {
                    errs.Add(Math.Abs(hypothesis[k].StartS - rw.StartS));
                    j = k + 1;
                    break;
                }
            }
        }
        return (Stats.Mean(errs), Stats.Percentile(errs, 50), Stats.Percentile(errs, 90), Stats.Percentile(errs, 95), errs.Count);
    }

    private static string Norm(string s) => new(s.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch)).ToArray());
}

public static class TemporalMetrics
{
    public static double IoU(double s1, double e1, double s2, double e2)
    {
        double inter = Math.Max(0, Math.Min(e1, e2) - Math.Max(s1, s2));
        double union = Math.Max(e1, e2) - Math.Min(s1, s2);
        return union <= 0 ? 0 : inter / union;
    }

    public static double RecallAtIoU(IReadOnlyList<(double s, double e)> pred, IReadOnlyList<(double s, double e)> gt, double thr)
    {
        if (gt.Count == 0) return double.NaN;
        int hit = gt.Count(g => pred.Any(p => IoU(p.s, p.e, g.s, g.e) >= thr));
        return (double)hit / gt.Count;
    }

    /// <summary>Success rate of |Δt| ≤ tolerance for point-like events (clicks).</summary>
    public static double ClickTimeSuccess(IReadOnlyList<double> predTimes, IReadOnlyList<double> gtTimes, double toleranceS)
    {
        if (gtTimes.Count == 0) return double.NaN;
        int hit = gtTimes.Count(g => predTimes.Any(p => Math.Abs(p - g) <= toleranceS));
        return (double)hit / gtTimes.Count;
    }
}

public static class SpatialMetrics
{
    public static bool PointInBox(int[] p, int[] box) => p[0] >= box[0] && p[0] <= box[2] && p[1] >= box[1] && p[1] <= box[3];

    public static double PixelDistance(int[] a, int[] b) => Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2));

    public static double NormalizedDistance(int[] a, int[] b, int w, int h) => PixelDistance(a, b) / Math.Sqrt((double)w * w + (double)h * h);
}

/// <summary>
/// Audio-Visual Action Grounding Accuracy: a case counts only if speaker ∧ phrase ∧ action ∧ |Δt| ≤ tol ∧ point ∈ target all hold.
/// </summary>
public static class Avaga
{
    public sealed record Result(double Avaga, double PointHitAccuracy, double ActionAccuracy, double TimeSuccess, double PhraseRecall,
                                double MeanPixelDistance, double P95PixelDistance, double FactualEventPrecision, int N);

    public static Result Evaluate(EventGraph pred, GroundTruthFile gt, double toleranceS = 0.25, bool requireSpeaker = false)
    {
        int n = gt.Events.Count;
        int avaga = 0, pointHit = 0, actionOk = 0, timeOk = 0, phraseOk = 0;
        var dists = new List<double>();

        foreach (var g in gt.Events)
        {
            // Candidate: predicted event bound to an audio ref that overlaps the GT utterance.
            var cand = pred.Events
                .Where(e => e.AudioReferences.Any(a => TemporalMetrics.IoU(a.StartS, a.EndS, g.UtteranceStartS, g.UtteranceEndS) > 0
                                                        || UiElementRegistry.TextSimilarity(a.Transcript, g.ActionUtterance) >= 0.6))
                .OrderBy(e => Math.Abs(e.Temporal.PeakS - g.EventTimeS))
                .FirstOrDefault();
            if (cand is null) continue;
            phraseOk++;

            bool speaker = !requireSpeaker || g.Speaker is null || cand.AudioReferences.Any(a => a.SpeakerId == g.Speaker);
            bool action = cand.Type == g.EventType;
            bool time = Math.Abs(cand.Temporal.PeakS - g.EventTimeS) <= toleranceS;
            bool point = false;
            if (!g.Observable)
                point = cand.GroundingStatus != GroundingStatus.Observed; // must NOT claim an observed coordinate
            else if (g.TargetBbox is not null && cand.Spatial?.PointXyPx is not null)
                point = SpatialMetrics.PointInBox(cand.Spatial.PointXyPx, g.TargetBbox);
            else if (g.UiTargetText is not null && cand.Target?.Text is not null)
                point = UiElementRegistry.TextSimilarity(g.UiTargetText, cand.Target.Text) >= 0.7;

            if (g.Point is not null && cand.Spatial?.PointXyPx is not null)
                dists.Add(SpatialMetrics.PixelDistance(g.Point, cand.Spatial.PointXyPx));

            if (action) actionOk++;
            if (time) timeOk++;
            if (point) pointHit++;
            if (speaker && action && time && point) avaga++;
        }

        // Factual Event Precision: fraction of described events that carry evidence objects (not pure VLM semantic claims).
        int described = pred.Events.Count;
        int backed = pred.Events.Count(e => e.Evidence.Any(x => x.Source is not "vlm_semantic") && e.GroundingStatus != GroundingStatus.Inferred);
        double fep = described == 0 ? double.NaN : (double)backed / described;

        return new Result(
            Avaga: Ratio(avaga, n), PointHitAccuracy: Ratio(pointHit, n), ActionAccuracy: Ratio(actionOk, n), TimeSuccess: Ratio(timeOk, n),
            PhraseRecall: Ratio(phraseOk, n), MeanPixelDistance: Stats.Mean(dists), P95PixelDistance: Stats.Percentile(dists, 95),
            FactualEventPrecision: fep, N: n);
    }

    private static double Ratio(int a, int n) => n == 0 ? double.NaN : (double)a / n;
}
