using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvAg.Core;

namespace AvAg.Pipeline.Articles;

/// <summary>
/// Writes the wiki article with any <see cref="ITextGenerator"/>. The model gets
/// <list type="bullet">
/// <item>the whole support call as numbered sentences (with speaker labels when the recogniser provides them),</item>
/// <item>the clicks observed in the video,</item>
/// <item>for models that read images and non-private videos: frames of the supporter's screen at the moments that matter,</item>
/// </list>
/// and returns JSON: problem, error messages, cause, solution steps, verification, notes and search keywords. Every step
/// names the sentences it comes from – those numbers, not times the model invents, place each step in the video, so
/// screenshots and click markers stay measured. Prompt construction (<see cref="BuildRequest"/>) and answer parsing
/// (<see cref="Parse"/>) are pure and unit-tested.
/// </summary>
public sealed class LlmArticleWriter : IArticleWriter, Services.IReportsFallback
{
    private readonly ITextGenerator _llm;
    public LlmArticleWriter(ITextGenerator llm) => _llm = llm;

    public string Name => "llm:" + _llm.Name;
    public bool WantsScreens => _llm.SupportsImages;
    public IReadOnlyList<string> FallbackNotes => _llm is Services.IReportsFallback f ? f.FallbackNotes : [];

    public async Task<WikiArticle> WriteAsync(ArticleWriterInput input, CancellationToken ct = default)
    {
        if (input.Sentences.Count == 0) throw new InvalidOperationException("no speech in the video");
        var request = BuildRequest(input.Draft.Language, input.Sentences, input.ObservedActions, _llm.SupportsImages ? input.Screens : []);
        var answer = await _llm.GenerateAsync(request, ct);
        var a = Parse(answer, input.Draft, input.Sentences);
        a.Method = Name;   // after the call: with a fallback chain this is the model that actually answered
        return a;
    }

    // ---- prompt ------------------------------------------------------------------------------------------------------
    public static TextGenerationRequest BuildRequest(string language, IReadOnlyList<Sentence> sentences,
                                                     IReadOnlyList<string>? observedActions = null, IReadOnlyList<PromptImage>? screens = null)
    {
        bool de = language == "de";
        observedActions ??= [];
        screens ??= [];
        var transcript = new StringBuilder();
        for (int i = 0; i < sentences.Count; i++)
            transcript.Append(CultureInfo.InvariantCulture,
                $"[{i + 1}] ({ArticleRenderer.Ts(sentences[i].StartS)}) {(sentences[i].Speaker is { } sp ? sp + ": " : "")}{sentences[i].Text}\n");

        string system = de
            ? """
              Du bist erfahrene technische Redakteurin im Kundensupport eines Softwareherstellers. Du bekommst einen aufgezeichneten Support-Anruf: Ein Kunde schildert ein Problem, ein Supporter löst es, meist per Bildschirmfreigabe oder Fernwartung.
              Daraus schreibst du einen Artikel für die Wissensdatenbank (Wiki), mit dem ein anderer Kunde dasselbe Problem künftig ohne Anruf selbst lösen kann.
              - Schreibe sachlich und präzise auf Deutsch, in der Sie-Form, allgemein gültig – keine Nacherzählung des Gesprächs.
              - Erfinde nichts. Verwende nur, was im Gespräch gesagt oder auf den Bildern zu sehen ist.
              - KEINE personenbezogenen Daten – auch nicht, wenn sie auf den Bildern zu sehen sind: keine Namen von Personen oder Firmen des Kunden, keine Telefonnummern, E-Mail-Adressen, Kunden-, Lizenz-, Vertrags- oder Belegnummern, Adressen, Passwörter. Schreibe allgemein („Ihre Kundennummer“).
              - Programm-, Menü-, Register- und Schaltflächennamen sowie Fehlermeldungen übernimmst du wörtlich und in Anführungszeichen („Speichern“).
              """
            : """
              You are an experienced technical writer in the customer support of a software company. You get a recorded support call: a customer describes a problem and a supporter solves it, usually via screen sharing or remote control.
              From it you write a knowledge-base (wiki) article that lets another customer solve the same problem themselves, without calling.
              - Write factually and precisely in English, addressed to the reader ("you"), generally valid – not a retelling of the call.
              - Do not invent anything. Use only what is said in the call or visible in the images.
              - NO personal data – not even if it is visible in the images: no names of people or of the customer's company, no phone numbers, e-mail addresses, customer, licence, contract or document numbers, addresses, passwords. Write generally ("your customer number").
              - Keep program, menu, tab and button names and error messages exactly as they are, in quotation marks ("Save").
              """;
        string rules = de
            ? """
              Regeln:
              - Zeilen können mit einem Sprecher-Label beginnen (SPEAKER_A, SPEAKER_00 …). Ohne Label erkennst du am Inhalt, wer Kunde und wer Supporter ist. Wenn der Supporter per Fernwartung selbst klickt, beschreibst du es als Schritt, den der Kunde ausführt.
              - "problem": was der Kunde erlebt (Symptome, in welchem Programmteil, wann), allgemein formuliert. "error_messages": Fehlermeldungen wörtlich (aus dem Gespräch oder von den Bildern).
              - "cause": die Ursache, wenn sie im Gespräch klar wird, sonst null. "applies_to": Produkt/Modul/Version, falls genannt oder sichtbar, sonst null.
              - "steps": NUR der Weg, der das Problem am Ende gelöst hat, in der Reihenfolge, wie der Kunde ihn selbst ausführt – GENAU EINE Handlung pro Schritt, kurzer Imperativsatz („Klicken Sie …“). Lass Fehlversuche, Rückfragen und Diagnose weg, die nicht zur Lösung gehören. Nenne Menüpfade vollständig („Datei › Einstellungen › Profil“), Tastenkombinationen als "Strg + C". Statt „hier“ oder „dort“ nennst du das Element – der Leser sieht das Video nicht.
              - "details": was danach auf dem Bildschirm passiert oder was man beachten muss – nur wenn bekannt, sonst null.
              - "actor": "customer", wenn der Kunde den Schritt selbst machen kann; "support", wenn nur der Support es kann (z. B. Lizenz, Server, Konto, Datenbank).
              - "section": nur wenn es mehrere alternative Lösungswege gibt (z. B. "Variante 1: …"), sonst null.
              - "sentences": die Nummern der Transkriptsätze, in denen der Schritt gesagt bzw. gezeigt wird (mindestens eine).
              - "verification": woran man erkennt, dass das Problem gelöst ist. "notes": Hinweise, z. B. wann man trotzdem den Support kontaktieren sollte.
              - "keywords": 3–8 Suchbegriffe, wie ein Kunde sie eintippen würde (inkl. Fehlermeldung). "resolved": ob das Problem im Gespräch gelöst wurde.
              """
            : """
              Rules:
              - Lines may start with a speaker label (SPEAKER_A, SPEAKER_00 …). Without labels, tell customer and supporter apart by what they say. If the supporter clicks via remote control, write it as a step the customer performs.
              - "problem": what the customer experiences (symptoms, where in the program, when), written generally. "error_messages": error messages verbatim (from the call or from the images).
              - "cause": the cause if the call makes it clear, otherwise null. "applies_to": product/module/version if mentioned or visible, otherwise null.
              - "steps": ONLY the way that finally solved the problem, in the order the customer would do it – EXACTLY ONE action per step, one short imperative sentence ("Click …"). Leave out failed attempts, questions and diagnosis that are not part of the solution. Give menu paths in full ("File › Settings › Profile"), key combinations as "Ctrl + C". Instead of "here" or "there", name the element – the reader does not see the video.
              - "details": what happens on screen afterwards or what to watch out for – only if known, otherwise null.
              - "actor": "customer" if the customer can do the step; "support" if only support can (e.g. licence, server, account, database).
              - "section": only when there are alternative ways to solve it (e.g. "Option 1: …"), otherwise null.
              - "sentences": the numbers of the transcript sentences in which the step is said or shown (at least one).
              - "verification": how to tell the problem is solved. "notes": caveats, e.g. when to contact support anyway.
              - "keywords": 3–8 search terms a customer would type (incl. the error message). "resolved": whether the problem was solved in the call.
              """;
        var context = new StringBuilder();
        if (observedActions.Count > 0)
        {
            context.AppendLine(de ? "Im Video beobachtete Klicks (Zeit, Ziel):" : "Clicks observed in the video (time, target):");
            foreach (var line in observedActions) context.AppendLine(line);
        }
        if (screens.Count > 0)
            context.AppendLine(de
                ? $"Dazu {screens.Count} Bilder vom Bildschirm des Supporters, jeweils mit Zeitangabe. Nutze sie, um Programmteile, Menüs, Schaltflächen und Fehlermeldungen genau zu benennen. Personenbezogene Daten auf den Bildern übernimmst du nicht."
                : $"Also {screens.Count} images of the supporter's screen, each with its time. Use them to name program parts, menus, buttons and error messages exactly. Do not copy personal data visible in the images.");

        string user = $$"""
            {{(de ? "Transkript, nummerierte Sätze [Nummer] (Zeit):" : "Transcript, numbered sentences [number] (time):")}}
            {{transcript}}
            {{context}}
            {{rules}}
            {{(de ? "Antworte NUR mit JSON in genau diesem Format:" : "Respond ONLY with JSON in exactly this format:")}}
            {"title": "{{(de ? "kurzer, problemorientierter Titel, wie ein Kunde danach suchen würde" : "short, problem-oriented title, as a customer would search for it")}}", "problem": "...", "error_messages": [], "cause": null, "applies_to": null,
             "steps": [{"section": null, "title": "{{(de ? "kurze Handlung" : "short action")}}", "instruction": "{{(de ? "Klicken Sie …" : "Click …")}}", "details": null, "actor": "customer", "sentences": [1]}],
             "verification": null, "notes": [], "keywords": [], "resolved": true}
            """;
        return new TextGenerationRequest(system, user, Json: true, MaxTokens: 4000, Temperature: 0.2) { Images = screens };
    }

    // ---- answer ------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Turns the model's JSON answer into an article whose steps carry the time span of their transcript sentences.
    /// Facts the model left empty (cause, verification, error messages, keywords) are taken from the rule-based draft.
    /// Throws <see cref="InvalidOperationException"/> when the answer is unusable: no JSON, no steps although the problem
    /// was solved, or fewer than half of the steps tied to real sentences.
    /// </summary>
    public static WikiArticle Parse(string answer, WikiArticle draft, IReadOnlyList<Sentence> sentences)
    {
        int a = answer.IndexOf('{'), z = answer.LastIndexOf('}');
        if (a < 0 || z <= a) throw new InvalidOperationException("answer is not JSON");
        JsonNode j;
        try { j = JsonNode.Parse(answer[a..(z + 1)]) ?? throw new InvalidOperationException("empty JSON"); }
        catch (JsonException ex) { throw new InvalidOperationException("answer is not valid JSON: " + ex.Message); }

        var article = new WikiArticle
        {
            Title = Str(j["title"]) ?? draft.Title, Language = draft.Language,
            Problem = Str(j["problem"]) ?? draft.Problem,
            // The renderer quotes error messages itself: strip the quotes models like to add.
            ErrorMessages = Strs(j["error_messages"]).Select(e => e.Trim('\'', '"', '„', '“', '”', '«', '»', '‚', '‘', '’', ' ')).Where(e => e.Length > 0).ToList()
                            is { Count: > 0 } errors ? errors : draft.ErrorMessages.ToList(),
            Cause = Str(j["cause"]) ?? draft.Cause, AppliesTo = Str(j["applies_to"]) ?? draft.AppliesTo,
            Verification = Str(j["verification"]) ?? draft.Verification,
            Notes = Strs(j["notes"]), Keywords = Strs(j["keywords"]) is { Count: > 0 } keywords ? keywords : draft.Keywords.ToList(),
            Resolved = j["resolved"] is JsonValue rv && rv.TryGetValue<bool>(out var resolved) ? resolved : draft.Resolved,
            SourceDurationS = draft.SourceDurationS,
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
            article.Steps.Add(new ArticleStep
            {
                Section = Str(s?["section"]), Title = Str(s?["title"]), Instruction = instruction, Details = Str(s?["details"]),
                Actor = string.Equals(Str(s?["actor"]), "support", StringComparison.OrdinalIgnoreCase) ? StepActor.Support : StepActor.Customer,
                TimeS = start, EndS = end,
            });
            prevEnd = end;
        }
        if (article.Steps.Count == 0 && article.Resolved != false)
            throw new InvalidOperationException("no solution steps");
        if (anchored * 2 < article.Steps.Count)
            throw new InvalidOperationException($"{article.Steps.Count} steps, only {anchored} tied to the transcript");

        // Video order (a stable sort keeps the model's order for steps from the same sentence).
        article.Steps = article.Steps.OrderBy(x => x.TimeS).ToList();
        for (int i = 0; i < article.Steps.Count; i++) article.Steps[i].Number = i + 1;
        return article;
    }

    private static readonly Regex NothingRx = new(@"^(keine?|none|n/?a|null|nothing|-)\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && !NothingRx.IsMatch(s.Trim()) ? s.Trim() : null;
    private static List<string> Strs(JsonNode? n) => n is JsonArray arr ? arr.Select(Str).Where(x => x is not null).Select(x => x!).Distinct().ToList() : new();
    private static double? Num(JsonNode? n) => n is not JsonValue v ? null
        : v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<string>(out var str) && double.TryParse(str.Trim().Trim('[', ']'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
}
