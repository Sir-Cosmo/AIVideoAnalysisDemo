using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Manuals;

/// <summary>Options for one manual.</summary>
/// <param name="Language">"de", "en" or null for the spoken language.</param>
/// <param name="UseWriter">Let the configured <see cref="IManualWriter"/> (language model) write the text.</param>
/// <param name="Private">Text only: no frame is extracted from the video and no click position is kept.</param>
public sealed record ManualRequest(string? Language = null, bool UseWriter = true, bool Private = false);

/// <summary>
/// Creates the step-by-step manual for an analysed video:
/// <list type="number">
/// <item>rule-based draft from the narration (<see cref="ManualBuilder"/>, always works offline),</item>
/// <item>optional rewrite by an <see cref="IManualWriter"/> (any language model); on failure the draft is kept,</item>
/// <item>screenshot moments and click markers (<see cref="ManualBuilder.PlaceScreenshots"/>),</item>
/// <item>screenshots refined and extracted (<see cref="ManualScreenshotService"/>) – skipped for private videos.</item>
/// </list>
/// </summary>
public sealed class ManualService
{
    private readonly IManualWriter? _writer;
    private readonly ManualScreenshotService _screenshots;
    public List<string> Log { get; } = new();

    public ManualService(IManualWriter? writer = null, FfmpegService? ff = null)
    {
        _writer = writer;
        _screenshots = new ManualScreenshotService(ff ?? new FfmpegService());
    }

    public bool HasWriter => _writer is not null;

    public async Task<Manual> CreateAsync(PipelineResult result, string videoPath, string? videoName, ManualRequest request, CancellationToken ct = default)
    {
        var manual = new ManualBuilder().Build(result.Transcript, result.AudioRefs, result.Graph, videoName, request.Language);
        Add($"{manual.Steps.Count} steps from the narration");

        if (request.UseWriter && _writer is not null)
            manual = await TryWriteAsync(manual, result, ct);

        if (request.Private)
        {
            manual.Private = true;
            foreach (var s in manual.Steps) { s.ScreenshotS = null; s.PointXyPx = null; s.BboxXyxyPx = null; s.ScreenshotJpeg = null; }
            Add("private video – text only, no screenshots taken");
            return manual;
        }

        Add(await _screenshots.RefineTimesAsync(manual, videoPath, ct));
        foreach (var problem in await _screenshots.ExtractAsync(manual, videoPath, result.Graph.Video, ct)) Add(problem);
        return manual;
    }

    private async Task<Manual> TryWriteAsync(Manual draft, PipelineResult result, CancellationToken ct)
    {
        var sentences = ManualBuilder.Sentences(result.Transcript);
        try
        {
            var written = await _writer!.WriteAsync(draft, sentences, ct);
            ManualBuilder.PlaceScreenshots(written, result.AudioRefs, result.Graph);
            Add($"written by {written.Method} → {written.Steps.Count} steps");
            double covered = written.Steps.Max(x => x.EndS), lastSpeech = sentences[^1].EndS;
            if (covered < 0.6 * lastSpeech) Add($"steps cover the video only up to {ManualRenderer.Ts(covered)} of {ManualRenderer.Ts(lastSpeech)}");
            return written;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Add($"language model not usable ({ex.Message}) – kept the rule-based manual");
            return draft;
        }
    }

    private void Add(string message) => Log.Add("manual: " + message);
}
