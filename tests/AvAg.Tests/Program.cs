using System.Globalization;
using System.Reflection;
using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Adapters;
using AvAg.Pipeline.Manuals;
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
    // Manual
    // --------------------------------------------------------------------------------------------
    static Transcript Spoken(string lang, params (double s, string text)[] sentences) =>
        new(lang, sentences.Select(x =>
        {
            var ws = x.text.Split(' ');
            var words = ws.Select((w, i) => new Word(w, x.s + i * 0.3, x.s + i * 0.3 + 0.25)).ToList();
            return new TranscriptSegment(x.s, words[^1].EndS, x.text, words);
        }).ToList());

    [Test] public static void Manual_Steps_From_Instructions_With_Click_Marker()
    {
        var tr = Spoken("de",
            (0.0, "In diesem Video zeige ich, wie man eine Rechnung speichert."),
            (4.0, "Äh, klicken Sie oben auf Datei."),
            (8.0, "Dann öffnet sich das Menü mit allen Optionen."),
            (12.0, "Klicken Sie hier auf Speichern."),
            (16.0, "Abonniert den Kanal und bis zum nächsten Mal."));
        var refs = new AudioRefParser().Parse(tr);
        var video = new VideoInfo("v", 20, 1920, 1080, 30);
        var ui = new UiElementRegistry();
        ui.Observe(13.0, [new UiElementRegistry.Detection(new BBox(1432, 812, 1538, 858), "Speichern", 0.98, 0.99, "button")]);
        var save = refs.Single(r => r.StartS >= 12);
        var e = Ev("vis_0001", save.AnchorS + 0.1, new(1489, 836)); e.TargetId = "ui_000";
        var graph = new EventGraphBuilder().Build(video, refs, [e], new CrossModalResolver().Resolve(refs, [e], ui), ui);

        var m = new ManualBuilder().Build(tr, refs, graph, "Rechnung speichern.mp4");
        Assert.Eq("de", m.Language, "language from transcript");
        Assert.Eq("Anleitung: Rechnung speichern", m.Title, "title from file name");
        Assert.Eq(2, m.Steps.Count, "two instructions → two steps");
        Assert.True(m.Steps[0].Instruction.StartsWith("Klicken Sie oben auf Datei"), "filler removed: " + m.Steps[0].Instruction);
        Assert.True(m.Steps[0].Details?.Contains("Menü") == true, "explanation attached to previous step");
        Assert.True(m.Summary?.Contains("Rechnung") == true, "intro becomes the overview");
        Assert.Eq(1489, m.Steps[1].PointXyPx?[0] ?? -1, "observed click marker on the save step");
        Assert.True(!m.Steps.Any(s => s.Instruction.Contains("Abonniert")) && m.Steps[1].Details is null, "outro dropped");

        var html = ManualRenderer.Html(m);
        Assert.True(html.Contains("Schritt für Schritt") && html.Contains("Klicken Sie hier auf Speichern."), "html");
        var docx = ManualDocx.Write(m);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
        var doc = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        System.Xml.Linq.XDocument.Parse(doc);
        Assert.True(doc.Contains("Speichern") && zip.GetEntry("word/styles.xml") is not null, "docx");
    }

    [Test] public static void Manual_Sections_And_Key_Combinations()
    {
        var m = new Manual
        {
            Title = "T", Language = "en",
            Steps =
            [
                new ManualStep { Number = 1, Section = "Method 1: Shortcut", Instruction = "Press Windows key + Shift + R.", TimeS = 1 },
                new ManualStep { Number = 2, Section = "Method 2: Snipping Tool", Instruction = "Open the Snipping Tool and pick step 1 + 2.", TimeS = 9 },
            ],
        };
        var html = ManualRenderer.Html(m);
        Assert.True(html.Contains("<kbd>Windows key</kbd> + <kbd>Shift</kbd> + <kbd>R</kbd>"), "key combination as kbd");
        Assert.True(!html.Contains("<kbd>1</kbd>"), "ordinary '1 + 2' is not a key combination");
        Assert.True(html.Contains("<h3 class=\"section\">Method 2: Snipping Tool</h3>") && html.Contains("start=\"2\""), "sections continue numbering");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(ManualDocx.Write(m)));
        var doc = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
        System.Xml.Linq.XDocument.Parse(doc);
        Assert.True(doc.Contains("<w:b/></w:rPr><w:t xml:space=\"preserve\">Windows key + Shift + R</w:t>") && doc.Contains("Heading3"), "docx bold keys + section headings");
    }

    [Test] public static async Task Manual_Private_Video_Is_Text_Only()
    {
        var tr = Spoken("de", (0.0, "Hier zeige ich das Speichern."), (4.0, "Klicken Sie hier auf Speichern."));
        var refs = new AudioRefParser().Parse(tr);
        var video = new VideoInfo("v", 10, 1920, 1080, 30);
        var e = Ev("vis_0001", refs[0].AnchorS + 0.1, new(1489, 836));
        var graph = new EventGraphBuilder().Build(video, refs, [e], new CrossModalResolver().Resolve(refs, [e], new UiElementRegistry()), new UiElementRegistry());
        var result = new PipelineResult { Graph = graph, Transcript = tr, AudioRefs = refs, VisualEvents = [e], TimelineDe = "", TimelineEn = "" };

        // The video path does not exist: a text-only manual must not even try to read a frame from it.
        var svc = new ManualService();
        var m = await svc.CreateAsync(result, "does-not-exist.mp4", "Speichern.mp4", new ManualRequest(Private: true));
        Assert.True(m.Private && m.Steps.Count == 1, "one private step");
        Assert.True(m.Steps.All(s => s.ScreenshotJpeg is null && s.ScreenshotS is null && s.PointXyPx is null), "no screenshot, no click position");
        Assert.True(!svc.Log.Any(l => l.Contains("screenshot at")), "no frame extraction attempted");
        var html = ManualRenderer.Html(m);
        Assert.True(!html.Contains("<img") && html.Contains("privates Video, nur Text") && html.Contains("Klicken Sie hier auf Speichern."), "html text only");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(ManualDocx.Write(m)));
        Assert.True(!zip.Entries.Any(x => x.FullName.StartsWith("word/media/")), "docx without images");
    }

    // --------------------------------------------------------------------------------------------
    // Replaceable AI services
    // --------------------------------------------------------------------------------------------
    sealed class FakeTextGenerator(string answer) : ITextGenerator
    {
        public TextGenerationRequest? LastRequest { get; private set; }
        public string Name => "fake";
        public Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken ct = default) { LastRequest = request; return Task.FromResult(answer); }
    }

    [Test] public static void ServiceFactory_Resolves_Providers_By_Name()
    {
        var f = AiServiceFactory.CreateDefault();
        Assert.True(f.Asr.Create(new ServiceOptions { Provider = "whisperx", Url = "http://127.0.0.1:8011" }) is WhisperXSidecar, "named provider");
        Assert.True(f.UiParser.Create(new ServiceOptions { Url = "http://127.0.0.1:8003" }) is OmniParserSidecar, "Url only → default provider");
        Assert.True(f.Grounder.Create(ServiceOptions.Off()) is null && f.Tracker.Create(new ServiceOptions()) is null, "none / empty → off");
        Assert.True(f.TextGenerator.Create(new ServiceOptions { Provider = "ollama", Url = "http://127.0.0.1:11434/v1", Model = "m" }) is OpenAiCompatibleTextGenerator, "alias");
        Assert.True(f.Grounder.Create(ServiceOptions.Off().WithUrl("http://127.0.0.1:8002")) is MolmoPointSidecar, "URL override switches an off capability on");

        try { f.Asr.Create(new ServiceOptions { Provider = "nope", Url = "http://x" }); throw new Exception("unknown provider accepted"); }
        catch (InvalidOperationException ex) { Assert.True(ex.Message.Contains("whisperx") && ex.Message.Contains("nope"), "error lists known providers: " + ex.Message); }

        // Another AI is one registration away.
        var custom = new FakeTextGenerator("{}");
        f.TextGenerator.Register("my-llm", _ => custom);
        Assert.True(f.CreateManualWriter(new AiServicesOptions { TextGenerator = new ServiceOptions { Provider = "my-llm" } }) is LlmManualWriter, "custom provider");
        var services = f.CreatePipelineServices(new AiServicesOptions { Asr = ServiceOptions.FromFile("whisperx-json", "t.json") });
        Assert.True(services.Asr is JsonFileAsr && services.UiParser is NullUiParser && services.Grounder is null, "pipeline services");
    }

    [Test] public static async Task LlmManualWriter_Anchors_Steps_To_Transcript_Sentences()
    {
        var tr = Spoken("en", (0.0, "Today we save a file."), (4.0, "Click File at the top."), (8.0, "Then press Ctrl + S to save it."));
        var sentences = ManualBuilder.Sentences(tr);
        var draft = new ManualBuilder().Build(tr, new AudioRefParser().Parse(tr), null, "save.mp4");
        var llm = new FakeTextGenerator("""
            Here you go: {"title": "Save a file", "summary": "You save a file.", "prerequisites": ["none"],
             "steps": [{"title": "Save", "instruction": "Press Ctrl + S.", "details": null, "sentences": [3]},
                       {"section": null, "title": "Open File", "instruction": "Click File.", "sentences": ["2"]}],
             "tips": ["Use the toolbar instead."]}
            """);
        var m = await new LlmManualWriter(llm).WriteAsync(draft, sentences);
        Assert.True(llm.LastRequest!.Json && llm.LastRequest.User.Contains("[2] (00:04) Click File at the top."), "numbered transcript in the prompt");
        Assert.Eq("llm:fake", m.Method, "method");
        Assert.Eq("Click File.", m.Steps[0].Instruction, "steps in video order");
        Assert.Near(4.0, m.Steps[0].TimeS, 1e-6, "time from sentence 2, not from the model");
        Assert.Near(8.0, m.Steps[1].TimeS, 1e-6, "time from sentence 3");
        Assert.True(m.Prerequisites.Count == 0 && m.Tips.Count == 1, "'none' prerequisite dropped");

        try { LlmManualWriter.Parse("""{"steps": [{"instruction": "Do it."}]}""", draft, sentences); throw new Exception("accepted"); }
        catch (InvalidOperationException) { /* fewer than 2 steps / not tied to the transcript */ }
    }

    [Test] public static async Task ManualService_Keeps_Draft_When_The_Model_Fails()
    {
        var tr = Spoken("en", (0.0, "Today we save a file."), (4.0, "Click File at the top."));
        var refs = new AudioRefParser().Parse(tr);
        var graph = new EventGraphBuilder().Build(new VideoInfo("v", 10, 640, 360, 30), refs, [], [], new UiElementRegistry());
        var result = new PipelineResult { Graph = graph, Transcript = tr, AudioRefs = refs, VisualEvents = [], TimelineDe = "", TimelineEn = "" };

        var svc = new ManualService(new LlmManualWriter(new FakeTextGenerator("sorry, no JSON")));
        var m = await svc.CreateAsync(result, "does-not-exist.mp4", "v.mp4", new ManualRequest(Private: true));
        Assert.Eq("extractive", m.Method, "rule-based draft kept");
        Assert.True(m.Steps.Count >= 1 && svc.Log.Any(l => l.Contains("kept the rule-based manual")), "fallback logged");
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
