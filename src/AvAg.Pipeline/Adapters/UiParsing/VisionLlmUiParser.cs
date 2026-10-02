using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Provider "openai-vision" (alias "vision-llm"): a vision language model (e.g. GPT-5.5) reads the UI elements around
/// the click – exact button, menu and tab texts, also icons without text – instead of a local OmniParser + OCR sidecar.
/// <list type="bullet">
/// <item>Only the region around the click (<see cref="CropWidth"/> × <see cref="CropHeight"/> source pixels) is sent: the
/// model reads small text reliably and its boxes are far more precise than on a whole screen. Without a known click
/// the whole frame is sent, scaled to 1280 px.</item>
/// <item>Frames are read <see cref="MaxConcurrency"/> at a time. Rate limits and server errors are retried; a frame that
/// still cannot be read is skipped and counted in <see cref="FallbackNotes"/> – naming targets is an enrichment, it
/// must not cost the analysis. A configuration error (invalid key, unknown model) is thrown.</item>
/// </list>
/// </summary>
public sealed class VisionLlmUiParser : IUiParser, Services.IReportsFallback
{
    public const int CropWidth = 640, CropHeight = 400, FullFrameWidth = 1280;

    private readonly ITextGenerator _llm;
    private readonly FfmpegService _ff;
    private int _failed, _total;
    private string? _lastError;

    public VisionLlmUiParser(ITextGenerator llm, FfmpegService? ff = null, int maxConcurrency = 6)
    {
        _llm = llm;
        _ff = ff ?? new FfmpegService();
        MaxConcurrency = Math.Max(1, maxConcurrency);
    }

    public int MaxConcurrency { get; }
    public bool ReadsClickRegionOnly => true;

    public IReadOnlyList<string> FallbackNotes => _failed == 0 ? []
        : [$"{_failed} of {_total} frames could not be read by {_llm.Name} ({_lastError}) – their click targets stay unnamed"];

    public Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default) =>
        ParseAsync(framePngPath, null, ct);

    public async Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, Point2D? click, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _total);
        var (w, h) = PngSize(framePngPath);
        // The region sent to the model, in source pixels, and the scale of the image the model sees.
        int x0 = 0, y0 = 0, cw = w, ch = h;
        if (click is not null && w > CropWidth && h > CropHeight)
        {
            cw = CropWidth; ch = CropHeight;
            x0 = Math.Clamp(click.X - cw / 2, 0, w - cw);
            y0 = Math.Clamp(click.Y - ch / 2, 0, h - ch);
        }
        double scale = click is null && w > FullFrameWidth ? (double)FullFrameWidth / w : 1.0;
        int iw = (int)Math.Round(cw * scale), ih = (int)Math.Round(ch * scale);
        var jpeg = await CropAsync(framePngPath, x0, y0, cw, ch, iw, ct);

        var request = new TextGenerationRequest(
            "You read screenshots of business software for a support knowledge base. Answer only with JSON.",
            Prompt(iw, ih, click is null ? null : ((click.X - x0) * scale, (click.Y - y0) * scale)), Json: true, MaxTokens: 3000, Temperature: 0)
        { Images = [new PromptImage("Screenshot", jpeg)] };

        string answer;
        try { answer = await GenerateWithRetryAsync(request, ct); }
        catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            _lastError = ex.Message.Split('\n')[0];
            return [];
        }
        return Parse(answer, iw, ih).Select(d => d with
        {
            Bbox = new BBox((int)Math.Round(x0 + d.Bbox.X1 / scale), (int)Math.Round(y0 + d.Bbox.Y1 / scale),
                            (int)Math.Round(x0 + d.Bbox.X2 / scale), (int)Math.Round(y0 + d.Bbox.Y2 / scale)),
        }).ToList();
    }

    private static string Prompt(int w, int h, (double X, double Y)? click) =>
        string.Create(CultureInfo.InvariantCulture, $$"""
        The image is {{w}} x {{h}} pixels{{(click is { } c ? $" and part of a screen recording; the user clicks at x={c.X:0}, y={c.Y:0}" : "")}}.
        List the user-interface elements visible in it: buttons, menu items, tabs, links, input fields, checkboxes, list or table rows, icons{{(click is null ? "" : " – always including the element under the click point")}}.
        For each element give:
        - "text": the visible text exactly as shown (same language, same spelling); for an icon without text a short description in square brackets, e.g. "[gear icon]";
        - "type": button, menu_item, tab, link, input, checkbox, list_item, icon, label or other;
        - "interactive": true if it can be clicked or typed into;
        - "box": [x1, y1, x2, y2] in pixels of this image, tight around the element.
        Do not invent elements that are cut off or unreadable. Respond with: {"elements": [{"text": "Speichern", "type": "button", "interactive": true, "box": [10, 20, 90, 44]}]}
        """);

    /// <summary>The model's answer → detections in image pixels; malformed entries are skipped.</summary>
    public static List<UiElementRegistry.Detection> Parse(string answer, int width, int height)
    {
        int a = answer.IndexOf('{'), b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return [];
        JsonNode? root;
        try { root = JsonNode.Parse(answer[a..(b + 1)]); }
        catch (JsonException) { return []; }
        var result = new List<UiElementRegistry.Detection>();
        foreach (var e in root?["elements"] as JsonArray ?? [])
        {
            if (e?["box"] is not JsonArray { Count: 4 } box) continue;
            var v = box.Select(n => n is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : double.NaN).ToArray();
            if (v.Any(double.IsNaN)) continue;
            int x1 = (int)Math.Clamp(Math.Min(v[0], v[2]), 0, width), x2 = (int)Math.Clamp(Math.Max(v[0], v[2]), 0, width);
            int y1 = (int)Math.Clamp(Math.Min(v[1], v[3]), 0, height), y2 = (int)Math.Clamp(Math.Max(v[1], v[3]), 0, height);
            if (x2 - x1 < 2 || y2 - y1 < 2) continue;
            string? text = Str(e["text"])?.Trim();
            bool icon = text is { Length: > 0 } && text[0] == '[';
            bool interactive = e["interactive"] is JsonValue iv && iv.TryGetValue<bool>(out var i) && i;
            result.Add(new UiElementRegistry.Detection(new BBox(x1, y1, x2, y2), string.IsNullOrEmpty(text) ? null : text,
                string.IsNullOrEmpty(text) ? 0 : icon ? 0.6 : 0.95, interactive ? 0.85 : 0.2, Str(e["type"]) ?? "unknown"));
        }
        return result;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task<string> GenerateWithRetryAsync(TextGenerationRequest request, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await _llm.GenerateAsync(request, ct); }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);   // 2, 4, 8 s
            }
        }
    }

    /// <summary>Worth retrying (or skipping the frame): rate limit, server error, timeout, unreadable answer – not a
    /// configuration error such as an invalid key or an unknown model.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException or InvalidOperationException => true,
        HttpRequestException h => h.StatusCode is null || (int)h.StatusCode is 408 or 409 or 429 or >= 500,
        _ => false,
    };

    private async Task<byte[]> CropAsync(string png, int x, int y, int w, int h, int outWidth, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"avag_ui_{Guid.NewGuid():N}.jpg");
        try
        {
            var (_, err, code) = await FfmpegService.RunAsync(_ff.FfmpegPath,
                ["-y", "-loglevel", "error", "-i", png, "-vf", $"crop={w}:{h}:{x}:{y},scale={outWidth}:-2", "-q:v", "3", tmp], null, ct);
            if (code != 0) throw new IOException($"ffmpeg crop failed: {err.Trim()}");
            return await File.ReadAllBytesAsync(tmp, ct);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>Width and height from the PNG header (IHDR).</summary>
    private static (int W, int H) PngSize(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> b = stackalloc byte[24];
        if (fs.Read(b) < 24 || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R')
            throw new InvalidDataException($"{path} is not a PNG");
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b[20..]));
    }
}
