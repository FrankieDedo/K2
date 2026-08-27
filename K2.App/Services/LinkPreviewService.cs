using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// Fetches Open Graph metadata (og:title/og:image) for the "Extra" link cards on the
/// Settings tab (see MainWindow.Settings.cs's InitExtraLinksPanel) so a card shows the
/// same title/preview image the linked page would show if shared on Discord/Slack.
/// Results (title + downloaded image bytes) are cached to disk under
/// %LocalAppData%\K2\ExtraLinkCache forever — these are static reference links, not
/// something expected to change, so a cache hit skips the network entirely and the
/// cards still work offline after the first successful fetch.
/// </summary>
internal static class LinkPreviewService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly Regex MetaTag = new(@"<meta\s+[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "K2", "ExtraLinkCache");

    static LinkPreviewService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win32; x86) K2-App/LinkPreview");
        Http.DefaultRequestHeaders.Accept.ParseAdd("text/html");
    }

    public sealed record Preview(string? Title, string? ImagePath);

    /// <summary>Returns cached metadata for <paramref name="url"/> if present on disk,
    /// otherwise downloads the page + preview image and caches them. Never throws —
    /// any failure (offline, page down, no og:image) just yields a Preview with null
    /// fields, and the caller keeps its fallback title / no image.</summary>
    public static async Task<Preview> GetPreviewAsync(string url)
    {
        Directory.CreateDirectory(CacheDir);
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        string metaPath = Path.Combine(CacheDir, key + ".json");

        if (File.Exists(metaPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metaPath));
                if (cached is not null)
                {
                    string? imgPath = cached.ImageFile is null ? null : Path.Combine(CacheDir, cached.ImageFile);
                    if (imgPath is not null && !File.Exists(imgPath)) imgPath = null;
                    return new Preview(cached.Title, imgPath);
                }
            }
            catch { /* corrupt cache entry — fall through and refetch */ }
        }

        string? title = null, imageFile = null;
        try
        {
            string html = await Http.GetStringAsync(url);
            title = ExtractOg(html, "og:title");
            string? imageUrl = ExtractOg(html, "og:image");
            if (imageUrl is not null)
            {
                byte[] bytes = await Http.GetByteArrayAsync(imageUrl);
                string ext = Path.GetExtension(new Uri(imageUrl).LocalPath);
                if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".img";
                imageFile = key + ext;
                await File.WriteAllBytesAsync(Path.Combine(CacheDir, imageFile), bytes);
            }
        }
        catch { /* offline / blocked / no og tags — cache what we have (possibly nothing) */ }

        try
        {
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new CacheEntry(title, imageFile)));
        }
        catch { /* best-effort cache write */ }

        return new Preview(title, imageFile is null ? null : Path.Combine(CacheDir, imageFile));
    }

    private static string? ExtractOg(string html, string property)
    {
        foreach (Match m in MetaTag.Matches(html))
        {
            string tag = m.Value;
            if (!Regex.IsMatch(tag, $@"property\s*=\s*[""']{Regex.Escape(property)}[""']", RegexOptions.IgnoreCase))
                continue;
            var cm = Regex.Match(tag, @"content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (cm.Success) return System.Net.WebUtility.HtmlDecode(cm.Groups[1].Value);
        }
        return null;
    }

    private sealed record CacheEntry(string? Title, string? ImageFile);
}
