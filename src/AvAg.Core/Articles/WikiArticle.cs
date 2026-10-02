using System.Text.Json.Serialization;

namespace AvAg.Core;

// ---------------------------------------------------------------------------------------------------------------------
// Knowledge-base ("wiki") article written from a recorded support call: what the customer's problem was, why it
// happened, and how the supporter fixed it – so the next customer with the same problem can fix it themselves.
// Deliberately contains no reference to the call itself (no file name, no names, no date of the call).
// ---------------------------------------------------------------------------------------------------------------------
public sealed class WikiArticle
{
    public required string Title { get; set; }
    /// <summary>"de" | "en" – language of the text and the fixed headings.</summary>
    public required string Language { get; set; }
    /// <summary>What the customer experiences (symptoms), written generally – not the story of the call.</summary>
    public string? Problem { get; set; }
    /// <summary>Error messages exactly as shown on screen / read out.</summary>
    public List<string> ErrorMessages { get; set; } = new();
    /// <summary>Root cause, if the call identified one.</summary>
    public string? Cause { get; set; }
    /// <summary>Product / module / version the article applies to, if mentioned.</summary>
    public string? AppliesTo { get; set; }
    public List<ArticleStep> Steps { get; set; } = new();
    /// <summary>How to check that the problem is solved.</summary>
    public string? Verification { get; set; }
    public List<string> Notes { get; set; } = new();
    /// <summary>Search terms for the wiki.</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>Whether the call ended with the problem solved (null = unknown).</summary>
    public bool? Resolved { get; set; }

    /// <summary>"rule-based" or "llm:&lt;model&gt;".</summary>
    public string Method { get; set; } = "rule-based";
    /// <summary>Private video: text only – no frame of the video is extracted or embedded.</summary>
    public bool Private { get; set; }
    /// <summary>Number of personal-data snippets the redactor removed (emails, phone numbers, names after "Herr/Frau", …).</summary>
    public int Redactions { get; set; }
    public double SourceDurationS { get; set; }
}

/// <summary>Who performs a solution step.</summary>
public enum StepActor
{
    /// <summary>The customer can do this themselves.</summary>
    Customer,
    /// <summary>Only the support team can do this (e.g. server, licence or account changes).</summary>
    Support,
}

public sealed class ArticleStep
{
    public int Number { get; set; }
    /// <summary>Part of the solution the step belongs to, e.g. "Variante 1: Druckerprofil neu zuweisen" – consecutive steps share it.</summary>
    public string? Section { get; set; }
    public string? Title { get; set; }
    public required string Instruction { get; set; }
    public string? Details { get; set; }
    public StepActor Actor { get; set; } = StepActor.Customer;

    /// <summary>Where in the video the step happens (start and end of the narration that belongs to it).</summary>
    public double TimeS { get; set; }
    public double EndS { get; set; }
    /// <summary>Frame used for the screenshot; null = no screenshot.</summary>
    public double? ScreenshotS { get; set; }
    /// <summary>Observed click position (source pixels) – drawn as a marker on the screenshot.</summary>
    public int[]? PointXyPx { get; set; }
    public int[]? BboxXyxyPx { get; set; }
    [JsonIgnore] public byte[]? ScreenshotJpeg { get; set; }
    public int ScreenshotWidth { get; set; }
    public int ScreenshotHeight { get; set; }
    /// <summary>File name of the screenshot inside the article package, e.g. "step-03.jpg".</summary>
    [JsonIgnore] public string? ScreenshotFile => ScreenshotJpeg is { Length: > 0 } ? $"step-{Number:00}.jpg" : null;
}
