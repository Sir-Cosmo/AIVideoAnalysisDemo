using System.Text;
using AvAg.Pipeline.Services;

namespace AvAg.Web.Endpoints;

/// <summary>Upload a video, follow the analysis, download results.</summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config", GetConfig);
        app.MapPost("/api/jobs", CreateJobAsync);
        app.MapGet("/api/jobs/{id}", (string id, JobStore store) =>
            store.TryGet(id, out var job) ? Results.Ok(job.ToStatus()) : Results.NotFound());
        app.MapGet("/api/jobs/{id}/graph.json", (string id, JobStore store) =>
            store.TryGet(id, out var job) && job.GraphJson is not null ? Results.Text(job.GraphJson, "application/json") : Results.NotFound());
        app.MapGet("/api/jobs/{id}/timeline.{lang}.txt", (string id, string lang, JobStore store) =>
            store.TryGet(id, out var job) && job.Result is not null
                ? Results.Text(lang == "en" ? job.Result.TimelineEn : job.Result.TimelineDe, "text/plain; charset=utf-8") : Results.NotFound());
        app.MapGet("/api/jobs/{id}/transcript.{fmt}", GetTranscript);
        // Range-enabled video streaming so the player can seek.
        app.MapGet("/api/jobs/{id}/video", (string id, JobStore store) =>
            store.TryGet(id, out var job) && job.VideoPath is not null && File.Exists(job.VideoPath)
                ? Results.File(job.VideoPath, Http.VideoContentType(job.VideoPath), enableRangeProcessing: true) : Results.NotFound());
        app.MapDelete("/api/jobs/{id}", (string id, JobStore store) => { store.Remove(id); return Results.NoContent(); });
    }

    /// <summary>Defaults shown in the page (from appsettings.json / environment).</summary>
    private static object GetConfig(WebSettings settings, AiServiceFactory factory)
    {
        var s = settings.Services;
        static string? UrlOf(ServiceOptions o) => o.IsOff ? null : o.Url;
        return new
        {
            asr_url = UrlOf(s.EffectiveAsr), ui_url = UrlOf(s.UiParser), molmo_url = UrlOf(s.Grounder), sam2_url = UrlOf(s.Tracker), qwen_url = UrlOf(s.ClipDescriber),
            has_demo = File.Exists(Path.Combine(AppContext.BaseDirectory, "samples", "demo.mp4")),
            retention_minutes = settings.RetentionMinutes,
            article_llm = factory.TextGenerator.Resolve(s.TextGenerator) is null ? null : s.TextGenerator.Model ?? s.TextGenerator.Url,
        };
    }

    /// <summary>
    /// Multipart form: video (or use_demo=true), optional transcript / ui_json / cursor files, per-job settings and
    /// service URLs. The page sends a URL field only when it was changed; one that is sent empty switches that service
    /// off for this job ("leave empty to disable").
    /// </summary>
    private static async Task<IResult> CreateJobAsync(HttpRequest req, JobStore store, WebSettings settings, AiServiceFactory factory, JobRunner runner)
    {
        if (!req.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
        var form = await req.ReadFormAsync();
        var job = store.Create();
        IResult Reject(string error) { store.Remove(job.Id); return Results.BadRequest(new { error }); }

        string? transcriptFile = null, uiFile = null;
        if (form.Files.GetFile("video") is { } video)
        {
            job.VideoPath = Path.Combine(job.Dir, "input" + Http.SafeExtension(video.FileName));
            await Http.SaveAsync(video, job.VideoPath);
            job.VideoName = video.FileName;
        }
        else if (form["use_demo"] == "true")
        {
            string samples = Path.Combine(AppContext.BaseDirectory, "samples");
            job.VideoPath = Path.Combine(job.Dir, "input.mp4");
            File.Copy(Path.Combine(samples, "demo.mp4"), job.VideoPath);
            job.VideoName = "demo.mp4 (bundled sample)";
            transcriptFile = Path.Combine(samples, "demo_whisperx.json");
            uiFile = Path.Combine(samples, "demo_ui.json");
        }
        else return Reject("No video uploaded.");

        // Uploaded files replace the corresponding service for this job.
        if (form.Files.GetFile("transcript") is { } tr)
        {
            if (!await Http.LooksLikeJsonAsync(tr))
                return Reject($"\"{tr.FileName}\" is not a WhisperX transcript JSON. Put the video in the video field and leave the transcript field empty to transcribe it.");
            transcriptFile = Path.Combine(job.Dir, "transcript.json");
            await Http.SaveAsync(tr, transcriptFile);
        }
        if (form.Files.GetFile("ui_json") is { } uj)
        {
            uiFile = Path.Combine(job.Dir, "ui.json");
            await Http.SaveAsync(uj, uiFile);
        }
        if (form.Files.GetFile("cursor") is { } cur)
        {
            job.CursorPath = Path.Combine(job.Dir, "cursor" + Http.SafeExtension(cur.FileName));
            await Http.SaveAsync(cur, job.CursorPath);
        }

        job.Private = form["private"] == "true";
        var services = settings.Services.WithOverrides(new ServiceOverrides(
            AsrUrl: Http.Field(form, "asr_url"), UiParserUrl: Http.Field(form, "ui_url"), GrounderUrl: Http.Field(form, "molmo_url"),
            TrackerUrl: Http.Field(form, "sam2_url"), ClipDescriberUrl: Http.Field(form, "qwen_url"),
            TranscriptFile: transcriptFile, UiElementsFile: uiFile));
        // A private video never leaves this machine / network: no cloud speech recognition, no cloud models.
        if (job.Private) services = services.LocalOnly();
        if (factory.Asr.Resolve(services.Asr) is null)
            return Reject(job.Private
                ? "No local speech recognition for a private video: start the local WhisperX sidecar or upload a WhisperX transcript JSON."
                : "No way to get speech: either upload a WhisperX transcript JSON or set the speech-recognition URL.");

        job.Options = new RunOptions
        {
            Services = services, ServiceSummary = factory.Describe(services),
            Language = Http.Opt(form, "lang"), Diarize = form["diarize"] != "false",
            CoarseFps = Http.Double(form, "coarse_fps", 3), FineFps = Http.Double(form, "fine_fps", 20),
        };
        runner.Start(job);
        return Results.Ok(new { id = job.Id });
    }

    /// <summary>Full speech transcript (all segments, not only the action references) as plain text or SRT subtitles.</summary>
    private static IResult GetTranscript(string id, string fmt, JobStore store)
    {
        if (!store.TryGet(id, out var job) || job.Result is null) return Results.NotFound();
        var segs = job.Result.Transcript.Segments;
        var sb = new StringBuilder();
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
    }
}
