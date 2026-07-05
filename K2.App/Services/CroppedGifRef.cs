using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace K2.App.Services;

/// <summary>
/// A "cropped GIF" reference (2026-07-05). Static images can be crop-baked into a single
/// new PNG (see <c>CropEditor.GetResultPath</c>), but an animated GIF can't be crop-baked
/// into a single new GIF FILE the same way: <c>System.Drawing</c>/GDI+ only reliably
/// DECODES multi-frame GIFs — it has no supported multi-frame GIF ENCODER, so hand-rolling
/// one is out of scope. Instead, cropping a GIF is recorded as a small JSON sidecar file
/// that points back at the ORIGINAL animated source plus a crop rectangle (in the source
/// image's own pixel coordinates — same for every frame, since all frames of one GIF share
/// dimensions) or a "no crop" flag.
///
/// <para>
/// Every consumer that resolves an assigned "image path" for playback
/// (<c>DpGifAnimator</c>, <c>DpFullscreenAnimator</c>) checks <see cref="IsCropRef"/> first
/// and, if true, loads this sidecar to find the REAL source file and crop rectangle, then
/// decodes frames from THAT file — applying the crop to every frame's draw call instead of
/// a full stretch. The sidecar's own path is what gets stored wherever a plain image path
/// would normally go (button/fullscreen assignment) — from the storage layer's point of
/// view it's still "just a path", no schema changes needed anywhere.
/// </para>
///
/// <para>
/// Deliberately NOT used by the Everest NDK flow (<c>ImageCropDialog</c> stays in its
/// default, non-gif-aware mode there) — NDK has no per-frame animation loop at all (see
/// TODO.md), so it must keep treating a picked GIF as a plain static image (frame 0 only),
/// exactly like before this file existed.
/// </para>
/// </summary>
internal sealed record CroppedGifRef(string Source, bool NoCrop, float RectX, float RectY, float RectW, float RectH)
{
    public const string Extension = ".cropgif.json";

    public static bool IsCropRef(string? path) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static CroppedGifRef? TryLoad(string path)
    {
        try { return JsonSerializer.Deserialize<CroppedGifRef>(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>Saves this reference to the shared crop cache directory, named by a hash of
    /// (source path + mtime + crop parameters) — same caching convention as the static PNG
    /// crop cache, so re-picking the exact same crop is instant and doesn't pile up files.</summary>
    public string Save(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(Source).Ticks; } catch { }
        string key = $"{Source}|{mtime}|" +
                     (NoCrop ? "nocrop" : $"{RectX:F1},{RectY:F1},{RectW:F1},{RectH:F1}");
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + Extension;
        string outPath = Path.Combine(cacheDir, name);
        File.WriteAllText(outPath, JsonSerializer.Serialize(this));
        return outPath;
    }
}
