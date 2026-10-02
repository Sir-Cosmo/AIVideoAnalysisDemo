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
            () => { _used = fallback; return fallback.GenerateAsync(fallback.SupportsImages ? request : request with { Images = [] }, ct); },
            primary.Name, fallback.Name, _notes, ct);
    }
}
