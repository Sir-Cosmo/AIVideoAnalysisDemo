using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

// ---------------------------------------------------------------------------
// WhisperX JSON format (as written by `whisperx --output_format json` or by sidecars/whisperx_server.py)
// ---------------------------------------------------------------------------
public sealed class WhisperXJson
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("segments")] public List<WhisperXSegment> Segments { get; set; } = new();
}

public sealed class WhisperXSegment
{
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
    [JsonPropertyName("words")] public List<WhisperXWord> Words { get; set; } = new();
}

public sealed class WhisperXWord
{
    [JsonPropertyName("word")] public string Word { get; set; } = "";
    [JsonPropertyName("start")] public double? Start { get; set; }
    [JsonPropertyName("end")] public double? End { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("speaker")] public string? Speaker { get; set; }
}

public static class WhisperXMapper
{
    /// <summary>
    /// Maps WhisperX output to the internal transcript. Words without alignment (not in the alignment
    /// vocabulary, numerals, …) get interpolated times and a reduced score so the uncertainty stays visible.
    /// </summary>
    public static Transcript ToTranscript(WhisperXJson wx)
    {
        var segs = new List<TranscriptSegment>();
        foreach (var s in wx.Segments)
        {
            var words = new List<Word>();
            for (int i = 0; i < s.Words.Count; i++)
            {
                var w = s.Words[i];
                double start = w.Start ?? Interp(s, i, true), end = w.End ?? Interp(s, i, false);
                double score = w.Start is null ? 0.3 * (w.Score ?? 1) : (w.Score ?? 1);
                words.Add(new Word(w.Word.Trim(), start, Math.Max(end, start), score, w.Speaker ?? s.Speaker));
            }
            segs.Add(new TranscriptSegment(s.Start, s.End, s.Text.Trim(), words, s.Speaker));
        }
        return new Transcript(wx.Language ?? "unknown", segs);
    }

    private static double Interp(WhisperXSegment s, int i, bool start)
    {
        int n = Math.Max(1, s.Words.Count);
        double frac = (i + (start ? 0 : 1)) / (double)n;
        return s.Start + (s.End - s.Start) * frac;
    }
}

/// <summary>Uses a precomputed WhisperX JSON file – for offline evaluation, tests and debugging.</summary>
public sealed class JsonFileAsr : IAsrService
{
    private readonly string _path;
    public JsonFileAsr(string path) => _path = path;

    public async Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(_path);
        var wx = await JsonSerializer.DeserializeAsync<WhisperXJson>(fs, cancellationToken: ct) ?? new WhisperXJson();
        return WhisperXMapper.ToTranscript(wx);
    }
}

// ---------------------------------------------------------------------------
// Generic HTTP sidecar helper
// ---------------------------------------------------------------------------
public abstract class HttpSidecar
{
    protected readonly HttpClient Http;
    protected readonly Uri BaseUri;
    protected HttpSidecar(string baseUrl, HttpClient? http = null)
    {
        BaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        Http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    protected async Task<TRes> PostAsync<TReq, TRes>(string route, TReq body, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(new Uri(BaseUri, route), body, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{GetType().Name} {route} → {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadFromJsonAsync<TRes>(cancellationToken: ct) ?? throw new InvalidOperationException("empty sidecar response");
    }
}

/// <summary>sidecars/whisperx_server.py — POST /transcribe {audio_path, language, diarize} → WhisperX JSON.</summary>
public sealed class WhisperXSidecar : HttpSidecar, IAsrService
{
    public WhisperXSidecar(string baseUrl, HttpClient? http = null) : base(baseUrl, http) { }
    public async Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default)
    {
        var wx = await PostAsync<object, WhisperXJson>("transcribe", new { audio_path = Path.GetFullPath(audioWavPath), language = languageHint, diarize }, ct);
        return WhisperXMapper.ToTranscript(wx);
    }
}

/// <summary>sidecars/molmo_server.py — POST /point {video_path, prompt, start_s, end_s} → {points:[{object_id,time_s,x,y,confidence,label}]}.</summary>
public sealed class MolmoPointSidecar : HttpSidecar, IVideoGrounder
{
    public MolmoPointSidecar(string baseUrl, HttpClient? http = null) : base(baseUrl, http) { }
    private sealed record Res(List<VideoPoint> Points);
    public async Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default)
    {
        var res = await PostAsync<object, Res>("point", new { video_path = Path.GetFullPath(videoPath), prompt, start_s = startS, end_s = endS }, ct);
        return res.Points;
    }
}

/// <summary>sidecars/omniparser_server.py — POST /parse {image_path} → {elements:[{bbox:[x1,y1,x2,y2],text,text_confidence,interactive_confidence,class}]}.</summary>
public sealed class OmniParserSidecar : HttpSidecar, IUiParser
{
    public OmniParserSidecar(string baseUrl, HttpClient? http = null) : base(baseUrl, http) { }
    private sealed record El(int[] Bbox, string? Text, double TextConfidence, double InteractiveConfidence, string? Class);
    private sealed record Res(List<El> Elements);
    public async Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default)
    {
        var res = await PostAsync<object, Res>("parse", new { image_path = Path.GetFullPath(framePngPath) }, ct);
        return res.Elements.Select(e => new UiElementRegistry.Detection(
            new BBox(e.Bbox[0], e.Bbox[1], e.Bbox[2], e.Bbox[3]), e.Text, e.TextConfidence, e.InteractiveConfidence, e.Class ?? "unknown")).ToList();
    }
}

/// <summary>sidecars/sam2_server.py — POST /track {video_path, seed_time_s, point, box, start_s, end_s} → {samples:[{time_s,bbox,confidence}]}.</summary>
public sealed class Sam2Sidecar : HttpSidecar, IObjectTracker
{
    public Sam2Sidecar(string baseUrl, HttpClient? http = null) : base(baseUrl, http) { }
    private sealed record S(double TimeS, int[] Bbox, double Confidence);
    private sealed record Res(List<S> Samples);
    public async Task<IReadOnlyList<TrackSample>> TrackAsync(string videoPath, double seedTimeS, Point2D? seedPoint, BBox? seedBox, double startS, double endS, CancellationToken ct = default)
    {
        var res = await PostAsync<object, Res>("track", new
        {
            video_path = Path.GetFullPath(videoPath), seed_time_s = seedTimeS,
            point = seedPoint is null ? null : new[] { seedPoint.X, seedPoint.Y },
            box = seedBox?.ToArray(), start_s = startS, end_s = endS,
        }, ct);
        return res.Samples.Select(s => new TrackSample(s.TimeS, new BBox(s.Bbox[0], s.Bbox[1], s.Bbox[2], s.Bbox[3]), s.Confidence)).ToList();
    }
}

/// <summary>
/// Qwen3-VL served by vLLM (OpenAI-compatible). Used for clip description and, as a fallback grounder,
/// for JSON-structured pointing. Pointing from a general VLM is tagged <see cref="EvidenceSource.VlmPointing"/>
/// by the orchestrator and therefore never becomes an "observed" coordinate.
/// </summary>
public sealed class Qwen3VlClient : HttpSidecar, IClipDescriber, IVideoGrounder
{
    private readonly string _model;
    public double SampleFps { get; init; } = 2.0;

    public Qwen3VlClient(string baseUrl, string model = "Qwen/Qwen3-VL-8B-Instruct", HttpClient? http = null) : base(baseUrl, http) => _model = model;

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
        string text = await ChatAsync(videoPath, p, startS, endS, ct);
        text = text.Replace("```json", "").Replace("```", "").Trim();
        try
        {
            var doc = JsonSerializer.Deserialize<PointsDoc>(text, Json.Options);
            return doc?.Points ?? new List<VideoPoint>();
        }
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

// ---------------------------------------------------------------------------
// Null adapters for offline runs / unit tests
// ---------------------------------------------------------------------------
public sealed class NullUiParser : IUiParser
{
    public Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UiElementRegistry.Detection>>(Array.Empty<UiElementRegistry.Detection>());
}

public sealed class NullGrounder : IVideoGrounder
{
    public Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VideoPoint>>(Array.Empty<VideoPoint>());
}

/// <summary>Reads UI elements for a frame from a JSON side file (same shape as the OmniParser sidecar). Useful for tests and golden runs.</summary>
public sealed class JsonFileUiParser : IUiParser
{
    private readonly string _path;
    public JsonFileUiParser(string path) => _path = path;
    private sealed record El(int[] Bbox, string? Text, double TextConfidence, double InteractiveConfidence, string? Class);
    private sealed record Res(List<El> Elements);
    public async Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default)
    {
        var res = JsonSerializer.Deserialize<Res>(await File.ReadAllTextAsync(_path, ct), Json.Options) ?? new Res(new());
        return res.Elements.Select(e => new UiElementRegistry.Detection(new BBox(e.Bbox[0], e.Bbox[1], e.Bbox[2], e.Bbox[3]), e.Text, e.TextConfidence, e.InteractiveConfidence, e.Class ?? "unknown")).ToList();
    }
}
