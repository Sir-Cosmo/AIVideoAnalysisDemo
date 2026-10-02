using System.Globalization;
using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>One spoken sentence with its time span and (if speaker separation ran) its speaker label.</summary>
public sealed record Sentence(double StartS, double EndS, string Text, string? Speaker = null);

/// <summary>Splits a word-timed transcript into clean sentences – the unit the article builders work with.</summary>
public static class TranscriptSentences
{
    private const int MaxWords = 40;

    // Pure fillers go everywhere; discourse words that also carry meaning ("So geht das", "Gut, dass …", "um … zu")
    // only at the start and only when set off by a comma ("Also, …", "Um, …").
    private static readonly Regex FillerRx = new(
        @"^(?:(?:äh|ähm|ehm|uh|uhm)\b[,\s]*|(?:also|so|okay|ok|ja|nun|gut|und|um|well|right|alright|now|and)\s*,\s*)+|\b(?:äh|ähm|ehm|uh|uhm)\b[,]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sentences from word timings. A sentence ends at ".", "!", "?" or "…", or after <see cref="MaxWords"/> words for
    /// unpunctuated run-ons; short fragments are glued to the sentence before. A change of speaker always starts a new
    /// sentence.
    /// </summary>
    public static List<Sentence> Split(Transcript t)
    {
        var result = new List<Sentence>();
        foreach (var seg in t.Segments)
        {
            if (seg.Words.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(seg.Text)) Add(seg.StartS, seg.EndS, seg.Text, seg.Speaker);
                continue;
            }
            var buf = new List<Word>();
            foreach (var w in seg.Words)
            {
                if (buf.Count > 0 && w.Speaker is not null && buf[^1].Speaker is not null && w.Speaker != buf[^1].Speaker) Flush();
                buf.Add(w);
                bool end = Regex.IsMatch(w.Text, @"[.!?…]$") && !Regex.IsMatch(w.Text, @"^\d+\.$");
                if (end || buf.Count >= MaxWords) Flush();
            }
            Flush();

            void Flush()
            {
                if (buf.Count == 0) return;
                string? speaker = buf.Where(x => x.Speaker is not null).GroupBy(x => x.Speaker).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? seg.Speaker;
                Add(buf[0].StartS, buf[^1].EndS, string.Join(" ", buf.Select(x => x.Text)), speaker);
                buf.Clear();
            }
        }
        return result;

        void Add(double s, double e, string text, string? speaker)
        {
            text = Clean(text);
            if (text.Length < 3) return;
            // Fragments ("…your entire" / "Screen right like so") belong to the previous sentence of the same speaker.
            // Only short pieces are glued – two long unpunctuated halves stay separate sentences.
            int nNew = text.Split(' ').Length, nPrev = result.Count > 0 ? result[^1].Text.Split(' ').Length : 0;
            if (result.Count > 0 && result[^1].Speaker == speaker && !Regex.IsMatch(result[^1].Text, @"[.!?…]$") && s - result[^1].EndS < 1.0
                && (nNew <= 6 || nPrev <= 6) && nNew + nPrev <= MaxWords + 5)
                result[^1] = result[^1] with { EndS = e, Text = result[^1].Text + " " + char.ToLower(text[0]) + text[1..] };
            else result.Add(new Sentence(s, e, text, speaker));
        }
    }

    /// <summary>Removes filler words ("äh", "uh", a leading "Also, / Okay, / Um,"), stutters and stray spaces; capitalises.</summary>
    public static string Clean(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        text = FillerRx.Replace(text, "").Trim();
        text = Regex.Replace(text, @"\s+([,.!?])", "$1");
        text = Regex.Replace(text, @"\b(\w+)( \1\b)+", "$1", RegexOptions.IgnoreCase); // "the the" → "the"; "way, way, way" stays
        if (text.Length > 0) text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        return text;
    }
}
