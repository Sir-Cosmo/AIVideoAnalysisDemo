namespace AvAg.Core;

// ---------------------------------------------------------------------------------------------------------------------
// Replaceable AI capabilities.
//
// The pipeline only talks to these interfaces. Each one is implemented by an adapter in AvAg.Pipeline/Adapters and
// selected by name in configuration (see AiServiceFactory). To use a different AI for a capability, implement the
// interface and register it under a new provider name – nothing else in the pipeline changes.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Speech recognition with word timings (+ optional speaker labels). Default: WhisperX sidecar.</summary>
public interface IAsrService
{
    /// <param name="languageHint">ISO code ("de", "en") or null to detect.</param>
    Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default);
}

/// <summary>UI elements and their text in one frame image. Default: OmniParser + PaddleOCR sidecar.</summary>
public interface IUiParser
{
    Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default);
}

/// <summary>A spatio-temporal point produced by a video grounder.</summary>
public sealed record VideoPoint(string ObjectId, double TimeS, int X, int Y, double Confidence, string? Label = null);

/// <summary>
/// "Where is X in this clip?" Used only as a fallback when no pointer is visible; its points are always tagged
/// <see cref="GroundingStatus.Inferred"/>, never observed. Defaults: MolmoPoint sidecar, Qwen3-VL.
/// </summary>
public interface IVideoGrounder
{
    Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default);
}

/// <summary>One propagated box of an object track.</summary>
public sealed record TrackSample(double TimeS, BBox Bbox, double Confidence);

/// <summary>Follows a UI element over time from a seed point or box. Default: SAM2 sidecar.</summary>
public interface IObjectTracker
{
    Task<IReadOnlyList<TrackSample>> TrackAsync(string videoPath, double seedTimeS, Point2D? seedPoint, BBox? seedBox, double startS, double endS, CancellationToken ct = default);
}

/// <summary>Free-text description of a clip – an auxiliary narrative, never used as grounding. Default: Qwen3-VL.</summary>
public interface IClipDescriber
{
    Task<string> DescribeAsync(string videoPath, double startS, double endS, string language, CancellationToken ct = default);
}

/// <summary>A text-only prompt for a language model.</summary>
/// <param name="Json">Ask the model for a single JSON object (providers that support a JSON mode enforce it).</param>
public sealed record TextGenerationRequest(string System, string User, bool Json = false, int MaxTokens = 2000, double Temperature = 0.2);

/// <summary>
/// Plain text-in / text-out language model. Everything that writes prose (the manual) goes through this, so switching
/// between a local model (Ollama), a hosted one (OpenAI, Azure OpenAI, …) or another vendor is one adapter.
/// </summary>
public interface ITextGenerator
{
    /// <summary>Short name for logs and the manual's "written by" line, e.g. the model name.</summary>
    string Name { get; }
    Task<string> GenerateAsync(TextGenerationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Turns the rule-based draft into the final manual text. Implementations must keep each step anchored to the
/// transcript (<see cref="ManualStep.TimeS"/>/<see cref="ManualStep.EndS"/> from real sentences) and throw
/// <see cref="InvalidOperationException"/> when they cannot produce a usable manual – the caller then keeps the draft.
/// </summary>
public interface IManualWriter
{
    string Name { get; }
    Task<Manual> WriteAsync(Manual draft, IReadOnlyList<ManualBuilder.Sentence> sentences, CancellationToken ct = default);
}
