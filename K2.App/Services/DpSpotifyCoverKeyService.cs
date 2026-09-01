using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using K2.Core;
using K2.Core.Services;
using Windows.Storage.Streams;

namespace K2.App.Services;

/// <summary>
/// Paints the currently-playing track's album cover on any DisplayPad key whose icon was
/// configured with "use Spotify cover" (<see cref="KeyIconSpec.SpotifyCover"/>) on a
/// <c>spotify</c> action — and keeps it in sync with what's playing via
/// <see cref="SpotifyMediaService.TrackChanged"/>.
///
/// Same shape as <see cref="DiscordVoiceKeyService"/> / <see cref="SpotifyCoverService"/> — a
/// transient overlay pushed straight to the hardware, never persisted in
/// <c>DisplayPadStore</c>, registered by every repaint path (<see cref="Sync"/>) and painted at
/// the tail of that repaint's batch (<see cref="Repaint"/>). It owns a key <b>only while a
/// cover is actually available</b> (<see cref="Owns"/>): with Spotify closed / nothing playing
/// the key falls back to its normal stored or generated picture. Unlike
/// <see cref="SpotifyCoverService"/> (the dedicated "Spotify profile" 2×2 block) this one is a
/// single 102×102 tile per key and works on ordinary user pages.
/// </summary>
internal static class DpSpotifyCoverKeyService
{
    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation, int[] Buttons);

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "K2.SpotifyCoverKey");
    private static string CoverPath => Path.Combine(CacheDir, "cover.png");

    private static readonly object _gate = new();
    private static readonly Dictionary<int, DeviceCtx> _devices = new();
    private static bool _subscribed;

    /// <summary>True while a decoded cover PNG is on disk — <see cref="Owns"/> gates on this so a
    /// key without a cover keeps its normal picture instead of going blank.</summary>
    private static bool _haveCover;

    /// <summary>Registers (refreshes / stops) the cover keys of one device from the page rows
    /// being painted. Registration only — the tiles are painted by <see cref="Repaint"/> at the
    /// END of that repaint's batch, so the profile's own icon for the key can't land on top.</summary>
    public static void Sync(IDisplayPadClient client, Action<string> log, int deviceId, int rotation,
                            IEnumerable<DpButtonRecord> rows)
    {
        int[] buttons = rows
            .Where(r => string.Equals(r.ActionType, "spotify", StringComparison.OrdinalIgnoreCase))
            .Where(r => KeyIconSpec.FromJson(r.IconSpec) is { SpotifyCover: true })
            .Select(r => r.ButtonIndex)
            .ToArray();

        if (buttons.Length == 0) { Stop(deviceId); return; }

        bool firstSubscriber;
        lock (_gate)
        {
            _devices[deviceId] = new DeviceCtx(client, log, rotation, buttons);
            firstSubscriber = !_subscribed;
            _subscribed = true;
        }
        if (firstSubscriber) SpotifyMediaService.Instance.TrackChanged += OnTrackChanged;

        _ = SpotifyMediaService.Instance.EnsureStartedAsync();
        _ = RefreshAndPushAsync(deviceId);
    }

    /// <summary>Paints the cover on every registered key of a device — called at the tail of the
    /// repaint batch that page belongs to. No-op for devices with no cover keys.</summary>
    public static void Repaint(int deviceId)
    {
        DeviceCtx ctx;
        lock (_gate) { if (!_devices.TryGetValue(deviceId, out ctx)) return; }
        PushDevice(deviceId, ctx);
    }

    /// <summary>True when this overlay currently owns that key — repaint paths use it to SKIP
    /// uploading the key's persisted picture. Only true while a cover is available, so a key
    /// with the option set still shows its normal icon when nothing is playing.</summary>
    public static bool Owns(int deviceId, int buttonIndex)
    {
        lock (_gate)
            return _haveCover
                && _devices.TryGetValue(deviceId, out var ctx)
                && Array.IndexOf(ctx.Buttons, buttonIndex) >= 0;
    }

    /// <summary>The cover tile currently on that key (for the press-bounce to shrink instead of
    /// the stored picture), or null when this overlay doesn't own the key right now.</summary>
    public static string? CurrentIconPath(int deviceId, int buttonIndex)
    {
        lock (_gate)
        {
            if (!_haveCover || !_devices.TryGetValue(deviceId, out var ctx)) return null;
            if (Array.IndexOf(ctx.Buttons, buttonIndex) < 0) return null;
        }
        return File.Exists(CoverPath) ? CoverPath : null;
    }

    public static void Stop(int deviceId)
    {
        lock (_gate) _devices.Remove(deviceId);
    }

    private static void OnTrackChanged() => _ = RefreshAndPushAsync(null);

    /// <summary>Re-decodes the current cover and pushes it to <paramref name="onlyDeviceId"/>
    /// (device just activated) or to every registered device (real track change).</summary>
    private static async Task RefreshAndPushAsync(int? onlyDeviceId)
    {
        lock (_gate) { if (_devices.Count == 0) return; }

        bool haveCover = false;
        try
        {
            var stream = await SpotifyMediaService.Instance.GetThumbnailStreamAsync();
            if (stream is not null)
            {
                Directory.CreateDirectory(CacheDir);
                haveCover = await DecodeSquareTileAsync(stream, CoverPath);
            }
        }
        catch { haveCover = false; }

        List<(int Id, DeviceCtx Ctx)> targets;
        lock (_gate)
        {
            _haveCover = haveCover;
            targets = onlyDeviceId is int id
                ? (_devices.TryGetValue(id, out var c) ? new List<(int, DeviceCtx)> { (id, c) } : new())
                : _devices.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        foreach (var (id, ctx) in targets) PushDevice(id, ctx);
    }

    private static void PushDevice(int deviceId, DeviceCtx ctx)
    {
        bool have;
        lock (_gate) have = _haveCover;
        if (!have || !File.Exists(CoverPath)) return;   // nothing to paint — key keeps its normal icon

        foreach (int btn in ctx.Buttons)
        {
            bool ok = ctx.Client.UploadImage(deviceId, CoverPath, btn, ctx.Rotation);
            ctx.Log($"[Spotify] cover key dev={deviceId} btn={btn} uploaded={ok}");
        }
    }

    /// <summary>Decodes the SMTC thumbnail stream, center-crops it to a square and resizes to a
    /// single 102×102 PNG. (Device rotation is applied by the client at upload time.)</summary>
    private static async Task<bool> DecodeSquareTileAsync(IRandomAccessStreamWithContentType stream, string outPath)
    {
        using var reader = new DataReader(stream);
        uint size = (uint)stream.Size;
        if (size == 0) return false;
        await reader.LoadAsync(size);
        byte[] raw = new byte[size];
        reader.ReadBytes(raw);

        using var ms = new MemoryStream(raw);
        using var src = new Bitmap(ms);

        int side = Math.Min(src.Width, src.Height);
        var cropRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);

        int n = DpHidNative.IconSize;
        using var tile = new Bitmap(n, n, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(tile))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, n, n), cropRect, GraphicsUnit.Pixel);
        }
        tile.Save(outPath, ImageFormat.Png);
        return true;
    }
}
