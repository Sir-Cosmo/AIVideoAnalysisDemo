using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Adapters;
using AvAg.Web;
using Microsoft.AspNetCore.Http.Features;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024); // 2 GB uploads
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton(builder.Configuration.GetSection("AvAg").Get<WebSettings>() ?? new WebSettings());
builder.Services.AddSingleton<SidecarLauncher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SidecarLauncher>());
builder.Services.AddHostedService<JobJanitor>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

var app = builder.Build();
var cfg = app.Services.GetRequiredService<WebSettings>();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- defaults shown in the UI (sidecar URLs from appsettings.json / environment) ----
app.MapGet("/api/config", () => new
{
    asr_url = cfg.AsrUrl, ui_url = cfg.UiUrl, molmo_url = cfg.MolmoUrl, sam2_url = cfg.Sam2Url, qwen_url = cfg.QwenUrl,
    has_demo = File.Exists(Path.Combine(AppContext.BaseDirectory, "samples", "demo.mp4")),
    retention_minutes = cfg.RetentionMinutes,
    manual_llm = string.IsNullOrWhiteSpace(cfg.ManualLlm.Url) ? null : cfg.ManualLlm.Model ?? cfg.ManualLlm.Url,
});

// ---- create a job: multipart form with video (+ optional transcript/ui JSON + options) ----
app.MapPost("/api/jobs", async (HttpRequest req, JobStore store, SidecarLauncher sidecars, ILogger<Program> log) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
    var form = await req.ReadFormAsync();
    var job = store.Create(cfg);

    var video = form.Files.GetFile("video");
    if (video is not null)
    {
        job.VideoPath = Path.Combine(job.Dir, "input" + SafeExt(video.FileName));
        await using var fs = File.Create(job.VideoPath);
        await video.CopyToAsync(fs);
        job.VideoName = video.FileName;
    }
    else if (form["use_demo"] == "true")
    {
        job.VideoPath = Path.Combine(job.Dir, "input.mp4");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "demo.mp4"), job.VideoPath);
        job.VideoName = "demo.mp4 (bundled sample)";
        job.TranscriptPath = Path.Combine(AppContext.BaseDirectory, "samples", "demo_whisperx.json");
        job.UiJsonPath = Path.Combine(AppContext.BaseDirectory, "samples", "demo_ui.json");
    }
    else return Results.BadRequest(new { error = "No video uploaded." });

    if (form.Files.GetFile("transcript") is { } tr)
    {
        if (!await LooksLikeJson(tr))
        {
            store.Remove(job.Id);
            return Results.BadRequest(new { error = $"\"{tr.FileName}\" is not a WhisperX transcript JSON. Put the video in the video field and leave the transcript field empty to transcribe it." });
        }
        job.TranscriptPath = Path.Combine(job.Dir, "transcript.json"); await Save(tr, job.TranscriptPath);
    }
    job.Private = form["private"] == "true";
    if (form.Files.GetFile("ui_json") is { } uj) { job.UiJsonPath = Path.Combine(job.Dir, "ui.json"); await Save(uj, job.UiJsonPath); }
    if (form.Files.GetFile("cursor") is { } cur) { job.CursorPath = Path.Combine(job.Dir, "cursor" + SafeExt(cur.FileName)); await Save(cur, job.CursorPath); }

    job.Options = new RunOptions
    {
        AsrUrl = Opt(form, "asr_url"), UiUrl = Opt(form, "ui_url"), MolmoUrl = Opt(form, "molmo_url"), Sam2Url = Opt(form, "sam2_url"), QwenUrl = Opt(form, "qwen_url"),
        Language = Opt(form, "lang"), Diarize = form["diarize"] != "false",
        CoarseFps = Dbl(form, "coarse_fps", 3), FineFps = Dbl(form, "fine_fps", 20),
    };

    if (job.TranscriptPath is null && string.IsNullOrWhiteSpace(job.Options.AsrUrl))
    {
        store.Remove(job.Id);
        return Results.BadRequest(new { error = "No way to get speech: either upload a WhisperX transcript JSON or set the ASR sidecar URL." });
    }

    _ = Task.Run(() => RunJobAsync(job, sidecars, log));
    return Results.Ok(new { id = job.Id });
});

app.MapGet("/api/jobs/{id}", (string id, JobStore store) =>
    store.TryGet(id, out var job) ? Results.Ok(job.ToStatus()) : Results.NotFound());

app.MapGet("/api/jobs/{id}/graph.json", (string id, JobStore store) =>
    store.TryGet(id, out var job) && job.GraphJson is not null
        ? Results.Text(job.GraphJson, "application/json") : Results.NotFound());

app.MapGet("/api/jobs/{id}/timeline.{lang}.txt", (string id, string lang, JobStore store) =>
    store.TryGet(id, out var job) && job.Result is not null
        ? Results.Text(lang == "en" ? job.Result.TimelineEn : job.Result.TimelineDe, "text/plain; charset=utf-8") : Results.NotFound());

// Full speech transcript (all segments, not only the action references) as plain text or SRT subtitles.
app.MapGet("/api/jobs/{id}/transcript.{fmt}", (string id, string fmt, JobStore store) =>
{
    if (!store.TryGet(id, out var job) || job.Result is null) return Results.NotFound();
    var segs = job.Result.Transcript.Segments;
    var sb = new System.Text.StringBuilder();
    if (fmt == "srt")
    {
        static string T(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss\,fff");
        for (int i = 0; i < segs.Count; i++)
            sb.Append(i + 1).Append('\n').Append(T(segs[i].StartS)).Append(" --> ").Append(T(segs[i].EndS)).Append('\n')
              .Append(segs[i].Speaker is { } sp ? $"[{sp}] " : "").Append(segs[i].Text).Append("\n\n");
    }
    else if (fmt == "txt")
        foreach (var s in segs)
            sb.Append('[').Append(TimeSpan.FromSeconds(s.StartS).ToString(@"hh\:mm\:ss")).Append("] ")
              .Append(s.Speaker is { } sp ? $"{sp}: " : "").Append(s.Text).Append('\n');
    else return Results.NotFound();
    return Results.Text(sb.ToString(), fmt == "srt" ? "application/x-subrip; charset=utf-8" : "text/plain; charset=utf-8");
});

// Range-enabled video streaming so the player can seek.
app.MapGet("/api/jobs/{id}/video", (string id, JobStore store) =>
    store.TryGet(id, out var job) && job.VideoPath is not null && File.Exists(job.VideoPath)
        ? Results.File(job.VideoPath, ContentType(job.VideoPath), enableRangeProcessing: true) : Results.NotFound());

// ---- step-by-step manual from a finished job ----
app.MapPost("/api/jobs/{id}/manual", async (string id, HttpRequest req, JobStore store) =>
{
    if (!store.TryGet(id, out var job) || job.Result is null || job.VideoPath is null) return Results.NotFound(new { error = "Analyse the video first." });
    var form = req.HasFormContentType ? await req.ReadFormAsync() : null;
    string? lang = form is null ? null : Opt(form, "lang");
    bool useLlm = form is not null && form["llm"] == "true";
    var svc = new ManualService(cfg.ManualLlm);
    // A video marked private at upload can never get screenshots; otherwise the manual request may ask for text only.
    bool textOnly = job.Private || form is not null && form["private"] == "true";
    job.Manual = await svc.CreateAsync(job.Result, job.VideoPath, job.VideoName, lang, useLlm, textOnly, job.Cancel.Token);
    job.Log.AddRange(svc.Log);
    return Results.Ok(new { title = job.Manual.Title, steps = job.Manual.Steps.Count, method = job.Manual.Method, @private = job.Manual.Private, log = svc.Log });
});

app.MapGet("/api/jobs/{id}/manual.{fmt}", (string id, string fmt, JobStore store) =>
{
    if (!store.TryGet(id, out var job) || job.Manual is not { } m) return Results.NotFound();
    string name = FileName(m.Title);
    return fmt switch
    {
        "html" => Results.Text(ManualRenderer.Html(m), "text/html; charset=utf-8"),
        "md" => Results.File(System.Text.Encoding.UTF8.GetBytes(ManualRenderer.Markdown(m)), "text/markdown; charset=utf-8", name + ".md"),
        "docx" => Results.File(ManualDocx.Write(m), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", name + ".docx"),
        _ => Results.NotFound(),
    };
});

app.MapDelete("/api/jobs/{id}", (string id, JobStore store) => { store.Remove(id); return Results.NoContent(); });

app.Run();

// ------------------------------------------------------------------------------------------------
static async Task RunJobAsync(Job job, SidecarLauncher sidecars, ILogger log)
{
    try
    {
        job.State = JobState.Running;
        var o = job.Options!;
        if (job.TranscriptPath is null) await sidecars.WaitUntilReadyAsync(o.AsrUrl, job.Log.Add, job.Cancel.Token);
        IAsrService asr = job.TranscriptPath is not null ? new JsonFileAsr(job.TranscriptPath) : new WhisperXSidecar(o.AsrUrl!);
        IUiParser ui = job.UiJsonPath is not null ? new JsonFileUiParser(job.UiJsonPath)
                     : !string.IsNullOrWhiteSpace(o.UiUrl) ? new OmniParserSidecar(o.UiUrl) : new NullUiParser();
        Qwen3VlClient? qwen = string.IsNullOrWhiteSpace(o.QwenUrl) ? null : new Qwen3VlClient(o.QwenUrl);
        IVideoGrounder? grounder = !string.IsNullOrWhiteSpace(o.MolmoUrl) ? new MolmoPointSidecar(o.MolmoUrl) : qwen;
        IObjectTracker? tracker = string.IsNullOrWhiteSpace(o.Sam2Url) ? null : new Sam2Sidecar(o.Sam2Url);

        var runner = new PipelineRunner(new PipelineConfig
        {
            WorkDir = Path.Combine(job.Dir, "work"), LanguageHint = o.Language, Diarize = o.Diarize,
            CoarseFps = o.CoarseFps, FineFps = o.FineFps, CursorTemplatePng = job.CursorPath,
        }, new PipelineServices { Asr = asr, UiParser = ui, Grounder = grounder, Tracker = tracker, ClipDescriber = qwen });

        var result = await runner.RunAsync(job.VideoPath!, job.Cancel.Token);
        job.Result = result;
        job.GraphJson = Json.Serialize(result.Graph);
        job.Log.AddRange(result.Log);
        job.State = JobState.Done;
    }
    catch (OperationCanceledException) { job.State = JobState.Failed; job.Error = "Cancelled."; }
    catch (HttpRequestException ex)
    {
        job.State = JobState.Failed;
        job.Error = $"A model service could not be reached ({ex.Message}). Start the Python sidecars, or upload a transcript JSON and UI-elements JSON to run without them.";
    }
    catch (Exception ex)
    {
        log.LogError(ex, "job {Id} failed", job.Id);
        job.State = JobState.Failed;
        job.Error = ex.Message;
    }
}

static string FileName(string title)
{
    var s = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? ' ' : c).ToArray());
    s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    return s.Length == 0 ? "manual" : s[..Math.Min(s.Length, 80)];
}
static string? Opt(IFormCollection f, string k) => string.IsNullOrWhiteSpace(f[k]) ? null : f[k].ToString().Trim();
static double Dbl(IFormCollection f, string k, double d) => double.TryParse(f[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
static string SafeExt(string name) { var e = Path.GetExtension(name).ToLowerInvariant(); return e is ".mp4" or ".mkv" or ".mov" or ".webm" or ".avi" or ".png" or ".jpg" ? e : ".bin"; }
static string ContentType(string p) => Path.GetExtension(p).ToLowerInvariant() switch { ".webm" => "video/webm", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4" };
static async Task Save(IFormFile f, string path) { await using var fs = File.Create(path); await f.CopyToAsync(fs); }
// A WhisperX transcript starts with '{' (after an optional UTF-8 BOM / whitespace); catches videos dropped into the wrong field.
static async Task<bool> LooksLikeJson(IFormFile f)
{
    await using var s = f.OpenReadStream();
    var buf = new byte[64];
    int n = await s.ReadAsync(buf);
    int i = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
    while (i < n && buf[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
    return i < n && buf[i] == (byte)'{';
}
