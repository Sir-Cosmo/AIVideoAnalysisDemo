using System.IO.Compression;
using System.Security;
using System.Text;

namespace AvAg.Core;

/// <summary>
/// Writes a <see cref="Manual"/> as a Word document (.docx) with embedded screenshots. Plain WordprocessingML via
/// <see cref="ZipArchive"/> – no Open XML SDK dependency. Uses Word's built-in Title/Heading styles so the document
/// gets a navigation pane and can be restyled with the user's template.
/// </summary>
public static class ManualDocx
{
    private const long EmuPerPx = 9525;              // 96 dpi
    private const long MaxWidthEmu = 5943600;        // 6.5 in = A4/Letter text width with default margins

    public static byte[] Write(Manual m)
    {
        bool de = m.Language == "de";
        var body = new StringBuilder();
        var images = new List<(string Name, byte[] Data)>();

        P(body, m.Title, "Title");
        P(body, (de ? "Automatisch aus dem Video erstellt" : "Generated automatically from the video") + (m.VideoName is null ? "" : $" ({m.VideoName})")
                 + (m.Private ? (de ? " · privates Video, nur Text" : " · private video, text only") : ""), "Subtitle");
        if (m.Summary is not null) { P(body, de ? "Überblick" : "Overview", "Heading1"); P(body, m.Summary); }
        if (m.Prerequisites.Count > 0)
        {
            P(body, de ? "Voraussetzungen" : "Before you start", "Heading1");
            foreach (var p in m.Prerequisites) P(body, "• " + p);
        }
        P(body, de ? "Schritt für Schritt" : "Step by step", "Heading1");
        bool sections = ManualRenderer.HasSections(m);
        string? section = null;
        foreach (var s in m.Steps)
        {
            if (sections && s.Section != section) { section = s.Section; if (section is not null) P(body, section, "Heading2"); }
            P(body, $"{s.Number}. {s.Title ?? ManualRenderer.FirstWords(s.Instruction)}", sections ? "Heading3" : "Heading2");
            Rich(body, s.Instruction);
            if (s.Details is not null) Rich(body, s.Details);
            if (s.ScreenshotJpeg is { Length: > 0 } jpg && s.ScreenshotWidth > 0 && s.ScreenshotHeight > 0)
            {
                images.Add(($"image{images.Count + 1}.jpeg", jpg));
                Image(body, images.Count, s.ScreenshotWidth, s.ScreenshotHeight, de ? $"Schritt {s.Number}" : $"Step {s.Number}");
            }
            P(body, (de ? "Im Video bei " : "Shown in the video at ") + ManualRenderer.Ts(s.ScreenshotS ?? s.TimeS), "Caption");
        }
        if (m.Tips.Count > 0)
        {
            P(body, de ? "Hinweise" : "Tips", "Heading1");
            foreach (var t in m.Tips) P(body, "• " + t);
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Default Extension="jpeg" ContentType="image/jpeg"/>
                <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            var rels = new StringBuilder("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                """);
            for (int i = 0; i < images.Count; i++)
                rels.Append($"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{images[i].Name}\"/>\n");
            rels.Append("</Relationships>");
            Entry(zip, "word/_rels/document.xml.rels", rels.ToString());
            Entry(zip, "word/styles.xml", Styles);
            Entry(zip, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                  xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                  xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                  xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <w:body>
                {body}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1417" w:right="1417" w:bottom="1134" w:left="1417" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr>
                </w:body>
                </w:document>
                """);
            foreach (var (name, data) in images)
            {
                using var s = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression).Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    private static void P(StringBuilder sb, string text, string? style = null)
    {
        sb.Append("<w:p>");
        if (style is not null) sb.Append($"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>");
        sb.Append($"<w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>\n");
    }

    /// <summary>Paragraph with key combinations ("Strg + C") set in bold.</summary>
    private static void Rich(StringBuilder sb, string text)
    {
        sb.Append("<w:p>");
        int at = 0;
        foreach (System.Text.RegularExpressions.Match k in ManualRenderer.KeyComboRx.Matches(text))
        {
            Run(text[at..k.Index], false);
            Run(k.Value, true);
            at = k.Index + k.Length;
        }
        Run(text[at..], false);
        sb.Append("</w:p>\n");

        void Run(string t, bool bold)
        {
            if (t.Length == 0) return;
            sb.Append("<w:r>").Append(bold ? "<w:rPr><w:b/></w:rPr>" : "").Append($"<w:t xml:space=\"preserve\">{SecurityElement.Escape(t)}</w:t></w:r>");
        }
    }

    private static void Image(StringBuilder sb, int n, int wPx, int hPx, string name)
    {
        long cx = wPx * EmuPerPx, cy = hPx * EmuPerPx;
        if (cx > MaxWidthEmu) { cy = cy * MaxWidthEmu / cx; cx = MaxWidthEmu; }
        sb.Append($"""
            <w:p><w:pPr><w:keepNext/></w:pPr><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0">
            <wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{n}" name="{SecurityElement.Escape(name)}"/>
            <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
            <pic:nvPicPr><pic:cNvPr id="{n}" name="image{n}.jpeg"/><pic:cNvPicPr/></pic:nvPicPr>
            <pic:blipFill><a:blip r:embed="rIdImg{n}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
            <a:ln w="6350"><a:solidFill><a:srgbClr val="BFC5D0"/></a:solidFill></a:ln></pic:spPr>
            </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>

            """);
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:lang w:val="de-CH"/></w:rPr></w:rPrDefault>
        <w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
        <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
          <w:pPr><w:spacing w:after="60"/></w:pPr><w:rPr><w:sz w:val="48"/><w:b/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Subtitle"><w:name w:val="Subtitle"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
          <w:pPr><w:spacing w:after="240"/></w:pPr><w:rPr><w:color w:val="5B6475"/><w:sz w:val="20"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
          <w:pPr><w:keepNext/><w:spacing w:before="360" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="1F7A63"/><w:sz w:val="32"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
          <w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:sz w:val="26"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>
          <w:pPr><w:keepNext/><w:spacing w:before="200" w:after="60"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:sz w:val="24"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/><w:qFormat/>
          <w:rPr><w:i/><w:color w:val="5B6475"/><w:sz w:val="18"/></w:rPr></w:style>
        </w:styles>
        """;
}
