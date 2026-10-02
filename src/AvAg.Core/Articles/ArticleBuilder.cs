using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// Rule-based wiki article from a support call (no language model, deterministic, offline). It is the fallback when no
/// model is configured or the model's answer is unusable, and the draft the model's article is checked against.
/// <list type="bullet">
/// <item>Problem: the first sentences that describe something not working (before the first instruction).</item>
/// <item>Error messages: quoted text in those sentences.</item>
/// <item>Steps: every sentence that tells the customer to do something; the explanation after it becomes its details.</item>
/// <item>Cause: the first sentence that explains why ("das liegt daran, dass …", "because …").</item>
/// <item>Verification: the last sentence that confirms it works again.</item>
/// </list>
/// Screenshot moments and click markers are placed by <see cref="PlaceScreenshots"/>.
/// </summary>
public sealed class ArticleBuilder
{
    public int MaxDetailSentences { get; init; } = 2;
    private const int MaxInstructionWords = 35;
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Greetings, small talk and goodbyes of a phone call – never part of the article.
    private static readonly Regex SmallTalkRx = new(
        @"^(?:(?:guten (?:tag|morgen|abend)|grüe?zi|hallo|hoi|servus|willkommen|vielen dank|danke(?: schön| vielmals)?|merci|auf wiederhören|auf wiedersehen|tschüss|schönen tag|einen schönen|gern geschehen|bitte schön|sehr gut|super|perfekt|alles klar|okay|ok|genau|" +
        @"hello|hi|good (?:morning|afternoon|evening)|thanks?(?: you)?(?: for calling)?|you're welcome|bye|goodbye|have a (?:nice|good|great) day|great|perfect|alright)\b)", Opts);
    private static readonly Regex OutroRx = new(@"\b(like and subscribe|subscribe|see you|thanks for watching|abonnier|bis zum nächsten|danke fürs zuschauen)", Opts);
    // Something is not working.
    private static readonly Regex ProblemRx = new(
        @"\b(geht nicht|funktioniert nicht|klappt nicht|kann (?:ich )?(?:nicht|kein\w*)|lässt sich nicht|fehler\w*|fehlermeldung|meldung|problem\w*|absturz|stürzt|hängt|leer|fehlt|nicht mehr|seit (?:heute|gestern)|" +
        @"doesn't work|does not work|not working|can't|cannot|won't|error|fails?|failed|crash\w*|missing|broken|problem|issue|no longer)\b", Opts);
    // Why it happened.
    private static readonly Regex CauseRx = new(
        @"\b(liegt (?:\w+ )?daran|der grund|die ursache|ursache ist|kommt daher|weil|because|the reason|is caused by|caused by|happens when)\b", Opts);
    // An error message read out without quotation marks: "es kommt die Meldung: Speichern nicht möglich".
    private static readonly Regex SpokenErrorRx = new(
        @"\b(?:fehler)?meldung(?:en)?\s*[:,]\s*(?<t>[^.!?]{3,80})|\berror(?: message)?(?: says)?\s*[:,]\s*(?<t>[^.!?]{3,80})", Opts);
    // It works again.
    private static readonly Regex SuccessRx = new(
        @"\b(funktioniert (?:jetzt|wieder)|geht (?:jetzt|wieder)|klappt (?:jetzt|wieder)|jetzt (?:geht|funktioniert|klappt) es|sieht gut aus|ist (?:jetzt )?(?:behoben|erledigt)|ist jetzt (?:da|weg)|ist (?:da|weg)[.!]?$|" +
        @"works (?:now|again)|it's working|that (?:worked|fixed it)|is (?:fixed|gone|back) now)\b", Opts);
    // Instructions the click parser does not cover (it only knows pointer actions): open, go to, save, copy, …
    private static readonly Regex InstructionRx = new(
        @"\b(open|go to|navigate|save|copy|paste|export|install|launch|start|restart|press|hit|type|enter|select|choose|set|turn on|turn off|enable|disable|find|search|log ?in|log ?out|update|delete|" +
        @"öffne\w*|geh\w* (?:sie )?(?:auf|zu|in)|speicher\w*|kopier\w*|einfüg\w*|export\w*|installier\w*|start\w*|neu ?start\w*|drück\w*|tipp\w*|wähl\w*|stell\w* (?:sie )?\w+ ein|aktivier\w*|deaktivier\w*|such\w*|meld\w* (?:sie )?(?:sich )?(?:an|ab)|aktualisier\w*|lösch\w*|setz\w*|trag\w* (?:sie )?\w+ ein)\b", Opts);
    // Addressed to the customer ("klicken Sie", "you can"), not narration.
    private static readonly Regex AddressRx = new(
        @"\b(you|you'll|you're|your|just|simply|please|sie|ihr|du|man|bitte|mal)\b|^(open|go|click|press|select|choose|save|copy|type|öffnen|gehen|klicken|drücken|wählen|speichern)\b", Opts);
    // Not inside a word: the apostrophes of "can't … isn't" are no quotation marks.
    private static readonly Regex QuotedRx = new(@"(?<!\w)[„""“«‚'](?<t>[^""“”»'‘’„]{3,120})[""“”»'‘’](?!\w)", RegexOptions.Compiled);

    public WikiArticle Build(Transcript transcript, IReadOnlyList<AudioRef> refs, EventGraph? graph, string? language = null)
    {
        string lang = language is "de" or "en" ? language : transcript.Language == "de" ? "de" : "en";
        var sentences = TranscriptSentences.Split(transcript)
            .Where(s => !OutroRx.IsMatch(s.Text) && !IsSmallTalk(s, refs))
            .ToList();

        var steps = new List<ArticleStep>();
        var problem = new List<string>();
        string? verification = null, cause = null;
        ArticleStep? current = null;
        int detailCount = 0;

        foreach (var s in sentences)
        {
            var sRefs = RefsIn(refs, s.StartS, s.EndS).Where(r => r.Action != ActionType.Point).ToList();
            bool instruction = IsInstruction(s, sRefs);
            // After "it works again" nothing more belongs to the last step – unless the sentence is itself an instruction.
            if (!instruction && SuccessRx.IsMatch(s.Text) && steps.Count > 0) { verification = s.Text; current = null; continue; }
            // A cause explains the problem, so it comes after it: the first complaint is the problem even if it says "weil".
            bool isComplaint = steps.Count == 0 && problem.Count == 0 && ProblemRx.IsMatch(s.Text);
            if (cause is null && !isComplaint && sRefs.Count == 0 && CauseRx.IsMatch(s.Text) && !AddressRx.IsMatch(s.Text)) { cause = s.Text; continue; }
            // Before the solution starts, a complaint is the problem even if it contains an action word
            // ("ich kann nicht mehr drucken") – unless it is addressed to the customer ("klicken Sie …").
            if (steps.Count == 0 && ProblemRx.IsMatch(s.Text) && !AddressRx.IsMatch(s.Text)) instruction = false;
            if (steps.Count == 0 && !instruction)
            {
                if (problem.Count < 3 && (ProblemRx.IsMatch(s.Text) || problem.Count > 0 && problem.Count < 2)) problem.Add(s.Text);
                continue;
            }
            if (instruction)
            {
                current = new ArticleStep { Number = steps.Count + 1, Title = StepTitle(sRefs, lang), Instruction = Instruction(s, sRefs), TimeS = s.StartS, EndS = s.EndS };
                steps.Add(current);
                detailCount = 0;
            }
            else if (current is not null && detailCount < MaxDetailSentences && s.Text.Split(' ').Length <= 2 * MaxInstructionWords)
            {
                current.Details = current.Details is null ? s.Text : current.Details + " " + s.Text;
                current.EndS = s.EndS;
                detailCount++;
            }
        }
        if (problem.Count == 0) problem.AddRange(sentences.Where(s => !SmallTalkRx.IsMatch(s.Text) && s.Text != cause).Take(1).Select(s => s.Text));

        var a = new WikiArticle
        {
            Title = TitleFrom(problem.FirstOrDefault(), lang),
            Language = lang,
            Problem = problem.Count > 0 ? string.Join(" ", problem) : null,
            ErrorMessages = problem.SelectMany(ErrorMessagesIn).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Cause = cause,
            Steps = steps,
            Verification = verification,
            Resolved = verification is not null ? true : null,
            Keywords = refs.Where(r => r.ExplicitTarget is not null).Select(r => r.ExplicitTarget!).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
            SourceDurationS = graph?.Video.DurationS ?? transcript.Segments.LastOrDefault()?.EndS ?? 0,
            Method = "rule-based",
        };
        PlaceScreenshots(a, refs, graph);
        return a;
    }

    private static bool IsInstruction(Sentence s, List<AudioRef> sRefs) =>
        sRefs.Count > 0 || InstructionRx.IsMatch(s.Text) && AddressRx.IsMatch(s.Text);

    /// <summary>A short greeting, thanks or acknowledgement ("Super.", "Alles klar, danke.") – but not when the sentence
    /// goes on with something that matters: "Genau, klicken Sie auf Datei.", "Hallo, ich kann nicht drucken.".</summary>
    private static bool IsSmallTalk(Sentence s, IReadOnlyList<AudioRef> refs)
    {
        if (!SmallTalkRx.IsMatch(s.Text) || s.Text.Split(' ').Length > 8) return false;
        var sRefs = RefsIn(refs, s.StartS, s.EndS).Where(r => r.Action != ActionType.Point).ToList();
        return !IsInstruction(s, sRefs) && !ProblemRx.IsMatch(s.Text) && !SuccessRx.IsMatch(s.Text);
    }

    /// <summary>Quoted text, or what follows "Meldung:"/"error:" when the message was only read out.</summary>
    private static IEnumerable<string> ErrorMessagesIn(string sentence)
    {
        var quoted = QuotedRx.Matches(sentence).Select(m => m.Groups["t"].Value.Trim()).ToList();
        if (quoted.Count > 0) return quoted;
        var spoken = SpokenErrorRx.Match(sentence);
        return spoken.Success ? [TranscriptSentences.Clean(spoken.Groups["t"].Value.Trim(' ', ',', ':'))] : [];
    }

    private static IEnumerable<AudioRef> RefsIn(IReadOnlyList<AudioRef> refs, double start, double end) => refs.Where(r => InSpan(r, start, end));

    // Half-open: an instruction that starts exactly where this sentence ends belongs to the next one.
    private static bool InSpan(AudioRef r, double start, double end) => r.StartS >= start - 0.05 && r.StartS < end - 0.05;

    /// <summary>
    /// Chooses one screenshot moment per step from the step's own narration span [TimeS, EndS]:
    /// a reliable observed click inside the span (drawn as a marker), else the moment the instruction is spoken
    /// (deictic word / end of the action clause), else early in the first sentence. Moments are kept in video
    /// order and at least <paramref name="minGapS"/> apart, so two steps never show the same frame.
    /// </summary>
    public static void PlaceScreenshots(WikiArticle a, IReadOnlyList<AudioRef> refs, EventGraph? graph, double minGapS = 1.5)
    {
        var refById = refs.ToDictionary(r => r.Id);
        double last = double.NegativeInfinity;
        foreach (var s in a.Steps)
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

    /// <summary>A marker is only drawn for a click bound to an instruction spoken inside this step (not the next one),
    /// and only if that instruction is a click or the click visibly changed the screen.</summary>
    private static bool ReliableClick(GroundedEvent e, Dictionary<string, AudioRef> refById, double start, double end)
    {
        var own = e.AudioReferences.Select(x => refById.GetValueOrDefault(x.AudioRefId)).Where(r => r is not null && InSpan(r, start, end)).ToList();
        if (own.Count == 0) return false;
        bool spokenClick = own.Any(r => r!.Action is ActionType.Click or ActionType.DoubleClick or ActionType.RightClick);
        double change = e.Evidence.Where(x => x.Source == "frame_state_change").Select(x => x.Confidence).DefaultIfEmpty(0).Max();
        return spokenClick && e.OverallConfidence >= 0.5 || change >= 0.9;
    }

    /// <summary>Unpunctuated run-on sentences are cut down to the instruction clauses the parser found in them.</summary>
    private static string Instruction(Sentence s, List<AudioRef> refs)
    {
        if (s.Text.Split(' ').Length <= MaxInstructionWords || refs.Count == 0) return s.Text;
        var clauses = refs.Select(r => TranscriptSentences.Clean(r.Text)).Where(c => c.Length > 0).Distinct().ToList();
        string text = string.Join("; ", clauses.Select((c, i) => i == 0 ? c : char.ToLower(c[0]) + c[1..]));
        return text.Length == 0 ? s.Text : text + (Regex.IsMatch(text, @"[.!?]$") ? "" : ".");
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

    private static string TitleFrom(string? problem, string lang)
    {
        if (string.IsNullOrWhiteSpace(problem)) return lang == "de" ? "Lösung aus einem Support-Fall" : "Solution from a support case";
        return ArticleRenderer.FirstWords(problem.TrimEnd('.', '!', '?'), 12);
    }
}
