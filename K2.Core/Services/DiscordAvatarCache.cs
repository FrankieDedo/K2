using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// On-disk cache of the pictures the Discord voice page paints: user avatars and server icons,
/// both plain CDN downloads (<c>cdn.discordapp.com</c> needs no authentication).
///
/// <para>
/// Non-blocking by design: <see cref="TryGet"/> answers with the local file when it is already
/// there and otherwise returns null and starts the download in the background, raising
/// <see cref="Downloaded"/> when it lands. The page therefore paints an initials placeholder
/// first and repaints the real picture a moment later, instead of stalling a panel repaint on
/// the network — the same "never block the upload chain" rule the rest of the DisplayPad code
/// follows.
/// </para>
/// </summary>
public static class DiscordAvatarCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, byte> _inFlight = new();

    private static string Dir => Path.Combine(Path.GetTempPath(), "K2.DiscordAvatars");

    /// <summary>Raised (on a thread-pool thread) once a picture asked for earlier is on disk.</summary>
    public static event Action? Downloaded;

    /// <summary>Local path of <paramref name="url"/>'s picture, or null when it isn't cached yet
    /// (in which case the download has just been started).</summary>
    public static string? TryGet(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = AsPng(url);
        string path = PathFor(url);
        if (File.Exists(path)) return path;
        Fetch(url, path);
        return null;
    }

    private static void Fetch(string url, string path)
    {
        if (!_inFlight.TryAdd(url, 0)) return;   // already downloading
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Dir);
                // Written next to the target and moved into place: a repaint racing the
                // download must never pick up a half-written PNG.
                string tmp = path + ".part";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                try { Downloaded?.Invoke(); } catch { /* a host repaint must never kill this task */ }
            }
            catch (Exception ex) { DiscordBridge.Log?.Invoke($"[Discord] avatar download failed: {ex.Message}"); }
            finally { _inFlight.TryRemove(url, out _); }
        });
    }

    /// <summary>
    /// Forces a Discord CDN url to the PNG form of the same picture.
    ///
    /// <para>
    /// The RPC hands out server icons as <c>.webp</c> (and animated ones as <c>.gif</c>), which
    /// <c>System.Drawing</c> cannot decode at all: the download succeeded, the file was there, and
    /// the tile still came out as the empty gray circle of the fallback (user report — "the group
    /// picture doesn't show, just a gray circle"). The CDN serves every one of those hashes as
    /// <c>.png</c> too, so the extension is simply swapped; a <c>size</c> query is normalized to
    /// 128 px, which is what a 102 px key needs.
    /// </para>
    /// </summary>
    private static string AsPng(string url)
    {
        if (!url.Contains("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)) return url;

        int query = url.IndexOf('?');
        string path = query < 0 ? url : url[..query];
        int dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/')) path = path[..dot];
        return path + ".png?size=128";
    }

    private static string PathFor(string url)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Path.Combine(Dir, Convert.ToHexString(hash).ToLowerInvariant() + ".png");
    }
}
