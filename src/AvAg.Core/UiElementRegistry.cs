namespace AvAg.Core;

/// <summary>
/// Merges per-frame UI detections (OmniParser boxes, OCR boxes, GroundingDINO boxes) into
/// stable <c>ui_element_id</c>s over time using IoU + text similarity, as recommended in the
/// design document. A detection matches an existing element if IoU ≥ <see cref="IouThreshold"/>
/// and (either has no text, or text similarity ≥ <see cref="TextSimilarityThreshold"/>).
/// </summary>
public sealed class UiElementRegistry
{
    public double IouThreshold { get; init; } = 0.5;
    public double TextSimilarityThreshold { get; init; } = 0.7;

    private readonly List<UiElement> _elements = new();
    private int _nextId;

    public IReadOnlyList<UiElement> Elements => _elements;

    public sealed record Detection(BBox Bbox, string? Text, double TextConfidence, double InteractiveConfidence, string Class = "unknown");

    /// <summary>Ingest the detections of one frame observed at <paramref name="timeS"/>. Returns the stable elements for that frame.</summary>
    public IReadOnlyList<UiElement> Observe(double timeS, IEnumerable<Detection> detections)
    {
        var result = new List<UiElement>();
        foreach (var d in detections)
        {
            UiElement? best = null;
            double bestScore = 0;
            foreach (var e in _elements)
            {
                double iou = e.Bbox.IoU(d.Bbox);
                if (iou < IouThreshold) continue;
                double textSim = TextSimilarity(e.Text, d.Text);
                if (e.Text is not null && d.Text is not null && textSim < TextSimilarityThreshold) continue;
                double score = iou + 0.5 * textSim;
                if (score > bestScore) { bestScore = score; best = e; }
            }

            if (best is null)
            {
                best = new UiElement
                {
                    Id = $"ui_{_nextId++:D3}",
                    Bbox = d.Bbox,
                    Text = d.Text,
                    TextConfidence = d.TextConfidence,
                    InteractiveConfidence = d.InteractiveConfidence,
                    Class = d.Class,
                    FirstSeenS = timeS,
                    LastSeenS = timeS,
                };
                _elements.Add(best);
            }
            else
            {
                // Running update: keep the box with slight smoothing, prefer more confident text.
                best.Observations++;
                best.LastSeenS = Math.Max(best.LastSeenS, timeS);
                best.Bbox = Smooth(best.Bbox, d.Bbox, 0.3);
                if (d.Text is not null && d.TextConfidence >= best.TextConfidence)
                {
                    best.Text = d.Text; best.TextConfidence = d.TextConfidence;
                }
                best.InteractiveConfidence = Math.Max(best.InteractiveConfidence, d.InteractiveConfidence);
                if (best.Class == "unknown" && d.Class != "unknown") best.Class = d.Class;
            }
            result.Add(best);
        }
        return result;
    }

    /// <summary>Elements visible (observed) within ±<paramref name="toleranceS"/> of <paramref name="timeS"/>.</summary>
    public IEnumerable<UiElement> VisibleAt(double timeS, double toleranceS = 1.0) =>
        _elements.Where(e => e.FirstSeenS - toleranceS <= timeS && timeS <= e.LastSeenS + toleranceS);

    /// <summary>Smallest element containing the point; interactive elements preferred.</summary>
    public UiElement? ResolveTarget(Point2D p, double timeS, double toleranceS = 1.0)
    {
        return VisibleAt(timeS, toleranceS)
            .Where(e => e.Bbox.Contains(p))
            .OrderByDescending(e => e.InteractiveConfidence >= 0.5 ? 1 : 0)
            .ThenBy(e => e.Bbox.Area)
            .FirstOrDefault();
    }

    /// <summary>Find an element by spoken text ("Speichern"), fuzzy.</summary>
    public UiElement? FindByText(string text, double timeS, double toleranceS = 2.0, double minSim = 0.6)
    {
        return VisibleAt(timeS, toleranceS)
            .Where(e => e.Text is not null)
            .Select(e => (e, sim: TextSimilarity(e.Text, text)))
            .Where(x => x.sim >= minSim)
            .OrderByDescending(x => x.sim)
            .Select(x => x.e)
            .FirstOrDefault();
    }

    private static BBox Smooth(BBox a, BBox b, double alpha) => new(
        (int)Math.Round(a.X1 + alpha * (b.X1 - a.X1)), (int)Math.Round(a.Y1 + alpha * (b.Y1 - a.Y1)),
        (int)Math.Round(a.X2 + alpha * (b.X2 - a.X2)), (int)Math.Round(a.Y2 + alpha * (b.Y2 - a.Y2)));

    /// <summary>Normalised Levenshtein similarity in [0,1]; 0 if either side is null.</summary>
    public static double TextSimilarity(string? a, string? b)
    {
        if (a is null || b is null) return 0;
        a = a.Trim().ToLowerInvariant(); b = b.Trim().ToLowerInvariant();
        if (a.Length == 0 && b.Length == 0) return 1;
        int dist = TextMetrics.Levenshtein(a, b);
        return 1.0 - (double)dist / Math.Max(a.Length, b.Length);
    }
}

public static class TextMetrics
{
    public static int Levenshtein<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : IEquatable<T>
    {
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) prev[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Count; j++)
            {
                int cost = a[i - 1].Equals(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Count];
    }

    public static int Levenshtein(string a, string b) => Levenshtein(a.ToCharArray(), b.ToCharArray());
}
