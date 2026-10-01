using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvAg.Core;

namespace AvAg.Pipeline.Manuals;

/// <summary>
/// Writes the manual with any <see cref="ITextGenerator"/>. The model reads the whole transcript as numbered sentences
/// and returns JSON; every step names the sentences it comes from. Those numbers – not times the model invents – place
/// each step in the video, so screenshots and click markers stay measured.
/// Prompt construction (<see cref="BuildRequest"/>) and answer parsing (<see cref="Parse"/>) are pure functions and
/// unit-tested without a model.
/// </summary>
public sealed class LlmManualWriter : IManualWriter
{
    private readonly ITextGenerator _llm;
    public LlmManualWriter(ITextGenerator llm) => _llm = llm;

    public string Name => "llm:" + _llm.Name;

    public async Task<Manual> WriteAsync(Manual draft, IReadOnlyList<ManualBuilder.Sentence> sentences, CancellationToken ct = default)
    {
        if (sentences.Count == 0) throw new InvalidOperationException("no speech in the video");
        var answer = await _llm.GenerateAsync(BuildRequest(draft.Language, sentences), ct);
        var m = Parse(answer, draft, sentences);
        m.Method = Name;
        return m;
    }

    // ---- prompt ------------------------------------------------------------------------------------------------------
    public static TextGenerationRequest BuildRequest(string language, IReadOnlyList<ManualBuilder.Sentence> sentences)
    {
        bool de = language == "de";
        var transcript = new StringBuilder();
        for (int i = 0; i < sentences.Count; i++)
            transcript.Append(CultureInfo.InvariantCulture, $"[{i + 1}] ({ManualRenderer.Ts(sentences[i].StartS)}) {sentences[i].Text}\n");

        string system = de
            ? "Du schreibst aus dem Transkript eines Bildschirm-Tutorials eine sachliche Schritt-für-Schritt-Anleitung auf Deutsch (Sie-Form, Imperativ). Programm-, Menü- und Schaltflächennamen bleiben so, wie sie im Transkript stehen (nicht übersetzen). Lass Füllwörter, Werbung und Abschiedsfloskeln weg und erfinde nichts, was nicht im Transkript steht."
            : "You turn the transcript of a screen-recording tutorial into a factual step-by-step manual in English (imperative mood). Keep program, menu and button names exactly as in the transcript. Drop filler, self-promotion and sign-offs, and do not invent anything that is not in the transcript.";
        string rules = de
            ? """
              Schreibe die Anleitung für das GANZE Video, vom ersten bis zum letzten Satz:
              - Jeder Schritt ist GENAU EINE Handlung, die der Zuschauer selbst ausführt (Taste, Klick, Auswahl, Eingabe, Speichern, Exportieren …), in der Reihenfolge des Videos. Lass keine Handlung aus und fasse nicht mehrere Handlungen in einen Schritt.
              - "instruction" ist ein kurzer Satz im Imperativ (Sie-Form). Tastenkombinationen schreibst du als "Strg + C" bzw. "Windows-Taste + Umschalt + R".
              - "details" sagt, was danach auf dem Bildschirm passiert oder warum der Schritt nötig ist – nur wenn es im Transkript steht, sonst null.
              - Zeigt das Video mehrere Wege (z. B. Tastenkürzel und ein Programm), gib jedem Weg einen "section"-Namen (z. B. "Methode 1: Tastenkürzel"); aufeinanderfolgende Schritte desselben Wegs haben denselben Namen. Gibt es nur einen Weg, ist "section" null.
              - Programme oder Wege, die nur erwähnt und nicht gezeigt werden, gehören in "tips".
              - "sentences" nennt die Nummern der Transkriptsätze, in denen der Schritt gesagt bzw. gezeigt wird.
              """
            : """
              Write the manual for the WHOLE video, from the first to the last sentence:
              - Each step is EXACTLY ONE action the viewer performs (key press, click, selection, typing, saving, exporting …), in the order of the video. Do not leave out any action and do not merge several actions into one step.
              - "instruction" is one short imperative sentence. Write key combinations as "Ctrl + C" or "Windows key + Shift + R".
              - "details" says what happens on screen afterwards or why the step is needed – only if the transcript says so, otherwise null.
              - If the video shows several ways (e.g. a shortcut and a program), give each way a "section" name (e.g. "Method 1: Keyboard shortcut"); consecutive steps of the same way share it. If there is only one way, "section" is null.
              - Programs or ways that are only mentioned but not shown go into "tips".
              - "sentences" lists the numbers of the transcript sentences in which the step is said or shown.
              """;
        string user = $$"""
            {{(de ? "Transkript, nummerierte Sätze [Nummer] (Zeit):" : "Transcript, numbered sentences [number] (time):")}}
            {{transcript}}
            {{rules}}
            {{(de ? "Antworte NUR mit JSON in genau diesem Format:" : "Respond ONLY with JSON in exactly this format:")}}
            {"title": "{{(de ? "kurzer Titel" : "short title")}}", "summary": "{{(de ? "2-3 Sätze: was man am Ende erreicht hat" : "2-3 sentences: what the viewer will have achieved")}}", "prerequisites": [],
             "steps": [{"section": null, "title": "{{(de ? "kurze Handlung" : "short action")}}", "instruction": "{{(de ? "Klicken Sie …" : "Click …")}}", "details": null, "sentences": [1]}],
             "tips": []}
            """;
        return new TextGenerationRequest(system, user, Json: true, MaxTokens: 3000, Temperature: 0.2);
    }

    // ---- answer ------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Turns the model's JSON answer into a manual whose steps carry the time span of their transcript sentences.
    /// Throws <see cref="InvalidOperationException"/> when the answer is unusable (no JSON, fewer than 2 steps, or
    /// fewer than half of the steps tied to real sentences).
    /// </summary>
    public static Manual Parse(string answer, Manual draft, IReadOnlyList<ManualBuilder.Sentence> sentences)
    {
        int a = answer.IndexOf('{'), z = answer.LastIndexOf('}');
        if (a < 0 || z <= a) throw new InvalidOperationException("answer is not JSON");
        JsonNode j;
        try { j = JsonNode.Parse(answer[a..(z + 1)]) ?? throw new InvalidOperationException("empty JSON"); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("answer is not valid JSON: " + ex.Message); }

        var m = new Manual
        {
            Title = Str(j["title"]) ?? draft.Title, Language = draft.Language, Summary = Str(j["summary"]) ?? draft.Summary,
            Prerequisites = Strs(j["prerequisites"]).Where(p => !Regex.IsMatch(p, @"^(keine|none|no special|nothing|n/a)", RegexOptions.IgnoreCase)).ToList(),
            Tips = Strs(j["tips"]),
            VideoName = draft.VideoName, VideoDurationS = draft.VideoDurationS,
        };
        int anchored = 0;
        double prevEnd = 0;
        foreach (var s in j["steps"] as JsonArray ?? new JsonArray())
        {
            var instruction = Str(s?["instruction"]);
            if (instruction is null) continue;
            var ids = (s?["sentences"] as JsonArray ?? new JsonArray()).Select(Num)
                        .Where(n => n >= 1 && n <= sentences.Count).Select(n => (int)n!.Value - 1).Distinct().ToList();
            if (ids.Count > 0) anchored++;
            double start = ids.Count > 0 ? ids.Min(i => sentences[i].StartS) : prevEnd;
            double end = ids.Count > 0 ? ids.Max(i => sentences[i].EndS) : prevEnd;
            m.Steps.Add(new ManualStep { Section = Str(s?["section"]), Title = Str(s?["title"]), Instruction = instruction, Details = Str(s?["details"]), TimeS = start, EndS = end });
            prevEnd = end;
        }
        if (m.Steps.Count < 2 || anchored * 2 < m.Steps.Count)
            throw new InvalidOperationException($"{m.Steps.Count} steps, {anchored} tied to the transcript");

        // Video order (a stable sort keeps the model's order for steps from the same sentence).
        m.Steps = m.Steps.OrderBy(x => x.TimeS).ToList();
        for (int i = 0; i < m.Steps.Count; i++) m.Steps[i].Number = i + 1;
        return m;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s != "null" ? s.Trim() : null;
    private static List<string> Strs(JsonNode? n) => n is JsonArray arr ? arr.Select(Str).Where(x => x is not null).Select(x => x!).ToList() : new();
    private static double? Num(JsonNode? n) => n is not JsonValue v ? null
        : v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<string>(out var str) && double.TryParse(str.Trim().Trim('[', ']'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
}
