using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvAg.Core;

// ---------------------------------------------------------------------------
// Shared JSON options: snake_case to match the schema in the design document.
// ---------------------------------------------------------------------------
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}");
}

// ---------------------------------------------------------------------------
// Action vocabulary shared by audio references and visual events.
// ---------------------------------------------------------------------------
public enum ActionType
{
    Unknown,
    Click,
    DoubleClick,
    RightClick,
    Drag,
    Tap,
    Type,
    Scroll,
    Select,
    Point,
}

/// <summary>
/// Evidence hierarchy from the design document. Order matters: lower value = stronger evidence.
/// </summary>
public enum EvidenceSource
{
    Telemetry,          // OS/browser pointer event log with real coordinates
    CursorTracker,      // visible cursor / touch indicator
    FingerHomography,   // visible finger projected onto a calibrated screen plane
    FrameStateChange,   // UI state change (with or without cursor position)
    Sam2Tracker,        // propagated mask/track
    OmniParser,         // UI element structure
    PaddleOcr,          // UI text
    VlmPointing,        // MolmoPoint / Qwen3-VL pointing
    VlmSemantic,        // pure semantic VLM estimate (weakest)
}

public enum GroundingStatus { Observed, Tracked, Inferred, Unobservable }

// ---------------------------------------------------------------------------
// ASR output (WhisperX-shaped): segments with word-level timestamps + speakers.
// ---------------------------------------------------------------------------
public sealed record Word(string Text, double StartS, double EndS, double Score = 1.0, string? Speaker = null);

public sealed record TranscriptSegment(double StartS, double EndS, string Text, List<Word> Words, string? Speaker = null);

public sealed record Transcript(string Language, List<TranscriptSegment> Segments)
{
    public IEnumerable<Word> AllWords() => Segments.SelectMany(s => s.Words);
}

// ---------------------------------------------------------------------------
// Audio reference = one parsed action instruction ("Klicken Sie hier").
// ---------------------------------------------------------------------------
public sealed class AudioRef
{
    public required string Id { get; init; }
    public required double StartS { get; init; }
    public required double EndS { get; init; }
    /// <summary>Time used as the temporal anchor for matching (end of the deictic word if present, else end of clause).</summary>
    public required double AnchorS { get; init; }
    public required string Text { get; init; }
    public required ActionType Action { get; init; }
    public bool Deictic { get; init; }
    /// <summary>Explicit target named in speech, e.g. "Speichern" from „auf Speichern klicken“.</summary>
    public string? ExplicitTarget { get; init; }
    /// <summary>Order within one utterance ("hier klicken und dann dort") – 0-based.</summary>
    public int Ordinal { get; init; }
    public string? SpeakerId { get; init; }
    public double AsrConfidence { get; init; } = 1.0;
    public double AlignmentConfidence { get; init; } = 1.0;
}

// ---------------------------------------------------------------------------
// Perception layer outputs.
// ---------------------------------------------------------------------------
public sealed record Point2D(int X, int Y);

public sealed record BBox(int X1, int Y1, int X2, int Y2)
{
    public int Width => X2 - X1;
    public int Height => Y2 - Y1;
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
    public bool Contains(Point2D p) => p.X >= X1 && p.X <= X2 && p.Y >= Y1 && p.Y <= Y2;
    public Point2D Center => new((X1 + X2) / 2, (Y1 + Y2) / 2);

    public double IoU(BBox o)
    {
        int ix1 = Math.Max(X1, o.X1), iy1 = Math.Max(Y1, o.Y1);
        int ix2 = Math.Min(X2, o.X2), iy2 = Math.Min(Y2, o.Y2);
        long inter = (long)Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
        long union = Area + o.Area - inter;
        return union <= 0 ? 0 : (double)inter / union;
    }

    public int[] ToArray() => [X1, Y1, X2, Y2];
    public double[] Normalized(int w, int h) => [(double)X1 / w, (double)Y1 / h, (double)X2 / w, (double)Y2 / h];
}

public sealed class UiElement
{
    public required string Id { get; set; }
    public string Class { get; set; } = "unknown";
    public string? Text { get; set; }
    public double TextConfidence { get; set; }
    public double InteractiveConfidence { get; set; }
    public required BBox Bbox { get; set; }
    public double FirstSeenS { get; set; }
    public double LastSeenS { get; set; }
    public int Observations { get; set; } = 1;
}

public sealed record Evidence(EvidenceSource Source, double Confidence);

public sealed class VisualEvent
{
    public required string Id { get; init; }
    public required ActionType Action { get; init; }
    public required double TimeS { get; init; }
    public double StartS { get; init; }
    public double EndS { get; init; }
    public Point2D? Point { get; init; }
    public BBox? ChangedRegion { get; init; }
    public string? TargetId { get; set; }
    public double DetectorConfidence { get; init; } = 1.0;
    public double UiInteractiveConfidence { get; set; }
    public double StateChangeConfidence { get; init; }
    public List<Evidence> Evidence { get; init; } = new();
    /// <summary>Strongest evidence source that produced the point coordinate (drives grounding_status).</summary>
    public EvidenceSource? PointSource { get; init; }
}

public sealed record VideoInfo(string Id, double DurationS, int WidthPx, int HeightPx, double NominalFps, string Timebase = "seconds_from_media_pts");

// ---------------------------------------------------------------------------
// Output schema (schema_version 1.0 from the design document).
// ---------------------------------------------------------------------------
public sealed class EventGraph
{
    public string SchemaVersion { get; set; } = "1.0";
    public required VideoInfo Video { get; set; }
    public List<GroundedEvent> Events { get; set; } = new();
    /// <summary>Audio instructions that could not be bound to any visual event (kept for auditability).</summary>
    public List<AudioReferenceOut> UnboundAudioReferences { get; set; } = new();
}

public sealed class GroundedEvent
{
    public required string EventId { get; set; }
    public required ActionType Type { get; set; }
    public string? DescriptionDe { get; set; }
    public string? DescriptionEn { get; set; }
    public required TemporalOut Temporal { get; set; }
    public SpatialOut? Spatial { get; set; }
    public TargetOut? Target { get; set; }
    public List<AudioReferenceOut> AudioReferences { get; set; } = new();
    public List<EvidenceOut> Evidence { get; set; } = new();
    public required GroundingStatus GroundingStatus { get; set; }
    public double OverallConfidence { get; set; }
}

public sealed record TemporalOut(double StartS, double PeakS, double EndS, double Confidence);

public sealed record SpatialOut(
    string CoordinateSystem,
    int[]? PointXyPx,
    double[]? PointXyNorm,
    int[]? BboxXyxyPx,
    double[]? BboxXyxyNorm,
    double Confidence);

public sealed record TargetOut(string ObjectId, string Class, string? Text, double TextConfidence, double InteractiveConfidence);

public sealed record AudioReferenceOut(
    string AudioRefId,
    string? SpeakerId,
    double StartS,
    double EndS,
    string Transcript,
    double AsrConfidence,
    double AlignmentConfidence,
    string Relation);

public sealed record EvidenceOut(string Source, double Confidence);
