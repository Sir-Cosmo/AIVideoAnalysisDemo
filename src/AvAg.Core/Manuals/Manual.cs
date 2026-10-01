using System.Text.Json.Serialization;

namespace AvAg.Core;

// ---------------------------------------------------------------------------
// Step-by-step manual ("Anleitung") derived from a tutorial video: what the viewer has to do to repeat what the
// presenter does. Built from the transcript (what is said) and the event graph (where it was clicked).
// ---------------------------------------------------------------------------
public sealed class Manual
{
    public required string Title { get; set; }
    public required string Language { get; set; }              // "de" | "en" – language of the fixed headings
    public string? Summary { get; set; }
    public List<string> Prerequisites { get; set; } = new();
    public List<ManualStep> Steps { get; set; } = new();
    public List<string> Tips { get; set; } = new();
    /// <summary>"extractive" (rule-based, from the transcript) or "llm:&lt;model&gt;".</summary>
    public string Method { get; set; } = "extractive";
    public string? VideoName { get; set; }
    public double VideoDurationS { get; set; }
    /// <summary>Private video: the manual is text only – no frame of the video is extracted or embedded.</summary>
    public bool Private { get; set; }
}

public sealed class ManualStep
{
    public int Number { get; set; }
    /// <summary>Part of the video the step belongs to, e.g. "Methode 1: Tastenkürzel" – consecutive steps share it.</summary>
    public string? Section { get; set; }
    public string? Title { get; set; }
    public required string Instruction { get; set; }
    public string? Details { get; set; }
    /// <summary>Where in the video this step is shown (start and end of the narration that belongs to it).</summary>
    public double TimeS { get; set; }
    public double EndS { get; set; }
    /// <summary>Frame used for the screenshot; null = no screenshot.</summary>
    public double? ScreenshotS { get; set; }
    /// <summary>Observed/tracked click position (source pixels) – drawn as a marker on the screenshot.</summary>
    public int[]? PointXyPx { get; set; }
    public int[]? BboxXyxyPx { get; set; }
    [JsonIgnore] public byte[]? ScreenshotJpeg { get; set; }
    public int ScreenshotWidth { get; set; }
    public int ScreenshotHeight { get; set; }
}
