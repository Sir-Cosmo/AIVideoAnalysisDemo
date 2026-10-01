using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// Deterministic parser that turns word-aligned ASR output into <see cref="AudioRef"/>s.
/// It deliberately does NOT try to resolve "hier"/"here" linguistically – it only
/// records the action type, whether the reference is deictic, an explicit target if
/// one is spoken, and precise word-level timing so the fusion stage can look for
/// a real visual event around the utterance.
/// </summary>
public sealed class AudioRefParser
{
    // Verb lemmas → action. Order matters: more specific first (Doppelklick before Klick).
    private static readonly (Regex Pattern, ActionType Action)[] ActionPatterns =
    {
        (Rx(@"doppelklick\w*|double[- ]?click\w*"), ActionType.DoubleClick),
        (Rx(@"rechtsklick\w*|right[- ]?click\w*|rechte[rn]? maustaste"), ActionType.RightClick),
        (Rx(@"klick\w*|anklick\w*|click\w*|dr[üu]ck\w*|press\w*"), ActionType.Click),
        (Rx(@"zieh\w*|ziehen|drag\w*|verschieb\w*"), ActionType.Drag),
        (Rx(@"tipp\w* (auf|hier|dort|da|darauf)|antipp\w*|tap\w*|touch\w*"), ActionType.Tap),
        (Rx(@"eingeb\w*|eintipp\w*|eintrag\w*|geben sie .{0,40}\bein\b|type\w*|enter\b"), ActionType.Type),
        (Rx(@"scroll\w*|blätter\w*"), ActionType.Scroll),
        (Rx(@"w[äa]hl\w*|ausw[äa]hl\w*|markier\w*|select\w*|choose\w*"), ActionType.Select),
        (Rx(@"zeig\w*|point\w*|hinweis\w*|sehen sie|look at|show\w*"), ActionType.Point),
    };

    private static readonly Regex DeicticRx = Rx(@"\b(hier|hierhin|hierher|dort|dorthin|da|dies\w*|das da|diese[rsnm]?|here|there|this|that one|that)\b");

    // Explicit target: „Speichern“, "Save", 'OK', or `auf <Capitalized words>` / `on <Capitalized words>`
    private static readonly Regex QuotedTargetRx = Rx(@"[„""“'‚](?<t>[^""“”'‘’]{1,40})[""“”'‘’]");
    private static readonly Regex PrepTargetRx = new(@"\b(?:auf|on|onto|den|die|das|the)\s+(?:(?:die|den|das|the)\s+)?(?:Schaltfläche|Button|Knopf|Reiter|Tab|Menü|Menu|Icon|Symbol|Eintrag|Link|Feld|button|tab|icon|entry|field)?\s*(?<t>[A-ZÄÖÜ][\wÄÖÜäöüß\-]*(?:\s+[A-ZÄÖÜ][\wÄÖÜäöüß\-]*){0,3})", RegexOptions.Compiled);

    // Sequencing markers split one utterance into ordered sub-actions.
    private static readonly Regex SequenceSplitRx = Rx(@"\b(und dann|dann|anschließend|danach|zuerst|zunächst|als nächstes|and then|then|after that|next|first)\b|[;,]|\.\s");

    private static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<AudioRef> Parse(Transcript transcript)
    {
        var refs = new List<AudioRef>();
        int counter = 0;
        foreach (var seg in transcript.Segments)
        {
            if (seg.Words.Count == 0) continue;
            foreach (var clause in SplitIntoClauses(seg))
            {
                var action = DetectAction(clause.Text);
                if (action == ActionType.Unknown) continue;

                var deicticWord = clause.Words.FirstOrDefault(w => DeicticRx.IsMatch(w.Text));
                var target = DetectExplicitTarget(clause.Text);
                // „auf hier“ is not a target; „das“ as an article before a target is handled by PrepTargetRx.
                if (target is not null && DeicticRx.IsMatch(target) && target.Split(' ').Length == 1) target = null;

                double anchor = deicticWord?.EndS ?? clause.Words[^1].EndS;
                double asrConf = clause.Words.Average(w => w.Score);
                double alignConf = clause.Words.All(w => w.EndS > w.StartS) ? 1.0 : 0.5;

                refs.Add(new AudioRef
                {
                    Id = $"audio_{counter++:D4}",
                    StartS = clause.Words[0].StartS,
                    EndS = clause.Words[^1].EndS,
                    AnchorS = anchor,
                    Text = clause.Text.Trim(),
                    Action = action,
                    Deictic = deicticWord is not null,
                    ExplicitTarget = target,
                    Ordinal = clause.Ordinal,
                    SpeakerId = seg.Speaker ?? clause.Words[0].Speaker,
                    AsrConfidence = Math.Round(asrConf, 3),
                    AlignmentConfidence = alignConf,
                });
            }
        }
        return refs;
    }

    public static ActionType DetectAction(string text)
    {
        foreach (var (pattern, action) in ActionPatterns)
            if (pattern.IsMatch(text)) return action;
        return ActionType.Unknown;
    }

    public static string? DetectExplicitTarget(string text)
    {
        var q = QuotedTargetRx.Match(text);
        if (q.Success) return q.Groups["t"].Value.Trim();
        var p = PrepTargetRx.Match(text);
        if (p.Success)
        {
            var t = p.Groups["t"].Value.Trim();
            // Drop polite pronoun "Sie" etc. that are capitalised in German.
            if (t is "Sie" or "Ihnen" or "Ihre" or "Ihr") return null;
            return t;
        }
        return null;
    }

    private sealed record Clause(string Text, List<Word> Words, int Ordinal);

    /// <summary>
    /// Splits a segment into ordered clauses on sequencing markers while preserving word timing.
    /// Splitting is done on the word list so each clause keeps exact timestamps.
    /// </summary>
    private static IEnumerable<Clause> SplitIntoClauses(TranscriptSegment seg)
    {
        var current = new List<Word>();
        int ordinal = 0;
        var words = seg.Words;
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var token = w.Text.Trim();
            bool isMarker = SequenceSplitRx.IsMatch(" " + token.TrimEnd(',', ';', '.') + " ")
                            && Regex.IsMatch(token, @"^(und|dann|anschließend|danach|zuerst|zunächst|then|next|first|after)\W*$", RegexOptions.IgnoreCase);
            bool endsClause = token.EndsWith(',') || token.EndsWith(';') || token.EndsWith('.');

            if (isMarker)
            {
                // "und dann": the "und" belongs to the marker if followed by "dann".
                if (Regex.IsMatch(token, "^und\\W*$", RegexOptions.IgnoreCase) &&
                    !(i + 1 < words.Count && Regex.IsMatch(words[i + 1].Text, "^(dann|anschließend|danach)", RegexOptions.IgnoreCase)))
                {
                    current.Add(w);
                    continue;
                }
                if (current.Count > 0 && AudioRefParser.DetectAction(JoinWords(current)) != ActionType.Unknown)
                {
                    yield return new Clause(JoinWords(current), current, ordinal++);
                    current = new List<Word>();
                }
                continue; // marker itself is not part of the clause text
            }

            current.Add(w);
            if (endsClause && DetectAction(JoinWords(current)) != ActionType.Unknown)
            {
                yield return new Clause(JoinWords(current), current, ordinal++);
                current = new List<Word>();
            }
        }
        if (current.Count > 0)
            yield return new Clause(JoinWords(current), current, ordinal);
    }

    private static string JoinWords(IEnumerable<Word> words) => string.Join(' ', words.Select(w => w.Text.Trim()));
}
