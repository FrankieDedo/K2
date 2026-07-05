using System;
using System.Collections.Concurrent;
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
/// Per-key animated GIF playback for the DisplayPad, replicating what the original Base
/// Camp does (confirmed in the decompiled worker: <c>DisplayPadOperations.UploadGIFImage</c>/
/// <c>SetGIFImage</c> — one long-running background task per animated key, looping frames
/// forever via a LIVE icon upload, cancelled on profile/page switch or re-assignment) —
/// and what BaseCampLinux's <c>panel.py</c> independently re-implements in Python.
///
/// <para>
/// <b>Speed</b>: every frame is decoded, resized to <c>IconSize</c> AND device-rotated
/// (using the identical convention as <c>DisplayPadNativeClient.LoadBgr</c> /
/// satellite's <c>ResolveForUpload</c> — 90°→Rotate270FlipNone, 270°→Rotate90FlipNone) ONCE,
/// when the animation starts — not on every displayed frame. Playback then prefers
/// <see cref="IDisplayPadClient.TryUploadRawBgr"/> (native engine only): a raw BGR buffer
/// straight to the wire, with ZERO GDI+ work left in the hot loop — before this, EVERY
/// displayed frame re-read the PNG from disk, re-decoded it and re-ran a full bicubic
/// resize through <c>UploadImage</c>'s normal path even though the cached frame was already
/// exactly the right size, which was most of why GIFs looked "laggy" on top of the
/// hardware's own per-icon transfer floor (~12 ms/icon, protocol-paced — see
/// DpHidNative.Pad.StreamLocked remarks; that floor is NOT something software can remove).
/// Because rotation is baked in upfront, the pre-rotated PNG fallback (used when
/// <c>TryUploadRawBgr</c> isn't supported — currently the satellite/SDK backend) is uploaded
/// with <c>rotation: 0</c>, so it is never rotated twice.
/// </para>
///
/// <para>
/// User-rotation (<see cref="K2.App.DpKeyConfigDialog"/>'s 0/90/180/270 picker) is
/// intentionally NOT applied to GIFs — baking it in would require re-encoding every cached
/// frame (PNG can't hold multiple frames) — see DpKeyConfigDialog remarks.
/// </para>
///
/// <para>
/// <b>Crop (2026-07-05)</b>: unlike rotation, cropping a GIF doesn't need re-encoding — the
/// SAME crop rectangle (in source pixel coordinates) applies identically to every frame, so
/// it's just an extra source-rect argument to the per-frame <c>DrawImage</c> call below.
/// The crop itself is recorded as a <see cref="CroppedGifRef"/> sidecar (produced by
/// <c>CropEditor</c>, since a GIF can't be crop-baked into one new GIF file) — <see cref="LoadFrames"/>
/// resolves that sidecar to the real source path + rect before decoding.
/// </para>
/// </summary>
internal static class DpGifAnimator
{
    /// <summary>Frame delay floor (ms) — protects the HID bus from being hammered by
    /// pathological GIFs with near-zero declared delays. Matches BaseCampLinux's default
    /// "min ms/frame" of 50.</summary>
    private const int MinFrameMs = 50;

    private const int PropertyTagFrameDelay = 0x5100;

    private readonly record struct KeyId(int DeviceId, int Btn);

    private sealed class Animation
    {
        public required CancellationTokenSource Cts;
        public required string SourcePath;
        public required int Rotation;
    }

    private static readonly Dictionary<KeyId, Animation> _running = new();
    private static readonly object _lock = new();

    /// <summary>On-disk manifest entry — <c>File</c> is the already-device-rotated PNG
    /// (fallback path for backends without <see cref="IDisplayPadClient.TryUploadRawBgr"/>).</summary>
    private sealed record FrameEntry(string File, int DelayMs);

    /// <summary>One ready-to-play frame: raw bytes for the fast path, PNG path for fallback.</summary>
    private sealed record Frame(byte[] Bgr, string PngPath, int DelayMs);

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>True if the file is a GIF with more than one frame (i.e. actually animated —
    /// a single-frame GIF is treated as a normal static image). Also resolves a
    /// <see cref="CroppedGifRef"/> sidecar (crop/zoom applied to a GIF — see that class'
    /// remarks) to check the REAL source it points at.</summary>
    public static bool IsAnimatedGif(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string realPath = path;
        if (CroppedGifRef.IsCropRef(path))
        {
            var cref = CroppedGifRef.TryLoad(path);
            if (cref is null) return false;
            realPath = cref.Source;
        }
        if (!realPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var img = Image.FromFile(realPath);
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            return img.GetFrameCount(dim) > 1;
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts (or restarts, if the source file or rotation changed) a background animation
    /// loop for one key. Safe to call repeatedly with the same path/rotation — a no-op if
    /// that exact GIF is already animating on that key.
    /// </summary>
    public static void StartOrUpdate(IDisplayPadClient client, Action<string> log,
                                      int deviceId, int btn, string gifPath, int rotation)
    {
        var id = new KeyId(deviceId, btn);
        lock (_lock)
        {
            if (_running.TryGetValue(id, out var existing))
            {
                if (existing.SourcePath == gifPath && existing.Rotation == rotation) return;
                existing.Cts.Cancel();
                _running.Remove(id);
            }
            var cts = new CancellationTokenSource();
            _running[id] = new Animation { Cts = cts, SourcePath = gifPath, Rotation = rotation };
            var token = cts.Token;
            Task.Run(() => RunLoop(client, log, deviceId, btn, gifPath, rotation, token), token);
        }
    }

    /// <summary>Stops the animation on one key (if any). Called whenever that key gets a
    /// static image, no image, or the device disconnects.</summary>
    public static void Stop(int deviceId, int btn)
    {
        lock (_lock)
        {
            var id = new KeyId(deviceId, btn);
            if (_running.TryGetValue(id, out var a)) { a.Cts.Cancel(); _running.Remove(id); }
        }
    }

    /// <summary>Stops every animation on a device — call before repainting/reloading a
    /// profile or page, exactly like BC cancels pending per-key tasks in
    /// <c>ChangeProfileFromUI</c> before it starts the new batch.</summary>
    public static void StopAllForDevice(int deviceId)
    {
        lock (_lock)
        {
            foreach (var id in _running.Keys.Where(k => k.DeviceId == deviceId).ToList())
            {
                _running[id].Cts.Cancel();
                _running.Remove(id);
            }
        }
    }

    /// <summary>Stops every animation on every device — call on app shutdown / client
    /// dispose so no background loop keeps writing to a closed HID handle.</summary>
    public static void StopAll()
    {
        lock (_lock)
        {
            foreach (var a in _running.Values) a.Cts.Cancel();
            _running.Clear();
        }
    }

    // ================================================================
    // Playback loop
    // ================================================================

    private static void RunLoop(IDisplayPadClient client, Action<string> log,
                                 int deviceId, int btn, string gifPath, int rotation,
                                 CancellationToken token)
    {
        List<Frame>? frames;
        try
        {
            frames = LoadFrames(gifPath, rotation);
        }
        catch (Exception ex)
        {
            log($"[DP-GIF] decode failed for key #{btn}: {ex.Message}");
            return;
        }
        if (frames is null || frames.Count < 2) return;

        log($"[DP-GIF] key #{btn}: playing {frames.Count} frame(s) from {Path.GetFileName(gifPath)}");
        int idx = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = frames[idx];
                // Fast path: raw bytes straight to the wire (native engine). Falls back to
                // the pre-rotated PNG file (rotation already baked in -> pass 0 here) for
                // backends that don't support raw uploads (satellite/SDK).
                if (!client.TryUploadRawBgr(deviceId, frame.Bgr, btn))
                    client.UploadImage(deviceId, frame.PngPath, btn, 0);
                idx = (idx + 1) % frames.Count;
                if (token.WaitHandle.WaitOne(frame.DelayMs)) return;   // cancelled while waiting
            }
        }
        catch (Exception ex)
        {
            log($"[DP-GIF] key #{btn}: animation loop stopped ({ex.Message})");
        }
    }

    // ================================================================
    // Frame decode + disk cache
    // ================================================================

    /// <summary>In-memory index of already-decoded GIFs this session, keyed by cache key
    /// (path + mtime + rotation) — avoids re-splitting/re-reading the same file on every
    /// profile reload.</summary>
    private static readonly ConcurrentDictionary<string, List<Frame>> _memCache = new();

    private static List<Frame>? LoadFrames(string gifPath, int rotation)
    {
        string cacheKey = ComputeCacheKey(gifPath, rotation);
        if (_memCache.TryGetValue(cacheKey, out var mem) && mem.All(f => File.Exists(f.PngPath)))
            return mem;

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.DisplayPad", "gif_frames", cacheKey);
        string manifestPath = Path.Combine(cacheDir, "frames.json");

        // A cached PNG (already device-rotated, from a previous session) still needs its
        // raw BGR bytes re-extracted once — cheap (one decode per frame, not per playback
        // tick) and keeps the manifest format simple (just file name + delay).
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<List<FrameEntry>>(File.ReadAllText(manifestPath));
                if (manifest is { Count: > 1 } &&
                    manifest.All(f => File.Exists(Path.Combine(cacheDir, f.File))))
                {
                    var cached = manifest
                        .Select(f => new Frame(ReadBgr(Path.Combine(cacheDir, f.File)), Path.Combine(cacheDir, f.File), f.DelayMs))
                        .ToList();
                    _memCache[cacheKey] = cached;
                    return cached;
                }
            }
            catch { /* corrupt/partial manifest — fall through and re-decode */ }
        }

        // Resolve a CroppedGifRef sidecar (crop/zoom chosen for this GIF via the inline
        // CropEditor — see that class' remarks) to the REAL source file + crop rect. The
        // cache key/dir above is still based on the ORIGINAL gifPath argument (the sidecar
        // itself, when present), so different crops of the same source naturally get
        // different cache entries.
        string realPath = gifPath;
        RectangleF? cropRect = null;
        if (CroppedGifRef.IsCropRef(gifPath))
        {
            var cref = CroppedGifRef.TryLoad(gifPath);
            if (cref is not null)
            {
                realPath = cref.Source;
                if (!cref.NoCrop)
                    cropRect = new RectangleF(cref.RectX, cref.RectY, cref.RectW, cref.RectH);
            }
        }

        Directory.CreateDirectory(cacheDir);
        using var img = Image.FromFile(realPath);
        var dim = new FrameDimension(img.FrameDimensionsList[0]);
        int frameCount = img.GetFrameCount(dim);
        if (frameCount < 2) return null;

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
            // No delay metadata (some encoders omit it) — standard GIF default is 100ms.
            for (int i = 0; i < frameCount; i++) delaysCs[i] = 10;
        }

        const int size = DpHidNative.IconSize;   // 102 — same as a static icon
        var result = new List<Frame>(frameCount);
        var manifestOut = new List<FrameEntry>(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            img.SelectActiveFrame(dim, i);
            using var frameBmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(frameBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (cropRect is { } rect)
                    g.DrawImage(img, new Rectangle(0, 0, size, size), rect.X, rect.Y, rect.Width, rect.Height, GraphicsUnit.Pixel);
                else
                    g.DrawImage(img, new Rectangle(0, 0, size, size));   // classic full stretch (no crop)
            }
            // Bake device-rotation in NOW (same convention as LoadBgr/ResolveForUpload) so
            // playback never has to rotate again.
            switch (rotation)
            {
                case 90: frameBmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                case 270: frameBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            }

            string fileName = $"frame_{i:0000}.png";
            string framePath = Path.Combine(cacheDir, fileName);
            frameBmp.Save(framePath, ImageFormat.Png);
            byte[] bgr = ExtractBgr(frameBmp);

            int delayMs = Math.Max(MinFrameMs, delaysCs[i] * 10);
            result.Add(new Frame(bgr, framePath, delayMs));
            manifestOut.Add(new FrameEntry(fileName, delayMs));
        }

        try { File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifestOut)); }
        catch { /* best-effort — playback still works without the manifest cache */ }

        _memCache[cacheKey] = result;
        return result;
    }

    /// <summary>Extracts a 24bpp bitmap's raw BGR bytes, row-major — identical layout to
    /// <c>DisplayPadNativeClient.LoadBgr</c> (GDI+ 24bpp memory layout is already B,G,R).</summary>
    private static byte[] ExtractBgr(Bitmap bmp)
    {
        int size = DpHidNative.IconSize;
        var rect = new Rectangle(0, 0, size, size);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = size * 3;
            var bgr = new byte[DpHidNative.IconBytes];
            for (int y = 0; y < size; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgr, y * rowBytes, rowBytes);
            return bgr;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Re-derives raw BGR bytes from an already-rotated cached PNG (session restart
    /// / cache hit from a previous run — no rotation applied here, the file already has it).</summary>
    private static byte[] ReadBgr(string pngPath)
    {
        using var bmp = new Bitmap(pngPath);
        using var converted = new Bitmap(DpHidNative.IconSize, DpHidNative.IconSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(converted))
            g.DrawImage(bmp, 0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        return ExtractBgr(converted);
    }

    /// <summary>Cache key = SHA1(path + last-write-time + rotation) — same scheme as
    /// <c>DpKeyConfigDialog.ApplyUserRotation</c>, so the cache self-invalidates when the
    /// source GIF is replaced OR the device rotation setting changes.</summary>
    private static string ComputeCacheKey(string path, int rotation)
    {
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{mtime}|rot{rotation}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
