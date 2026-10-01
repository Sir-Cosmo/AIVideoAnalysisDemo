using System.Globalization;
using System.Text.RegularExpressions;

namespace AvAg.Web.Endpoints;

/// <summary>Small helpers for form fields, uploads and file names.</summary>
internal static class Http
{
    public static string? Opt(IFormCollection f, string key) => string.IsNullOrWhiteSpace(f[key]) ? null : f[key].ToString().Trim();

    public static double Double(IFormCollection f, string key, double fallback) =>
        double.TryParse(f[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Known video/image extensions are kept; everything else is stored as .bin.</summary>
    public static string SafeExtension(string name)
    {
        var e = Path.GetExtension(name).ToLowerInvariant();
        return e is ".mp4" or ".mkv" or ".mov" or ".webm" or ".avi" or ".png" or ".jpg" ? e : ".bin";
    }

    public static string VideoContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4",
    };

    public static async Task SaveAsync(IFormFile f, string path)
    {
        await using var fs = File.Create(path);
        await f.CopyToAsync(fs);
    }

    /// <summary>A WhisperX transcript starts with '{' (after an optional UTF-8 BOM / whitespace); catches videos dropped into the wrong field.</summary>
    public static async Task<bool> LooksLikeJsonAsync(IFormFile f)
    {
        await using var s = f.OpenReadStream();
        var buf = new byte[64];
        int n = await s.ReadAsync(buf);
        int i = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
        while (i < n && buf[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < n && buf[i] == (byte)'{';
    }

    /// <summary>A title turned into a safe download file name.</summary>
    public static string FileName(string title, string fallback)
    {
        var s = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? ' ' : c).ToArray());
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? fallback : s[..Math.Min(s.Length, 80)];
    }
}
