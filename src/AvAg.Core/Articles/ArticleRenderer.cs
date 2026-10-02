using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// Renders a <see cref="WikiArticle"/>:
/// <list type="bullet">
/// <item><see cref="Markdown"/> – the deliverable: CommonMark with YAML front matter (title, tags, status: draft …) and
/// screenshots as relative links (<c>images/step-01.jpg</c>), so it can be imported into most wikis (Wiki.js, GitHub/GitLab,
/// Azure DevOps, Docusaurus, Obsidian …) together with the images folder from <see cref="ArticlePackage"/>.</item>
/// <item><see cref="Html"/> – a self-contained preview for the web page (screenshots embedded).</item>
/// </list>
/// </summary>
public static class ArticleRenderer
{
    private sealed record L(string Problem, string ErrorMessage, string Cause, string AppliesTo, string Solution, string Verification,
                            string Notes, string SupportOnly, string Review, string NotResolved, string Screenshot, string Source);

    private static L Labels(string lang) => lang == "de"
        ? new("Problem", "Fehlermeldung", "Ursache", "Betrifft", "Lösung", "Prüfen, ob es funktioniert", "Hinweise",
              "nur durch den Support", "Automatisch aus einer Support-Aufzeichnung erstellt – bitte vor dem Veröffentlichen prüfen.",
              "Im aufgezeichneten Fall war das Problem am Ende des Gesprächs noch nicht gelöst.", "Bildschirm bei Schritt", "Support-Aufzeichnung")
        : new("Problem", "Error message", "Cause", "Applies to", "Solution", "Check that it works", "Notes",
              "support only", "Generated automatically from a support recording – please review before publishing.",
              "In the recorded case the problem was not yet solved at the end of the call.", "Screen at step", "support recording");

    /// <summary>Wiki Markdown. Images are referenced as <c>{imageFolder}/step-NN.jpg</c> (only for steps that have one).</summary>
    public static string Markdown(WikiArticle a, string imageFolder = "images")
    {
        var l = Labels(a.Language);
        var sb = new StringBuilder();

        // Front matter: understood by most wiki engines and static site generators; ignored as text by the rest.
        sb.AppendLine("---");
        sb.Append("title: ").AppendLine(Yaml(a.Title));
        if (a.Keywords.Count > 0) sb.Append("tags: [").Append(string.Join(", ", a.Keywords.Select(Yaml))).AppendLine("]");
        sb.Append("language: ").AppendLine(a.Language);
        sb.AppendLine("status: draft");
        if (a.Resolved is { } resolved) sb.Append("resolved: ").AppendLine(resolved ? "true" : "false");
        sb.Append("source: ").AppendLine(Yaml($"{l.Source} ({a.Method}{(a.Private ? ", private" : "")})"));
        sb.Append("generated: ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.AppendLine("---").AppendLine();

        sb.Append("# ").AppendLine(a.Title).AppendLine();
        sb.Append("> ").AppendLine(l.Review).AppendLine();
        if (a.Resolved == false) sb.Append("> **").Append(l.NotResolved).AppendLine("**").AppendLine();

        if (a.Problem is not null || a.ErrorMessages.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Problem).AppendLine();
            if (a.Problem is not null) sb.AppendLine(a.Problem).AppendLine();
            foreach (var e in a.ErrorMessages) sb.Append("**").Append(l.ErrorMessage).Append(":** `").Append(e.Replace("`", "'")).AppendLine("`").AppendLine();
        }
        if (a.Cause is not null) sb.Append("## ").AppendLine(l.Cause).AppendLine().AppendLine(a.Cause).AppendLine();
        if (a.AppliesTo is not null) sb.Append("**").Append(l.AppliesTo).Append(":** ").AppendLine(a.AppliesTo).AppendLine();

        if (a.Steps.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Solution).AppendLine();
            bool sections = HasSections(a);
            string? section = null;
            foreach (var s in a.Steps)
            {
                if (sections && s.Section != section) { section = s.Section; if (section is not null) sb.Append("### ").AppendLine(section).AppendLine(); }
                sb.Append(sections ? "#### " : "### ").Append(s.Number).Append(". ").Append(s.Title ?? FirstWords(s.Instruction));
                if (s.Actor == StepActor.Support) sb.Append(" *(").Append(l.SupportOnly).Append(")*");
                sb.AppendLine().AppendLine();
                sb.AppendLine(KeysMarkdown(s.Instruction)).AppendLine();
                if (s.Details is not null) sb.AppendLine(KeysMarkdown(s.Details)).AppendLine();
                if (s.ScreenshotFile is { } file) sb.Append("![").Append(l.Screenshot).Append(' ').Append(s.Number).Append("](").Append(imageFolder).Append('/').Append(file).AppendLine(")").AppendLine();
                // For reviewers: where to look in the recording. Comments are not shown in rendered wikis.
                sb.Append("<!-- video ").Append(Ts(s.TimeS)).AppendLine(" -->").AppendLine();
            }
        }
        if (a.Verification is not null) sb.Append("## ").AppendLine(l.Verification).AppendLine().AppendLine(KeysMarkdown(a.Verification)).AppendLine();
        if (a.Notes.Count > 0)
        {
            sb.Append("## ").AppendLine(l.Notes).AppendLine();
            foreach (var n in a.Notes) sb.Append("- ").AppendLine(KeysMarkdown(n));
            sb.AppendLine();
        }
        return sb.ToString().Replace("\r\n", "\n").TrimEnd() + "\n";   // LF only, whatever the OS
    }

    /// <summary>Self-contained HTML preview of the article (screenshots embedded as data URIs).</summary>
    public static string Html(WikiArticle a)
    {
        var l = Labels(a.Language);
        // Only the HTML-significant characters: WebUtility.HtmlEncode would also turn every umlaut into an entity.
        static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        var sb = new StringBuilder();
        sb.Append($"""
            <!doctype html>
            <html lang="{a.Language}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(a.Title)}</title>
            <style>
              :root {"{"} --bg:#fff; --text:#1d2330; --muted:#5b6475; --line:#d9dde5; --accent:#1f7a63; --card:#f6f7f9; --warn:#a35b00; {"}"}
              @media (prefers-color-scheme: dark) {"{"} :root:not([data-theme=light]) {"{"} --bg:#1c2230; --text:#eceff4; --muted:#98a2b8; --line:#3a4559; --accent:#45c9a5; --card:#262e3d; --warn:#f0b44c; {"}"} {"}"}
              body {"{"} margin:0; background:var(--bg); color:var(--text); font:16px/1.55 "Segoe UI", system-ui, sans-serif; {"}"}
              main {"{"} max-width:860px; margin:0 auto; padding:2rem 1rem 4rem; {"}"}
              h1 {"{"} font-size:1.7rem; margin:0 0 .5rem; line-height:1.25; {"}"}
              h2 {"{"} font-size:1.2rem; margin:2rem 0 .75rem; padding-bottom:.3rem; border-bottom:1px solid var(--line); {"}"}
              h3 {"{"} font-size:1.05rem; margin:1.5rem 0 .35rem; {"}"}
              h3.section {"{"} color:var(--accent); {"}"}
              .meta {"{"} color:var(--muted); font-size:.9rem; margin:0 0 1rem; {"}"}
              .review {"{"} border-left:3px solid var(--warn); padding:.4rem .8rem; color:var(--muted); background:var(--card); border-radius:0 6px 6px 0; {"}"}
              .tags span {"{"} display:inline-block; font-size:.8rem; border:1px solid var(--line); border-radius:999px; padding:.05rem .55rem; margin:0 .3rem .3rem 0; color:var(--muted); {"}"}
              code {"{"} background:var(--card); border:1px solid var(--line); border-radius:4px; padding:.05rem .35rem; {"}"}
              kbd {"{"} font:600 .85em "Segoe UI", system-ui, sans-serif; background:var(--card); border:1px solid var(--line); border-bottom-width:2px; border-radius:4px; padding:.05rem .4rem; white-space:nowrap; {"}"}
              .support {"{"} font-size:.8rem; font-weight:500; color:var(--warn); margin-left:.4rem; {"}"}
              .details {"{"} color:var(--muted); {"}"}
              figure {"{"} margin:.75rem 0 0; {"}"}
              figure img {"{"} max-width:100%; height:auto; border:1px solid var(--line); border-radius:6px; display:block; {"}"}
              .step {"{"} padding-bottom:1rem; border-bottom:1px solid var(--line); break-inside:avoid; {"}"}
              @media print {"{"} body {"{"} background:#fff; color:#000; {"}"} main {"{"} padding:0; {"}"} {"}"}
            </style>
            </head>
            <body><main>
            <h1>{E(a.Title)}</h1>
            <p class="meta">{E(l.Source)} · {E(a.Method)}{(a.Private ? " · private" : "")}{(a.Redactions > 0 ? $" · {a.Redactions} × [{(a.Language == "de" ? "entfernt" : "removed")}]" : "")}</p>
            {(a.Keywords.Count > 0 ? "<p class=\"tags\">" + string.Concat(a.Keywords.Select(k => $"<span>{E(k)}</span>")) + "</p>" : "")}
            <p class="review">{E(l.Review)}</p>

            """);
        if (a.Resolved == false) sb.Append($"<p class=\"review\"><strong>{E(l.NotResolved)}</strong></p>\n");
        if (a.Problem is not null || a.ErrorMessages.Count > 0)
        {
            sb.Append($"<h2>{E(l.Problem)}</h2>\n");
            if (a.Problem is not null) sb.Append($"<p>{E(a.Problem)}</p>\n");
            foreach (var e in a.ErrorMessages) sb.Append($"<p><strong>{E(l.ErrorMessage)}:</strong> <code>{E(e)}</code></p>\n");
        }
        if (a.Cause is not null) sb.Append($"<h2>{E(l.Cause)}</h2>\n<p>{E(a.Cause)}</p>\n");
        if (a.AppliesTo is not null) sb.Append($"<p><strong>{E(l.AppliesTo)}:</strong> {E(a.AppliesTo)}</p>\n");
        if (a.Steps.Count > 0)
        {
            sb.Append($"<h2>{E(l.Solution)}</h2>\n");
            bool sections = HasSections(a);
            string? section = null;
            foreach (var s in a.Steps)
            {
                if (sections && s.Section != section) { section = s.Section; if (section is not null) sb.Append($"<h3 class=\"section\">{E(section)}</h3>\n"); }
                sb.Append("<div class=\"step\">");
                sb.Append($"<h3>{s.Number}. {E(s.Title ?? FirstWords(s.Instruction))}{(s.Actor == StepActor.Support ? $"<span class=\"support\">({E(l.SupportOnly)})</span>" : "")}</h3>");
                sb.Append($"<p>{KeysHtml(E(s.Instruction))}</p>");
                if (s.Details is not null) sb.Append($"<p class=\"details\">{KeysHtml(E(s.Details))}</p>");
                if (s.ScreenshotJpeg is { Length: > 0 } img)
                    sb.Append($"<figure><img alt=\"{E(l.Screenshot)} {s.Number}\" width=\"{s.ScreenshotWidth}\" height=\"{s.ScreenshotHeight}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(img)}\"></figure>");
                sb.Append("</div>\n");
            }
        }
        if (a.Verification is not null) sb.Append($"<h2>{E(l.Verification)}</h2>\n<p>{KeysHtml(E(a.Verification))}</p>\n");
        if (a.Notes.Count > 0) sb.Append($"<h2>{E(l.Notes)}</h2>\n<ul>").Append(string.Concat(a.Notes.Select(n => $"<li>{KeysHtml(E(n))}</li>"))).Append("</ul>\n");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }

    /// <summary>Sections are only shown when the solution has at least two parts (e.g. two alternatives).</summary>
    public static bool HasSections(WikiArticle a) => a.Steps.Select(s => s.Section).Where(s => s is not null).Distinct().Count() >= 2;

    // Key combinations such as "Windows key + Shift + R" or "Strg + C".
    private const string NamedKey =
        @"(?:Ctrl|Strg|Control|AltGr|Alt|Shift|Umschalt|Cmd|Command|Win(?:dows)?(?:[- ]?(?:key|Taste|logo key))?|Enter|Eingabe(?:taste)?|Return|Tab|Esc|Escape|Entf|Del|Delete|Space|Leertaste|Backspace|Pos1|F\d{1,2})";
    private const string KeyToken = $@"(?:{NamedKey}|[A-Z0-9])";
    /// <summary>Starts with a named key so that "1 + 2" or "A + B" in ordinary text is left alone.</summary>
    public static readonly Regex KeyComboRx = new($@"\b{NamedKey}(?:\s*\+\s*{KeyToken})+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IEnumerable<string> Keys(string combo) => Regex.Split(combo, @"\s*\+\s*");
    /// <summary>Each key as a code span: <c>`Strg` + `C`</c> – plain Markdown that every wiki renders.</summary>
    public static string KeysMarkdown(string text) => KeyComboRx.Replace(text, m => string.Join(" + ", Keys(m.Value).Select(k => $"`{k}`")));
    private static string KeysHtml(string escaped) => KeyComboRx.Replace(escaped, m => string.Join(" + ", Keys(m.Value).Select(k => $"<kbd>{k}</kbd>")));

    public static string FirstWords(string s, int n = 8)
    {
        var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var t = string.Join(" ", w.Take(n)).TrimEnd(',', '.', ';', ':');
        return w.Length > n ? t + " …" : t;
    }

    public static string Ts(double s) { var t = TimeSpan.FromSeconds(s); return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; }

    /// <summary>A YAML scalar in double quotes (safe for colons, #, quotes).</summary>
    private static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
}
