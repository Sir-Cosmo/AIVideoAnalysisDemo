using System.Text;
using AvAg.Core;
using AvAg.Pipeline.Articles;
using AvAg.Pipeline.Services;

namespace AvAg.Web.Endpoints;

/// <summary>Create and download the wiki article of an analysed support call.</summary>
public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/jobs/{id}/article", CreateArticleAsync);
        app.MapGet("/api/jobs/{id}/article.{fmt}", GetArticle);
    }

    /// <summary>Form: lang (de/en/empty = spoken), llm (true/false), private (true/false).</summary>
    private static async Task<IResult> CreateArticleAsync(string id, HttpRequest req, JobStore store, WebSettings settings, AiServiceFactory factory)
    {
        if (!store.TryGet(id, out var job) || job.Result is null || job.VideoPath is null) return Results.NotFound(new { error = "Analyse the video first." });
        var form = req.HasFormContentType ? await req.ReadFormAsync() : null;
        var request = new ArticleRequest(
            Language: form is null ? null : Http.Opt(form, "lang"),
            UseWriter: form is not null && form["llm"] == "true",
            // A video marked private at upload can never get screenshots; otherwise the request may ask for text only.
            Private: job.Private || form is not null && form["private"] == "true");

        // A broken model configuration must not cost the article: fall back to the rule-based one and say why.
        string? problem = null;
        var writer = request.UseWriter ? factory.TryCreateArticleWriter(settings.Services, out problem) : null;
        var svc = new ArticleService(writer, writerProblem: problem);
        job.Article = await svc.CreateAsync(job.Result, job.VideoPath, request, job.Cancel.Token);
        job.Log.AddRange(svc.Log);
        var a = job.Article;
        return Results.Ok(new
        {
            title = a.Title, steps = a.Steps.Count, method = a.Method, @private = a.Private, resolved = a.Resolved,
            screenshots = a.Steps.Count(s => s.ScreenshotFile is not null), redactions = a.Redactions, log = svc.Log,
        });
    }

    /// <summary>md: Markdown only (private articles, or to copy into a wiki editor); zip: Markdown + images folder; html: preview.</summary>
    private static IResult GetArticle(string id, string fmt, JobStore store)
    {
        if (!store.TryGet(id, out var job) || job.Article is not { } a) return Results.NotFound();
        string name = ArticlePackage.Slug(a.Title);
        return fmt switch
        {
            "md" => Results.File(Encoding.UTF8.GetBytes(ArticleRenderer.Markdown(a)), "text/markdown; charset=utf-8", name + ".md"),
            "zip" => Results.File(ArticlePackage.Zip(a), "application/zip", name + ".zip"),
            "html" => Results.Text(ArticleRenderer.Html(a), "text/html; charset=utf-8"),
            _ => Results.NotFound(),
        };
    }
}
