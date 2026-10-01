using System.Globalization;
using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Manuals;
using AvAg.Pipeline.Services;

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
        --lang <de|en|auto>            spoken language (default de; auto = detect)
        --cursor <cursor.png>          cursor template (greatly improves tracking)
        --no-diarize                   disable speaker labels
        --coarse-fps <n>  --fine-fps <n>  --coarse-width <px>
        --keep                         keep intermediate frames/WAV (default: deleted for privacy)
        --describe                     add clip narratives (needs a clip describer, e.g. --qwen-url)

      AI services – every capability can use any registered provider:
        --<cap>-provider <name>  --<cap>-url <url>  --<cap>-model <name>  --<cap>-key <key>
        with <cap> = asr | ui | grounder | tracker | describer | llm   (provider "none" switches it off)
        Built-in providers: asr: whisperx (default, http://127.0.0.1:8011), whisperx-json
                            ui: omniparser, ui-json        grounder: molmo, qwen-vl
                            tracker: sam2                  describer: qwen-vl
                            llm: openai-compatible (aliases ollama, openai, azure-openai, lm-studio, vllm)
      Shortcuts:
        --transcript <whisperx.json>   = --asr-provider whisperx-json (no speech service needed)
        --ui-json <elements.json>      = --ui-provider ui-json
        --molmo-url <url>  --sam2-url <url>  --qwen-url <url> (Qwen3-VL for narratives and fallback pointing)

      Manual:
        --manual <manual.docx>         also write a step-by-step manual: .docx, .html and .md next to each other
        --manual-lang <de|en>          manual language (default: spoken language)
        --private                      private video: manual is text only, no screenshots
        --llm-url <url/v1> --llm-model <name> [--llm-key <key>]
                                       language model that writes the manual (default: rule-based)

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

/// <summary>CLI flags → the same service configuration the web app reads from appsettings.json.</summary>
static AiServicesOptions ServicesFrom(Dictionary<string, string> o)
{
    var s = new AiServicesOptions();
    if (o.TryGetValue("transcript", out var tr)) s.Asr = ServiceOptions.FromFile("whisperx-json", tr);
    if (o.TryGetValue("ui-json", out var uj)) s.UiParser = ServiceOptions.FromFile("ui-json", uj);
    if (o.TryGetValue("molmo-url", out var mu)) s.Grounder = new() { Provider = "molmo", Url = mu };
    if (o.TryGetValue("sam2-url", out var su)) s.Tracker = new() { Provider = "sam2", Url = su };
    if (o.TryGetValue("qwen-url", out var qu))
    {
        s.ClipDescriber = new() { Provider = "qwen-vl", Url = qu };
        if (!o.ContainsKey("molmo-url")) s.Grounder = new() { Provider = "qwen-vl", Url = qu };
    }
    Apply(s.Asr, "asr"); Apply(s.UiParser, "ui"); Apply(s.Grounder, "grounder");
    Apply(s.Tracker, "tracker"); Apply(s.ClipDescriber, "describer"); Apply(s.TextGenerator, "llm");
    return s;

    void Apply(ServiceOptions so, string cap)
    {
        if (o.TryGetValue($"{cap}-url", out var url)) { so.Url = url; if (so.Provider == ServiceOptions.None) so.Provider = null; }
        if (o.TryGetValue($"{cap}-provider", out var p)) so.Provider = p;
        if (o.TryGetValue($"{cap}-model", out var m)) so.Model = m;
        if (o.TryGetValue($"{cap}-key", out var k)) so.ApiKey = k;
    }
}

static async Task<int> RunAsync(Dictionary<string, string> o)
{
    string video = Req(o, "video");
    string outPath = o.GetValueOrDefault("out", Path.ChangeExtension(video, ".events.json"));
    var factory = AiServiceFactory.CreateDefault();
    var services = ServicesFrom(o);
    var pipelineServices = factory.CreatePipelineServices(services);

    var cfg = new PipelineConfig
    {
        LanguageHint = o.GetValueOrDefault("lang", "de") is "auto" or "" ? null : o.GetValueOrDefault("lang", "de"),
        Diarize = !o.ContainsKey("no-diarize"),
        CursorTemplatePng = o.GetValueOrDefault("cursor"),
        CoarseFps = Dbl(o, "coarse-fps", 3), FineFps = Dbl(o, "fine-fps", 20), CoarseWidth = (int)Dbl(o, "coarse-width", 960),
        KeepIntermediateFiles = o.ContainsKey("keep"),
        DescribeClips = o.ContainsKey("describe") && pipelineServices.ClipDescriber is not null,
    };
    var result = await new PipelineRunner(cfg, pipelineServices).RunAsync(video);
    foreach (var line in result.Log) Console.Error.WriteLine("  " + line);

    await File.WriteAllTextAsync(outPath, Json.Serialize(result.Graph));
    await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".timeline.de.txt"), result.TimelineDe);
    await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".timeline.en.txt"), result.TimelineEn);
    Console.WriteLine(result.TimelineDe);
    Console.Error.WriteLine($"wrote {outPath}");

    if (o.TryGetValue("manual", out var manualPath))
    {
        var svc = new ManualService(factory.CreateManualWriter(services));
        var manual = await svc.CreateAsync(result, video, Path.GetFileName(video),
            new ManualRequest(o.GetValueOrDefault("manual-lang"), UseWriter: true, Private: o.ContainsKey("private")));
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
