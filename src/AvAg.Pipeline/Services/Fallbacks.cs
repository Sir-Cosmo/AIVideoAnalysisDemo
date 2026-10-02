using AvAg.Core;

namespace AvAg.Pipeline.Services;

/// <summary>A service that may have switched to its fallback; the notes say when and why (for the job log).</summary>
public interface IReportsFallback
{
    IReadOnlyList<string> FallbackNotes { get; }
}

/// <summary>
/// Configured with <c>"Fallback": { … }</c>: tries the primary service and, if it fails for any reason other than the
/// user cancelling (not reachable, out of credit, invalid key, timeout, unusable answer), uses the fallback.
/// Typical: OpenAI first, the local WhisperX / Ollama when OpenAI is not available.
/// </summary>
internal static class Fallback
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> primary, Func<Task<T>> fallback, string primaryName, string fallbackName,
                                           List<string> notes, CancellationToken ct)
    {
        try { return await primary(); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            notes.Add($"{primaryName} not usable ({Short(ex.Message)}) – used {fallbackName}");
            return await fallback();
        }
    }

    private static string Short(string s) => s.Length <= 200 ? s : s[..200] + " …";
}

public sealed class FallbackAsrService(IAsrService primary, string primaryName, IAsrService fallback, string fallbackName) : IAsrService, IReportsFallback
{
    private readonly List<string> _notes = new();
    /// <summary>This wrapper's notes plus those of the services it wraps (e.g. "word alignment not available").</summary>
    public IReadOnlyList<string> FallbackNotes =>
        [.. _notes, .. (primary as IReportsFallback)?.FallbackNotes ?? [], .. (fallback as IReportsFallback)?.FallbackNotes ?? []];

    public Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default) =>
        Fallback.RunAsync(() => primary.TranscribeAsync(audioWavPath, languageHint, diarize, ct),
                          () => fallback.TranscribeAsync(audioWavPath, languageHint, diarize, ct), primaryName, fallbackName, _notes, ct);
}

public sealed class FallbackTextGenerator(ITextGenerator primary, ITextGenerator fallback) : ITextGenerator, IReportsFallback
{
    private readonly List<string> _notes = new();
    private ITextGenerator _used = primary;
    public IReadOnlyList<string> FallbackNotes => _notes;

    /// <summary>The model that actually answered the last request.</summary>
    public string Name => _used.Name;
    /// <summary>Images are only sent if the model that will answer can read them – the fallback decides on its own.</summary>
    public bool SupportsImages => primary.SupportsImages;

    public async Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken ct = default)
    {
        _used = primary;
        return await Fallback.RunAsync(() => primary.GenerateAsync(request, ct),
            () => { _used = fallback; return fallback.GenerateAsync(fallback.SupportsImages ? request : request.TextOnly(), ct); },
            primary.Name, fallback.Name, _notes, ct);
    }
}

/// <summary>
/// Fallback for services called many times per video (frames, clips, events): after the primary failed once, the rest of
/// the run uses the fallback directly instead of waiting for the same failure again on every call.
/// </summary>
internal sealed class StickyFallback<T>(T primary, string primaryName, T fallback, string fallbackName)
{
    private readonly List<string> _notes = new();
    private volatile bool _primaryFailed;

    public IReadOnlyList<string> Notes { get { lock (_notes) return _notes.ToList(); } }

    public async Task<TResult> RunAsync<TResult>(Func<T, Task<TResult>> call, CancellationToken ct)
    {
        if (!_primaryFailed)
        {
            try { return await call(primary); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _primaryFailed = true;
                lock (_notes) _notes.Add($"{primaryName} not usable ({(ex.Message.Length <= 200 ? ex.Message : ex.Message[..200] + " …")}) – used {fallbackName} for the rest of the run");
            }
        }
        return await call(fallback);
    }
}

public sealed class FallbackUiParser(IUiParser primary, string primaryName, IUiParser fallback, string fallbackName) : IUiParser, IReportsFallback
{
    private readonly StickyFallback<IUiParser> _f = new(primary, primaryName, fallback, fallbackName);
    public IReadOnlyList<string> FallbackNotes => _f.Notes;
    public Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default) =>
        _f.RunAsync(s => s.ParseAsync(framePngPath, ct), ct);
}

public sealed class FallbackGrounder(IVideoGrounder primary, string primaryName, IVideoGrounder fallback, string fallbackName) : IVideoGrounder, IReportsFallback
{
    private readonly StickyFallback<IVideoGrounder> _f = new(primary, primaryName, fallback, fallbackName);
    public IReadOnlyList<string> FallbackNotes => _f.Notes;
    public Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default) =>
        _f.RunAsync(s => s.PointAsync(videoPath, prompt, startS, endS, ct), ct);
}

public sealed class FallbackTracker(IObjectTracker primary, string primaryName, IObjectTracker fallback, string fallbackName) : IObjectTracker, IReportsFallback
{
    private readonly StickyFallback<IObjectTracker> _f = new(primary, primaryName, fallback, fallbackName);
    public IReadOnlyList<string> FallbackNotes => _f.Notes;
    public Task<IReadOnlyList<TrackSample>> TrackAsync(string videoPath, double seedTimeS, Point2D? seedPoint, BBox? seedBox, double startS, double endS, CancellationToken ct = default) =>
        _f.RunAsync(s => s.TrackAsync(videoPath, seedTimeS, seedPoint, seedBox, startS, endS, ct), ct);
}

public sealed class FallbackClipDescriber(IClipDescriber primary, string primaryName, IClipDescriber fallback, string fallbackName) : IClipDescriber, IReportsFallback
{
    private readonly StickyFallback<IClipDescriber> _f = new(primary, primaryName, fallback, fallbackName);
    public IReadOnlyList<string> FallbackNotes => _f.Notes;
    public Task<string> DescribeAsync(string videoPath, double startS, double endS, string language, CancellationToken ct = default) =>
        _f.RunAsync(s => s.DescribeAsync(videoPath, startS, endS, language, ct), ct);
}
