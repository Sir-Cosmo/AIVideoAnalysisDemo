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
/// spoken instructions cannot be tied to the clicks.
/// gpt-4o models accept at most 1400 s (diarize) / 1500 s of audio per request. A longer call is cut at pauses into
/// parts of about 20 min; the first part is transcribed alone, and its speakers are passed to the other parts as known
/// speakers (a few seconds of each voice), so "SPEAKER_A" is the same person in the whole call. The other parts run in
/// parallel.
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
    private double? MaxDurationS => MaxRequestSeconds ?? (Diarize ? 1400 : _model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase) ? 1500 : null);

    /// <summary>Longest audio per request; null = the model's limit. For tests and new models.</summary>
    public double? MaxRequestSeconds { get; init; }

    public async Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default)
    {
        if (_apiKey is null) throw new InvalidOperationException("OpenAI speech recognition needs an API key (Services:Asr:ApiKey).");
        double? duration = WavDurationS(audioWavPath);
        var wx = MaxDurationS is { } max && duration is { } dur && dur > max
            ? await TranscribeInPartsAsync(audioWavPath, dur, max, languageHint, ct)
            : Parse(await TranscribeSpanAsync(audioWavPath, null, null, languageHint, [], ct), languageHint);
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

    /// <summary>One request for [<paramref name="startS"/>, <paramref name="endS"/>] of the recording (null = all of it).</summary>
    private async Task<string> TranscribeSpanAsync(string wav, double? startS, double? endS, string? language,
                                                   IReadOnlyList<(string Name, string DataUrl)> speakers, CancellationToken ct)
    {
        var mp3 = Path.Combine(Path.GetTempPath(), $"avag_asr_{Guid.NewGuid():N}.mp3");
        try
        {
            await _ff.EncodeMp3Async(wav, mp3, 32, ct, startS, endS);
            if (new FileInfo(mp3).Length > MaxUploadBytes) await _ff.EncodeMp3Async(wav, mp3, 16, ct, startS, endS);
            if (new FileInfo(mp3).Length > MaxUploadBytes)
                throw new InvalidOperationException("the recording is too long for one OpenAI transcription request (25 MB)");
            return await PostAsync(mp3, language, speakers, ct);
        }
        finally { try { File.Delete(mp3); } catch { /* best effort */ } }
    }

    /// <summary>
    /// A call longer than one request allows: parts cut at pauses, the first transcribed alone, the rest in parallel with
    /// the first part's voices as known speakers. Times are shifted back onto the whole recording.
    /// </summary>
    private async Task<WhisperXJson> TranscribeInPartsAsync(string wav, double duration, double maxS, string? language, CancellationToken ct)
    {
        var parts = SplitAtPauses(duration, maxS, await _ff.DetectSilencesAsync(wav, ct: ct));
        var first = Parse(await TranscribeSpanAsync(wav, parts[0].Start, parts[0].End, language, [], ct), language);
        language ??= first.Language;
        var speakers = Diarize ? await SpeakerSamplesAsync(wav, first, parts[0].Start, ct) : [];
        var rest = await Task.WhenAll(parts.Skip(1).Select(async p =>
            (p.Start, Wx: Parse(await TranscribeSpanAsync(wav, p.Start, p.End, language, speakers, ct), language))));

        var all = new WhisperXJson { Language = first.Language };
        foreach (var (offset, part) in rest.Prepend((parts[0].Start, first)))
            foreach (var seg in part.Segments)
            {
                seg.Start += offset; seg.End += offset;
                foreach (var w in seg.Words) { w.Start += offset; w.End += offset; }
                all.Segments.Add(seg);
            }
        _notes.Add($"{duration / 60:0} min call transcribed in {parts.Count} parts" +
                   (speakers.Count > 0 ? $" with {speakers.Count} known speakers carried across" : ""));
        return all;
    }

    /// <summary>Parts of at most <paramref name="maxS"/> minus a safety margin, each ending in the longest pause in its
    /// last quarter (no word is cut in half there); without a pause, at the limit.</summary>
    public static List<(double Start, double End)> SplitAtPauses(double duration, double maxS, IReadOnlyList<(double StartS, double EndS)> silences)
    {
        double target = Math.Max(60, maxS - 120);
        var parts = new List<(double, double)>();
        double start = 0;
        while (duration - start > target)
        {
            double limit = start + target;
            var pause = silences.Where(s => (s.StartS + s.EndS) / 2 > start + 0.75 * target && (s.StartS + s.EndS) / 2 < limit)
                                .OrderByDescending(s => s.EndS - s.StartS).FirstOrDefault();
            double cut = pause == default ? limit : (pause.StartS + pause.EndS) / 2;
            parts.Add((start, cut));
            start = cut;
        }
        parts.Add((start, duration));
        return parts;
    }

    /// <summary>For up to four speakers of the first part: one clear stretch of 3–10 s of their voice, as a data URL.</summary>
    private async Task<List<(string Name, string DataUrl)>> SpeakerSamplesAsync(string wav, WhisperXJson part, double offset, CancellationToken ct)
    {
        var picks = part.Segments.Where(s => s.Speaker is not null && s.End - s.Start >= 3)
            .GroupBy(s => s.Speaker!).Take(4)
            .Select(g => (Name: g.Key.Replace("SPEAKER_", ""), Seg: g.OrderBy(s => Math.Abs(s.End - s.Start - 6)).First()))
            .ToList();
        var result = new List<(string, string)>();
        foreach (var (name, seg) in picks)
        {
            var mp3 = Path.Combine(Path.GetTempPath(), $"avag_spk_{Guid.NewGuid():N}.mp3");
            try
            {
                double s = offset + seg.Start, e = Math.Min(offset + seg.End, s + 10);
                await _ff.EncodeMp3Async(wav, mp3, 64, ct, s, e);
                result.Add((name, "data:audio/mpeg;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(mp3, ct))));
            }
            finally { try { File.Delete(mp3); } catch { /* best effort */ } }
        }
        return result;
    }

    private async Task<string> PostAsync(string mp3, string? language, IReadOnlyList<(string Name, string DataUrl)> speakers, CancellationToken ct)
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
            foreach (var (name, dataUrl) in speakers)
            {
                form.Add(new StringContent(name), "known_speaker_names[]");
                form.Add(new StringContent(dataUrl), "known_speaker_references[]");
            }
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
