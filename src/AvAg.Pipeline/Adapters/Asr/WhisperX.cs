using System.Text.Json;
using System.Text.Json.Serialization;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

// ---------------------------------------------------------------------------
// WhisperX JSON format (as written by `whisperx --output_format json` or by sidecars/whisperx_server.py).
// Any speech recogniser that can produce this shape can reuse WhisperXMapper.
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

/// <summary>Provider "whisperx": sidecars/whisperx_server.py — POST /transcribe {audio_path, language, diarize} → WhisperX JSON.</summary>
public sealed class WhisperXSidecar : HttpServiceClient, IAsrService
{
    public WhisperXSidecar(string baseUrl, HttpClient? http = null, TimeSpan? timeout = null) : base(baseUrl, http, timeout) { }

    public async Task<Transcript> TranscribeAsync(string audioWavPath, string? languageHint, bool diarize, CancellationToken ct = default)
    {
        var wx = await PostAsync<object, WhisperXJson>("transcribe", new { audio_path = Path.GetFullPath(audioWavPath), language = languageHint, diarize }, ct);
        return WhisperXMapper.ToTranscript(wx);
    }
}

/// <summary>Provider "whisperx-json": a precomputed WhisperX JSON file – for offline runs, tests and debugging.</summary>
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
