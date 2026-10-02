using System.Globalization;
using System.Reflection;
using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Adapters;
using AvAg.Pipeline.Articles;
using AvAg.Pipeline.Services;
using AvAg.Pipeline.Media;
using AvAg.Pipeline.Vision;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

// --- tiny test runner (NuGet-free) ----------------------------------------------------------
int passed = 0, failed = 0;
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<TestAttribute>() is not null))
{
    try
    {
        var r = m.Invoke(null, null);
        if (r is Task t) await t;
        Console.WriteLine($"  ok   {m.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException! : ex;
        Console.WriteLine($"  FAIL {m.Name}: {inner.Message}");
        failed++;
    }
}
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

[AttributeUsage(AttributeTargets.Method)] sealed class TestAttribute : Attribute { }

static class Assert
{
    public static void True(bool c, string msg) { if (!c) throw new Exception(msg); }
    public static void Eq<T>(T expected, T actual, string what) { if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}"); }
    public static void Near(double expected, double actual, double tol, string what) { if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: expected {expected}±{tol}, got {actual}"); }
}

static class Tests
{
    // --------------------------------------------------------------------------------------------
    // Audio reference parser
    // --------------------------------------------------------------------------------------------
    static Transcript T(params (string w, double s, double e)[] words) =>
        new("de", [new TranscriptSegment(words[0].s, words[^1].e, string.Join(' ', words.Select(x => x.w)), words.Select(x => new Word(x.w, x.s, x.e, 0.95, "speaker_0")).ToList(), "speaker_0")]);

    [Test] public static void Parser_German_Deictic_Click()
    {
        var refs = new AudioRefParser().Parse(T(("Klicken", 12.22, 12.38), ("Sie", 12.38, 12.45), ("hier.", 12.45, 12.58)));
        Assert.Eq(1, refs.Count, "ref count");
        Assert.Eq(ActionType.Click, refs[0].Action, "action");
        Assert.True(refs[0].Deictic, "deictic");
        Assert.Near(12.58, refs[0].AnchorS, 1e-6, "anchor = end of 'hier'");
        Assert.Eq(null, refs[0].ExplicitTarget, "no explicit target");
        Assert.Eq("speaker_0", refs[0].SpeakerId, "speaker");
    }

    [Test] public static void Parser_English_Explicit_Target()
    {
        var refs = new AudioRefParser().Parse(T(("Now", 1, 1.2), ("click", 1.2, 1.5), ("on", 1.5, 1.6), ("the", 1.6, 1.7), ("Save", 1.7, 2.0), ("button.", 2.0, 2.4)));
        Assert.Eq(1, refs.Count, "ref count");
        Assert.Eq(ActionType.Click, refs[0].Action, "action");
        Assert.True(!refs[0].Deictic, "not deictic");
        Assert.Eq("Save", refs[0].ExplicitTarget, "target");
    }

    [Test] public static void Parser_Quoted_Target_And_DoubleClick()
    {
        var refs = new AudioRefParser().Parse(T(("Doppelklicken", 0, 0.5), ("Sie", 0.5, 0.6), ("auf", 0.6, 0.7), ("„Speichern“", 0.7, 1.2)));
        Assert.Eq(ActionType.DoubleClick, refs[0].Action, "action");
        Assert.Eq("Speichern", refs[0].ExplicitTarget, "target");
    }

    [Test] public static void Parser_Multi_Action_Ordinals()
    {
        var refs = new AudioRefParser().Parse(T(("Hier", 0, 0.3), ("klicken", 0.3, 0.7), ("und", 0.7, 0.8), ("dann", 0.8, 1.0), ("dort", 1.0, 1.2), ("ziehen.", 1.2, 1.6)));
        Assert.Eq(2, refs.Count, "two sub-actions");
        Assert.Eq(ActionType.Click, refs[0].Action, "first action");
        Assert.Eq(ActionType.Drag, refs[1].Action, "second action");
        Assert.Eq(0, refs[0].Ordinal, "ordinal 0"); Assert.Eq(1, refs[1].Ordinal, "ordinal 1");
        Assert.Near(0.3, refs[0].AnchorS, 1e-6, "anchor at 'Hier'");
        Assert.Near(1.2, refs[1].AnchorS, 1e-6, "anchor at 'dort'");
    }

    [Test] public static void Parser_Ignores_Non_Action_Speech()
    {
        var refs = new AudioRefParser().Parse(T(("Das", 0, 0.2), ("Programm", 0.2, 0.6), ("startet", 0.6, 1.0), ("jetzt.", 1.0, 1.3)));
        Assert.Eq(0, refs.Count, "no refs");
    }

    // --------------------------------------------------------------------------------------------
    // UI registry
    // --------------------------------------------------------------------------------------------
    [Test] public static void UiRegistry_Merges_Across_Frames()
    {
        var reg = new UiElementRegistry();
        reg.Observe(1.0, [new UiElementRegistry.Detection(new BBox(100, 100, 200, 150), "Speichern", 0.9, 0.95, "button")]);
        reg.Observe(2.0, [new UiElementRegistry.Detection(new BBox(102, 101, 201, 152), "Speichem", 0.7, 0.9, "button")]); // OCR noise
        reg.Observe(2.0, [new UiElementRegistry.Detection(new BBox(400, 100, 500, 150), "Abbrechen", 0.9, 0.95, "button")]);
        Assert.Eq(2, reg.Elements.Count, "two stable elements");
        Assert.Eq("Speichern", reg.Elements[0].Text, "keeps higher-confidence text");
        Assert.Eq(2, reg.Elements[0].Observations, "observations");
        var tgt = reg.ResolveTarget(new Point2D(150, 120), 2.0);
        Assert.Eq("ui_000", tgt?.Id, "point→target");
        Assert.Eq("ui_001", reg.FindByText("abbrechen", 2.0)?.Id, "fuzzy text lookup");
    }

    // --------------------------------------------------------------------------------------------
    // Fusion
    // --------------------------------------------------------------------------------------------
    static AudioRef Ref(string id, double anchor, ActionType a, int ordinal = 0, double start = -1) => new()
    { Id = id, StartS = start < 0 ? anchor - 0.4 : start, EndS = anchor, AnchorS = anchor, Text = "Klicken Sie hier", Action = a, Deictic = true, Ordinal = ordinal };

    static VisualEvent Ev(string id, double t, Point2D? p, EvidenceSource? src = EvidenceSource.CursorTracker, double change = 0.9) => new()
    {
        Id = id, Action = ActionType.Click, TimeS = t, Point = p, PointSource = src, DetectorConfidence = 0.9, StateChangeConfidence = change,
        UiInteractiveConfidence = 0.9, Evidence = { new Evidence(src ?? EvidenceSource.FrameStateChange, 0.9) },
    };

    [Test] public static void Fusion_Picks_Closest_Compatible_Event()
    {
        var a = Ref("a1", 12.58, ActionType.Click);
        var events = new List<VisualEvent> { Ev("e_far", 9.0, new(10, 10)), Ev("e_hit", 12.71, new(1489, 836)), Ev("e_late", 14.9, new(5, 5)) };
        var res = new CrossModalResolver().Resolve([a], events, new UiElementRegistry());
        Assert.Eq(1, res.Count, "one association");
        Assert.Eq("e_hit", res[0].Event.Id, "closest event");
        Assert.True(res[0].Score > 0.7, $"score {res[0].Score}");
    }

    [Test] public static void Fusion_Respects_Spoken_Order_For_Multi_Action()
    {
        var a0 = Ref("a0", 10.0, ActionType.Click, 0, 9.5);
        var a1 = Ref("a1", 10.6, ActionType.Click, 1, 9.5);
        var events = new List<VisualEvent> { Ev("first", 10.2, new(1, 1)), Ev("second", 10.9, new(2, 2)) };
        var res = new CrossModalResolver().Resolve([a0, a1], events, new UiElementRegistry());
        Assert.Eq(2, res.Count, "two bindings");
        Assert.Eq("first", res.First(r => r.Audio.Id == "a0").Event.Id, "ordinal 0 → earlier event");
        Assert.Eq("second", res.First(r => r.Audio.Id == "a1").Event.Id, "ordinal 1 → later event");
    }

    [Test] public static void GroundingStatus_Follows_Evidence_Hierarchy()
    {
        Assert.Eq(GroundingStatus.Observed, GroundingPolicy.Classify(Ev("x", 1, new(1, 1), EvidenceSource.CursorTracker)), "cursor");
        Assert.Eq(GroundingStatus.Tracked, GroundingPolicy.Classify(Ev("x", 1, new(1, 1), EvidenceSource.Sam2Tracker)), "sam2");
        Assert.Eq(GroundingStatus.Inferred, GroundingPolicy.Classify(Ev("x", 1, new(1, 1), EvidenceSource.VlmPointing)), "vlm");
        Assert.Eq(GroundingStatus.Unobservable, GroundingPolicy.Classify(Ev("x", 1, null, null)), "no point");
    }

    [Test] public static void EventGraph_And_Describer_Separate_Perception_From_Claim()
    {
        var video = new VideoInfo("demo", 63.4, 1920, 1080, 30);
        var ui = new UiElementRegistry();
        ui.Observe(12.4, [new UiElementRegistry.Detection(new BBox(1432, 812, 1538, 858), "Speichern", 0.98, 0.99, "button")]);
        var a = Ref("audio_0017", 12.58, ActionType.Click);
        var e = Ev("vis_0001", 12.71, new(1489, 836)); e.TargetId = "ui_000";
        var unobs = Ev("vis_0002", 30.0, null, null);
        var assoc = new CrossModalResolver().Resolve([a], [e, unobs], ui);
        var graph = new EventGraphBuilder().Build(video, [a], [e, unobs], assoc, ui);
        new Describer().Annotate(graph);

        var ge = graph.Events[0];
        Assert.Eq(GroundingStatus.Observed, ge.GroundingStatus, "observed");
        Assert.Eq("Speichern", ge.Target?.Text, "target text");
        Assert.Near(0.7755, ge.Spatial!.PointXyNorm![0], 1e-3, "norm x");
        Assert.True(ge.DescriptionDe!.Contains("„Speichern“") && ge.DescriptionDe.Contains("x=1489"), ge.DescriptionDe);
        Assert.True(graph.Events[1].GroundingStatus == GroundingStatus.Unobservable, "second unobservable");
        Assert.True(graph.Events[1].DescriptionDe!.Contains("nicht beobachtbar"), graph.Events[1].DescriptionDe!);

        var json = Json.Serialize(graph);
        Assert.True(json.Contains("\"grounding_status\": \"observed\"") && json.Contains("\"schema_version\": \"1.0\""), "snake_case schema");
        var back = Json.Deserialize<EventGraph>(json);
        Assert.Eq(2, back.Events.Count, "round trip");
    }

    // --------------------------------------------------------------------------------------------
    // Wiki article from a support call
    // --------------------------------------------------------------------------------------------
    static Transcript Call(string lang, params (double s, string speaker, string text)[] sentences) =>
        new(lang, sentences.Select(x =>
        {
            var ws = x.text.Split(' ');
            var words = ws.Select((w, i) => new Word(w, x.s + i * 0.3, x.s + i * 0.3 + 0.25, 1.0, x.speaker)).ToList();
            return new TranscriptSegment(x.s, words[^1].EndS, x.text, words, x.speaker);
        }).ToList());

    static Transcript SupportCall() => Call("de",
        (0.0, "SPEAKER_01", "Grüezi, hier ist Müller von der Beispiel AG."),
        (3.0, "SPEAKER_01", "Ich kann seit heute keine Rechnungen drucken, es kommt die Meldung „Kein Drucker zugewiesen“."),
        (9.0, "SPEAKER_00", "Klicken Sie bitte oben auf Datei."),
        (13.0, "SPEAKER_00", "Dann sehen Sie die Druckereinstellungen."),
        (17.0, "SPEAKER_00", "Wählen Sie hier Ihren Drucker aus und klicken Sie auf Speichern."),
        (23.0, "SPEAKER_01", "Ja, jetzt funktioniert es wieder."),
        (26.0, "SPEAKER_01", "Vielen Dank, auf Wiederhören."));

    [Test] public static void Article_From_Support_Call_Problem_Steps_Verification()
    {
        var tr = SupportCall();
        var refs = new AudioRefParser().Parse(tr);
        var video = new VideoInfo("v", 30, 1920, 1080, 30);
        var ui = new UiElementRegistry();
        ui.Observe(19.0, [new UiElementRegistry.Detection(new BBox(1432, 812, 1538, 858), "Speichern", 0.98, 0.99, "button")]);
        var save = refs.Last(r => r.StartS >= 17);
        var e = Ev("vis_0001", save.AnchorS + 0.1, new(1489, 836)); e.TargetId = "ui_000";
        var graph = new EventGraphBuilder().Build(video, refs, [e], new CrossModalResolver().Resolve(refs, [e], ui), ui);

        var a = new ArticleBuilder().Build(tr, refs, graph);
        Assert.Eq("de", a.Language, "language from transcript");
        Assert.True(a.Problem?.Contains("keine Rechnungen drucken") == true, "problem: " + a.Problem);
        Assert.True(a.ErrorMessages.SequenceEqual(["Kein Drucker zugewiesen"]), "error message: " + string.Join("|", a.ErrorMessages));
        Assert.Eq(2, a.Steps.Count, "two instructions");
        Assert.True(a.Steps[0].Instruction.StartsWith("Klicken Sie bitte oben auf Datei") && a.Steps[0].Details?.Contains("Druckereinstellungen") == true, "step 1 + details");
        Assert.Eq(1489, a.Steps[1].PointXyPx?[0] ?? -1, "observed click marker on the save step");
        Assert.True(a.Verification?.Contains("funktioniert es wieder") == true && a.Resolved == true, "verification");
        Assert.True(!a.Steps.Any(s => s.Instruction.Contains("Wiederhören")) && a.Problem!.Contains("Grüezi") == false, "small talk dropped");
    }

    [Test] public static void Redactor_Removes_Personal_Data_Not_Ordinary_Words()
    {
        string R(string s) => PersonalDataRedactor.Redact(s, "de").Text;
        Assert.Eq("Schreiben Sie an [entfernt] oder rufen Sie [entfernt] an.", R("Schreiben Sie an hans.muster@example.ch oder rufen Sie 079 123 45 67 an."), "e-mail + Swiss mobile");
        Assert.Eq("Nummer [entfernt].", R("Nummer +41 44 123 45 67."), "international phone");
        Assert.Eq("IBAN [entfernt]", R("IBAN CH93 0076 2011 6238 5295 7"), "IBAN");
        Assert.Eq("Ihre Kundennummer ist [entfernt].", R("Ihre Kundennummer ist 4711-08."), "labelled number");
        Assert.Eq("Frau [entfernt] hat angerufen.", R("Frau Müller hat angerufen."), "title + name");
        Assert.Eq("Grüezi, mein Name ist [entfernt].", R("Grüezi, mein Name ist Hans Muster."), "introduced name");
        Assert.Eq("Hier ist [entfernt] von der [entfernt].", R("Hier ist Müller von der Beispiel AG."), "caller + company");
        foreach (var ok in new[] { "Klicken Sie auf Speichern.", "this is the menu", "Drücken Sie Strg + C.", "Version 2024.1 ab 10:30", "Hier ist das Menü Datei." })
            Assert.Eq(ok, R(ok), "no false positive");
    }

    [Test] public static void Article_Markdown_Package_And_Private_Text_Only()
    {
        var a = new WikiArticle
        {
            Title = "Rechnung lässt sich nicht drucken", Language = "de", Problem = "Beim Drucken erscheint ein Fehler.",
            ErrorMessages = ["Kein Drucker zugewiesen"], Cause = "Kein Standarddrucker im Profil.", Keywords = ["Drucken", "Rechnung"],
            Verification = "Drucken Sie die Rechnung erneut.", Resolved = true,
            Steps =
            [
                new ArticleStep { Number = 1, Section = "Variante 1: Profil", Instruction = "Drücken Sie Strg + P.", ScreenshotJpeg = [1, 2, 3], ScreenshotWidth = 10, ScreenshotHeight = 6 },
                new ArticleStep { Number = 2, Section = "Variante 2: Support", Instruction = "Lassen Sie die Lizenz zurücksetzen.", Actor = StepActor.Support },
            ],
        };
        var md = ArticleRenderer.Markdown(a);
        Assert.True(md.StartsWith("---\ntitle: \"Rechnung lässt sich nicht drucken\"\ntags: [\"Drucken\", \"Rechnung\"]") && md.Contains("status: draft"), "front matter");
        Assert.True(md.Contains("## Problem") && md.Contains("**Fehlermeldung:** `Kein Drucker zugewiesen`") && md.Contains("## Ursache"), "problem + cause");
        Assert.True(md.Contains("### Variante 2: Support") && md.Contains("*(nur durch den Support)*"), "sections + support-only steps");
        Assert.True(md.Contains("Drücken Sie `Strg` + `P`.") && md.Contains("![Bildschirm bei Schritt 1](images/step-01.jpg)") && !md.Contains("step-02.jpg"), "keys + only existing images");
        Assert.True(ArticleRenderer.Html(a).Contains("<kbd>Strg</kbd> + <kbd>P</kbd>"), "html preview");
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(ArticlePackage.Zip(a))))
            Assert.True(zip.Entries.Select(x => x.FullName).SequenceEqual(["article.md", "images/step-01.jpg"]), "zip layout");
        Assert.Eq("rechnung-laesst-sich-nicht-drucken", ArticlePackage.Slug(a.Title), "slug");
    }

    [Test] public static async Task Article_Private_Video_Never_Reads_A_Frame()
    {
        var tr = SupportCall();
        var refs = new AudioRefParser().Parse(tr);
        var video = new VideoInfo("v", 30, 1920, 1080, 30);
        var e = Ev("vis_0001", refs.Last().AnchorS + 0.1, new(1489, 836));
        var graph = new EventGraphBuilder().Build(video, refs, [e], new CrossModalResolver().Resolve(refs, [e], new UiElementRegistry()), new UiElementRegistry());
        var result = new PipelineResult { Graph = graph, Transcript = tr, AudioRefs = refs, VisualEvents = [e], TimelineDe = "", TimelineEn = "" };

        // The video path does not exist: a text-only article must not even try to read a frame from it.
        var svc = new ArticleService();
        var a = await svc.CreateAsync(result, "does-not-exist.mp4", new ArticleRequest(Private: true));
        Assert.True(a.Private && a.Steps.Count == 2, "private article with steps");
        Assert.True(a.Steps.All(s => s.ScreenshotJpeg is null && s.ScreenshotS is null && s.PointXyPx is null), "no screenshot, no click position");
        Assert.True(!svc.Log.Any(l => l.Contains("screenshot")) || svc.Log.Any(l => l.Contains("no screenshots taken")), "no frame extraction");
        var md = ArticleRenderer.Markdown(a);
        Assert.True(!md.Contains("![") && md.Contains("private") && !md.Contains("Müller") && !md.Contains("Beispiel AG"), "text only, no names");
        Assert.True(ArticlePackage.Files(a).Select(f => f.Path).SequenceEqual(["article.md"]), "package has no images");
    }

    // --------------------------------------------------------------------------------------------
    // Replaceable AI services
    // --------------------------------------------------------------------------------------------
    sealed class FakeTextGenerator(Func<string> answer) : ITextGenerator
    {
        public FakeTextGenerator(string answer) : this(() => answer) { }
        public TextGenerationRequest? LastRequest { get; private set; }
        public string Name => "fake";
        public Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken ct = default) { LastRequest = request; return Task.FromResult(answer()); }
    }

    sealed class ThrowingWriter(Exception ex) : IArticleWriter
    {
        public string Name => "throwing";
        public Task<WikiArticle> WriteAsync(ArticleWriterInput input, CancellationToken ct = default) => throw ex;
    }

    /// <summary>
    /// Every file git would commit (tracked + new, not ignored) is scanned for API keys. Keys belong in user secrets or
    /// environment variables. The same patterns are enforced by tools/hooks/pre-commit.
    /// </summary>
    [Test] public static void Repository_Contains_No_Api_Keys()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AvAg.sln"))) root = root.Parent;
        if (root is null) return; // not running from a checkout
        var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files --cached --others --exclude-standard")
            { WorkingDirectory = root.FullName, RedirectStandardOutput = true, UseShellExecute = false };
        string files;
        try { using var p = System.Diagnostics.Process.Start(psi)!; files = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        catch (System.ComponentModel.Win32Exception) { return; } // git not installed

        var key = new System.Text.RegularExpressions.Regex(
            @"sk-(?:proj-|live-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}|""ApiKey""\s*:\s*""[^""]+""|AIza[0-9A-Za-z_-]{30,}|xox[abp]-[0-9A-Za-z-]{10,}");
        var text = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".md", ".py", ".yml", ".yaml", ".html", ".js", ".props", ".csproj", ".sln", ".txt", ".sh", ".Modelfile", ".editorconfig", ".gitignore", "" };
        var hits = files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => text.Contains(Path.GetExtension(f)))
            .Select(f => Path.Combine(root.FullName, f))
            .Where(File.Exists)
            .Where(f => key.IsMatch(File.ReadAllText(f)))
            .ToList();
        Assert.True(hits.Count == 0, "possible API key in: " + string.Join(", ", hits));
    }

    [Test] public static void ServiceFactory_Resolves_Providers_By_Name()
    {
        var f = AiServiceFactory.CreateDefault();
        Assert.True(f.Asr.Create(new ServiceOptions { Provider = "whisperx", Url = "http://127.0.0.1:8011" }) is WhisperXSidecar, "named provider");
        Assert.True(f.Tracker.Create(new ServiceOptions { Url = "http://127.0.0.1:8004" }) is Sam2Sidecar, "Url only → default provider");
        Assert.True(f.Grounder.Create(new ServiceOptions { Provider = "NONE", Url = "http://x" }) is null && f.Tracker.Create(new ServiceOptions()) is null, "none (any case) / empty → off");
        Assert.True(f.TextGenerator.Create(new ServiceOptions { Provider = "ollama", Url = "http://127.0.0.1:11434/v1", Model = "m" }) is OpenAiCompatibleTextGenerator, "alias");
        Assert.True(f.CreatePipelineServices(new AiServicesOptions()).Asr is WhisperXSidecar, "no ASR configured → local WhisperX");

        try { f.Asr.Create(new ServiceOptions { Provider = "nope", Url = "http://x" }); throw new Exception("unknown provider accepted"); }
        catch (InvalidOperationException ex) { Assert.True(ex.Message.Contains("whisperx") && ex.Message.Contains("nope"), "error lists known providers: " + ex.Message); }

        // A broken model configuration is reported, not thrown, when the article is created.
        var broken = new AiServicesOptions { TextGenerator = new ServiceOptions { Provider = "olama", Url = "http://x/v1" } };
        Assert.True(f.TryCreateArticleWriter(broken, out var problem) is null && problem!.Contains("olama"), "typo reported: " + problem);

        // Another AI is one registration away.
        f.TextGenerator.Register("my-llm", _ => new FakeTextGenerator("{}"));
        Assert.True(f.CreateArticleWriter(new AiServicesOptions { TextGenerator = new ServiceOptions { Provider = "my-llm" } }) is LlmArticleWriter, "custom provider");
        Assert.True(f.Describe(new AiServicesOptions { TextGenerator = new ServiceOptions { Provider = "my-llm", ApiKey = "secret" } }).Values.All(v => v is null || !v.Contains("secret")), "no keys in status");
    }

    [Test] public static void ServiceOverrides_Follow_One_Set_Of_Rules()
    {
        var cfg = new AiServicesOptions
        {
            Asr = new ServiceOptions { Provider = "whisperx", Url = "http://127.0.0.1:8011", LocalModule = "whisperx_server:app" },
            Tracker = new ServiceOptions { Provider = "sam2", Url = "http://127.0.0.1:8004" },
            ClipDescriber = new ServiceOptions { Provider = "qwen-vl", Url = "http://127.0.0.1:8005/v1", Model = "Qwen/Qwen3-VL-32B-Instruct", TimeoutSeconds = 60 },
        };
        var none = cfg.WithOverrides(new ServiceOverrides());
        Assert.True(none.Tracker.Url == "http://127.0.0.1:8004" && none.Asr.LocalModule == "whisperx_server:app", "no override keeps the configuration");
        Assert.True(none.Grounder.Provider == "qwen-vl" && none.Grounder.Model == "Qwen/Qwen3-VL-32B-Instruct" && none.Grounder.TimeoutSeconds == 60 && none.Grounder.LocalModule is null,
            "qwen describer also points, with its model and timeout");

        var cleared = cfg.WithOverrides(new ServiceOverrides(TrackerUrl: "", GrounderUrl: ""));
        Assert.True(cleared.Tracker.IsOff && cleared.Grounder.IsOff, "an empty field switches the service off");
        var explicitNone = new AiServicesOptions { Grounder = ServiceOptions.Off(), ClipDescriber = cfg.ClipDescriber }.WithOverrides(new ServiceOverrides());
        Assert.True(explicitNone.Grounder.IsOff, "an explicit \"none\" grounder is respected");

        var custom = new AiServicesOptions { Asr = new ServiceOptions { Provider = "my-asr", Url = "http://127.0.0.1:9000" } };
        Assert.True(custom.WithOverrides(new ServiceOverrides()).Asr.LocalModule is null, "a custom ASR does not inherit the WhisperX auto-start");
        var file = cfg.WithOverrides(new ServiceOverrides(AsrUrl: "", TranscriptFile: "t.json"));
        Assert.True(file.Asr.Provider == "whisperx-json" && file.Asr.LocalModule is null, "a transcript file replaces speech recognition");
    }

    [Test] public static void A_Url_Override_Never_Takes_Keys_To_Another_Host()
    {
        var cfg = new AiServicesOptions
        {
            Asr = new ServiceOptions { Provider = "openai", Url = "https://api.openai.com/v1", ApiKey = "sk-secret", AlignUrl = "http://127.0.0.1:8011",
                                       Fallback = new ServiceOptions { Provider = "whisperx", Url = "http://127.0.0.1:8011" } },
            ClipDescriber = new ServiceOptions { Provider = "qwen-vl", Url = "http://127.0.0.1:8005/v1", Model = "Qwen/Qwen3-VL-32B-Instruct" },
        };
        var other = cfg.WithOverrides(new ServiceOverrides(AsrUrl: "http://10.0.0.5:8011"));
        Assert.True(other.Asr.Provider == "whisperx" && other.Asr.ApiKey is null && other.Asr.AlignUrl is null && other.Asr.Fallback is null,
            "another URL in the WhisperX field is WhisperX there, without the OpenAI key");
        var same = cfg.WithOverrides(new ServiceOverrides(AsrUrl: "https://api.openai.com/v1/"));
        Assert.True(same.Asr.Provider == "openai" && same.Asr.ApiKey == "sk-secret" && same.Asr.Fallback is not null, "the configured URL keeps the configuration");
        var qwen = cfg.WithOverrides(new ServiceOverrides(ClipDescriberUrl: "http://10.0.0.6:8005/v1"));
        Assert.True(qwen.ClipDescriber.Provider == "qwen-vl" && qwen.ClipDescriber.Model == "Qwen/Qwen3-VL-32B-Instruct", "same sidecar elsewhere keeps its model");
        Assert.True(new AiServicesOptions().EffectiveAsr.LocalModule == AiServicesOptions.LocalAsrModule, "the default WhisperX is started automatically");
    }

    [Test] public static void Redactor_And_Cleaner_Keep_Ordinary_Instructions()
    {
        string R(string s, string lang = "de") => PersonalDataRedactor.Redact(s, lang).Text;
        foreach (var ok in new[] { "Geben Sie Ihr Passwort ein.", "Wählen Sie den Mandant aus.", "Das Passwort ist falsch." })
            Assert.Eq(ok, R(ok), "no false positive");
        Assert.Eq("Enter your password again.", R("Enter your password again.", "en"), "no false positive (en)");
        Assert.Eq("Passwort: [entfernt].", R("Passwort: geheim."), "value after a colon");
        Assert.Eq("Mein Benutzername ist [entfernt].", R("Mein Benutzername ist h_muster."), "identifier after 'ist'");

        Assert.Eq("Klicken Sie auf Speichern, um die Änderung zu übernehmen.", TranscriptSentences.Clean("Klicken Sie auf Speichern, um die Änderung zu übernehmen."), "'um … zu' stays");
        Assert.Eq("So geht das.", TranscriptSentences.Clean("So geht das."), "a meaningful 'So' stays");
        Assert.Eq("Klicken Sie hier.", TranscriptSentences.Clean("Also, äh, klicken Sie hier."), "fillers go");
    }

    [Test] public static void Article_Rules_Do_Not_Swallow_Steps_Or_The_Problem()
    {
        var tr = Call("de",
            (0.0, "SPEAKER_01", "Ich kann nicht drucken, weil der Drucker fehlt."),
            (4.0, "SPEAKER_00", "Genau, klicken Sie oben auf Datei."),
            (8.0, "SPEAKER_00", "Perfekt, dann wählen Sie den Drucker aus."),
            (12.0, "SPEAKER_00", "Klicken Sie dann auf das Zahnrad, das ist da oben rechts."),
            (17.0, "SPEAKER_01", "Jetzt funktioniert es wieder."));
        var a = new ArticleBuilder().Build(tr, new AudioRefParser().Parse(tr), null);
        Assert.Eq(3, a.Steps.Count, "steps: " + string.Join(" | ", a.Steps.Select(s => s.Instruction)));
        Assert.True(a.Problem?.Contains("nicht drucken") == true && a.Cause is null, $"complaint is the problem, not the cause: {a.Problem} / {a.Cause}");
        Assert.True(a.Verification?.Contains("funktioniert") == true, "verification");

        var en = Call("en", (0.0, "SPEAKER_01", "I can't print, it says the printer isn't assigned."), (5.0, "SPEAKER_00", "Click on File."));
        Assert.Eq(0, new ArticleBuilder().Build(en, new AudioRefParser().Parse(en), null).ErrorMessages.Count, "contractions are no quotes");
    }

    [Test] public static void OpenAiTranscriber_Knows_Language_And_Length_Without_Being_Told()
    {
        var diarized = OpenAiTranscriber.Parse("""{"text":"…","segments":[{"start":0,"end":3,"text":"Klicken Sie bitte hier auf das Menü und dann auf Speichern.","speaker":"A"}]}""", null);
        Assert.Eq("de", diarized.Language, "language recognised from the text");
        Assert.Eq("en", OpenAiTranscriber.GuessLanguage("Now you can click on the button and then it is saved."), "english");
        var textOnly = OpenAiTranscriber.Parse("""{"text":"Klicken Sie hier."}""", "de", audioDurationS: 42.5);
        Assert.Near(42.5, textOnly.Segments.Single().End, 1e-9, "text-only answer spans the recording");
    }

    [Test] public static void Restored_Step_Joins_The_Section_Before_It()
    {
        var draft = new WikiArticle { Title = "T", Language = "de", Steps = [new ArticleStep { Instruction = "Klicken Sie auf Speichern.", TimeS = 40, EndS = 43, PointXyPx = [1, 2] }] };
        var written = new WikiArticle { Title = "T", Language = "de", Steps =
        [
            new ArticleStep { Section = "Option 1", Instruction = "A", TimeS = 30, EndS = 35 },
            new ArticleStep { Section = "Option 1", Instruction = "B", TimeS = 50, EndS = 55 },
        ] };
        Assert.Eq(1, ArticleService.RestoreObservedSteps(draft, written), "restored");
        Assert.True(written.Steps.All(s => s.Section == "Option 1"), "no repeated section heading");
    }

    [Test] public static void Sidecar_Json_Is_Read_As_Snake_Case()
    {
        var p = System.Text.Json.JsonSerializer.Deserialize<VideoPoint>("""{"object_id":"btn","time_s":12.5,"x":10,"y":20,"confidence":0.9,"label":"OK"}""", Json.Options)!;
        Assert.True(p.ObjectId == "btn" && Math.Abs(p.TimeS - 12.5) < 1e-9 && p.Confidence > 0.8, "molmo point fields bind");
    }

    [Test] public static async Task LlmArticleWriter_Anchors_Steps_To_Transcript_Sentences()
    {
        var tr = SupportCall();
        var sentences = TranscriptSentences.Split(tr);
        var draft = new ArticleBuilder().Build(tr, new AudioRefParser().Parse(tr), null);
        var llm = new FakeTextGenerator("""
            Hier ist der Artikel: {"title": "Rechnung lässt sich nicht drucken", "problem": "Beim Drucken erscheint eine Fehlermeldung.",
             "error_messages": ["„Kein Drucker zugewiesen“"], "cause": "none", "applies_to": null,
             "steps": [{"title": "Drucker wählen", "instruction": "Wählen Sie den Drucker aus und klicken Sie auf Speichern.", "actor": "customer", "sentences": [5]},
                       {"section": null, "title": "Datei öffnen", "instruction": "Klicken Sie auf Datei.", "actor": "customer", "sentences": ["3"]},
                       {"title": "Lizenz", "instruction": "Der Support setzt die Lizenz zurück.", "actor": "support", "sentences": [4]}],
             "verification": "Drucken Sie erneut.", "notes": [], "keywords": ["Drucken", "Drucker"], "resolved": true}
            """);
        var a = await new LlmArticleWriter(llm).WriteAsync(new ArticleWriterInput(draft, sentences) { ObservedActions = ["[00:19] Klick auf „Speichern“"] });
        Assert.True(llm.LastRequest!.Json && llm.LastRequest.User.Contains("SPEAKER_00: Klicken Sie bitte oben auf Datei."), "numbered transcript with speakers in the prompt");
        Assert.True(llm.LastRequest.User.Contains("[00:19] Klick auf „Speichern“") && llm.LastRequest.Images.Count == 0, "observed clicks in the prompt; no images for a text-only model");
        Assert.Eq("llm:fake", a.Method, "method");
        Assert.Eq("Klicken Sie auf Datei.", a.Steps[0].Instruction, "steps in video order");
        Assert.Near(sentences[2].StartS, a.Steps[0].TimeS, 1e-6, "time from sentence 3, not from the model");
        Assert.True(a.Steps.Single(s => s.Title == "Lizenz").Actor == StepActor.Support, "support-only step");
        Assert.True(a.Cause is null && a.Resolved == true && a.ErrorMessages.SequenceEqual(["Kein Drucker zugewiesen"]), "'none' dropped, quotes stripped, fields read");

        var unsolved = LlmArticleWriter.Parse("""{"title": "X", "problem": "Y", "steps": [], "resolved": false}""", draft, sentences);
        Assert.True(unsolved.Steps.Count == 0 && unsolved.Resolved == false, "an unsolved case may have no steps");
        try { LlmArticleWriter.Parse("""{"steps": [], "resolved": true}""", draft, sentences); throw new Exception("accepted"); }
        catch (InvalidOperationException) { /* solved but no steps */ }
    }

    // ---- OpenAI adapters, against fake HTTP answers in the documented formats --------------------------------------
    sealed class FakeHttp(Func<HttpRequestMessage, string, (int Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var (status, text) = respond(request, body);
            return new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(text) };
        }
    }

    static string Chat(string content) => System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } });

    [Test] public static void OpenAiTranscriber_Reads_Diarized_And_Word_Timestamps()
    {
        var diarized = OpenAiTranscriber.Parse("""
            {"text":"…","segments":[
              {"type":"transcript.text.segment","id":"seg_0","start":0.5,"end":4.0,"text":"Ich kann nicht speichern.","speaker":"A"},
              {"type":"transcript.text.segment","id":"seg_1","start":4.2,"end":7.0,"text":"Klicken Sie auf Speichern.","speaker":"B"}]}
            """, "de");
        var t = WhisperXMapper.ToTranscript(diarized);
        Assert.True(t.Language == "de" && t.Segments.Count == 2 && t.Segments[1].Speaker == "SPEAKER_B", "segments with speakers");
        Assert.True(t.Segments[1].Words.Count == 4 && t.Segments[1].Words[0].StartS >= 4.2 && t.Segments[1].Words[^1].EndS <= 7.0 + 1e-9, "words spread over their segment");

        var verbose = OpenAiTranscriber.Parse("""
            {"language":"german","duration":3.0,"text":"Klicken Sie hier.",
             "words":[{"word":"Klicken","start":0.1,"end":0.5},{"word":"Sie","start":0.6,"end":0.8},{"word":"hier.","start":0.9,"end":1.2}],
             "segments":[{"id":0,"start":0.0,"end":1.3,"text":"Klicken Sie hier."}]}
            """, null);
        var v = WhisperXMapper.ToTranscript(verbose);
        Assert.True(v.Language == "de" && Math.Abs(v.Segments[0].Words[2].EndS - 1.2) < 1e-9 && v.Segments[0].Words[2].Score == 1.0, "word times from whisper-1, 'german' → de");
        Assert.Eq(1, new AudioRefParser().Parse(v).Count, "instruction found in the transcript");
    }

    [Test] public static async Task OpenAiTranscriber_Sends_The_Documented_Request()
    {
        var dir = Directory.CreateTempSubdirectory("avag_asr_test").FullName;
        var wav = Path.Combine(dir, "a.wav");
        await FfmpegService.RunAsync("ffmpeg", ["-y", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-ac", "1", "-ar", "16000", wav], null, default);
        var http = new FakeHttp((_, _) => (200, """{"text":"Hallo.","segments":[{"start":0.1,"end":1.5,"text":"Hallo.","speaker":"A"}]}"""));
        var asr = new OpenAiTranscriber("https://api.openai.com/v1", null, "test-key", TimeSpan.FromSeconds(30), prompt: "Messerli", http: new HttpClient(http));
        var t = await asr.TranscribeAsync(wav, "de", diarize: true);
        var (req, body) = http.Calls.Single();
        Assert.True(req.RequestUri!.ToString() == "https://api.openai.com/v1/audio/transcriptions" && req.Headers.Authorization?.Parameter == "test-key", "endpoint + bearer key");
        Assert.True(body.Contains("gpt-4o-transcribe-diarize") && body.Contains("diarized_json") && body.Contains("chunking_strategy") && body.Contains("audio/mpeg"), "diarize request as MP3");
        Assert.True(!body.Contains("Messerli"), "no prompt for the diarize model");
        Assert.True(t.Segments.Single().Speaker == "SPEAKER_A", "answer mapped");
        try { await new OpenAiTranscriber("https://api.openai.com/v1", null, null, TimeSpan.FromSeconds(5), http: new HttpClient(http)).TranscribeAsync(wav, "de", true); throw new Exception("no key accepted"); }
        catch (InvalidOperationException ex) { Assert.True(ex.Message.Contains("API key"), "missing key reported"); }
        Directory.Delete(dir, true);
    }

    [Test] public static async Task OpenAiTextGenerator_Uses_Reasoning_Parameters_Images_And_Retries()
    {
        var http = new FakeHttp((_, _) => (200, Chat("{\"ok\":true}")));
        var gpt5 = new OpenAiCompatibleTextGenerator("https://api.openai.com/v1", "gpt-5.5", "k", TimeSpan.FromSeconds(30), vision: true, reasoningEffort: "medium", http: new HttpClient(http));
        var answer = await gpt5.GenerateAsync(new TextGenerationRequest("sys", "user", Json: true) { Images = [new PromptImage("Bild 1 (00:05)", [1, 2, 3])] });
        var body = System.Text.Json.Nodes.JsonNode.Parse(http.Calls.Single().Body)!;
        Assert.True(answer == "{\"ok\":true}" && gpt5.SupportsImages, "answer");
        Assert.True(body["max_completion_tokens"] is not null && body["max_tokens"] is null && body["temperature"] is null && (string?)body["reasoning_effort"] == "medium", "reasoning-model parameters");
        var parts = body["messages"]![1]!["content"]!.AsArray();
        Assert.True(parts.Count == 3 && (string?)parts[1]!["text"] == "Bild 1 (00:05)" && ((string)parts[2]!["image_url"]!["url"]!).StartsWith("data:image/jpeg;base64,"), "image after its label");

        // A server that rejects a parameter: the request is repeated without it.
        int n = 0;
        var picky = new FakeHttp((_, b) => ++n == 1 && b.Contains("\"temperature\"")
            ? (400, """{"error":{"message":"Unsupported parameter: 'temperature' is not supported with this model."}}""") : (200, Chat("fine")));
        Assert.Eq("fine", await new OpenAiCompatibleTextGenerator("https://x/v1", "some-model", null, http: new HttpClient(picky)).GenerateAsync(new TextGenerationRequest("s", "u")), "retried");
        Assert.True(picky.Calls.Count == 2 && !picky.Calls[1].Body.Contains("\"temperature\""), "second request without temperature");

        var quota = new FakeHttp((_, _) => (429, """{"error":{"message":"You have no credits remaining.","code":"credit_balance_exhausted"}}"""));
        try { await new OpenAiCompatibleTextGenerator("https://x/v1", "gpt-5.5", "k", http: new HttpClient(quota)).GenerateAsync(new TextGenerationRequest("s", "u")); throw new Exception("accepted"); }
        catch (HttpRequestException ex) { Assert.True(ex.Message.Contains("no credits"), "API error message passed on: " + ex.Message); }
    }

    sealed class FailingAsr(Exception ex) : IAsrService
    {
        public Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default) => throw ex;
    }

    [Test] public static async Task Fallbacks_Switch_To_The_Local_Service()
    {
        var json = Path.Combine(Path.GetTempPath(), $"avag_tr_{Guid.NewGuid():N}.json");
        File.WriteAllText(json, """{"language":"de","segments":[{"start":0,"end":1,"text":"Hallo.","words":[{"word":"Hallo.","start":0,"end":1}]}]}""");
        var asr = new FallbackAsrService(new FailingAsr(new HttpRequestException("You have no credits remaining.")), "openai", new JsonFileAsr(json), "whisperx-json");
        var t = await asr.TranscribeAsync("x.wav", "de", true);
        Assert.True(t.Segments.Count > 0 && asr.FallbackNotes.Single().Contains("openai not usable (You have no credits remaining.) – used whisperx-json"), "ASR fallback: " + string.Join("|", asr.FallbackNotes));

        var local = new FakeTextGenerator("local answer");
        var llm = new FallbackTextGenerator(new LlmThatFails(), local);
        var answer = await llm.GenerateAsync(new TextGenerationRequest("s", "u") { Images = [new PromptImage("Bild 1", [1])] });
        Assert.True(answer == "local answer" && llm.Name == "fake" && local.LastRequest!.Images.Count == 0, "text fallback, images not sent to a text-only model");

        var f = AiServiceFactory.CreateDefault();
        var cfg = new AiServicesOptions
        {
            Asr = new ServiceOptions { Provider = "openai", Url = "https://api.openai.com/v1", Fallback = new ServiceOptions { Provider = "whisperx", Url = "http://127.0.0.1:8011", LocalModule = "whisperx_server:app" } },
            TextGenerator = new ServiceOptions { Provider = "openai", Url = "https://api.openai.com/v1", Model = "gpt-5.5", Vision = true, Fallback = new ServiceOptions { Provider = "ollama", Url = "http://127.0.0.1:11434/v1", Model = "avag-article" } },
        };
        Assert.True(f.CreatePipelineServices(cfg).Asr is FallbackAsrService && f.CreateArticleWriter(cfg) is LlmArticleWriter { WantsScreens: true }, "fallback chains built");
        Assert.True(cfg.All.Any(o => o.LocalModule == "whisperx_server:app"), "the fallback sidecar is started too");
        Assert.True(f.Describe(cfg)["text_generator"] == "openai gpt-5.5 @ https://api.openai.com/v1 → fallback ollama avag-article @ http://127.0.0.1:11434/v1", "status shows the chain: " + f.Describe(cfg)["text_generator"]);
    }

    sealed class LlmThatFails : ITextGenerator
    {
        public string Name => "gpt-5.5";
        public bool SupportsImages => true;
        public Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken ct = default) => throw new HttpRequestException("429 no credits");
    }

    [Test] public static void ArticleService_Restores_Observed_Steps_The_Model_Left_Out()
    {
        var draft = new WikiArticle { Title = "T", Language = "de", Steps =
        [
            new ArticleStep { Number = 1, Instruction = "Öffnen Sie die Einstellungen.", TimeS = 30, EndS = 35 },
            new ArticleStep { Number = 2, Instruction = "Klicken Sie auf Speichern.", TimeS = 40, EndS = 43, PointXyPx = [496, 264] },
        ] };
        var written = new WikiArticle { Title = "T", Language = "de", Steps = [new ArticleStep { Number = 1, Instruction = "Hinterlegen Sie den Speicherpfad.", TimeS = 30, EndS = 35 }] };
        Assert.Eq(1, ArticleService.RestoreObservedSteps(draft, written), "the observed click step comes back");
        Assert.True(written.Steps.Select(s => s.Number + ":" + s.Instruction).SequenceEqual(["1:Hinterlegen Sie den Speicherpfad.", "2:Klicken Sie auf Speichern."]), "in video order");
        Assert.Eq(0, ArticleService.RestoreObservedSteps(draft, written), "nothing added twice");
    }

    [Test] public static async Task ArticleService_Keeps_Draft_Whatever_The_Model_Does()
    {
        var tr = SupportCall();
        var refs = new AudioRefParser().Parse(tr);
        var graph = new EventGraphBuilder().Build(new VideoInfo("v", 30, 640, 360, 30), refs, [], [], new UiElementRegistry());
        var result = new PipelineResult { Graph = graph, Transcript = tr, AudioRefs = refs, VisualEvents = [], TimelineDe = "", TimelineEn = "" };

        IArticleWriter[] failing =
        [
            new LlmArticleWriter(new FakeTextGenerator("sorry, no JSON")),
            new LlmArticleWriter(new FakeTextGenerator(() => throw new System.Text.Json.JsonException("gateway returned HTML"))),
            new LlmArticleWriter(new FakeTextGenerator(() => throw new TimeoutException("too slow"))),
            new ThrowingWriter(new ArgumentOutOfRangeException("index")),
        ];
        foreach (var w in failing)
        {
            var svc = new ArticleService(w);
            var a = await svc.CreateAsync(result, "does-not-exist.mp4", new ArticleRequest(Private: true));
            Assert.True(a.Method == "rule-based" && a.Steps.Count == 2 && svc.Log.Any(l => l.Contains("kept the rule-based article")), $"fallback for {w.Name}: {string.Join(" | ", svc.Log)}");
        }
    }

    // --------------------------------------------------------------------------------------------
    // Metrics
    // --------------------------------------------------------------------------------------------
    [Test] public static void Metrics_Wer_IoU_Avaga()
    {
        Assert.Near(0.25, AsrMetrics.Wer(["klicken", "sie", "hier", "bitte"], ["klicken", "sie", "dort", "bitte"]), 1e-9, "WER");
        Assert.Near(1.0 / 3, TemporalMetrics.IoU(0, 2, 1, 3), 1e-9, "IoU");
        Assert.True(SpatialMetrics.PointInBox([1489, 836], [1432, 812, 1538, 858]), "point in box");

        var video = new VideoInfo("demo", 60, 1920, 1080, 30);
        var ui = new UiElementRegistry();
        ui.Observe(12.4, [new UiElementRegistry.Detection(new BBox(1432, 812, 1538, 858), "Speichern", 0.98, 0.99, "button")]);
        var a = Ref("a", 12.58, ActionType.Click);
        var e = Ev("e", 12.71, new(1489, 836)); e.TargetId = "ui_000";
        var graph = new EventGraphBuilder().Build(video, [a], [e], new CrossModalResolver().Resolve([a], [e], ui), ui);
        var gt = new GroundTruthFile
        {
            VideoId = "demo",
            Events = { new GroundTruthEvent { VideoId = "demo", ActionUtterance = "Klicken Sie hier", UtteranceStartS = 12.22, UtteranceEndS = 12.58,
                                              EventTimeS = 12.70, EventType = ActionType.Click, Point = [1490, 835], TargetBbox = [1432, 812, 1538, 858], UiTargetText = "Speichern" } },
        };
        var r = Avaga.Evaluate(graph, gt, 0.25);
        Assert.Near(1.0, r.Avaga, 1e-9, "AVAGA");
        Assert.Near(1.0, r.FactualEventPrecision, 1e-9, "FEP");
        Assert.True(r.MeanPixelDistance < 2, "pixel distance");
    }

    // --------------------------------------------------------------------------------------------
    // Frame analysis on synthetic in-memory frames
    // --------------------------------------------------------------------------------------------
    static GrayFrame Frame(int i, double t, int w, int h, Action<byte[]> draw)
    {
        var px = new byte[w * h]; Array.Fill(px, (byte)64); draw(px);
        return new GrayFrame { Index = i, PtsS = t, Width = w, Height = h, Pixels = px };
    }
    static void Rect(byte[] px, int w, int x, int y, int rw, int rh, byte v) { for (int yy = y; yy < y + rh; yy++) for (int xx = x; xx < x + rw; xx++) px[yy * w + xx] = v; }

    [Test] public static void Vision_Detects_Cursor_Dwell_Plus_State_Change_As_Click()
    {
        const int W = 320, H = 180; var frames = new List<GrayFrame>();
        for (int i = 0; i < 40; i++)
        {
            double t = i * 0.05; // 20 fps, 2 s
            int cx = Math.Min(200, 20 + i * 8), cy = Math.Min(100, 20 + i * 4); // moves until frame ~23, then dwells at (200,100)
            bool clicked = t >= 1.5;
            frames.Add(Frame(i, t, W, H, px => { Rect(px, W, 190, 90, 60, 30, clicked ? (byte)200 : (byte)110); Rect(px, W, cx, cy, 4, 6, 255); }));
        }
        var changes = new StateChangeDetector().Detect(frames);
        var cursor = new CursorTracker().Track(frames);
        var events = new ClickCandidateDetector().Detect(cursor, changes);
        Assert.True(changes.Any(c => Math.Abs(c.TimeS - 1.5) < 0.06), $"state change at 1.5s, got [{string.Join(",", changes.Select(c => c.TimeS))}]");
        Assert.Eq(1, events.Count, "one click candidate");
        Assert.True(events[0].Point is not null && Math.Abs(events[0].Point!.X - 200) <= 2 && Math.Abs(events[0].Point!.Y - 100) <= 2, $"point {events[0].Point}");
        Assert.Eq(GroundingStatus.Observed, GroundingPolicy.Classify(events[0]), "observed");
    }

    // --------------------------------------------------------------------------------------------
    // End-to-end on a synthetic FFmpeg screen recording
    // --------------------------------------------------------------------------------------------
    [Test] public static async Task EndToEnd_Synthetic_Screen_Recording()
    {
        var ff = new FfmpegService();
        try { await FfmpegService.RunAsync("ffmpeg", ["-version"], null, default); } catch { Console.WriteLine("       (ffmpeg not found, skipped)"); return; }

        string dir = Path.Combine(Path.GetTempPath(), "avag_e2e_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string video = Path.Combine(dir, "synth.mp4");
        // Cursor moves from (40,60) to (496,264) during 0–12 s, dwells, button at [440,240,560,290] turns green at 12.71 s.
        var (_, err, code) = await FfmpegService.RunAsync("ffmpeg", [
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=0x404040:s=640x360:r=25:d=16",
            "-f", "lavfi", "-i", "color=c=white:s=8x12:r=25:d=16",
            "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono:d=16",
            "-filter_complex", "[0:v]drawbox=x=440:y=240:w=120:h=50:color=0x3366cc@1:t=fill,drawbox=x=440:y=240:w=120:h=50:color=0x66aa33@1:t=fill:enable='gte(t,12.71)'[bg];" +
                               "[bg][1:v]overlay=x='if(lt(t,12),40+t*38,496)':y='if(lt(t,12),60+t*17,264)':eval=frame[v]",
            "-map", "[v]", "-map", "2:a", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video], null, default);
        Assert.Eq(0, code, "ffmpeg synth: " + err);

        string transcript = Path.Combine(dir, "whisperx.json");
        await File.WriteAllTextAsync(transcript, """
        {"language":"de","segments":[{"start":12.22,"end":12.58,"text":"Klicken Sie hier.","speaker":"SPEAKER_00",
          "words":[{"word":"Klicken","start":12.22,"end":12.38,"score":0.97,"speaker":"SPEAKER_00"},{"word":"Sie","start":12.38,"end":12.45,"score":0.95,"speaker":"SPEAKER_00"},{"word":"hier.","start":12.45,"end":12.58,"score":0.96,"speaker":"SPEAKER_00"}]},
          {"start":2.0,"end":3.1,"text":"Das Programm ist jetzt geöffnet.","speaker":"SPEAKER_00",
          "words":[{"word":"Das","start":2.0,"end":2.2,"score":0.9},{"word":"Programm","start":2.2,"end":2.6,"score":0.9},{"word":"ist","start":2.6,"end":2.7,"score":0.9},{"word":"jetzt","start":2.7,"end":2.9,"score":0.9},{"word":"geöffnet.","start":2.9,"end":3.1,"score":0.9}]}]}
        """);
        string uiJson = Path.Combine(dir, "ui.json");
        await File.WriteAllTextAsync(uiJson, """{"elements":[{"bbox":[440,240,560,290],"text":"Speichern","text_confidence":0.98,"interactive_confidence":0.99,"class":"button"}]}""");

        var runner = new PipelineRunner(new PipelineConfig { WorkDir = dir, CoarseFps = 3, FineFps = 20, CoarseWidth = 640 },
            new PipelineServices { Asr = new JsonFileAsr(transcript), UiParser = new JsonFileUiParser(uiJson) });
        var result = await runner.RunAsync(video);
        foreach (var l in result.Log) Console.WriteLine("       " + l);

        Assert.Eq(1, result.AudioRefs.Count, "one action reference parsed");
        var bound = result.Graph.Events.FirstOrDefault(e => e.AudioReferences.Count > 0);
        Assert.True(bound is not null, "audio reference bound to a visual event");
        Assert.Near(12.71, bound!.Temporal.PeakS, 0.12, "click time");
        Assert.Eq(GroundingStatus.Observed, bound.GroundingStatus, "observed grounding");
        Assert.True(bound.Spatial?.PointXyPx is { } p && SpatialMetrics.PointInBox(p, [440, 240, 560, 290]), $"point in button: {bound.Spatial?.PointXyPx?[0]},{bound.Spatial?.PointXyPx?[1]}");
        Assert.Eq("Speichern", bound.Target?.Text, "target text");
        Assert.True(bound.DescriptionDe!.Contains("Speichern"), bound.DescriptionDe!);
        Console.WriteLine("       " + result.TimelineDe.Trim().Replace("\n", "\n       "));
        Directory.Delete(dir, true);
    }
}
