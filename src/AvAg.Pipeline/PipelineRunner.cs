using AvAg.Core;
using AvAg.Pipeline.Adapters;
using AvAg.Pipeline.Media;
using AvAg.Pipeline.Vision;

namespace AvAg.Pipeline;

public sealed class PipelineConfig
{
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "avag");
    public string? LanguageHint { get; init; } = "de";
    public bool Diarize { get; init; } = true;
    public double CoarseFps { get; init; } = 3.0;
    public int CoarseWidth { get; init; } = 960;         // analysis width for coarse pass (cursor ≥ ~4px wide at 1080p→960)
    public double FineFps { get; init; } = 20.0;          // re-decode around audio references at native resolution
    public string? CursorTemplatePng { get; init; }
    public FusionConfig Fusion { get; init; } = new();
    public bool UseVlmFallbackPointing { get; init; } = true;
    public bool IncludeUnspokenEvents { get; init; } = true;
    public bool KeepIntermediateFiles { get; init; } = false;   // privacy: raw frames/WAV are deleted by default
    public bool DescribeClips { get; init; } = false;            // optional auxiliary narrative via IClipDescriber
}

public sealed class PipelineServices
{
    public required IAsrService Asr { get; init; }
    public IUiParser UiParser { get; init; } = new NullUiParser();
    public IVideoGrounder? Grounder { get; init; }
    public IObjectTracker? Tracker { get; init; }
    public IClipDescriber? ClipDescriber { get; init; }
    public FfmpegService Ffmpeg { get; init; } = new();
}

public sealed class PipelineResult
{
    public required EventGraph Graph { get; init; }
    public required Transcript Transcript { get; init; }
    public required IReadOnlyList<AudioRef> AudioRefs { get; init; }
    public required IReadOnlyList<VisualEvent> VisualEvents { get; init; }
    public required string TimelineDe { get; init; }
    public required string TimelineEn { get; init; }
    public Dictionary<string, string> ClipNarratives { get; init; } = new();
    public List<string> Log { get; init; } = new();
}

/// <summary>
/// Orchestrates: FFmpeg demux (PTS time base) → WhisperX → audio-reference parser →
/// coarse frame analysis → fine re-decode around utterances → cursor/click detection →
/// UI parsing (OmniParser/OCR) with stable element IDs → optional VLM pointing fallback →
/// optional SAM2 stabilisation → cross-modal fusion → JSON event graph → text.
/// </summary>
public sealed class PipelineRunner
{
    private readonly PipelineConfig _cfg;
    private readonly PipelineServices _svc;
    private readonly List<string> _log = new();

    public PipelineRunner(PipelineConfig cfg, PipelineServices services) { _cfg = cfg; _svc = services; }

    public async Task<PipelineResult> RunAsync(string videoPath, CancellationToken ct = default)
    {
        var ff = _svc.Ffmpeg;
        var work = Path.Combine(_cfg.WorkDir, Path.GetFileNameWithoutExtension(videoPath) + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            // 1. Ingest -------------------------------------------------------------
            var info = await ff.ProbeAsync(videoPath, ct);
            Log($"probe: {info.WidthPx}x{info.HeightPx} @ {info.NominalFps:0.##} fps, {info.DurationS:0.###} s");

            // 2. Audio → ASR → audio references -------------------------------------
            Transcript transcript;
            if (await ff.HasAudioAsync(videoPath, ct))
            {
                var wav = await ff.ExtractAudioAsync(videoPath, Path.Combine(work, "audio_16k_mono.wav"), ct);
                transcript = await _svc.Asr.TranscribeAsync(wav, _cfg.LanguageHint, _cfg.Diarize, ct);
                if (_svc.Asr is Services.IReportsFallback f) foreach (var note in f.FallbackNotes) Log("asr: " + note);
                int speakers = transcript.Segments.Select(s => s.Speaker).Where(s => s is not null).Distinct().Count();
                if (speakers > 0) Log($"asr: {speakers} speakers");
            }
            else
            {
                Log("no audio stream – ASR skipped");
                transcript = new Transcript("none", new());
            }
            var audioRefs = new AudioRefParser().Parse(transcript);
            Log($"asr: {transcript.Segments.Count} segments, {transcript.AllWords().Count()} words → {audioRefs.Count} action references");

            // 3. Coarse visual pass -------------------------------------------------
            var template = _cfg.CursorTemplatePng is null ? null : await LoadTemplateAsync(ff, _cfg.CursorTemplatePng, ct);
            int coarseW = Math.Min(_cfg.CoarseWidth, info.WidthPx);
            var coarseFrames = await ff.DecodeGrayFramesAsync(videoPath, _cfg.CoarseFps, scaleWidth: coarseW, ct: ct);
            double sx = (double)info.WidthPx / coarseW, sy = coarseFrames.Count > 0 ? (double)info.HeightPx / coarseFrames[0].Height : 1;
            var coarseChanges = new StateChangeDetector().Detect(coarseFrames);
            var coarseTemplate = template is null ? null : Downscale(template, 1 / sx);
            var coarseCursor = new CursorTracker { Template = coarseTemplate }.Track(coarseFrames);
            var events = new ClickCandidateDetector { ScaleX = sx, ScaleY = sy, MaxChangeDelayS = 1.0 / _cfg.CoarseFps + 0.3 }.Detect(coarseCursor, coarseChanges);
            Log($"coarse: {coarseFrames.Count} frames, {coarseChanges.Count} state changes, {coarseCursor.Count} cursor samples → {events.Count} candidates");

            // 4. Fine pass around every audio reference -----------------------------
            var fineEvents = new List<VisualEvent>();
            foreach (var window in MergeWindows(audioRefs.Select(a => (a.AnchorS - _cfg.Fusion.WindowBeforeS, a.AnchorS + _cfg.Fusion.WindowAfterS)), info.DurationS))
            {
                var fineFrames = await ff.DecodeGrayFramesAsync(videoPath, _cfg.FineFps, window.start, window.end, ct: ct);
                var changes = new StateChangeDetector().Detect(fineFrames);
                var cursor = new CursorTracker { Template = template }.Track(fineFrames);
                var cands = new ClickCandidateDetector().Detect(cursor, changes);
                fineEvents.AddRange(cands);
                Log($"fine [{window.start:0.00}-{window.end:0.00}]: {fineFrames.Count} frames, {changes.Count} changes, {cursor.Count} cursor, {cands.Count} candidates");
            }
            events = Deduplicate(events, fineEvents);

            // 5. UI structure + OCR → stable element IDs ----------------------------
            var ui = new UiElementRegistry();
            foreach (var e in events)
            {
                // Frames before the click (target unoccluded, state unchanged) and after (to see the effect).
                foreach (var t in new[] { Math.Max(0, e.TimeS - 0.3), Math.Min(info.DurationS, e.TimeS + 0.4) })
                {
                    var png = await ff.ExtractFramePngAsync(videoPath, t, Path.Combine(work, $"frame_{t:0.000}.png"), ct);
                    var dets = await _svc.UiParser.ParseAsync(png, ct);
                    ui.Observe(t, dets);
                    if (!_cfg.KeepIntermediateFiles) File.Delete(png);
                }
            }
            Log($"ui: {ui.Elements.Count} stable elements");

            // 6. Target assignment + VLM fallback pointing --------------------------
            var finalEvents = new List<VisualEvent>();
            foreach (var e in events)
            {
                if (e.Point is not null)
                {
                    var target = ui.ResolveTarget(e.Point, e.TimeS - 0.3, 0.6) ?? ui.ResolveTarget(e.Point, e.TimeS, 1.0);
                    if (target is not null)
                    {
                        e.TargetId = target.Id;
                        e.UiInteractiveConfidence = target.InteractiveConfidence;
                        e.Evidence.Add(new Evidence(EvidenceSource.OmniParser, target.InteractiveConfidence));
                        if (target.Text is not null) e.Evidence.Add(new Evidence(EvidenceSource.PaddleOcr, target.TextConfidence));
                    }
                    finalEvents.Add(e);
                    continue;
                }

                // No visible pointer: try the grounder (MolmoPoint / Qwen3-VL) – result is tagged as inferred.
                VisualEvent resolved = e;
                if (_cfg.UseVlmFallbackPointing && _svc.Grounder is not null)
                {
                    var pts = await _svc.Grounder.PointAsync(videoPath,
                        "Point to the exact location where the user clicks or taps in this clip.", e.TimeS - 0.5, e.TimeS + 0.5, ct);
                    var best = pts.OrderByDescending(p => p.Confidence).FirstOrDefault();
                    if (best is not null)
                    {
                        var p = new Point2D(best.X, best.Y);
                        var target = ui.ResolveTarget(p, e.TimeS, 1.0);
                        resolved = new VisualEvent
                        {
                            Id = e.Id, Action = e.Action, TimeS = e.TimeS, StartS = e.StartS, EndS = e.EndS, Point = p, ChangedRegion = e.ChangedRegion,
                            DetectorConfidence = e.DetectorConfidence, StateChangeConfidence = e.StateChangeConfidence, TargetId = target?.Id,
                            UiInteractiveConfidence = target?.InteractiveConfidence ?? 0, PointSource = EvidenceSource.VlmPointing,
                            Evidence = e.Evidence.Concat([new Evidence(EvidenceSource.VlmPointing, best.Confidence)]).ToList(),
                        };
                    }
                }
                finalEvents.Add(resolved);
            }

            // Spoken explicit targets without a resolved UI target: look the text up in the registry (semantic, inferred).
            foreach (var a in audioRefs.Where(a => a.ExplicitTarget is not null))
            {
                var el = ui.FindByText(a.ExplicitTarget!, a.AnchorS);
                if (el is null) continue;
                foreach (var e in finalEvents.Where(e => e.TargetId is null && Math.Abs(e.TimeS - a.AnchorS) <= _cfg.Fusion.WindowAfterS))
                {
                    e.TargetId = el.Id;
                    e.UiInteractiveConfidence = el.InteractiveConfidence;
                    e.Evidence.Add(new Evidence(EvidenceSource.VlmSemantic, 0.5));
                }
            }

            // 7. Optional SAM2 stabilisation of target boxes --------------------------
            if (_svc.Tracker is not null)
            {
                foreach (var e in finalEvents.Where(e => e.TargetId is not null))
                {
                    var el = ui.Elements.First(x => x.Id == e.TargetId);
                    var samples = await _svc.Tracker.TrackAsync(videoPath, e.TimeS - 0.3, null, el.Bbox, e.TimeS - 1.0, e.TimeS + 1.0, ct);
                    if (samples.Count > 0)
                    {
                        e.Evidence.Add(new Evidence(EvidenceSource.Sam2Tracker, samples.Average(s => s.Confidence)));
                        el.FirstSeenS = Math.Min(el.FirstSeenS, samples.Min(s => s.TimeS));
                        el.LastSeenS = Math.Max(el.LastSeenS, samples.Max(s => s.TimeS));
                    }
                }
            }

            // 8. Fusion → event graph → text ----------------------------------------
            var assocs = new CrossModalResolver(_cfg.Fusion).Resolve(audioRefs, finalEvents, ui);
            var graph = new EventGraphBuilder().Build(info, audioRefs, finalEvents, assocs, ui, _cfg.IncludeUnspokenEvents);
            var describer = new Describer();
            describer.Annotate(graph);
            Log($"fusion: {assocs.Count} audio↔event bindings, {graph.UnboundAudioReferences.Count} unbound references, {graph.Events.Count} events in graph");

            var narratives = new Dictionary<string, string>();
            if (_cfg.DescribeClips && _svc.ClipDescriber is not null)
                foreach (var ge in graph.Events)
                    narratives[ge.EventId] = await _svc.ClipDescriber.DescribeAsync(videoPath, Math.Max(0, ge.Temporal.StartS - 1), ge.Temporal.EndS + 1, "de", ct);

            // Services that switched to their fallback during the run say so once.
            foreach (var (name, svc) in new (string, object?)[] { ("ui", _svc.UiParser), ("grounder", _svc.Grounder), ("tracker", _svc.Tracker), ("describer", _svc.ClipDescriber) })
                if (svc is Services.IReportsFallback rf) foreach (var note in rf.FallbackNotes) Log($"{name}: {note}");

            return new PipelineResult
            {
                Graph = graph, Transcript = transcript, AudioRefs = audioRefs, VisualEvents = finalEvents,
                TimelineDe = describer.Timeline(graph, "de"), TimelineEn = describer.Timeline(graph, "en"),
                ClipNarratives = narratives, Log = _log,
            };
        }
        finally
        {
            if (!_cfg.KeepIntermediateFiles) { try { Directory.Delete(work, true); } catch { /* best effort */ } }
        }
    }

    private void Log(string msg) => _log.Add(msg);

    /// <summary>Merge overlapping analysis windows, clamp to the video duration.</summary>
    public static List<(double start, double end)> MergeWindows(IEnumerable<(double s, double e)> windows, double duration)
    {
        var sorted = windows.Select(w => (s: Math.Max(0, w.s), e: Math.Min(duration <= 0 ? double.MaxValue : duration, w.e)))
                            .Where(w => w.e > w.s).OrderBy(w => w.s).ToList();
        var merged = new List<(double start, double end)>();
        foreach (var w in sorted)
        {
            if (merged.Count > 0 && w.s <= merged[^1].end + 0.05) merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, w.e));
            else merged.Add(w);
        }
        return merged;
    }

    /// <summary>Fine candidates replace coarse ones that fall within ±0.35 s of them.</summary>
    public static List<VisualEvent> Deduplicate(List<VisualEvent> coarse, List<VisualEvent> fine)
    {
        var result = new List<VisualEvent>(fine);
        foreach (var c in coarse)
            if (!fine.Any(f => Math.Abs(f.TimeS - c.TimeS) <= 0.35)) result.Add(c);
        return result.OrderBy(e => e.TimeS).Select((e, i) => Renumber(e, i)).ToList();
    }

    private static VisualEvent Renumber(VisualEvent e, int i) => new()
    {
        Id = $"vis_{i:D4}", Action = e.Action, TimeS = e.TimeS, StartS = e.StartS, EndS = e.EndS, Point = e.Point, ChangedRegion = e.ChangedRegion,
        TargetId = e.TargetId, DetectorConfidence = e.DetectorConfidence, UiInteractiveConfidence = e.UiInteractiveConfidence,
        StateChangeConfidence = e.StateChangeConfidence, Evidence = e.Evidence.ToList(), PointSource = e.PointSource,
    };

    private static async Task<GrayFrame> LoadTemplateAsync(FfmpegService ff, string pngPath, CancellationToken ct)
    {
        var frames = await ff.DecodeGrayFramesAsync(pngPath, 1, ct: ct);
        return frames.Count > 0 ? frames[0] : throw new InvalidOperationException($"Could not decode cursor template {pngPath}");
    }

    private static GrayFrame Downscale(GrayFrame t, double factor)
    {
        int w = Math.Max(2, (int)Math.Round(t.Width * factor)), h = Math.Max(2, (int)Math.Round(t.Height * factor));
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = t[Math.Min(t.Width - 1, (int)(x / factor)), Math.Min(t.Height - 1, (int)(y / factor))];
        return new GrayFrame { Index = 0, PtsS = 0, Width = w, Height = h, Pixels = px };
    }
}
