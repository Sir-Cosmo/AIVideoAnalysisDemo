using System.Text;
using AvAg.Core;
using AvAg.Pipeline.Manuals;
using AvAg.Pipeline.Services;

namespace AvAg.Web.Endpoints;

/// <summary>Create and download the step-by-step manual of an analysed video.</summary>
public static class ManualEndpoints
{
    public static void MapManualEndpoints(this WebApplication app)
    {
        app.MapPost("/api/jobs/{id}/manual", CreateManualAsync);
        app.MapGet("/api/jobs/{id}/manual.{fmt}", GetManual);
    }

    /// <summary>Form: lang (de/en/empty = spoken), llm (true/false), private (true/false).</summary>
    private static async Task<IResult> CreateManualAsync(string id, HttpRequest req, JobStore store, WebSettings settings, AiServiceFactory factory)
    {
        if (!store.TryGet(id, out var job) || job.Result is null || job.VideoPath is null) return Results.NotFound(new { error = "Analyse the video first." });
        var form = req.HasFormContentType ? await req.ReadFormAsync() : null;
        var request = new ManualRequest(
            Language: form is null ? null : Http.Opt(form, "lang"),
            UseWriter: form is not null && form["llm"] == "true",
            // A video marked private at upload can never get screenshots; otherwise the request may ask for text only.
            Private: job.Private || form is not null && form["private"] == "true");

        var svc = new ManualService(request.UseWriter ? factory.CreateManualWriter(settings.Services) : null);
        job.Manual = await svc.CreateAsync(job.Result, job.VideoPath, job.VideoName, request, job.Cancel.Token);
        job.Log.AddRange(svc.Log);
        return Results.Ok(new { title = job.Manual.Title, steps = job.Manual.Steps.Count, method = job.Manual.Method, @private = job.Manual.Private, log = svc.Log });
    }

    private static IResult GetManual(string id, string fmt, JobStore store)
    {
        if (!store.TryGet(id, out var job) || job.Manual is not { } m) return Results.NotFound();
        string name = Http.FileName(m.Title, "manual");
        return fmt switch
        {
            "html" => Results.Text(ManualRenderer.Html(m), "text/html; charset=utf-8"),
            "md" => Results.File(Encoding.UTF8.GetBytes(ManualRenderer.Markdown(m)), "text/markdown; charset=utf-8", name + ".md"),
            "docx" => Results.File(ManualDocx.Write(m), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", name + ".docx"),
            _ => Results.NotFound(),
        };
    }
}
