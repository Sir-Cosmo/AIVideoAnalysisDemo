using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AvAg.Core;

// ---------------------------------------------------------------------------
// Step-by-step manual ("Anleitung") derived from a tutorial video: what the viewer has to do to repeat what the
// presenter does. Built from the transcript (what is said) and the event graph (where it was clicked).
// ---------------------------------------------------------------------------
public sealed class Manual
{
    public required string Title { get; set; }
    public required string Language { get; set; }              // "de" | "en" – language of the fixed headings
    public string? Summary { get; set; }
    public List<string> Prerequisites { get; set; } = new();
    public List<ManualStep> Steps { get; set; } = new();
    public List<string> Tips { get; set; } = new();
    /// <summary>"extractive" (rule-based, from the transcript) or "llm:&lt;model&gt;".</summary>
    public string Method { get; set; } = "extractive";
    public string? VideoName { get; set; }
    public double VideoDurationS { get; set; }
    /// <summary>Private video: the manual is text only – no frame of the video is extracted or embedded.</summary>
    public bool Private { get; set; }
}

public sealed class ManualStep
{
    public int Number { get; set; }
    /// <summary>Part of the video the step belongs to, e.g. "Methode 1: Tastenkürzel" – consecutive steps share it.</summary>
    public string? Section { get; set; }
    public string? Title { get; set; }
    public required string Instruction { get; set; }
    public string? Details { get; set; }
    /// <summary>Where in the video this step is shown (start and end of the narration that belongs to it).</summary>
    public double TimeS { get; set; }
    public double EndS { get; set; }
    /// <summary>Frame used for the screenshot; null = no screenshot.</summary>
    public double? ScreenshotS { get; set; }
    /// <summary>Observed/tracked click position (source pixels) – drawn as a marker on the screenshot.</summary>
    public int[]? PointXyPx { get; set; }
    public int[]? BboxXyxyPx { get; set; }
    [JsonIgnore] public byte[]? ScreenshotJpeg { get; set; }
    public int ScreenshotWidth { get; set; }
    public int ScreenshotHeight { get; set; }
}

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

/// <summary>Markdown and self-contained HTML (screenshots embedded) for a <see cref="Manual"/>.</summary>
public static class ManualRenderer
{
    private sealed record L(string Overview, string Before, string Steps, string Tips, string Shown, string Generated, string Rule, string Llm, string Private);
    private static L Labels(string lang) => lang == "de"
        ? new("Überblick", "Voraussetzungen", "Schritt für Schritt", "Hinweise", "Im Video bei", "Automatisch aus dem Video erstellt", "regelbasiert aus der Tonspur", "mit Sprachmodell zusammengefasst", "privates Video, nur Text")
        : new("Overview", "Before you start", "Step by step", "Tips", "Shown in the video at", "Generated automatically from the video", "rule-based from the narration", "summarised by a language model", "private video, text only");

    public static string Markdown(Manual m, Func<ManualStep, string?>? imagePath = null)
    {
        var l = Labels(m.Language);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(m.Title).AppendLine();
        sb.Append('_').Append(l.Generated).Append(m.VideoName is null ? "" : $" ({m.VideoName})").Append(" · ")
          .Append(m.Method.StartsWith("llm") ? l.Llm : l.Rule).Append(m.Private ? " · " + l.Private : "").AppendLine("_").AppendLine();
        if (m.Summary is not null) sb.Append("## ").AppendLine(l.Overview).AppendLine().AppendLine(m.Summary).AppendLine();
        if (m.Prerequisites.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Before).AppendLine();
            foreach (var p in m.Prerequisites) sb.Append("- ").AppendLine(p);
            sb.AppendLine();
        }
        sb.Append("## ").AppendLine(l.Steps).AppendLine();
        bool sections = HasSections(m);
        string? section = null;
        foreach (var s in m.Steps)
        {
            if (sections && s.Section != section) { section = s.Section; if (section is not null) sb.Append("### ").AppendLine(section).AppendLine(); }
            sb.Append(sections ? "#### " : "### ").Append(s.Number).Append(". ").AppendLine(s.Title ?? FirstWords(s.Instruction)).AppendLine();
            sb.AppendLine(s.Instruction).AppendLine();
            if (s.Details is not null) sb.AppendLine(s.Details).AppendLine();
            if (imagePath?.Invoke(s) is { } img) sb.Append("![").Append(s.Number).Append("](").Append(img).AppendLine(")").AppendLine();
            sb.Append('_').Append(l.Shown).Append(' ').Append(Ts(s.TimeS)).AppendLine("_").AppendLine();
        }
        if (m.Tips.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Tips).AppendLine();
            foreach (var t in m.Tips) sb.Append("- ").AppendLine(t);
        }
        return sb.ToString();
    }

    public static string Html(Manual m)
    {
        var l = Labels(m.Language);
        // Only the HTML-significant characters: WebUtility.HtmlEncode would also turn every umlaut into an entity.
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="{m.Language}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(m.Title)}</title>
            <style>
              :root {"{"} --bg:#fff; --text:#1d2330; --muted:#5b6475; --line:#d9dde5; --accent:#1f7a63; --card:#f6f7f9; {"}"}
              @media (prefers-color-scheme: dark) {"{"} :root:not([data-theme=light]) {"{"} --bg:#1c2230; --text:#eceff4; --muted:#98a2b8; --line:#3a4559; --accent:#45c9a5; --card:#262e3d; {"}"} {"}"}
              body {"{"} margin:0; background:var(--bg); color:var(--text); font:16px/1.55 "Segoe UI", system-ui, sans-serif; {"}"}
              main {"{"} max-width:860px; margin:0 auto; padding:2rem 1rem 4rem; {"}"}
              h1 {"{"} font-size:1.8rem; margin:0 0 .25rem; line-height:1.2; {"}"}
              .meta {"{"} color:var(--muted); font-size:.9rem; margin:0 0 2rem; {"}"}
              h2 {"{"} font-size:1.2rem; margin:2rem 0 .75rem; padding-bottom:.3rem; border-bottom:1px solid var(--line); {"}"}
              ol.steps {"{"} list-style:none; padding:0; margin:0; counter-reset:step; {"}"}
              ol.steps > li {"{"} display:grid; grid-template-columns:2.25rem 1fr; gap:.25rem 1rem; padding:1.1rem 0; border-bottom:1px solid var(--line); break-inside:avoid; {"}"}
              .num {"{"} width:2rem; height:2rem; border-radius:50%; background:var(--accent); color:var(--bg); display:grid; place-items:center; font-weight:600; {"}"}
              .step h3 {"{"} margin:.15rem 0 .35rem; font-size:1.05rem; {"}"}
              .step p {"{"} margin:.25rem 0; {"}"}
              .details {"{"} color:var(--muted); {"}"}
              figure {"{"} margin:.75rem 0 0; {"}"}
              figure img {"{"} max-width:100%; height:auto; border:1px solid var(--line); border-radius:6px; display:block; {"}"}
              figcaption, .time {"{"} color:var(--muted); font-size:.85rem; margin-top:.3rem; {"}"}
              .summary {"{"} background:var(--card); border-radius:6px; padding:.9rem 1rem; {"}"}
              h3.section {"{"} font-size:1.05rem; color:var(--accent); margin:1.75rem 0 0; {"}"}
              kbd {"{"} font:600 .85em "Segoe UI", system-ui, sans-serif; background:var(--card); border:1px solid var(--line); border-bottom-width:2px; border-radius:4px; padding:.05rem .4rem; white-space:nowrap; {"}"}
              @media print {"{"} body {"{"} background:#fff; color:#000; {"}"} main {"{"} padding:0; {"}"} {"}"}
            </style>
            </head>
            <body><main>
            <h1>{E(m.Title)}</h1>
            <p class="meta">{E(l.Generated)}{(m.VideoName is null ? "" : $" ({E(m.VideoName)})")} · {E(m.Method.StartsWith("llm") ? l.Llm : l.Rule)}{(m.Private ? " · " + E(l.Private) : "")}</p>

            """);
        if (m.Summary is not null) sb.Append($"<h2>{E(l.Overview)}</h2>\n<p class=\"summary\">{E(m.Summary)}</p>\n");
        if (m.Prerequisites.Count > 0)
            sb.Append($"<h2>{E(l.Before)}</h2>\n<ul>").Append(string.Concat(m.Prerequisites.Select(p => $"<li>{E(p)}</li>"))).Append("</ul>\n");
        sb.Append($"<h2>{E(l.Steps)}</h2>\n<ol class=\"steps\">\n");
        bool sections = HasSections(m);
        string? section = null;
        foreach (var s in m.Steps)
        {
            // A new section closes the list and continues the numbering after the heading.
            if (sections && s.Section != section)
            {
                section = s.Section;
                if (section is not null) sb.Append($"</ol>\n<h3 class=\"section\">{E(section)}</h3>\n<ol class=\"steps\" start=\"{s.Number}\">\n");
            }
            sb.Append($"<li><span class=\"num\">{s.Number}</span><div class=\"step\">");
            sb.Append($"<h3>{E(s.Title ?? FirstWords(s.Instruction))}</h3><p>{Keys(E(s.Instruction))}</p>");
            if (s.Details is not null) sb.Append($"<p class=\"details\">{Keys(E(s.Details))}</p>");
            if (s.ScreenshotJpeg is { Length: > 0 } img)
                sb.Append($"<figure><img alt=\"{E(m.Language == "de" ? $"Bildschirm bei Schritt {s.Number}" : $"Screen at step {s.Number}")}\" width=\"{s.ScreenshotWidth}\" height=\"{s.ScreenshotHeight}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(img)}\"><figcaption>{E(l.Shown)} {Ts(s.ScreenshotS ?? s.TimeS)}</figcaption></figure>");
            else sb.Append($"<p class=\"time\">{E(l.Shown)} {Ts(s.TimeS)}</p>");
            sb.Append("</div></li>\n");
        }
        sb.Append("</ol>\n");
        if (m.Tips.Count > 0)
            sb.Append($"<h2>{E(l.Tips)}</h2>\n<ul>").Append(string.Concat(m.Tips.Select(t => $"<li>{E(t)}</li>"))).Append("</ul>\n");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    /// <summary>Sections are only shown when the model split the video into at least two parts (e.g. two methods).</summary>
    public static bool HasSections(Manual m) => m.Steps.Select(s => s.Section).Where(s => s is not null).Distinct().Count() >= 2;

    // Key combinations such as "Windows key + Shift + R" or "Strg + C": each key becomes a <kbd> (HTML) or bold run (Word).
    private const string NamedKey =
        @"(?:Ctrl|Strg|Control|AltGr|Alt|Shift|Umschalt|Cmd|Command|Win(?:dows)?(?:[- ]?(?:key|Taste|logo key))?|Enter|Eingabe(?:taste)?|Return|Tab|Esc|Escape|Entf|Del|Delete|Space|Leertaste|Backspace|Pos1|F\d{1,2})";
    private const string KeyToken = $@"(?:{NamedKey}|[A-Z0-9])";
    // Starts with a named key so that "1 + 2" or "A + B" in ordinary text is left alone.
    public static readonly Regex KeyComboRx = new($@"\b{NamedKey}(?:\s*\+\s*{KeyToken})+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Keys(string escaped) =>
        KeyComboRx.Replace(escaped, m => string.Join(" + ", Regex.Split(m.Value, @"\s*\+\s*").Select(k => $"<kbd>{k}</kbd>")));

    public static string FirstWords(string s, int n = 8)
    {
        var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var t = string.Join(" ", w.Take(n)).TrimEnd(',', '.', ';', ':');
        return w.Length > n ? t + " …" : t;
    }

    public static string Ts(double s) { var t = TimeSpan.FromSeconds(s); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }
}
