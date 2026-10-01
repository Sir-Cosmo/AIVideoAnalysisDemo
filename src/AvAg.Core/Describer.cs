using System.Globalization;
using System.Text;

namespace AvAg.Core;

/// <summary>
/// Separates perception from claim: the generator never asserts a click target or coordinate
/// unless the event graph carries that grounding. Inferred/unobservable events are hedged.
/// Produces both the per-event <c>description_de/en</c> fields and a timeline transcript.
/// </summary>
public sealed class Describer
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public void Annotate(EventGraph graph)
    {
        foreach (var e in graph.Events)
        {
            e.DescriptionDe = DescribeEvent(e, graph.Video, "de");
            e.DescriptionEn = DescribeEvent(e, graph.Video, "en");
        }
    }

    public string Timeline(EventGraph graph, string lang = "de")
    {
        var sb = new StringBuilder();
        var items = new List<(double t, string line)>();
        foreach (var e in graph.Events)
        {
            foreach (var a in e.AudioReferences)
                items.Add((a.StartS, Speech(a, lang)));
            items.Add((e.Temporal.PeakS, $"{Ts(e.Temporal.PeakS)}: {(lang == "de" ? e.DescriptionDe : e.DescriptionEn)}"));
        }
        foreach (var a in graph.UnboundAudioReferences)
            items.Add((a.StartS, Speech(a, lang) + (lang == "de"
                ? " (Kein zugeordnetes visuelles Ereignis beobachtet.)"
                : " (No associated visual event observed.)")));
        foreach (var (_, line) in items.OrderBy(i => i.t)) sb.AppendLine(line);
        return sb.ToString();
    }

    private static string Speech(AudioReferenceOut a, string lang)
    {
        string spk = a.SpeakerId ?? (lang == "de" ? "Sprecher" : "Speaker");
        return lang == "de"
            ? $"{Ts(a.StartS)}–{Ts(a.EndS)}: {spk} sagt: „{a.Transcript}“"
            : $"{Ts(a.StartS)}–{Ts(a.EndS)}: {spk} says: \"{a.Transcript}\"";
    }

    public static string DescribeEvent(GroundedEvent e, VideoInfo v, string lang)
    {
        bool de = lang == "de";
        var c = de ? De : En;
        string actor = de ? ActorDe(e) : ActorEn(e);
        string verb = de ? VerbDe(e.Type) : VerbEn(e.Type);
        string hedge = e.GroundingStatus switch
        {
            GroundingStatus.Observed => "",
            GroundingStatus.Tracked => de ? " (per Tracking verfolgt)" : " (position tracked)",
            GroundingStatus.Inferred => de ? " (Position geschätzt, nicht direkt beobachtet)" : " (position inferred, not directly observed)",
            _ => "",
        };

        var sb = new StringBuilder();
        if (e.GroundingStatus == GroundingStatus.Unobservable)
        {
            sb.Append(de
                ? $"{actor} {verb}; die genaue Position ist im Video nicht beobachtbar"
                : $"{actor} {verb}; the exact position is not observable in the video");
            if (e.Target?.Text is not null)
                sb.Append(de ? $" – vermutlich gemeint: „{e.Target.Text}“" : $" – probably intended: \"{e.Target.Text}\"");
            sb.Append('.');
            return sb.ToString();
        }

        sb.Append(actor).Append(' ').Append(verb);
        if (e.Spatial?.PointXyPx is { } p)
            sb.Append(de ? $" bei x={p[0]}, y={p[1]}" : $" at x={p[0]}, y={p[1]}");
        if (e.Target is not null)
        {
            string cls = de ? ClassDe(e.Target.Class) : ClassEn(e.Target.Class);
            sb.Append(e.Target.Text is not null
                ? (de ? $" auf {cls} „{e.Target.Text}“" : $" on the {cls} \"{e.Target.Text}\"")
                : (de ? $" auf {cls} {e.Target.ObjectId}" : $" on {cls} {e.Target.ObjectId}"));
            if (e.Spatial?.BboxXyxyPx is { } b)
                sb.Append(de ? $" {Region(b, v, de)}" : $" {Region(b, v, de)}");
        }
        sb.Append(hedge).Append('.');

        if (e.Spatial?.BboxXyxyPx is { } bb)
            sb.Append(de
                ? $" Das Element belegt ungefähr [{bb[0]}, {bb[1]}]–[{bb[2]}, {bb[3]}] px."
                : $" The element occupies approximately [{bb[0]}, {bb[1]}]–[{bb[2]}, {bb[3]}] px.");
        if (e.Evidence.Any(x => x.Source == "frame_state_change" && x.Confidence >= 0.5))
            sb.Append(de ? " Unmittelbar anschließend ändert sich die Ansicht." : " The view changes immediately afterwards.");
        if (e.AudioReferences.Count > 0)
            sb.Append(de
                ? $" Die Zuordnung zur Audioanweisung wird mit {e.OverallConfidence.ToString("0.00", c)} Gesamtvertrauen bewertet."
                : $" The association with the spoken instruction is rated at {e.OverallConfidence.ToString("0.00", c)} overall confidence.");
        return sb.ToString();
    }

    private static string Region(int[] b, VideoInfo v, bool de)
    {
        double cx = (b[0] + b[2]) / 2.0 / v.WidthPx, cy = (b[1] + b[3]) / 2.0 / v.HeightPx;
        string h = cx < 0.33 ? (de ? "linken" : "left") : cx > 0.66 ? (de ? "rechten" : "right") : (de ? "mittleren" : "center");
        string vv = cy < 0.33 ? (de ? "oberen" : "upper") : cy > 0.66 ? (de ? "unteren" : "lower") : (de ? "mittleren" : "middle");
        return de ? $"im {h} {vv} Bereich des Fensters" : $"in the {vv} {h} area of the window";
    }

    private static string ActorDe(GroundedEvent e) =>
        e.Evidence.Any(x => x.Source == "cursor_tracker" || x.Source == "telemetry") ? "Der sichtbare Mauszeiger"
        : e.Evidence.Any(x => x.Source == "finger_homography") ? "Der Finger"
        : "Die Aktion";

    private static string ActorEn(GroundedEvent e) =>
        e.Evidence.Any(x => x.Source == "cursor_tracker" || x.Source == "telemetry") ? "The visible mouse cursor"
        : e.Evidence.Any(x => x.Source == "finger_homography") ? "The finger"
        : "The action";

    private static string VerbDe(ActionType t) => t switch
    {
        ActionType.Click => "klickt", ActionType.DoubleClick => "doppelklickt", ActionType.RightClick => "klickt mit der rechten Maustaste",
        ActionType.Drag => "zieht", ActionType.Tap => "tippt", ActionType.Type => "gibt Text ein", ActionType.Scroll => "scrollt",
        ActionType.Select => "wählt aus", ActionType.Point => "zeigt", _ => "wird ausgeführt",
    };

    private static string VerbEn(ActionType t) => t switch
    {
        ActionType.Click => "clicks", ActionType.DoubleClick => "double-clicks", ActionType.RightClick => "right-clicks",
        ActionType.Drag => "drags", ActionType.Tap => "taps", ActionType.Type => "types", ActionType.Scroll => "scrolls",
        ActionType.Select => "selects", ActionType.Point => "points", _ => "is performed",
    };

    private static string ClassDe(string c) => c switch
    {
        "button" => "die Schaltfläche", "icon" => "das Symbol", "text" => "den Text", "input" => "das Eingabefeld",
        "link" => "den Link", "menu" => "das Menü", _ => "das Element",
    };

    private static string ClassEn(string c) => c switch
    {
        "button" => "button", "icon" => "icon", "text" => "text", "input" => "input field", "link" => "link", "menu" => "menu", _ => "element",
    };

    public static string Ts(double s)
    {
        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }
}
