using System.Globalization;
using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// Rule-based manual writer (no language model): every sentence that tells the viewer to do something becomes a step,
/// the explaining sentences that follow it become the step's details. Deterministic and offline; an LLM writer can
/// replace the wording. Screenshot moments and click markers are placed by <see cref="PlaceScreenshots"/>.
/// </summary>
public sealed class ManualBuilder
{
    public int MaxDetailSentences { get; init; } = 3;

    private static readonly Regex FillerRx = new(
        @"^(?:(?:also|so|okay|ok|ja|nun|gut|und|äh|ähm|ehm|uh|um|well|right|alright|now|and)\b[,\s]*)+|\b(?:äh|ähm|ehm|uh|uhm|um)\b[,]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OutroRx = new(
        @"\b(like and subscribe|subscribe|see you|next (one|video)|thanks for watching|i am out|i'm out|peace\b|bye\b|abonnier|bis zum nächsten|danke fürs zuschauen|tschüss|ciao)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Instructions the click parser does not cover (it only knows pointer actions): open, go to, save, copy, …
    private static readonly Regex InstructionRx = new(
        @"\b(open|go to|navigate|save|copy|paste|export|install|launch|start|press|hit|type|enter|select|choose|set|turn on|turn off|enable|disable|find|search|" +
        @"öffne\w*|geh\w* (sie )?(auf|zu|in)|speicher\w*|kopier\w*|einfüg\w*|export\w*|installier\w*|start\w*|drück\w*|tipp\w*|wähl\w*|stell\w* (sie )?\w+ ein|aktivier\w*|deaktivier\w*|such\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "you can / you'll / Sie können / klicken Sie" – addressed to the viewer, not narration about the past.
    private static readonly Regex AddressRx = new(
        @"\b(you|you'll|you're|your|just|simply|sie|ihr|du|man)\b|^(open|go|click|press|select|choose|save|copy|type|öffnen|gehen|klicken|drücken|wählen|speichern)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record Sentence(double StartS, double EndS, string Text);

    public Manual Build(Transcript transcript, IReadOnlyList<AudioRef> refs, EventGraph? graph, string? videoName, string? language = null)
    {
        string lang = language is "de" or "en" ? language : transcript.Language == "de" ? "de" : "en";
        var sentences = Sentences(transcript).Where(s => !OutroRx.IsMatch(s.Text)).ToList();
        var steps = new List<ManualStep>();
        ManualStep? current = null;
        int detailCount = 0;
        var intro = new List<string>();

        foreach (var s in sentences)
        {
            var sRefs = RefsIn(refs, s.StartS, s.EndS).Where(r => r.Action != ActionType.Point).ToList();
            bool instruction = sRefs.Count > 0 || InstructionRx.IsMatch(s.Text) && AddressRx.IsMatch(s.Text);
            // The very first sentence usually says what the video is about – keep it for the overview as well.
            if (steps.Count == 0 && intro.Count == 0 && sentences.Count > 2 && sRefs.Count == 0) { intro.Add(s.Text); continue; }
            if (instruction)
            {
                current = new ManualStep
                {
                    Number = steps.Count + 1,
                    Title = StepTitle(sRefs, lang),
                    Instruction = Instruction(s, sRefs),
                    TimeS = s.StartS,
                    EndS = s.EndS,
                };
                steps.Add(current);
                detailCount = 0;
            }
            else if (current is null) intro.Add(s.Text);
            else if (detailCount < MaxDetailSentences && s.Text.Split(' ').Length <= 2 * MaxInstructionWords)
            {
                current.Details = current.Details is null ? s.Text : current.Details + " " + s.Text;
                current.EndS = s.EndS;
                detailCount++;
            }
        }

        // Without any recognisable instruction the narration itself is the manual: one step per sentence.
        if (steps.Count == 0)
            foreach (var s in sentences)
                steps.Add(new ManualStep { Number = steps.Count + 1, Instruction = s.Text, TimeS = s.StartS, EndS = s.EndS });

        string summarySrc = intro.Count > 0 ? string.Join(" ", intro.Take(2)) : sentences.FirstOrDefault()?.Text ?? "";
        var m = new Manual
        {
            Title = TitleFrom(videoName, lang),
            Language = lang,
            Summary = string.IsNullOrWhiteSpace(summarySrc) ? null : summarySrc,
            Steps = steps,
            VideoName = videoName,
            VideoDurationS = graph?.Video.DurationS ?? transcript.Segments.LastOrDefault()?.EndS ?? 0,
            Method = "extractive",
        };
        PlaceScreenshots(m, refs, graph);
        return m;
    }

    private static IEnumerable<AudioRef> RefsIn(IReadOnlyList<AudioRef> refs, double start, double end) =>
        refs.Where(r => InSpan(r, start, end));

    // Half-open: an instruction that starts exactly where this sentence ends belongs to the next one.
    private static bool InSpan(AudioRef r, double start, double end) => r.StartS >= start - 0.05 && r.StartS < end - 0.05;

    /// <summary>
    /// Chooses one screenshot moment per step from the step's own narration span [TimeS, EndS]:
    /// a reliable observed click inside the span (drawn as a marker), else the moment the instruction is spoken
    /// (deictic word / end of the action clause), else the middle of the first sentence. Moments are kept in video
    /// order and at least <paramref name="minGapS"/> apart, so two steps never show the same frame.
    /// </summary>
    public static void PlaceScreenshots(Manual m, IReadOnlyList<AudioRef> refs, EventGraph? graph, double minGapS = 1.5)
    {
        var refById = refs.ToDictionary(r => r.Id);
        double last = double.NegativeInfinity;
        foreach (var s in m.Steps)
        {
            double start = s.TimeS, end = Math.Max(s.EndS, s.TimeS + 0.5);
            s.PointXyPx = null; s.BboxXyxyPx = null;

            var click = graph?.Events
                .Where(e => e.GroundingStatus is GroundingStatus.Observed or GroundingStatus.Tracked && e.Spatial?.PointXyPx is not null)
                .Where(e => e.Temporal.PeakS >= start - 0.3 && e.Temporal.PeakS <= end + 1.5 && e.Temporal.PeakS - 0.1 >= last + minGapS)
                .Where(e => ReliableClick(e, refById, start, end))
                .OrderByDescending(e => e.OverallConfidence).FirstOrDefault();
            if (click is not null)
            {
                s.ScreenshotS = Math.Max(0, click.Temporal.PeakS - 0.1);
                s.PointXyPx = click.Spatial!.PointXyPx; s.BboxXyxyPx = click.Spatial.BboxXyxyPx;
            }
            else
            {
                var anchor = RefsIn(refs, start, end).Where(r => r.Action != ActionType.Point).Select(r => (double?)r.AnchorS).FirstOrDefault();
                double t = anchor ?? start + Math.Min(3.0, (end - start) / 2);
                if (t < last + minGapS) t = Math.Min(end, last + minGapS);
                s.ScreenshotS = t >= last + minGapS - 0.01 ? t : null;     // no distinct frame left in this step's span
            }
            if (s.ScreenshotS is { } shot) last = shot;
        }
    }

    /// <summary>
    /// A marker is only drawn for a click bound to an instruction spoken inside this step (not the next one), and only if
    /// that instruction is a click or the click visibly changed the screen.
    /// </summary>
    private static bool ReliableClick(GroundedEvent e, Dictionary<string, AudioRef> refById, double start, double end)
    {
        var own = e.AudioReferences.Select(a => refById.GetValueOrDefault(a.AudioRefId))
                   .Where(r => r is not null && InSpan(r, start, end)).ToList();
        if (own.Count == 0) return false;
        bool spokenClick = own.Any(r => r!.Action is ActionType.Click or ActionType.DoubleClick or ActionType.RightClick);
        double change = e.Evidence.Where(x => x.Source == "frame_state_change").Select(x => x.Confidence).DefaultIfEmpty(0).Max();
        return spokenClick && e.OverallConfidence >= 0.5 || change >= 0.9;
    }

    /// <summary>Unpunctuated run-on sentences are cut down to the instruction clauses the parser found in them.</summary>
    private static string Instruction(Sentence s, List<AudioRef> refs)
    {
        if (s.Text.Split(' ').Length <= MaxInstructionWords || refs.Count == 0) return s.Text;
        var clauses = refs.Select(r => Clean(r.Text)).Where(c => c.Length > 0).Distinct().ToList();
        string text = string.Join("; ", clauses.Select((c, i) => i == 0 ? c : char.ToLower(c[0]) + c[1..]));
        return text.Length == 0 ? s.Text : text + (Regex.IsMatch(text, @"[.!?]$") ? "" : ".");
    }

    private const int MaxInstructionWords = 35;

    /// <summary>Split the transcript into sentences using word timings; unpunctuated run-ons are cut at ~40 words.</summary>
    public static List<Sentence> Sentences(Transcript t)
    {
        var result = new List<Sentence>();
        foreach (var seg in t.Segments)
        {
            if (seg.Words.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(seg.Text)) Add(seg.StartS, seg.EndS, seg.Text);
                continue;
            }
            var buf = new List<Word>();
            foreach (var w in seg.Words)
            {
                buf.Add(w);
                bool end = Regex.IsMatch(w.Text, @"[.!?…]$") && !Regex.IsMatch(w.Text, @"^\d+\.$");
                if (end || buf.Count >= 40) { Flush(); }
            }
            Flush();

            void Flush()
            {
                if (buf.Count == 0) return;
                Add(buf[0].StartS, buf[^1].EndS, string.Join(" ", buf.Select(x => x.Text)));
                buf.Clear();
            }
        }
        return result;

        void Add(double s, double e, string text)
        {
            text = Clean(text);
            if (text.Length < 3) return;
            // Fragments ("…your entire" / "Screen right like so") belong to the previous sentence.
            // Only short pieces are glued – two long unpunctuated halves stay separate sentences.
            int nNew = text.Split(' ').Length, nPrev = result.Count > 0 ? result[^1].Text.Split(' ').Length : 0;
            if (result.Count > 0 && !Regex.IsMatch(result[^1].Text, @"[.!?…]$") && s - result[^1].EndS < 1.0
                && (nNew <= 6 || nPrev <= 6) && nNew + nPrev <= 45)
                result[^1] = result[^1] with { EndS = e, Text = result[^1].Text + " " + char.ToLower(text[0]) + text[1..] };
            else result.Add(new Sentence(s, e, text));
        }
    }

    public static string Clean(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        text = FillerRx.Replace(text, m => m.Index == 0 ? "" : "").Trim();
        text = Regex.Replace(text, @"\s+([,.!?])", "$1");
        text = Regex.Replace(text, @"\b(\w+)( \1\b)+", "$1", RegexOptions.IgnoreCase); // "way, way, way" stays; "the the" → "the"
        if (text.Length > 0) text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        return text;
    }

    private static string? StepTitle(List<AudioRef> refs, string lang)
    {
        var r = refs.FirstOrDefault(x => x.ExplicitTarget is not null);
        if (r is null) return null;
        string t = r.ExplicitTarget!;
        return (lang, r.Action) switch
        {
            ("de", ActionType.DoubleClick) => $"Doppelklick auf „{t}“",
            ("de", ActionType.RightClick) => $"Rechtsklick auf „{t}“",
            ("de", ActionType.Type) => $"In „{t}“ eingeben",
            ("de", ActionType.Select) => $"„{t}“ auswählen",
            ("de", ActionType.Drag) => $"„{t}“ ziehen",
            ("de", _) => $"„{t}“ anklicken",
            (_, ActionType.DoubleClick) => $"Double-click \"{t}\"",
            (_, ActionType.RightClick) => $"Right-click \"{t}\"",
            (_, ActionType.Type) => $"Type into \"{t}\"",
            (_, ActionType.Select) => $"Select \"{t}\"",
            (_, ActionType.Drag) => $"Drag \"{t}\"",
            _ => $"Click \"{t}\"",
        };
    }

    private static string TitleFrom(string? videoName, string lang)
    {
        string? n = videoName is null ? null : Path.GetFileNameWithoutExtension(videoName.Split(" (")[0]);
        if (string.IsNullOrWhiteSpace(n) || n.Equals("input", StringComparison.OrdinalIgnoreCase)) return lang == "de" ? "Anleitung" : "Manual";
        n = Regex.Replace(n, @"[_\-]+", " ").Trim();
        return lang == "de" ? $"Anleitung: {n}" : $"Manual: {n}";
    }
}
