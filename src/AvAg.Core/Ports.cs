namespace AvAg.Core;

/// <summary>WhisperX-shaped ASR: transcription + word alignment (+ optional diarization).</summary>
public interface IAsrService
{
    Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default);
}

/// <summary>A spatio-temporal point produced by a video grounder (MolmoPoint / Qwen3-VL).</summary>
public sealed record VideoPoint(string ObjectId, double TimeS, int X, int Y, double Confidence, string? Label = null);

public interface IVideoGrounder
{
    /// <summary>Ask the model to point at where a described action/object is between start and end seconds.</summary>
    Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default);
}

/// <summary>UI structure + text for one frame image (OmniParser + PaddleOCR).</summary>
public interface IUiParser
{
    Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default);
}

/// <summary>Mask/track propagation from a seed point or box (SAM2).</summary>
public sealed record TrackSample(double TimeS, BBox Bbox, double Confidence);

public interface IObjectTracker
{
    Task<IReadOnlyList<TrackSample>> TrackAsync(string videoPath, double seedTimeS, Point2D? seedPoint, BBox? seedBox, double startS, double endS, CancellationToken ct = default);
}

/// <summary>Free-text dense description of a clip (Qwen3-VL / Molmo2 captioning). Used only as an auxiliary narrative, never as grounding.</summary>
public interface IClipDescriber
{
    Task<string> DescribeAsync(string videoPath, double startS, double endS, string language, CancellationToken ct = default);
}
