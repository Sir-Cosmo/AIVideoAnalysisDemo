using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AvAg.Core;

/// <summary>
/// The files that make up one wiki article: <c>article.md</c> plus <c>images/step-NN.jpg</c> for every step that has a
/// screenshot. Written as a folder (CLI) or a zip (web download) – the layout most wikis import directly.
/// A private article has no images, only <c>article.md</c>.
/// </summary>
public static class ArticlePackage
{
    public const string MarkdownFile = "article.md";
    public const string ImageFolder = "images";

    public static IEnumerable<(string Path, byte[] Content)> Files(WikiArticle a)
    {
        yield return (MarkdownFile, new UTF8Encoding(false).GetBytes(ArticleRenderer.Markdown(a, ImageFolder)));
        foreach (var s in a.Steps)
            if (s.ScreenshotFile is { } file) yield return ($"{ImageFolder}/{file}", s.ScreenshotJpeg!);
    }

    public static byte[] Zip(WikiArticle a)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in Files(a))
            {
                using var s = zip.CreateEntry(path, path.EndsWith(".jpg") ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    /// <summary>Writes the package into <paramref name="folder"/> (created if needed); returns the Markdown path.
    /// Screenshots of an earlier article in the same folder are deleted first, so none of them can be published by mistake.</summary>
    public static string WriteTo(WikiArticle a, string folder)
    {
        var images = Path.Combine(folder, ImageFolder);
        if (Directory.Exists(images))
            foreach (var old in Directory.EnumerateFiles(images, "step-*.jpg")) File.Delete(old);
        foreach (var (path, content) in Files(a))
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }
        return Path.Combine(folder, MarkdownFile);
    }

    /// <summary>URL- and file-name-safe slug of the title, e.g. "rechnung-laesst-sich-nicht-drucken".</summary>
    public static string Slug(string title)
    {
        var s = title.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        s = Regex.Replace(s.Normalize(NormalizationForm.FormD), @"\p{Mn}", "");
        s = Regex.Replace(s, @"[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "article" : s[..Math.Min(s.Length, 60)].TrimEnd('-');
    }
}
