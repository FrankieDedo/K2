using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using K2.Core.Services;
using Windows.Storage.Streams;

namespace K2.App.Services;

/// <summary>
/// Drives the DisplayPad "Spotify" profile's 2×2 cover-art block (keys 0,1,6,7 — see
/// MainWindow.DisplayPad.cs's DpSwitchProfile hook): on every SpotifyMediaService.TrackChanged
/// event, decodes the SMTC thumbnail, center-crops/resizes it to 204×204 and splits it into
/// 4 quadrants of 102×102 (the device's native icon size), pushed live via the fast raw-BGR
/// path (IDisplayPadClient.TryUploadRawBgr) — bypassing DisplayPadStore/SQLite entirely, since
/// this is a transient "now playing" overlay, not a persisted per-key icon assignment.
/// </summary>
internal static class SpotifyCoverService
{
    // Physical key layout (2 rows × 6 cols, index = row*6+col): the left-most 2×2 block is
    // keys {0,1,6,7} — a fixed physical square (0=(row0,col0), 1=(row0,col1), 6=(row1,col0),
    // 7=(row1,col1)) that never moves. What changes with _dpRotation is which of those 4 keys
    // visually ends up at the top-left/etc. from the VIEWER's perspective, because the on-screen
    // grid (and the physical device as mounted) is rotated as a whole — see CvsDpKeys.LayoutTransform
    // in MainWindow.DisplayPad.cs. Quadrants are always decoded TL/TR/BL/BR in the image's own
    // (unrotated) frame; this table says which physical key should receive each quadrant so the
    // 4 tiles still read as one coherent picture once the device's rotation is accounted for.
    // Derived from "rotate a 2×2 matrix N×90° clockwise": new[r][c] = old[1-c][r].
    private static int[] QuadrantButtonsFor(int rotation) => rotation switch
    {
        90  => new[] { 6, 0, 7, 1 },
        180 => new[] { 7, 6, 1, 0 },
        270 => new[] { 1, 7, 0, 6 },
        _   => new[] { 0, 1, 6, 7 },
    };

    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "K2.SpotifyCover");

    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation);

    private static readonly object _gate = new();
    private static readonly Dictionary<int, DeviceCtx> _devices = new();
    private static byte[]?[]? _lastQuadrantPngs; // 4 unrotated 102x102 PNGs, or null = no cover
    private static bool _subscribed;

    public static void Start(IDisplayPadClient client, Action<string> log, int deviceId, int rotation)
    {
        bool firstSubscriber;
        lock (_gate)
        {
            _devices[deviceId] = new DeviceCtx(client, log, rotation);
            firstSubscriber = !_subscribed;
            _subscribed = true;
        }
        if (firstSubscriber)
            SpotifyMediaService.Instance.TrackChanged += OnTrackChanged;

        _ = SpotifyMediaService.Instance.EnsureStartedAsync();
        _ = RefreshAndPushAsync(deviceId);
    }

    public static void Stop(int deviceId)
    {
        lock (_gate) _devices.Remove(deviceId);
    }

    private static void OnTrackChanged() => _ = RefreshAndPushAsync(null);

    /// <summary>Re-fetches the current thumbnail and pushes it to <paramref name="onlyDeviceId"/>
    /// (device just activated) or to every active device (real track change).</summary>
    private static async Task RefreshAndPushAsync(int? onlyDeviceId)
    {
        byte[]?[]? quadrants;
        try
        {
            var stream = await SpotifyMediaService.Instance.GetThumbnailStreamAsync();
            quadrants = stream is null ? null : await DecodeAndSliceAsync(stream);
        }
        catch
        {
            quadrants = null;
        }
        lock (_gate) _lastQuadrantPngs = quadrants;

        List<(int Id, DeviceCtx Ctx)> targets;
        lock (_gate)
        {
            targets = onlyDeviceId is int id
                ? (_devices.TryGetValue(id, out var c) ? new List<(int, DeviceCtx)> { (id, c) } : new())
                : _devices.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        foreach (var (id, ctx) in targets)
            PushToDevice(id, ctx, quadrants);
    }

    /// <summary>Decodes the SMTC thumbnail stream, center-crops to a square, resizes to
    /// 204×204 and slices it into 4 unrotated 102×102 PNGs (TL,TR,BL,BR) — rotation is applied
    /// per-device at push time since different devices may have different mounting rotations.</summary>
    private static async Task<byte[]?[]> DecodeAndSliceAsync(IRandomAccessStreamWithContentType stream)
    {
        using var reader = new DataReader(stream);
        uint size = (uint)stream.Size;
        await reader.LoadAsync(size);
        byte[] raw = new byte[size];
        reader.ReadBytes(raw);

        using var ms = new MemoryStream(raw);
        using var src = new Bitmap(ms);

        const int full = 2 * DpHidNative.IconSize; // 204
        int side = Math.Min(src.Width, src.Height);
        var cropRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);

        using var square = new Bitmap(full, full, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(square))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, full, full), cropRect, GraphicsUnit.Pixel);
        }

        var result = new byte[4][];
        var quadRects = new[]
        {
            new Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize),                                   // TL
            new Rectangle(DpHidNative.IconSize, 0, DpHidNative.IconSize, DpHidNative.IconSize),                 // TR
            new Rectangle(0, DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize),                 // BL
            new Rectangle(DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize), // BR
        };
        for (int i = 0; i < 4; i++)
        {
            using var tile = square.Clone(quadRects[i], PixelFormat.Format24bppRgb);
            using var msOut = new MemoryStream();
            tile.Save(msOut, ImageFormat.Png);
            result[i] = msOut.ToArray();
        }
        return result;
    }

    private static void PushToDevice(int deviceId, DeviceCtx ctx, byte[]?[]? quadrantPngs)
    {
        Directory.CreateDirectory(CacheDir);
        int[] quadrantButtons = QuadrantButtonsFor(ctx.Rotation);
        for (int i = 0; i < 4; i++)
        {
            int btn = quadrantButtons[i];
            byte[] bgr;
            string cachePath = Path.Combine(CacheDir, $"q{i}_r{ctx.Rotation}.png");

            using var tile = new Bitmap(DpHidNative.IconSize, DpHidNative.IconSize, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(tile))
            {
                g.Clear(Color.Black);
                if (quadrantPngs?[i] is byte[] png)
                {
                    using var ms = new MemoryStream(png);
                    using var src = new Bitmap(ms);
                    g.DrawImage(src, 0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
                }
            }
            switch (ctx.Rotation)
            {
                case 90: tile.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                case 180: tile.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 270: tile.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            }
            tile.Save(cachePath, ImageFormat.Png);
            bgr = ExtractBgr24(tile);

            if (!ctx.Client.TryUploadRawBgr(deviceId, bgr, btn))
                ctx.Client.UploadImage(deviceId, cachePath, btn, 0);
        }
        ctx.Log($"[Spotify] cover pushed to device {deviceId}");
    }

    private static byte[] ExtractBgr24(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = DpHidNative.IconSize * 3;
            var bgr = new byte[DpHidNative.IconBytes];
            for (int y = 0; y < DpHidNative.IconSize; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, bgr, y * rowBytes, rowBytes);
            return bgr;
        }
        finally { bmp.UnlockBits(data); }
    }
}
