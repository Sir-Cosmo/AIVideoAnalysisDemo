using System.Net.Http.Json;
using System.Text.Json;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Provider "qwen-vl": a video-language model behind an OpenAI-compatible endpoint (e.g. Qwen3-VL served by vLLM).
/// Used for clip description and, as a fallback grounder, for JSON-structured pointing. Pointing from a general
/// VLM is tagged <see cref="EvidenceSource.VlmPointing"/> by the orchestrator and never becomes an "observed" coordinate.
/// </summary>
public sealed class Qwen3VlClient : HttpServiceClient, IClipDescriber, IVideoGrounder
{
    public const string DefaultModel = "Qwen/Qwen3-VL-8B-Instruct";
    private readonly string _model;
    public double SampleFps { get; init; } = 2.0;

    public Qwen3VlClient(string baseUrl, string? model = null, HttpClient? http = null, TimeSpan? timeout = null) : base(baseUrl, http, timeout)
        => _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;

    public async Task<string> DescribeAsync(string videoPath, double startS, double endS, string language, CancellationToken ct = default)
    {
        string prompt = language == "de"
            ? $"Beschreibe detailliert und sachlich, was zwischen {startS:0.0}s und {endS:0.0}s in diesem Bildschirmvideo passiert. Nenne nur, was sichtbar ist."
            : $"Describe in detail and factually what happens between {startS:0.0}s and {endS:0.0}s in this screen recording. Only mention what is visible.";
        return await ChatAsync(videoPath, prompt, startS, endS, ct);
    }

    public async Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default)
    {
        string p = prompt + "\nRespond ONLY with JSON: {\"points\":[{\"object_id\":\"...\",\"time_s\":0.0,\"x\":0,\"y\":0,\"confidence\":0.0,\"label\":\"...\"}]} " +
                   "with x,y in source pixel coordinates (origin top-left).";
        string text = (await ChatAsync(videoPath, p, startS, endS, ct)).Replace("```json", "").Replace("```", "").Trim();
        try { return JsonSerializer.Deserialize<PointsDoc>(text, Json.Options)?.Points ?? new List<VideoPoint>(); }
        catch (JsonException) { return Array.Empty<VideoPoint>(); }
    }

    private sealed class PointsDoc { public List<VideoPoint> Points { get; set; } = new(); }

    private async Task<string> ChatAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct)
    {
        // vLLM accepts file:// video URLs when started with --allowed-local-media-path.
        var body = new
        {
            model = _model,
            max_tokens = 800,
            temperature = 0.0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "video_url", video_url = new { url = "file://" + Path.GetFullPath(videoPath) } },
                        new { type = "text", text = $"[clip {startS:0.00}s–{endS:0.00}s] {prompt}" },
                    },
                },
            },
            mm_processor_kwargs = new { fps = SampleFps, video_start = startS, video_end = endS },
        };
        using var resp = await Http.PostAsJsonAsync(new Uri(BaseUri, "v1/chat/completions"), body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
