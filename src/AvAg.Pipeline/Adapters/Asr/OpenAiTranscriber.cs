using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Provider "openai": speech recognition with the OpenAI audio API (<c>POST {Url}/audio/transcriptions</c>).
/// <list type="bullet">
/// <item><c>gpt-4o-transcribe-diarize</c> (default): best text plus speaker labels (customer / supporter), times per
/// segment.</item>
/// <item><c>whisper-1</c>: times per word, no speaker labels.</item>
/// </list>
/// Text-only models (<c>gpt-4o-transcribe</c>, <c>gpt-4o-mini-transcribe</c>) are refused: without times per segment the
/// spoken instructions cannot be tied to the clicks. gpt-4o models accept at most 1400 s (diarize) / 1500 s of audio;
/// a longer recording is refused before the upload, so the fallback (local WhisperX) takes over at no cost.
/// The diarize model reports no language: without a language hint it is recognised from the text (German or English).
/// Word times matter: they tie "klicken Sie hier" to the click on screen. With an <c>AlignUrl</c> (the local WhisperX
/// sidecar's POST /align) the cloud transcript is aligned to exact word times locally; otherwise words are spread
/// evenly over their segment and marked less certain. The audio is uploaded as compressed MP3 (max. 25 MB ≈ 14 h).
/// </summary>
public sealed class OpenAiTranscriber : IAsrService, Services.IReportsFallback
{
    public const string DefaultModel = "gpt-4o-transcribe-diarize";
    private const long MaxUploadBytes = 25L * 1024 * 1024 - 64 * 1024;

    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey, _prompt, _alignUrl;
    private readonly TimeSpan _timeout;
    private readonly FfmpegService _ff;
    private readonly HttpClient _http;

    public OpenAiTranscriber(string baseUrl, string? model, string? apiKey, TimeSpan timeout, string? prompt = null, string? alignUrl = null,
                             FfmpegService? ff = null, HttpClient? http = null)
    {
        _endpoint = new Uri(baseUrl.TrimEnd('/') + "/audio/transcriptions");
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        if (!Diarize && !WordTimes)
            throw new InvalidOperationException($"OpenAI model {_model} returns no timestamps – use {DefaultModel} or whisper-1");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt;
        _alignUrl = string.IsNullOrWhiteSpace(alignUrl) ? null : alignUrl.TrimEnd('/') + "/";
        _timeout = timeout;
        _ff = ff ?? new FfmpegService();
        _http = http ?? HttpServiceClient.Shared;
    }

    private readonly List<string> _notes = new();
    /// <summary>Set when the local alignment was not available and word times had to be estimated.</summary>
    public IReadOnlyList<string> FallbackNotes => _notes;

    private bool Diarize => _model.Contains("diarize", StringComparison.OrdinalIgnoreCase);
    private bool WordTimes => _model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase);
    /// <summary>Longest audio the model accepts (OpenAI: "audio duration … is longer than 1400 seconds"); null = no limit.</summary>
    private double? MaxDurationS => Diarize ? 1400 : _model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase) ? 1500 : null;

    public async Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default)
    {
        if (_apiKey is null) throw new InvalidOperationException("OpenAI speech recognition needs an API key (Services:Asr:ApiKey).");
        if (MaxDurationS is { } max && WavDurationS(audioWavPath) is { } dur && dur > max)
            throw new InvalidOperationException($"the recording is {dur / 60:0} min long; {_model} accepts at most {max / 60:0.#} min");
        var mp3 = Path.Combine(Path.GetTempPath(), $"avag_asr_{Guid.NewGuid():N}.mp3");
        try
        {
            await _ff.EncodeMp3Async(audioWavPath, mp3, 32, ct);
            if (new FileInfo(mp3).Length > MaxUploadBytes) await _ff.EncodeMp3Async(audioWavPath, mp3, 16, ct);
            if (new FileInfo(mp3).Length > MaxUploadBytes)
                throw new InvalidOperationException("the recording is too long for one OpenAI transcription request (25 MB)");

            var body = await PostAsync(mp3, languageHint, ct);
            var wx = Parse(body, languageHint);
            if (_alignUrl is not null && wx.Segments.Count > 0)
            {
                // Exact word times are a refinement: without the local aligner the transcript is still used.
                try { wx = await AlignAsync(audioWavPath, wx, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _notes.Add($"word alignment not available ({ex.Message.Split('\n')[0]}) – word times estimated per segment");
                }
            }
            return WhisperXMapper.ToTranscript(wx);
        }
        finally { try { File.Delete(mp3); } catch { /* best effort */ } }
    }

    private async Task<string> PostAsync(string mp3, string? language, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(mp3, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "audio.mp3");
        form.Add(new StringContent(_model), "model");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        if (Diarize)
        {
            form.Add(new StringContent("diarized_json"), "response_format");
            form.Add(new StringContent("auto"), "chunking_strategy");   // required for recordings longer than 30 s
        }
        else if (WordTimes)
        {
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("word"), "timestamp_granularities[]");
            form.Add(new StringContent("segment"), "timestamp_granularities[]");
        }
        else form.Add(new StringContent("json"), "response_format");
        if (_prompt is not null && !Diarize) form.Add(new StringContent(_prompt), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var (status, text) = await HttpServiceClient.SendAsync(_http, req, _timeout, $"OpenAI {_model}", ct);
        if (status >= 300)
            throw new HttpRequestException($"OpenAI {_model}: HTTP {status}: {HttpServiceClient.ErrorMessage(text)}", null, (System.Net.HttpStatusCode)status);
        return text;
    }

    /// <summary>
    /// Any of the three answer shapes → WhisperX JSON. Diarized segments get "SPEAKER_A"-style labels and words without
    /// times (spread over the segment later); verbose_json words are assigned to their segments by time. Without a
    /// reported language or a hint, the language is recognised from the text.
    /// </summary>
    public static WhisperXJson Parse(string body, string? languageHint)
    {
        JsonNode j;
        try { j = JsonNode.Parse(body) ?? throw new InvalidOperationException("empty answer"); }
        catch (JsonException ex) { throw new InvalidOperationException("OpenAI transcription answer is not JSON: " + ex.Message); }

        var wx = new WhisperXJson { Language = Iso(j["language"]?.GetValue<string>()) ?? languageHint };
        var segments = j["segments"] as JsonArray;
        var words = (j["words"] as JsonArray)?.Select(w => new WhisperXWord
        {
            Word = w?["word"]?.GetValue<string>() ?? "", Start = Num(w?["start"]), End = Num(w?["end"]), Score = 1.0,
        }).Where(w => w.Word.Length > 0).ToList() ?? new();

        if (segments is { Count: > 0 })
        {
            foreach (var s in segments)
            {
                string text = s?["text"]?.GetValue<string>()?.Trim() ?? "";
                if (text.Length == 0) continue;
                string? speaker = s?["speaker"]?.GetValue<string>() is { Length: > 0 } sp ? (sp.StartsWith("SPEAKER", StringComparison.OrdinalIgnoreCase) ? sp : "SPEAKER_" + sp) : null;
                double start = Num(s?["start"]) ?? 0, end = Num(s?["end"]) ?? start;
                var seg = new WhisperXSegment { Start = start, End = end, Text = text, Speaker = speaker };
                var own = words.Where(w => w.Start is { } ws && ws >= start - 0.05 && ws < end + 0.05 && !wx.Segments.Any(x => x.Words.Contains(w))).ToList();
                seg.Words = own.Count > 0 ? own : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => new WhisperXWord { Word = t, Score = 0.9 }).ToList();
                foreach (var w in seg.Words) w.Speaker = speaker;
                wx.Segments.Add(seg);
            }
        }
        else if (j["text"]?.GetValue<string>() is { Length: > 0 } all)
        {
            double end = words.Count > 0 ? words[^1].End ?? 0 : Num(j["duration"]) ?? 0;
            wx.Segments.Add(new WhisperXSegment
            {
                Start = 0, End = end, Text = all.Trim(),
                Words = words.Count > 0 ? words : all.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => new WhisperXWord { Word = t, Score = 0.9 }).ToList(),
            });
        }
        wx.Language ??= GuessLanguage(string.Join(" ", wx.Segments.Select(s => s.Text)));
        return wx;
    }

    private static readonly HashSet<string> GermanWords = new(StringComparer.OrdinalIgnoreCase)
        { "und", "ich", "sie", "die", "der", "das", "nicht", "ist", "auf", "ein", "eine", "mit", "es", "wir", "den", "zu", "dann", "hier", "jetzt", "bitte", "kann", "haben" };
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "you", "is", "to", "it", "not", "on", "a", "with", "we", "this", "that", "of", "can", "then", "here", "now", "please", "have" };

    /// <summary>"de" or "en" by frequent function words; null when the text is too short or neither clearly wins.</summary>
    public static string? GuessLanguage(string text)
    {
        var words = text.Split([' ', ',', '.', '!', '?', ';', ':', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int de = words.Count(GermanWords.Contains), en = words.Count(EnglishWords.Contains);
        if (de + en < 3) return null;
        return de >= 2 * en ? "de" : en >= 2 * de ? "en" : null;
    }

    /// <summary>Exact word times from the local WhisperX sidecar; speaker labels survive.</summary>
    private async Task<WhisperXJson> AlignAsync(string wav, WhisperXJson wx, CancellationToken ct)
    {
        if (wx.Language is not { Length: 2 } language)
            throw new InvalidOperationException("the spoken language is unknown – set a language for the job");
        var body = new
        {
            audio_path = Path.GetFullPath(wav),
            language,
            segments = wx.Segments.Select(s => new { start = s.Start, end = s.End, text = s.Text, speaker = s.Speaker }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_alignUrl!), "align")) { Content = JsonContent.Create(body) };
        var (status, text) = await HttpServiceClient.SendAsync(_http, req, _timeout, "WhisperX /align", ct);
        if (status >= 300) throw new HttpRequestException($"WhisperX /align: HTTP {status}: {text[..Math.Min(text.Length, 300)]}");
        var aligned = JsonSerializer.Deserialize<WhisperXJson>(text) ?? throw new InvalidOperationException("WhisperX /align: empty answer");
        aligned.Language ??= wx.Language;
        return aligned;
    }

    /// <summary>Length of a PCM WAV file from its header; null if it cannot be read.</summary>
    private static double? WavDurationS(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            string Id() => System.Text.Encoding.ASCII.GetString(r.ReadBytes(4));
            if (Id() != "RIFF") return null;
            r.ReadInt32();
            if (Id() != "WAVE") return null;
            int byteRate = 0;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = Id();
                long size = r.ReadUInt32(), next = r.BaseStream.Position + size + (size & 1);
                if (id == "data") return byteRate > 0 ? Math.Min(size, r.BaseStream.Length - r.BaseStream.Position) / (double)byteRate : null;
                if (id == "fmt ") { r.ReadBytes(8); byteRate = r.ReadInt32(); }
                r.BaseStream.Position = next;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>verbose_json reports "german"; the rest of the pipeline uses ISO codes.</summary>
    private static string? Iso(string? language) => language?.ToLowerInvariant() switch
    {
        null or "" => null,
        "german" or "deutsch" => "de", "english" => "en", "french" => "fr", "italian" => "it", "spanish" => "es",
        var l when l.Length == 2 => l,
        var l => CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.EnglishName.Equals(l, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName,
    };

}
