namespace AvAg.Core;

/// <summary>
/// Calibratable weights for the deterministic resolver
/// S(a,e) = wt·S_time + ws·S_semantic + wp·S_pointer + wu·S_UI + wc·S_change, scaled by detector confidence.
/// These are engineering start values from the design document – calibrate on validation data.
/// </summary>
public sealed class FusionConfig
{
    public double WindowBeforeS { get; init; } = 1.5;   // search window before the utterance anchor
    public double WindowAfterS { get; init; } = 2.5;    // search window after the utterance anchor
    public double TemporalTauS { get; init; } = 0.75;
    public double WTime { get; init; } = 0.35;
    public double WSemantic { get; init; } = 0.20;
    public double WPointer { get; init; } = 0.15;
    public double WUi { get; init; } = 0.15;
    public double WChange { get; init; } = 0.15;
    public double MinAssociationScore { get; init; } = 0.35;
    /// <summary>Bonus when the spoken explicit target text matches the resolved UI target text.</summary>
    public double ExplicitTargetBonus { get; init; } = 0.15;
}

public sealed record Association(AudioRef Audio, VisualEvent Event, double Score);

public sealed class AssociationScorer
{
    private readonly FusionConfig _cfg;
    public AssociationScorer(FusionConfig? cfg = null) => _cfg = cfg ?? new FusionConfig();

    public bool InWindow(AudioRef a, VisualEvent e) =>
        e.TimeS >= a.AnchorS - _cfg.WindowBeforeS && e.TimeS <= a.AnchorS + _cfg.WindowAfterS;

    public double Score(AudioRef a, VisualEvent e, UiElement? target)
    {
        double dt = Math.Abs(e.TimeS - a.AnchorS);
        double temporal = Math.Exp(-dt / _cfg.TemporalTauS);
        double semantic = SemanticMatch(a.Action, e.Action);
        double pointer = e.Point is not null ? 1.0 : 0.2;
        double ui = Math.Clamp(e.UiInteractiveConfidence, 0, 1);
        double change = Math.Clamp(e.StateChangeConfidence, 0, 1);

        double s = _cfg.WTime * temporal + _cfg.WSemantic * semantic + _cfg.WPointer * pointer
                 + _cfg.WUi * ui + _cfg.WChange * change;

        if (a.ExplicitTarget is not null && target?.Text is not null &&
            UiElementRegistry.TextSimilarity(a.ExplicitTarget, target.Text) >= 0.6)
            s += _cfg.ExplicitTargetBonus;

        return Math.Clamp(s, 0, 1) * Math.Clamp(e.DetectorConfidence, 0, 1);
    }

    public static double SemanticMatch(ActionType spoken, ActionType seen)
    {
        if (spoken == seen) return 1.0;
        if (spoken == ActionType.Unknown || seen == ActionType.Unknown) return 0.5;
        // Compatible families
        bool clicky(ActionType t) => t is ActionType.Click or ActionType.DoubleClick or ActionType.RightClick or ActionType.Tap or ActionType.Select;
        if (clicky(spoken) && clicky(seen)) return 0.7;
        if (spoken == ActionType.Point && clicky(seen)) return 0.5; // "sehen Sie hier" + a click nearby
        return 0.25;
    }
}

/// <summary>
/// Greedy one-to-one resolver: scores every (audio ref, visual event) pair inside the window and
/// accepts pairs in descending score order. Multi-action utterances ("hier … und dann dort") are
/// kept in spoken order: a later ordinal may not bind to an earlier event than its predecessor.
/// </summary>
public sealed class CrossModalResolver
{
    private readonly AssociationScorer _scorer;
    private readonly FusionConfig _cfg;

    public CrossModalResolver(FusionConfig? cfg = null)
    {
        _cfg = cfg ?? new FusionConfig();
        _scorer = new AssociationScorer(_cfg);
    }

    public IReadOnlyList<Association> Resolve(IReadOnlyList<AudioRef> audioRefs, IReadOnlyList<VisualEvent> events, UiElementRegistry ui)
    {
        var candidates = new List<Association>();
        foreach (var a in audioRefs)
            foreach (var e in events)
            {
                if (!_scorer.InWindow(a, e)) continue;
                var target = e.TargetId is null ? null : ui.Elements.FirstOrDefault(x => x.Id == e.TargetId);
                double s = _scorer.Score(a, e, target);
                if (s >= _cfg.MinAssociationScore) candidates.Add(new Association(a, e, s));
            }

        var accepted = new List<Association>();
        var usedAudio = new HashSet<string>();
        var usedEvents = new HashSet<string>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (usedAudio.Contains(c.Audio.Id) || usedEvents.Contains(c.Event.Id)) continue;
            if (ViolatesOrder(c, accepted)) continue;
            accepted.Add(c);
            usedAudio.Add(c.Audio.Id);
            usedEvents.Add(c.Event.Id);
        }
        return accepted.OrderBy(a => a.Event.TimeS).ToList();
    }

    private static bool ViolatesOrder(Association c, List<Association> accepted)
    {
        foreach (var other in accepted)
        {
            // Same utterance (same start) → ordinals must map to events in time order.
            if (Math.Abs(other.Audio.StartS - c.Audio.StartS) > 1e-6 || other.Audio.Ordinal == c.Audio.Ordinal) continue;
            bool cLater = c.Audio.Ordinal > other.Audio.Ordinal;
            if (cLater && c.Event.TimeS <= other.Event.TimeS) return true;
            if (!cLater && c.Event.TimeS >= other.Event.TimeS) return true;
        }
        return false;
    }
}

/// <summary>Maps the evidence hierarchy of the design document to a grounding status.</summary>
public static class GroundingPolicy
{
    public static GroundingStatus Classify(VisualEvent e)
    {
        if (e.Point is null) return GroundingStatus.Unobservable;
        return e.PointSource switch
        {
            EvidenceSource.Telemetry => GroundingStatus.Observed,
            EvidenceSource.CursorTracker => GroundingStatus.Observed,
            EvidenceSource.FingerHomography => GroundingStatus.Observed,
            EvidenceSource.FrameStateChange => GroundingStatus.Observed, // UI changed exactly where cursor was
            EvidenceSource.Sam2Tracker => GroundingStatus.Tracked,
            EvidenceSource.VlmPointing => GroundingStatus.Inferred,
            EvidenceSource.VlmSemantic => GroundingStatus.Inferred,
            _ => GroundingStatus.Inferred,
        };
    }
}

/// <summary>Builds the auditable JSON event graph: every claim is backed by evidence objects.</summary>
public sealed class EventGraphBuilder
{
    public EventGraph Build(VideoInfo video, IReadOnlyList<AudioRef> audioRefs, IReadOnlyList<VisualEvent> events,
                            IReadOnlyList<Association> associations, UiElementRegistry ui, bool includeUnspokenEvents = true)
    {
        var graph = new EventGraph { Video = video };
        var byEvent = associations.GroupBy(a => a.Event.Id).ToDictionary(g => g.Key, g => g.ToList());
        var boundAudio = new HashSet<string>(associations.Select(a => a.Audio.Id));
        int n = 0;

        foreach (var e in events.OrderBy(e => e.TimeS))
        {
            byEvent.TryGetValue(e.Id, out var assocs);
            if (assocs is null && !includeUnspokenEvents) continue;

            var target = e.TargetId is null ? null : ui.Elements.FirstOrDefault(x => x.Id == e.TargetId);
            var status = GroundingPolicy.Classify(e);
            var spatialConf = e.Point is null ? 0 : e.Evidence.Where(x => x.Source <= EvidenceSource.Sam2Tracker).Select(x => x.Confidence).DefaultIfEmpty(0.4).Max();
            var temporalConf = Math.Clamp(0.6 * e.DetectorConfidence + 0.4 * e.StateChangeConfidence, 0, 1);
            double assocConf = assocs?.Max(a => a.Score) ?? 0;
            double overall = assocs is null
                ? Math.Round(0.5 * temporalConf + 0.5 * spatialConf, 3)
                : Math.Round(0.4 * assocConf + 0.3 * temporalConf + 0.3 * spatialConf, 3);

            var ge = new GroundedEvent
            {
                EventId = $"event_{n++:D4}",
                Type = e.Action,
                Temporal = new TemporalOut(Round(e.StartS), Round(e.TimeS), Round(e.EndS <= 0 ? e.TimeS : e.EndS), Round(temporalConf)),
                Spatial = e.Point is null && target is null ? null : new SpatialOut(
                    "pixel_xy_origin_top_left",
                    e.Point?.ToArrayXY(),
                    e.Point is null ? null : [Round((double)e.Point.X / video.WidthPx, 4), Round((double)e.Point.Y / video.HeightPx, 4)],
                    target?.Bbox.ToArray(),
                    target?.Bbox.Normalized(video.WidthPx, video.HeightPx).Select(v => Round(v, 4)).ToArray(),
                    Round(spatialConf)),
                Target = target is null ? null : new TargetOut(target.Id, target.Class, target.Text, Round(target.TextConfidence), Round(target.InteractiveConfidence)),
                AudioReferences = (assocs ?? new()).Select(a => ToAudioOut(a.Audio, "deictic_instruction_for_event", a.Audio.Deictic)).ToList(),
                Evidence = e.Evidence.Select(x => new EvidenceOut(SourceName(x.Source), Round(x.Confidence))).ToList(),
                GroundingStatus = status,
                OverallConfidence = overall,
            };
            graph.Events.Add(ge);
        }

        foreach (var a in audioRefs.Where(a => !boundAudio.Contains(a.Id)))
            graph.UnboundAudioReferences.Add(ToAudioOut(a, "no_visual_event_found", a.Deictic));

        return graph;
    }

    private static AudioReferenceOut ToAudioOut(AudioRef a, string relation, bool deictic) => new(
        a.Id, a.SpeakerId, Round(a.StartS), Round(a.EndS), a.Text, Round(a.AsrConfidence), Round(a.AlignmentConfidence),
        deictic ? relation : relation.Replace("deictic_", "explicit_"));

    public static string SourceName(EvidenceSource s) => s switch
    {
        EvidenceSource.Telemetry => "telemetry",
        EvidenceSource.CursorTracker => "cursor_tracker",
        EvidenceSource.FingerHomography => "finger_homography",
        EvidenceSource.FrameStateChange => "frame_state_change",
        EvidenceSource.Sam2Tracker => "sam2",
        EvidenceSource.OmniParser => "omniparser",
        EvidenceSource.PaddleOcr => "paddleocr",
        EvidenceSource.VlmPointing => "vlm_pointing",
        EvidenceSource.VlmSemantic => "vlm_semantic",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static double Round(double v, int d = 3) => Math.Round(v, d);
}

internal static class PointExt
{
    public static int[] ToArrayXY(this Point2D p) => [p.X, p.Y];
}
