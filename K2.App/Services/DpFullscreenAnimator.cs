using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// Whole-panel image/GIF for the DisplayPad: one picture (or animated GIF) is split across
/// all 12 keys so the 2×6 physical grid reads as a single "screen" instead of 12 separate
/// icons — same idea as BaseCampLinux's fullscreen mode (<c>panel.py</c>,
/// <c>_split_gif_to_tiles</c>/<c>_fullscreen_group</c>); not present in the original Base
/// Camp, which only ever assigns images per key.
///
/// <para>
/// Grid geometry is the FIXED physical layout confirmed in
/// <c>MainWindow.DisplayPad.DpRebuildKeyGrid</c> ("Always physical 2×6 layout — rotation is
/// handled by LayoutTransform"): key index <c>i</c> sits at row <c>i/6</c>, column
/// <c>i%6</c> in the UNROTATED grid. <paramref name="userRotation" /> (0/90/180/270, picked
/// by the user) rotates the whole source picture BEFORE slicing — independent of, and
/// applied before, the per-tile DEVICE counter-rotation.
/// </para>
///
/// <para>
/// <b>Speed</b>: like <see cref="DpGifAnimator"/>, the device counter-rotation is now baked
/// into each cached tile ONCE (at slice time, both cache key AND file depend on
/// <c>deviceRotation</c>) instead of being re-applied by <c>UploadImage</c> on every
/// displayed frame. Playback prefers <see cref="IDisplayPadClient.TryUploadRawBgr"/> (native
/// engine) — raw bytes straight to the wire — falling back to the pre-rotated PNG (rotation
/// 0, already baked in) otherwise.
/// </para>
///
/// <para>
/// <b>Single-transfer panel mode (2026-07-05)</b>: when <see cref="IDisplayPadClient.SupportsRawPanel"/>
/// is true (native engine), frames are instead composed as ONE full 800×240 buffer and sent
/// via <see cref="IDisplayPadClient.TryUploadRawPanel"/> — a single HID transfer instead of
/// 12 sequential ones, AND it covers the true full 800×240 physical LCD edge-to-edge (the
/// 12-tile path only ever covered the 612×204 union of the icon squares, leaving a border of
/// dead pixels — panel mode is a genuine "fullscreen", not just "all icons at once"). Cuts
/// the ×12 handshake/settle overhead, but NOT the raw byte count (576000 B either way,
/// comparable to 12×31212 B) — expect the per-frame refresh to drop from the old ~140-180 ms
/// down to roughly the wire-time floor (~110-140 ms for 576000 B at the protocol's fixed
/// pacing), a meaningful but not dramatic speedup. Falls back to the 12-tile path on any
/// backend without panel support, or if panel decode fails for some reason.
/// </para>
///
/// <para>
/// <b>Non-square canvas + device rotation</b>: the wire buffer is ALWAYS 800(w)×240(h)
/// regardless of mount. When the device is mounted rotated 90/270, a human viewing it sees a
/// 240×800 "portrait" shape — so the picture is first composed onto a 240×800 logical canvas
/// in THAT case (so it reads upright to the viewer), then <c>Bitmap.RotateFlip</c> (same
/// convention as the per-icon counter-rotation: 90°→Rotate270FlipNone, 270°→Rotate90FlipNone)
/// swaps it back to the required 800×240 physical shape. This is not just corrective — it is
/// REQUIRED for the byte count to come out right; see <see cref="BuildPanelBgr"/>.
/// <b>DA VERIFICARE su hardware fisico</b> for the 90°/270° case specifically (0° has been
/// exercised via the 12-tile path already; the panel path's rotation math is new and
/// un-tested on real hardware).
/// </para>
/// </summary>
internal static class DpFullscreenAnimator
{
    /// <summary>Physical grid geometry — public so callers (e.g. the fullscreen dialog's
    /// "show key outline" overlay, see <c>CropEditor.SetKeyGrid</c>) can reference it instead
    /// of hardcoding 2×6.</summary>
    public const int Rows = 2, Cols = 6;
    /// <summary>Full composed-canvas size in pixels (Cols×Rows icon tiles) — retained as the
    /// crop target for the 12-tile FALLBACK path only. When panel mode is available, the
    /// crop target should instead be <see cref="DpHidNative.PanelW"/>×<see cref="DpHidNative.PanelH"/>
    /// (or swapped, if the device is rotated 90/270) — see <c>MainWindow.ShowFullscreenDialog</c>.</summary>
    public const int CanvasWidth = Cols * DpHidNative.IconSize, CanvasHeight = Rows * DpHidNative.IconSize;
    private const int MinFrameMs = 50;
    private const int PropertyTagFrameDelay = 0x5100;

    private sealed class Session
    {
        public required CancellationTokenSource Cts;
        public required string SourceKey;
    }

    private static readonly Dictionary<int, Session> _running = new();   // deviceId -> session
    private static readonly object _lock = new();

    /// <summary>On-disk manifest entry — <c>Tiles</c> are already device-rotated PNGs.</summary>
    private sealed record FsFrameEntry(string[] Tiles, int DelayMs);

    /// <summary>One ready-to-play frame: raw BGR bytes + PNG fallback path per tile.</summary>
    private sealed record TileImg(byte[] Bgr, string PngPath);
    private sealed record FsFrame(TileImg[] Tiles, int DelayMs);

    // ================================================================
    // Public API
    // ================================================================

    public static bool IsAnimatedGif(string? path) => DpGifAnimator.IsAnimatedGif(path);

    /// <summary>
    /// Crop target size for a "what you see is what you get" fullscreen picture, given the
    /// CURRENT device rotation: the true full 800×240 panel if unrotated, or 240×800 if the
    /// device is mounted rotated 90°/270° (a viewer sees a portrait shape then — see
    /// <see cref="BuildPanelBgr"/>). Callers should prefer this over <see cref="CanvasWidth"/>/
    /// <see cref="CanvasHeight"/> whenever <see cref="IDisplayPadClient.SupportsRawPanel"/> is
    /// true, since panel mode paints the true edge-to-edge panel, not just the icon union.
    /// </summary>
    public static (int Width, int Height) PanelCanvasSize(int deviceRotation) =>
        deviceRotation is 90 or 270 ? (DpHidNative.PanelH, DpHidNative.PanelW) : (DpHidNative.PanelW, DpHidNative.PanelH);

    /// <summary>
    /// Starts (or restarts, if the source/rotation changed) showing <paramref name="sourcePath"/>
    /// across all 12 keys of one device. Caller must have already stopped any per-key GIF
    /// loops for this device (<see cref="DpGifAnimator.StopAllForDevice"/>) — fullscreen and
    /// per-key playback must never write to the same keys at the same time.
    /// </summary>
    public static void Start(IDisplayPadClient client, Action<string> log, int deviceId,
                              string sourcePath, int userRotation, int deviceRotation)
    {
        string sourceKey = $"{sourcePath}|{userRotation}|{deviceRotation}";
        lock (_lock)
        {
            if (_running.TryGetValue(deviceId, out var existing))
            {
                if (existing.SourceKey == sourceKey) return;
                existing.Cts.Cancel();
                _running.Remove(deviceId);
            }
            var cts = new CancellationTokenSource();
            _running[deviceId] = new Session { Cts = cts, SourceKey = sourceKey };
            var token = cts.Token;
            Task.Run(() => RunLoop(client, log, deviceId, sourcePath, userRotation, deviceRotation, token), token);
        }
    }

    /// <summary>Stops the fullscreen playback on one device (if any).</summary>
    public static void Stop(int deviceId)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(deviceId, out var s)) { s.Cts.Cancel(); _running.Remove(deviceId); }
        }
    }

    /// <summary>Stops every fullscreen playback on every device — app shutdown / client dispose.</summary>
    public static void StopAll()
    {
        lock (_lock)
        {
            foreach (var s in _running.Values) s.Cts.Cancel();
            _running.Clear();
        }
    }

    // ================================================================
    // Playback loop
    // ================================================================

    private static void RunLoop(IDisplayPadClient client, Action<string> log, int deviceId,
                                 string sourcePath, int userRotation, int deviceRotation,
                                 CancellationToken token)
    {
        if (client.SupportsRawPanel)
        {
            List<(byte[] Panel, int DelayMs)>? panelFrames = null;
            try { panelFrames = LoadPanelFrames(sourcePath, userRotation, deviceRotation); }
            catch (Exception ex) { log($"[DP-FS] panel decode failed, falling back to tiles: {ex.Message}"); }

            if (panelFrames is { Count: > 0 })
            {
                RunPanelLoop(client, log, deviceId, sourcePath, panelFrames, token);
                return;
            }
        }

        List<FsFrame>? frames;
        try { frames = LoadFrames(sourcePath, userRotation, deviceRotation); }
        catch (Exception ex) { log($"[DP-FS] decode failed: {ex.Message}"); return; }
        if (frames is null || frames.Count == 0) return;

        log($"[DP-FS] dev {deviceId}: fullscreen (12-tile) {(frames.Count > 1 ? $"({frames.Count} frames)" : "(static)")} " +
            $"<- {Path.GetFileName(sourcePath)}");
        int idx = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = frames[idx];
                for (int k = 0; k < frame.Tiles.Length; k++)
                {
                    if (token.IsCancellationRequested) return;
                    var tile = frame.Tiles[k];
                    if (!client.TryUploadRawBgr(deviceId, tile.Bgr, k))
                        client.UploadImage(deviceId, tile.PngPath, k, 0);
                }
                if (frames.Count == 1) return;   // static image: painted once, nothing to loop
                idx = (idx + 1) % frames.Count;
                if (token.WaitHandle.WaitOne(frame.DelayMs)) return;
            }
        }
        catch (Exception ex)
        {
            log($"[DP-FS] dev {deviceId}: loop stopped ({ex.Message})");
        }
    }

    /// <summary>Playback loop for the single-transfer panel path (native engine only).</summary>
    private static void RunPanelLoop(IDisplayPadClient client, Action<string> log, int deviceId,
                                      string sourcePath, List<(byte[] Panel, int DelayMs)> frames,
                                      CancellationToken token)
    {
        log($"[DP-FS] dev {deviceId}: fullscreen (single-transfer panel) " +
            $"{(frames.Count > 1 ? $"({frames.Count} frames)" : "(static)")} <- {Path.GetFileName(sourcePath)}");
        int idx = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var (panel, delayMs) = frames[idx];
                if (!client.TryUploadRawPanel(deviceId, panel))
                {
                    log($"[DP-FS] dev {deviceId}: panel upload failed, aborting panel loop");
                    return;
                }
                if (frames.Count == 1) return;
                idx = (idx + 1) % frames.Count;
                if (token.WaitHandle.WaitOne(delayMs)) return;
            }
        }
        catch (Exception ex)
        {
            log($"[DP-FS] dev {deviceId}: panel loop stopped ({ex.Message})");
        }
    }

    // ================================================================
    // Decode + disk cache (mirrors DpGifAnimator's cache scheme)
    // ================================================================

    private static List<FsFrame>? LoadFrames(string sourcePath, int userRotation, int deviceRotation)
    {
        string cacheKey = ComputeCacheKey(sourcePath, userRotation, deviceRotation);
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad", "fullscreen_frames", cacheKey);
        string manifestPath = Path.Combine(cacheDir, "frames.json");

        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<List<FsFrameEntry>>(File.ReadAllText(manifestPath));
                if (manifest is { Count: > 0 } &&
                    manifest.All(f => f.Tiles.All(t => File.Exists(Path.Combine(cacheDir, t)))))
                {
                    return manifest
                        .Select(f => new FsFrame(
                            f.Tiles.Select(t => new TileImg(ReadBgr(Path.Combine(cacheDir, t)), Path.Combine(cacheDir, t))).ToArray(),
                            f.DelayMs))
                        .ToList();
                }
            }
            catch { /* corrupt/partial manifest — fall through and re-decode */ }
        }

        // Resolve a CroppedGifRef sidecar (crop/zoom chosen for this GIF via the inline
        // CropEditor) to the real source file + crop rect — see DpGifAnimator.LoadFrames
        // for the identical pattern. Statics never use this (their crop is already baked
        // into a plain PNG upstream by CropEditor.GetResultPath, so IsCropRef is false).
        string realSourcePath = sourcePath;
        RectangleF? cropRect = null;
        if (CroppedGifRef.IsCropRef(sourcePath))
        {
            var cref = CroppedGifRef.TryLoad(sourcePath);
            if (cref is not null)
            {
                realSourcePath = cref.Source;
                if (!cref.NoCrop)
                    cropRect = new RectangleF(cref.RectX, cref.RectY, cref.RectW, cref.RectH);
            }
        }

        Directory.CreateDirectory(cacheDir);
        bool isGif = IsAnimatedGif(sourcePath);
        using var img = Image.FromFile(realSourcePath);

        var result = new List<FsFrame>();
        var manifestOut = new List<FsFrameEntry>();

        if (isGif)
        {
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = img.GetFrameCount(dim);
            int[] delaysCs = new int[frameCount];
            try
            {
                var prop = img.GetPropertyItem(PropertyTagFrameDelay);
                if (prop?.Value is not { } delayBytes)
                    throw new InvalidOperationException("no frame delay data");
                for (int i = 0; i < frameCount; i++)
                    delaysCs[i] = BitConverter.ToInt32(delayBytes, i * 4);
            }
            catch
            {
                for (int i = 0; i < frameCount; i++) delaysCs[i] = 10;   // 100ms fallback
            }

            for (int i = 0; i < frameCount; i++)
            {
                img.SelectActiveFrame(dim, i);
                using var cropped = CropFrame(img, cropRect);
                using var rotated = RotateWhole(cropped, userRotation);
                var (tiles, tileFiles) = SliceAndSave(rotated, cacheDir, $"f{i:0000}", deviceRotation);
                int delayMs = Math.Max(MinFrameMs, delaysCs[i] * 10);
                result.Add(new FsFrame(tiles, delayMs));
                manifestOut.Add(new FsFrameEntry(tileFiles, delayMs));
            }
        }
        else
        {
            using var rotated = RotateWhole(img, userRotation);
            var (tiles, tileFiles) = SliceAndSave(rotated, cacheDir, "static", deviceRotation);
            result.Add(new FsFrame(tiles, 0));
            manifestOut.Add(new FsFrameEntry(tileFiles, 0));
        }

        try { File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestOut)); }
        catch { /* best-effort — playback still works without the manifest cache */ }

        return result;
    }

    // ================================================================
    // Panel mode (single-transfer, native engine only) — in-memory only,
    // no disk cache: decode is a one-time cost per animation start.
    // ================================================================

    private static readonly Dictionary<string, List<(byte[] Panel, int DelayMs)>> _panelMemCache = new();

    private static List<(byte[] Panel, int DelayMs)>? LoadPanelFrames(string sourcePath, int userRotation, int deviceRotation)
    {
        string cacheKey = ComputeCacheKey(sourcePath, userRotation, deviceRotation) + "|panel";
        lock (_panelMemCache)
        {
            if (_panelMemCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        // Resolve a CroppedGifRef sidecar, same as the 12-tile LoadFrames above.
        string realSourcePath = sourcePath;
        RectangleF? cropRect = null;
        if (CroppedGifRef.IsCropRef(sourcePath))
        {
            var cref = CroppedGifRef.TryLoad(sourcePath);
            if (cref is not null)
            {
                realSourcePath = cref.Source;
                if (!cref.NoCrop)
                    cropRect = new RectangleF(cref.RectX, cref.RectY, cref.RectW, cref.RectH);
            }
        }

        bool isGif = IsAnimatedGif(sourcePath);
        using var img = Image.FromFile(realSourcePath);
        var result = new List<(byte[], int)>();

        if (isGif)
        {
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int frameCount = img.GetFrameCount(dim);
            int[] delaysCs = new int[frameCount];
            try
            {
                var prop = img.GetPropertyItem(PropertyTagFrameDelay);
                if (prop?.Value is not { } delayBytes)
                    throw new InvalidOperationException("no frame delay data");
                for (int i = 0; i < frameCount; i++)
                    delaysCs[i] = BitConverter.ToInt32(delayBytes, i * 4);
            }
            catch
            {
                for (int i = 0; i < frameCount; i++) delaysCs[i] = 10;
            }

            for (int i = 0; i < frameCount; i++)
            {
                img.SelectActiveFrame(dim, i);
                using var cropped = CropFrame(img, cropRect);
                byte[] panel = BuildPanelBgr(cropped, userRotation, deviceRotation);
                int delayMs = Math.Max(MinFrameMs, delaysCs[i] * 10);
                result.Add((panel, delayMs));
            }
        }
        else
        {
            byte[] panel = BuildPanelBgr(img, userRotation, deviceRotation);
            result.Add((panel, 0));
        }

        lock (_panelMemCache) { _panelMemCache[cacheKey] = result; }
        return result;
    }

    /// <summary>Crops <paramref name="src"/>'s currently-selected frame to
    /// <paramref name="cropRect"/> (source pixel coordinates) — or, if null, just snapshots
    /// it unchanged (identical to the pre-crop-feature behavior). Applied BEFORE
    /// <see cref="RotateWhole"/>, matching the same crop-then-rotate order already used for
    /// static images (see <c>CropEditor.GetResultPath</c>/<c>ShowFullscreenDialog</c>).</summary>
    private static Bitmap CropFrame(Image src, RectangleF? cropRect)
    {
        if (cropRect is not { } rect) return new Bitmap(src);
        int w = Math.Max(1, (int)Math.Round(rect.Width));
        int h = Math.Max(1, (int)Math.Round(rect.Height));
        var cropped = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), rect.X, rect.Y, rect.Width, rect.Height, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    /// <summary>
    /// Composes ONE frame into the physical <c>PanelW</c>×<c>PanelH</c> (800×240) BGR buffer
    /// expected by <c>Pad.UploadPanel</c>.
    ///
    /// <para>
    /// The wire buffer is ALWAYS 800×240 regardless of mount. If the device is mounted
    /// rotated 90°/270°, a viewer sees a 240×800 "portrait" shape, so the picture is first
    /// stretched onto a 240×800 LOGICAL canvas (so it reads upright to the viewer), then
    /// <c>RotateFlip</c> (same convention as the per-icon counter-rotation) swaps it back to
    /// 800×240 — <c>Bitmap.RotateFlip</c> swaps Width/Height for 90°/270°, so this is not
    /// optional: it's the only way the byte count comes out to exactly <c>PanelBytes</c>.
    /// </para>
    /// </summary>
    private static byte[] BuildPanelBgr(Image src, int userRotation, int deviceRotation)
    {
        bool portraitLogical = deviceRotation is 90 or 270;
        int logicalW = portraitLogical ? DpHidNative.PanelH : DpHidNative.PanelW;
        int logicalH = portraitLogical ? DpHidNative.PanelW : DpHidNative.PanelH;

        using var rotatedUser = RotateWhole(src, userRotation);
        using var canvas = new Bitmap(logicalW, logicalH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(rotatedUser, 0, 0, logicalW, logicalH);
        }

        switch (deviceRotation)
        {
            case 90:  canvas.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 270: canvas.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }
        // canvas is now guaranteed exactly PanelW×PanelH regardless of the branch above.
        return ExtractBgr(canvas);
    }

    /// <summary>Rotates the whole source picture (or current GIF frame) by the user-chosen
    /// angle — independent of, and applied BEFORE, the per-tile device counter-rotation.</summary>
    private static Bitmap RotateWhole(Image src, int userRotation)
    {
        var flip = userRotation switch
        {
            90  => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _   => RotateFlipType.RotateNoneFlipNone,
        };
        var bmp = new Bitmap(src);   // snapshot of the currently-selected frame
        bmp.RotateFlip(flip);
        return bmp;
    }

    /// <summary>
    /// Stretch-fits <paramref name="rotated"/> onto a Cols×Rows canvas sized in whole icon
    /// tiles, cuts it into the 12 individual 102×102 tiles, bakes in the DEVICE
    /// counter-rotation (same convention as <c>DisplayPadNativeClient.LoadBgr</c>) and saves
    /// each as PNG. Returns both the ready-to-upload tiles (raw bytes + PNG path) and the
    /// bare file names (for the manifest).
    /// </summary>
    private static (TileImg[] tiles, string[] fileNames) SliceAndSave(
        Bitmap rotated, string cacheDir, string namePrefix, int deviceRotation)
    {
        int tile = DpHidNative.IconSize;   // 102 — same as a static icon
        int canvasW = Cols * tile, canvasH = Rows * tile;
        using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(rotated, 0, 0, canvasW, canvasH);   // stretch-to-fit the 6×2 grid
        }

        var tileImgs = new TileImg[Rows * Cols];
        var fileNames = new string[Rows * Cols];
        for (int i = 0; i < fileNames.Length; i++)
        {
            int r = i / Cols, c = i % Cols;
            using var tileBmp = new Bitmap(tile, tile, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(tileBmp))
                g.DrawImage(canvas, new Rectangle(0, 0, tile, tile),
                            new Rectangle(c * tile, r * tile, tile, tile), GraphicsUnit.Pixel);
            switch (deviceRotation)
            {
                case 90: tileBmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                case 270: tileBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            }

            string fileName = $"{namePrefix}_k{i:00}.png";
            string fullPath = Path.Combine(cacheDir, fileName);
            tileBmp.Save(fullPath, ImageFormat.Png);
            tileImgs[i] = new TileImg(ExtractBgr(tileBmp), fullPath);
            fileNames[i] = fileName;
        }
        return (tileImgs, fileNames);
    }

    /// <summary>Extracts a 24bpp bitmap's raw BGR bytes, row-major — identical layout to
    /// <c>DisplayPadNativeClient.LoadBgr</c>. Sized off the bitmap's own Width/Height so it
    /// works for both 102×102 tiles and the 800×240 full panel.</summary>
    private static byte[] ExtractBgr(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = w * 3;
            var bgr = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgr, y * rowBytes, rowBytes);
            return bgr;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Re-derives raw BGR bytes from an already-rotated cached tile PNG (session
    /// restart / cache hit — no rotation applied here, the file already has it baked in).</summary>
    private static byte[] ReadBgr(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        using var converted = new Bitmap(DpHidNative.IconSize, DpHidNative.IconSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(converted))
            g.DrawImage(bmp, 0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        return ExtractBgr(converted);
    }

    private static string ComputeCacheKey(string path, int userRotation, int deviceRotation)
    {
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{mtime}|ur{userRotation}|dr{deviceRotation}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
