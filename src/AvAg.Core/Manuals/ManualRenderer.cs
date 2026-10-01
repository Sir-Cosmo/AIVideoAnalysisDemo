using System.Text;
using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>Markdown and self-contained HTML (screenshots embedded) for a <see cref="Manual"/>.</summary>
public static class ManualRenderer
{
    private sealed record L(string Overview, string Before, string Steps, string Tips, string Shown, string Generated, string Rule, string Llm, string Private);
    private static L Labels(string lang) => lang == "de"
        ? new("Überblick", "Voraussetzungen", "Schritt für Schritt", "Hinweise", "Im Video bei", "Automatisch aus dem Video erstellt", "regelbasiert aus der Tonspur", "mit Sprachmodell zusammengefasst", "privates Video, nur Text")
        : new("Overview", "Before you start", "Step by step", "Tips", "Shown in the video at", "Generated automatically from the video", "rule-based from the narration", "summarised by a language model", "private video, text only");

    public static string Markdown(Manual m, Func<ManualStep, string?>? imagePath = null)
    {
        var l = Labels(m.Language);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(m.Title).AppendLine();
        sb.Append('_').Append(l.Generated).Append(m.VideoName is null ? "" : $" ({m.VideoName})").Append(" · ")
          .Append(m.Method.StartsWith("llm") ? l.Llm : l.Rule).Append(m.Private ? " · " + l.Private : "").AppendLine("_").AppendLine();
        if (m.Summary is not null) sb.Append("## ").AppendLine(l.Overview).AppendLine().AppendLine(m.Summary).AppendLine();
        if (m.Prerequisites.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Before).AppendLine();
            foreach (var p in m.Prerequisites) sb.Append("- ").AppendLine(p);
            sb.AppendLine();
        }
        sb.Append("## ").AppendLine(l.Steps).AppendLine();
        bool sections = HasSections(m);
        string? section = null;
        foreach (var s in m.Steps)
        {
            if (sections && s.Section != section) { section = s.Section; if (section is not null) sb.Append("### ").AppendLine(section).AppendLine(); }
            sb.Append(sections ? "#### " : "### ").Append(s.Number).Append(". ").AppendLine(s.Title ?? FirstWords(s.Instruction)).AppendLine();
            sb.AppendLine(s.Instruction).AppendLine();
            if (s.Details is not null) sb.AppendLine(s.Details).AppendLine();
            if (imagePath?.Invoke(s) is { } img) sb.Append("![").Append(s.Number).Append("](").Append(img).AppendLine(")").AppendLine();
            sb.Append('_').Append(l.Shown).Append(' ').Append(Ts(s.TimeS)).AppendLine("_").AppendLine();
        }
        if (m.Tips.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Tips).AppendLine();
            foreach (var t in m.Tips) sb.Append("- ").AppendLine(t);
        }
        return sb.ToString();
    }

    public static string Html(Manual m)
    {
        var l = Labels(m.Language);
        // Only the HTML-significant characters: WebUtility.HtmlEncode would also turn every umlaut into an entity.
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="{m.Language}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(m.Title)}</title>
            <style>
              :root {"{"} --bg:#fff; --text:#1d2330; --muted:#5b6475; --line:#d9dde5; --accent:#1f7a63; --card:#f6f7f9; {"}"}
              @media (prefers-color-scheme: dark) {"{"} :root:not([data-theme=light]) {"{"} --bg:#1c2230; --text:#eceff4; --muted:#98a2b8; --line:#3a4559; --accent:#45c9a5; --card:#262e3d; {"}"} {"}"}
              body {"{"} margin:0; background:var(--bg); color:var(--text); font:16px/1.55 "Segoe UI", system-ui, sans-serif; {"}"}
              main {"{"} max-width:860px; margin:0 auto; padding:2rem 1rem 4rem; {"}"}
              h1 {"{"} font-size:1.8rem; margin:0 0 .25rem; line-height:1.2; {"}"}
              .meta {"{"} color:var(--muted); font-size:.9rem; margin:0 0 2rem; {"}"}
              h2 {"{"} font-size:1.2rem; margin:2rem 0 .75rem; padding-bottom:.3rem; border-bottom:1px solid var(--line); {"}"}
              ol.steps {"{"} list-style:none; padding:0; margin:0; counter-reset:step; {"}"}
              ol.steps > li {"{"} display:grid; grid-template-columns:2.25rem 1fr; gap:.25rem 1rem; padding:1.1rem 0; border-bottom:1px solid var(--line); break-inside:avoid; {"}"}
              .num {"{"} width:2rem; height:2rem; border-radius:50%; background:var(--accent); color:var(--bg); display:grid; place-items:center; font-weight:600; {"}"}
              .step h3 {"{"} margin:.15rem 0 .35rem; font-size:1.05rem; {"}"}
              .step p {"{"} margin:.25rem 0; {"}"}
              .details {"{"} color:var(--muted); {"}"}
              figure {"{"} margin:.75rem 0 0; {"}"}
              figure img {"{"} max-width:100%; height:auto; border:1px solid var(--line); border-radius:6px; display:block; {"}"}
              figcaption, .time {"{"} color:var(--muted); font-size:.85rem; margin-top:.3rem; {"}"}
              .summary {"{"} background:var(--card); border-radius:6px; padding:.9rem 1rem; {"}"}
              h3.section {"{"} font-size:1.05rem; color:var(--accent); margin:1.75rem 0 0; {"}"}
              kbd {"{"} font:600 .85em "Segoe UI", system-ui, sans-serif; background:var(--card); border:1px solid var(--line); border-bottom-width:2px; border-radius:4px; padding:.05rem .4rem; white-space:nowrap; {"}"}
              @media print {"{"} body {"{"} background:#fff; color:#000; {"}"} main {"{"} padding:0; {"}"} {"}"}
            </style>
            </head>
            <body><main>
            <h1>{E(m.Title)}</h1>
            <p class="meta">{E(l.Generated)}{(m.VideoName is null ? "" : $" ({E(m.VideoName)})")} · {E(m.Method.StartsWith("llm") ? l.Llm : l.Rule)}{(m.Private ? " · " + E(l.Private) : "")}</p>

            """);
        if (m.Summary is not null) sb.Append($"<h2>{E(l.Overview)}</h2>\n<p class=\"summary\">{E(m.Summary)}</p>\n");
        if (m.Prerequisites.Count > 0)
            sb.Append($"<h2>{E(l.Before)}</h2>\n<ul>").Append(string.Concat(m.Prerequisites.Select(p => $"<li>{E(p)}</li>"))).Append("</ul>\n");
        sb.Append($"<h2>{E(l.Steps)}</h2>\n<ol class=\"steps\">\n");
        bool sections = HasSections(m);
        string? section = null;
        foreach (var s in m.Steps)
        {
            // A new section closes the list and continues the numbering after the heading.
            if (sections && s.Section != section)
            {
                section = s.Section;
                if (section is not null) sb.Append($"</ol>\n<h3 class=\"section\">{E(section)}</h3>\n<ol class=\"steps\" start=\"{s.Number}\">\n");
            }
            sb.Append($"<li><span class=\"num\">{s.Number}</span><div class=\"step\">");
            sb.Append($"<h3>{E(s.Title ?? FirstWords(s.Instruction))}</h3><p>{Keys(E(s.Instruction))}</p>");
            if (s.Details is not null) sb.Append($"<p class=\"details\">{Keys(E(s.Details))}</p>");
            if (s.ScreenshotJpeg is { Length: > 0 } img)
                sb.Append($"<figure><img alt=\"{E(m.Language == "de" ? $"Bildschirm bei Schritt {s.Number}" : $"Screen at step {s.Number}")}\" width=\"{s.ScreenshotWidth}\" height=\"{s.ScreenshotHeight}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(img)}\"><figcaption>{E(l.Shown)} {Ts(s.ScreenshotS ?? s.TimeS)}</figcaption></figure>");
            else sb.Append($"<p class=\"time\">{E(l.Shown)} {Ts(s.TimeS)}</p>");
            sb.Append("</div></li>\n");
        }
        sb.Append("</ol>\n");
        if (m.Tips.Count > 0)
            sb.Append($"<h2>{E(l.Tips)}</h2>\n<ul>").Append(string.Concat(m.Tips.Select(t => $"<li>{E(t)}</li>"))).Append("</ul>\n");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    /// <summary>Sections are only shown when the model split the video into at least two parts (e.g. two methods).</summary>
    public static bool HasSections(Manual m) => m.Steps.Select(s => s.Section).Where(s => s is not null).Distinct().Count() >= 2;

    // Key combinations such as "Windows key + Shift + R" or "Strg + C": each key becomes a <kbd> (HTML) or bold run (Word).
    private const string NamedKey =
        @"(?:Ctrl|Strg|Control|AltGr|Alt|Shift|Umschalt|Cmd|Command|Win(?:dows)?(?:[- ]?(?:key|Taste|logo key))?|Enter|Eingabe(?:taste)?|Return|Tab|Esc|Escape|Entf|Del|Delete|Space|Leertaste|Backspace|Pos1|F\d{1,2})";
    private const string KeyToken = $@"(?:{NamedKey}|[A-Z0-9])";
    // Starts with a named key so that "1 + 2" or "A + B" in ordinary text is left alone.
    public static readonly Regex KeyComboRx = new($@"\b{NamedKey}(?:\s*\+\s*{KeyToken})+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Keys(string escaped) =>
        KeyComboRx.Replace(escaped, m => string.Join(" + ", Regex.Split(m.Value, @"\s*\+\s*").Select(k => $"<kbd>{k}</kbd>")));

    public static string FirstWords(string s, int n = 8)
    {
        var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var t = string.Join(" ", w.Take(n)).TrimEnd(',', '.', ';', ':');
        return w.Length > n ? t + " …" : t;
    }

    public static string Ts(double s) { var t = TimeSpan.FromSeconds(s); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }
}
