using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvAg.Core;
using AvAg.Pipeline.Media;

namespace AvAg.Pipeline;

/// <summary>Optional language model for the manual text: any OpenAI-compatible chat endpoint
/// (Ollama, LM Studio, vLLM, Azure OpenAI v1, …). Empty <see cref="Url"/> = rule-based manual only.</summary>
public sealed class ManualLlmSettings
{
    /// <summary>Base URL up to and including /v1, e.g. http://127.0.0.1:11434/v1 (Ollama) or https://&lt;res&gt;.openai.azure.com/openai/v1.</summary>
    public string? Url { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Turns a pipeline result into a <see cref="Manual"/>: rule-based steps from the narration (always available), optionally
/// rewritten by a language model into a concise imperative manual, plus one screenshot per step (click marker drawn when
/// the click was observed). Screenshots are extracted one at a time with FFmpeg – cheap on CPU, no GPU needed.
/// </summary>
public sealed class ManualService
{
    private readonly FfmpegService _ff;
    private readonly ManualLlmSettings? _llm;
    public int ScreenshotWidth { get; init; } = 1280;
    public List<string> Log { get; } = new();

    public ManualService(ManualLlmSettings? llm = null, FfmpegService? ff = null) { _llm = llm; _ff = ff ?? new FfmpegService(); }

    /// <param name="privateVideo">Text-only manual: no frame is extracted from the video and no click position is kept.</param>
    public async Task<Manual> CreateAsync(PipelineResult result, string videoPath, string? videoName, string? language, bool useLlm,
                                          bool privateVideo = false, CancellationToken ct = default)
    {
        var manual = new ManualBuilder().Build(result.Transcript, result.AudioRefs, result.Graph, videoName, language);
        Log.Add($"manual: {manual.Steps.Count} steps from the narration");

        if (useLlm && !string.IsNullOrWhiteSpace(_llm?.Url))
        {
            try
            {
                var written = await WriteWithLlmAsync(manual, result, ct);
                manual = written;
                Log.Add($"manual: written by {manual.Method} → {manual.Steps.Count} steps");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Add($"manual: language model not usable ({ex.Message}) – kept the rule-based manual");
            }
        }

        if (privateVideo)
        {
            manual.Private = true;
            foreach (var s in manual.Steps) { s.ScreenshotS = null; s.PointXyPx = null; s.BboxXyxyPx = null; s.ScreenshotJpeg = null; }
            Log.Add("manual: private video – text only, no screenshots taken");
        }
        else
        {
            await RefineScreenshotTimesAsync(manual, videoPath, ct);
            await AddScreenshotsAsync(manual, videoPath, result.Graph.Video, ct);
        }
        return manual;
    }

    /// <summary>Mean absolute difference (0–255) below which two thumbnails count as "the same screen".</summary>
    public double SameScreenThreshold { get; init; } = 2.5;

    /// <summary>
    /// For steps without an observed click, looks at small thumbnails of the step's own part of the video (2 fps, 160 px)
    /// and picks the frame that is settled (barely changes in the next half second, so menus have finished opening) and
    /// differs most from the previous step's screenshot. If every candidate looks like the previous screenshot, the step
    /// gets no picture instead of a repeated one. Cheap: FFmpeg decodes a few seconds per step at thumbnail size.
    /// </summary>
    public async Task RefineScreenshotTimesAsync(Manual m, string videoPath, CancellationToken ct)
    {
        GrayFrame? prev = null;
        int moved = 0, dropped = 0;
        foreach (var s in m.Steps)
        {
            if (s.PointXyPx is not null && s.ScreenshotS is { } fixedT)
            {
                prev = (await _ff.DecodeGrayFramesAsync(videoPath, 2, fixedT, fixedT + 0.5, scaleWidth: 160, ct: ct)).FirstOrDefault() ?? prev;
                continue;
            }
            double from = s.TimeS, to = Math.Min(Math.Max(s.EndS, s.TimeS + 1.0) + 1.0, s.TimeS + 15.0);
            var frames = await _ff.DecodeGrayFramesAsync(videoPath, 2, from, to, scaleWidth: 160, ct: ct);
            if (frames.Count == 0) continue;

            double bestScore = double.NegativeInfinity, bestNovelty = 0;
            GrayFrame? best = null;
            for (int i = 0; i < frames.Count; i++)
            {
                double unsettled = i + 1 < frames.Count ? Diff(frames[i], frames[i + 1]) : 0;
                double novelty = prev is null ? 255 : Diff(frames[i], prev);
                // Prefer frames near the spoken instruction when everything else is equal.
                double nearSpoken = s.ScreenshotS is { } t0 ? -0.05 * Math.Abs(frames[i].PtsS - t0) : 0;
                double score = Math.Min(novelty, 40) - 2.0 * unsettled + nearSpoken;
                if (score > bestScore) { bestScore = score; best = frames[i]; bestNovelty = novelty; }
            }
            if (best is null) continue;
            if (prev is not null && bestNovelty < SameScreenThreshold) { s.ScreenshotS = null; dropped++; continue; }
            if (s.ScreenshotS is not { } old || Math.Abs(old - best.PtsS) > 0.25) moved++;
            s.ScreenshotS = best.PtsS;
            prev = best;
        }
        Log.Add($"manual: screenshots – {moved} moved to a settled, distinct frame, {dropped} skipped (same screen as the step before)");
    }

    private static double Diff(GrayFrame a, GrayFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i++) sum += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        return (double)sum / a.Pixels.Length;
    }

    // ---- screenshots -------------------------------------------------------------------------------
    public async Task AddScreenshotsAsync(Manual m, string videoPath, VideoInfo v, CancellationToken ct)
    {
        int w = Math.Min(ScreenshotWidth, v.WidthPx > 0 ? v.WidthPx : ScreenshotWidth);
        int h = v.WidthPx > 0 ? (int)Math.Round(v.HeightPx * (double)w / v.WidthPx / 2) * 2 : 0;
        foreach (var s in m.Steps)
        {
            if (s.ScreenshotS is not { } t) continue;
            t = Math.Clamp(t, 0, Math.Max(0, v.DurationS - 0.1));
            var tmp = Path.Combine(Path.GetTempPath(), $"avag_manual_{Guid.NewGuid():N}.jpg");
            try
            {
                var vf = new StringBuilder();
                int stroke = Math.Max(3, v.WidthPx / 400);
                if (s.BboxXyxyPx is { Length: 4 } b)
                    vf.Append(CultureInfo.InvariantCulture, $"drawbox=x={b[0] - 4}:y={b[1] - 4}:w={b[2] - b[0] + 8}:h={b[3] - b[1] + 8}:color=0xE8336B@0.95:t={stroke},");
                if (s.PointXyPx is { Length: 2 } p)
                {
                    int r = Math.Max(18, v.WidthPx / 70);
                    vf.Append(CultureInfo.InvariantCulture, $"drawbox=x={p[0] - r}:y={p[1] - r}:w={2 * r}:h={2 * r}:color=0xE8336B@0.95:t={stroke},");
                }
                vf.Append(CultureInfo.InvariantCulture, $"scale={w}:-2");
                var (_, err, code) = await FfmpegService.RunAsync(_ff.FfmpegPath,
                    ["-y", "-loglevel", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", vf.ToString(), "-q:v", "4", tmp], null, ct);
                if (code != 0 || !File.Exists(tmp)) { Log.Add($"manual: screenshot at {t:0.0}s failed: {err.Trim()}"); continue; }
                s.ScreenshotJpeg = await File.ReadAllBytesAsync(tmp, ct);
                s.ScreenshotS = t; s.ScreenshotWidth = w; s.ScreenshotHeight = h;
            }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
    }

    // ---- language model ----------------------------------------------------------------------------
    /// <summary>
    /// The model reads the whole numbered transcript and writes the manual; every step names the transcript sentences
    /// it comes from. Those numbers – not model-generated times – give each step its place in the video, so the
    /// screenshots and click markers stay measured. Throws <see cref="InvalidOperationException"/> when the answer is
    /// unusable; the caller then keeps the rule-based manual.
    /// </summary>
    private async Task<Manual> WriteWithLlmAsync(Manual draft, PipelineResult result, CancellationToken ct)
    {
        bool de = draft.Language == "de";
        var sentences = ManualBuilder.Sentences(result.Transcript);
        if (sentences.Count == 0) throw new InvalidOperationException("no speech in the video");
        var transcript = new StringBuilder();
        for (int i = 0; i < sentences.Count; i++)
            transcript.Append(CultureInfo.InvariantCulture, $"[{i + 1}] ({ManualRenderer.Ts(sentences[i].StartS)}) {sentences[i].Text}\n");

        string system = de
            ? "Du schreibst aus dem Transkript eines Bildschirm-Tutorials eine sachliche Schritt-für-Schritt-Anleitung auf Deutsch (Sie-Form, Imperativ). Programm-, Menü- und Schaltflächennamen bleiben so, wie sie im Transkript stehen (nicht übersetzen). Lass Füllwörter, Werbung und Abschiedsfloskeln weg und erfinde nichts, was nicht im Transkript steht."
            : "You turn the transcript of a screen-recording tutorial into a factual step-by-step manual in English (imperative mood). Keep program, menu and button names exactly as in the transcript. Drop filler, self-promotion and sign-offs, and do not invent anything that is not in the transcript.";
        string user = $$"""
            {{(de ? "Transkript, nummerierte Sätze [Nummer] (Zeit):" : "Transcript, numbered sentences [number] (time):")}}
            {{transcript}}
            {{(de
                ? """
                  Schreibe die Anleitung für das GANZE Video, vom ersten bis zum letzten Satz:
                  - Jeder Schritt ist GENAU EINE Handlung, die der Zuschauer selbst ausführt (Taste, Klick, Auswahl, Eingabe, Speichern, Exportieren …), in der Reihenfolge des Videos. Lass keine Handlung aus und fasse nicht mehrere Handlungen in einen Schritt.
                  - "instruction" ist ein kurzer Satz im Imperativ (Sie-Form). Tastenkombinationen schreibst du als "Strg + C" bzw. "Windows-Taste + Umschalt + R".
                  - "details" sagt, was danach auf dem Bildschirm passiert oder warum der Schritt nötig ist – nur wenn es im Transkript steht, sonst null.
                  - Zeigt das Video mehrere Wege (z. B. Tastenkürzel und ein Programm), gib jedem Weg einen "section"-Namen (z. B. "Methode 1: Tastenkürzel"); aufeinanderfolgende Schritte desselben Wegs haben denselben Namen. Gibt es nur einen Weg, ist "section" null.
                  - Programme oder Wege, die nur erwähnt und nicht gezeigt werden, gehören in "tips".
                  - "sentences" nennt die Nummern der Transkriptsätze, in denen der Schritt gesagt bzw. gezeigt wird.
                  """
                : """
                  Write the manual for the WHOLE video, from the first to the last sentence:
                  - Each step is EXACTLY ONE action the viewer performs (key press, click, selection, typing, saving, exporting …), in the order of the video. Do not leave out any action and do not merge several actions into one step.
                  - "instruction" is one short imperative sentence. Write key combinations as "Ctrl + C" or "Windows key + Shift + R".
                  - "details" says what happens on screen afterwards or why the step is needed – only if the transcript says so, otherwise null.
                  - If the video shows several ways (e.g. a shortcut and a program), give each way a "section" name (e.g. "Method 1: Keyboard shortcut"); consecutive steps of the same way share it. If there is only one way, "section" is null.
                  - Programs or ways that are only mentioned but not shown go into "tips".
                  - "sentences" lists the numbers of the transcript sentences in which the step is said or shown.
                  """)}}
            {{(de ? "Antworte NUR mit JSON in genau diesem Format:" : "Respond ONLY with JSON in exactly this format:")}}
            {"title": "{{(de ? "kurzer Titel" : "short title")}}", "summary": "{{(de ? "2-3 Sätze: was man am Ende erreicht hat" : "2-3 sentences: what the viewer will have achieved")}}", "prerequisites": [],
             "steps": [{"section": null, "title": "{{(de ? "kurze Handlung" : "short action")}}", "instruction": "{{(de ? "Klicken Sie …" : "Click …")}}", "details": null, "sentences": [1]}],
             "tips": []}
            """;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_llm!.TimeoutSeconds) };
        using var req = new HttpRequestMessage(HttpMethod.Post, _llm.Url!.TrimEnd('/') + "/chat/completions");
        if (!string.IsNullOrWhiteSpace(_llm.ApiKey))
        {
            req.Headers.Add("Authorization", "Bearer " + _llm.ApiKey);
            req.Headers.Add("api-key", _llm.ApiKey); // Azure OpenAI
        }
        req.Content = JsonContent.Create(new
        {
            model = _llm.Model ?? "",
            temperature = 0.2,
            max_tokens = 3000,
            response_format = new { type = "json_object" },
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        });
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidOperationException("empty answer");
        int a = content.IndexOf('{'), z = content.LastIndexOf('}');
        if (a < 0 || z <= a) throw new InvalidOperationException("answer is not JSON");
        var j = JsonNode.Parse(content[a..(z + 1)])!;

        var m = new Manual
        {
            Title = Str(j["title"]) ?? draft.Title, Language = draft.Language, Summary = Str(j["summary"]) ?? draft.Summary,
            Prerequisites = Strs(j["prerequisites"]).Where(p => !Regex.IsMatch(p, @"^(keine|none|no special|nothing|n/a)", RegexOptions.IgnoreCase)).ToList(),
            Tips = Strs(j["tips"]),
            Method = "llm:" + (_llm.Model ?? "model"), VideoName = draft.VideoName, VideoDurationS = draft.VideoDurationS,
        };
        int anchored = 0;
        double prevEnd = 0;
        foreach (var s in j["steps"]?.AsArray() ?? new JsonArray())
        {
            var instr = Str(s?["instruction"]);
            if (instr is null) continue;
            var ids = (s?["sentences"] as JsonArray ?? new JsonArray()).Select(Num)
                        .Where(n => n >= 1 && n <= sentences.Count).Select(n => (int)n!.Value - 1).Distinct().ToList();
            if (ids.Count > 0) anchored++;
            double start = ids.Count > 0 ? ids.Min(i => sentences[i].StartS) : prevEnd;
            double end = ids.Count > 0 ? ids.Max(i => sentences[i].EndS) : prevEnd;
            m.Steps.Add(new ManualStep { Section = Str(s?["section"]), Title = Str(s?["title"]), Instruction = instr, Details = Str(s?["details"]), TimeS = start, EndS = end });
            prevEnd = end;
        }
        if (m.Steps.Count < 2 || anchored * 2 < m.Steps.Count)
            throw new InvalidOperationException($"{m.Steps.Count} steps, {anchored} tied to the transcript");

        // Video order (a stable sort keeps the model's order for steps from the same sentence).
        m.Steps = m.Steps.OrderBy(x => x.TimeS).ToList();
        for (int i = 0; i < m.Steps.Count; i++) m.Steps[i].Number = i + 1;
        double covered = m.Steps.Max(x => x.EndS), lastSpeech = sentences[^1].EndS;
        if (covered < 0.6 * lastSpeech) Log.Add($"manual: steps cover the video only up to {ManualRenderer.Ts(covered)} of {ManualRenderer.Ts(lastSpeech)}");

        ManualBuilder.PlaceScreenshots(m, result.AudioRefs, result.Graph);
        return m;

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s != "null" ? s.Trim() : null;
        static double? Num(JsonNode? n) => n is not JsonValue v ? null
            : v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<string>(out var str) && double.TryParse(str.Trim().Trim('[', ']'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
        static List<string> Strs(JsonNode? n) => n is JsonArray arr ? arr.Select(Str).Where(x => x is not null).Select(x => x!).ToList() : new();
    }
}
