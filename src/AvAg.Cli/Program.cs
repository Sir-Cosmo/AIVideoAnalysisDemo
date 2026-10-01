using System.Globalization;
using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Adapters;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return 0; }

var opts = ParseOptions(args.Skip(1));
try
{
    switch (args[0])
    {
        case "run": return await RunAsync(opts);
        case "eval": return Eval(opts);
        case "timeline": return Timeline(opts);
        default: Usage(); return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void Usage()
{
    Console.WriteLine("""
    avag — Audio-Visual Action Grounding pipeline (C#)

    avag run --video <file.mp4> --out <graph.json> [options]
        --transcript <whisperx.json>   use a precomputed WhisperX JSON instead of the ASR sidecar
        --asr-url <http://host:port>   WhisperX sidecar (default http://127.0.0.1:8011)
        --ui-url <url>                 OmniParser+PaddleOCR sidecar (omit → no UI parsing)
        --ui-json <elements.json>      static UI elements file (tests / golden runs)
        --molmo-url <url>              MolmoPoint sidecar for fallback pointing
        --qwen-url <url>               vLLM OpenAI-compatible endpoint (Qwen3-VL) for description / fallback pointing
        --sam2-url <url>               SAM2 sidecar for target stabilisation
        --cursor <cursor.png>          cursor template (greatly improves tracking)
        --lang <de|en|auto>            ASR language hint (default de; auto = detect)
        --no-diarize                   disable speaker diarization
        --coarse-fps <n>  --fine-fps <n>  --coarse-width <px>
        --keep                         keep intermediate frames/WAV (default: deleted for privacy)
        --describe                     add VLM clip narratives (requires --qwen-url)
        --manual <manual.docx>         also write a step-by-step manual: .docx, .html and .md next to each other
        --manual-lang <de|en>          manual language (default: spoken language)
        --private                      private video: manual is text only, no screenshots
        --llm-url <url/v1> --llm-model <name> [--llm-key <key>]
                                       OpenAI-compatible model that summarises the manual (default: rule-based)

    avag eval --pred <graph.json> --gt <groundtruth.json> [--tol 0.25]
    avag timeline --graph <graph.json> [--lang de|en]
    """);
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var list = args.ToList();
    for (int i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--")) continue;
        string key = list[i][2..];
        if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) d[key] = list[++i];
        else d[key] = "true";
    }
    return d;
}

static async Task<int> RunAsync(Dictionary<string, string> o)
{
    string video = Req(o, "video");
    string outPath = o.GetValueOrDefault("out", Path.ChangeExtension(video, ".events.json"));

    IAsrService asr = o.TryGetValue("transcript", out var tr) ? new JsonFileAsr(tr) : new WhisperXSidecar(o.GetValueOrDefault("asr-url", "http://127.0.0.1:8011"));
    IUiParser ui = o.TryGetValue("ui-json", out var uj) ? new JsonFileUiParser(uj)
                 : o.TryGetValue("ui-url", out var uu) ? new OmniParserSidecar(uu) : new NullUiParser();
    Qwen3VlClient? qwen = o.TryGetValue("qwen-url", out var qu) ? new Qwen3VlClient(qu) : null;
    IVideoGrounder? grounder = o.TryGetValue("molmo-url", out var mu) ? new MolmoPointSidecar(mu) : qwen;
    IObjectTracker? tracker = o.TryGetValue("sam2-url", out var su) ? new Sam2Sidecar(su) : null;

    var cfg = new PipelineConfig
    {
        LanguageHint = o.GetValueOrDefault("lang", "de") is "auto" or "" ? null : o.GetValueOrDefault("lang", "de"),
        Diarize = !o.ContainsKey("no-diarize"),
        CursorTemplatePng = o.GetValueOrDefault("cursor"),
        CoarseFps = Dbl(o, "coarse-fps", 3), FineFps = Dbl(o, "fine-fps", 20), CoarseWidth = (int)Dbl(o, "coarse-width", 960),
        KeepIntermediateFiles = o.ContainsKey("keep"),
        DescribeClips = o.ContainsKey("describe") && qwen is not null,
    };
    var runner = new PipelineRunner(cfg, new PipelineServices { Asr = asr, UiParser = ui, Grounder = grounder, Tracker = tracker, ClipDescriber = qwen });

    var result = await runner.RunAsync(video);
    foreach (var line in result.Log) Console.Error.WriteLine("  " + line);

    await File.WriteAllTextAsync(outPath, Json.Serialize(result.Graph));
    await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".timeline.de.txt"), result.TimelineDe);
    await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".timeline.en.txt"), result.TimelineEn);
    Console.WriteLine(result.TimelineDe);
    Console.Error.WriteLine($"wrote {outPath}");

    if (o.TryGetValue("manual", out var manualPath))
    {
        var llm = o.TryGetValue("llm-url", out var lu)
            ? new ManualLlmSettings { Url = lu, Model = o.GetValueOrDefault("llm-model"), ApiKey = o.GetValueOrDefault("llm-key") } : null;
        var svc = new ManualService(llm);
        var manual = await svc.CreateAsync(result, video, Path.GetFileName(video), o.GetValueOrDefault("manual-lang"), useLlm: llm is not null, privateVideo: o.ContainsKey("private"));
        foreach (var line in svc.Log) Console.Error.WriteLine("  " + line);
        await File.WriteAllBytesAsync(Path.ChangeExtension(manualPath, ".docx"), ManualDocx.Write(manual));
        await File.WriteAllTextAsync(Path.ChangeExtension(manualPath, ".html"), ManualRenderer.Html(manual));
        await File.WriteAllTextAsync(Path.ChangeExtension(manualPath, ".md"), ManualRenderer.Markdown(manual));
        Console.Error.WriteLine($"wrote {Path.ChangeExtension(manualPath, ".docx")} (+ .html, .md): {manual.Steps.Count} steps");
    }
    return 0;
}

static int Eval(Dictionary<string, string> o)
{
    var pred = Json.Deserialize<EventGraph>(File.ReadAllText(Req(o, "pred")));
    var gt = Json.Deserialize<GroundTruthFile>(File.ReadAllText(Req(o, "gt")));
    double tol = Dbl(o, "tol", 0.25);
    var r = Avaga.Evaluate(pred, gt, tol);
    Console.WriteLine($"N={r.N}");
    Console.WriteLine($"AVAGA@{tol * 1000:0}ms          {r.Avaga:P1}");
    Console.WriteLine($"Phrase recall           {r.PhraseRecall:P1}");
    Console.WriteLine($"Action accuracy         {r.ActionAccuracy:P1}");
    Console.WriteLine($"Time success (±{tol * 1000:0} ms) {r.TimeSuccess:P1}");
    Console.WriteLine($"Point hit accuracy      {r.PointHitAccuracy:P1}");
    Console.WriteLine($"Mean / P95 px distance  {r.MeanPixelDistance:0.0} / {r.P95PixelDistance:0.0}");
    Console.WriteLine($"Factual event precision {r.FactualEventPrecision:P1}");
    return 0;
}

static int Timeline(Dictionary<string, string> o)
{
    var graph = Json.Deserialize<EventGraph>(File.ReadAllText(Req(o, "graph")));
    Console.Write(new Describer().Timeline(graph, o.GetValueOrDefault("lang", "de")));
    return 0;
}

static string Req(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
static double Dbl(Dictionary<string, string> o, string k, double def) => o.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
