using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Articles;

/// <summary>Options for one article.</summary>
/// <param name="Language">"de", "en" or null for the spoken language.</param>
/// <param name="UseWriter">Let the configured <see cref="IArticleWriter"/> (language model) write the text.</param>
/// <param name="Private">Text only: no frame is extracted from the video and no click position is kept.</param>
public sealed record ArticleRequest(string? Language = null, bool UseWriter = true, bool Private = false);

/// <summary>
/// Creates the wiki article for an analysed support call:
/// <list type="number">
/// <item>rule-based draft (<see cref="ArticleBuilder"/>) – always works, offline;</item>
/// <item>optional article by an <see cref="IArticleWriter"/> (any language model). Whatever goes wrong there –
/// unreachable, timeout, unusable answer, a bug in a custom writer – the draft is kept and the reason logged;</item>
/// <item>personal data removed from all text (<see cref="PersonalDataRedactor"/>);</item>
/// <item>screenshots: moments and click markers planned, refined and extracted (<see cref="ArticleScreenshotService"/>) –
/// skipped entirely for private videos.</item>
/// </list>
/// </summary>
public sealed class ArticleService
{
    private readonly IArticleWriter? _writer;
    private readonly ArticleScreenshotService _screenshots;
    public List<string> Log { get; } = new();

    /// <param name="writer">Language-model writer, or null for the rule-based article.</param>
    /// <param name="writerProblem">Why no writer is available (e.g. a configuration error) – logged when one was wanted.</param>
    public ArticleService(IArticleWriter? writer = null, FfmpegService? ff = null, string? writerProblem = null)
    {
        _writer = writer;
        _screenshots = new ArticleScreenshotService(ff ?? new FfmpegService());
        if (writerProblem is not null) Add($"language model not usable ({writerProblem}) – rule-based article");
    }

    public async Task<WikiArticle> CreateAsync(PipelineResult result, string videoPath, ArticleRequest request, CancellationToken ct = default)
    {
        var article = new ArticleBuilder().Build(result.Transcript, result.AudioRefs, result.Graph, request.Language);
        Add($"rule-based draft: {article.Steps.Count} steps");

        if (request.UseWriter && _writer is not null)
            article = await TryWriteAsync(article, result, videoPath, request.Private, ct);

        int removed = PersonalDataRedactor.Redact(article);
        if (removed > 0) Add($"{removed} personal-data snippets removed (e-mail, phone, names, numbers)");

        if (request.Private)
        {
            article.Private = true;
            foreach (var s in article.Steps) { s.ScreenshotS = null; s.PointXyPx = null; s.BboxXyxyPx = null; s.ScreenshotJpeg = null; }
            Add("private video – text only, no screenshots taken");
            return article;
        }

        Add(await _screenshots.RefineTimesAsync(article, videoPath, ct));
        foreach (var problem in await _screenshots.ExtractAsync(article, videoPath, result.Graph.Video, ct)) Add(problem);
        return article;
    }

    private async Task<WikiArticle> TryWriteAsync(WikiArticle draft, PipelineResult result, string videoPath, bool privateVideo, CancellationToken ct)
    {
        var sentences = TranscriptSentences.Split(result.Transcript);
        try
        {
            var input = new ArticleWriterInput(draft, sentences) { ObservedActions = ObservedActions(result.Graph, draft.Language) };
            // Frames of the screen help a vision model name menus and buttons – never for private videos.
            if (!privateVideo && _writer!.WantsScreens)
            {
                // Images are a help, not a requirement: without them the model still writes from the text.
                try
                {
                    var screens = await _screenshots.KeyframesAsync(videoPath, result.Graph, ct);
                    input = input with { Screens = screens };
                    Add($"{screens.Count} screen images sent to the language model");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Add($"no screen images for the language model ({ex.Message.Split('\n')[0]})");
                }
            }
            var written = await _writer!.WriteAsync(input, ct);
            if (_writer is Services.IReportsFallback f) foreach (var note in f.FallbackNotes) Add(note);
            Add($"written by {written.Method}: {written.Steps.Count} steps");
            // Draft steps are transcript sentences: they can only be put back into an article in the spoken language.
            if (written.Language == result.Transcript.Language)
            {
                int restored = RestoreObservedSteps(draft, written);
                if (restored > 0) Add($"{restored} step(s) with an observed click the model left out were added back");
            }
            else if (MissingObservedSteps(draft, written).Count is > 0 and var missing)
                Add($"{missing} observed click(s) are not covered by the model's steps (not added back: the article is in another language than the call)");
            ArticleBuilder.PlaceScreenshots(written, result.AudioRefs, result.Graph);
            if (sentences.Count > 0 && written.Steps.Count > 0)
            {
                double covered = written.Steps.Max(x => x.EndS), lastSpeech = sentences[^1].EndS;
                if (covered < 0.5 * lastSpeech) Add($"steps only cover the call up to {ArticleRenderer.Ts(covered)} of {ArticleRenderer.Ts(lastSpeech)}");
            }
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (_writer is Services.IReportsFallback f) foreach (var note in f.FallbackNotes) Add(note);
            Add($"language model not usable ({ex.Message}) – kept the rule-based article");
            return draft;
        }
    }

    /// <summary>Observed/tracked clicks as short lines for the prompt: "[00:41] Klick auf „Speichern“ – gesagt: „Klicken Sie hier …“".</summary>
    public static IReadOnlyList<string> ObservedActions(EventGraph graph, string language, int max = 40)
    {
        bool de = language == "de";
        return graph.Events
            .Where(e => e.GroundingStatus is GroundingStatus.Observed or GroundingStatus.Tracked && e.Spatial?.PointXyPx is not null)
            .OrderByDescending(e => e.AudioReferences.Count > 0).ThenByDescending(e => e.OverallConfidence).Take(max)
            .OrderBy(e => e.Temporal.PeakS)
            .Select(e =>
            {
                var p = e.Spatial!.PointXyPx!;
                string what = e.Target?.Text is { } t ? (de ? $"Klick auf „{t}“" : $"click on \"{t}\"") : (de ? $"Klick bei ({p[0]}, {p[1]})" : $"click at ({p[0]}, {p[1]})");
                string said = e.AudioReferences.FirstOrDefault()?.Transcript is { } tr ? (de ? $" – gesagt: „{tr}“" : $" – said: \"{tr}\"") : "";
                return $"[{ArticleRenderer.Ts(e.Temporal.PeakS)}] {what}{said}";
            }).ToList();
    }

    /// <summary>
    /// A click that was observed in the video is hard evidence of a solution step. If the model's steps do not cover
    /// the narration of such a draft step, the draft step is inserted at its place in the video. Returns how many.
    /// The inserted step joins the section (and actor) of the model's step before it, so section headings stay intact.
    /// </summary>
    public static int RestoreObservedSteps(WikiArticle draft, WikiArticle written)
    {
        var missing = MissingObservedSteps(draft, written);
        if (missing.Count == 0) return 0;
        var restored = missing.Select(d =>
        {
            var before = written.Steps.Where(w => w.TimeS <= d.TimeS).MaxBy(w => w.TimeS);
            return new ArticleStep
            {
                Title = d.Title, Instruction = d.Instruction, Details = d.Details, TimeS = d.TimeS, EndS = d.EndS,
                Section = before?.Section, Actor = before?.Actor ?? StepActor.Customer,
            };
        }).ToList();
        written.Steps = written.Steps.Concat(restored).OrderBy(s => s.TimeS).ToList();
        for (int i = 0; i < written.Steps.Count; i++) written.Steps[i].Number = i + 1;
        return missing.Count;
    }

    /// <summary>
    /// Draft steps with an observed click whose narration no step of the model's article covers. Only gaps in a solution:
    /// nothing when the model found no solution (no steps, or not resolved), and nothing before its first step – clicks
    /// there are diagnosis or failed attempts, which the model is told to leave out.
    /// </summary>
    public static List<ArticleStep> MissingObservedSteps(WikiArticle draft, WikiArticle written)
    {
        if (written.Steps.Count == 0 || written.Resolved == false) return [];
        double solutionStart = written.Steps.Min(w => w.TimeS);
        return draft.Steps
            .Where(d => d.PointXyPx is not null && d.TimeS >= solutionStart)
            .Where(d => !written.Steps.Any(w => w.TimeS < d.EndS && d.TimeS < Math.Max(w.EndS, w.TimeS + 0.01)))
            .ToList();
    }

    private void Add(string message) => Log.Add("article: " + message);
}
