using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace K2.App.Services;

/// <summary>
/// The last frame captured of each program, kept on disk so a probe can be aimed, re-aimed and
/// re-read with the game CLOSED.
///
/// <para>
/// Calibration is done on a still frame (see <see cref="ScreenProbeDialog"/>): the capture is a
/// copy of the screen, so the game has to be running and in front to take one. That is fine the
/// first time and a nuisance every time after — moving a rectangle two pixels, renaming a reading,
/// switching a probe from Fill to Number all meant launching the game again. Keeping the frame
/// turns those into ordinary edits.
/// </para>
///
/// <para>
/// PNG, not JPEG: every number in the dialog is MEASURED on this frame by the same code the pad
/// runs, so compression artefacts would show up as readings that disagree with the live tile —
/// exactly the drift the still-frame preview exists to rule out. The size that costs is bounded
/// instead by <see cref="Keep"/>, oldest first.
/// </para>
/// </summary>
internal static class ScreenFrameCache
{
    /// <summary>How many programs' frames are kept. A 4K PNG is a few MB and the user calibrates
    /// against a handful of games, so this is generous and still bounded.</summary>
    private const int Keep = 12;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "frames");

    /// <summary>Path of the frame kept for a process. The name is sanitised rather than hashed so
    /// the folder stays something a human can look through.</summary>
    private static string PathFor(string process)
    {
        var safe = new StringBuilder(process.Length);
        foreach (char c in process)
            safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return Path.Combine(Dir, safe.Length == 0 ? "_" : safe.ToString() + ".png");
    }

    /// <summary>When the kept frame was taken, or null when there is none.</summary>
    public static DateTime? StampOf(string process)
    {
        if (process.Length == 0) return null;
        try
        {
            var path = PathFor(process);
            return File.Exists(path) ? File.GetLastWriteTime(path) : null;
        }
        catch { return null; }
    }

    /// <summary>The kept frame for a process, or null when there is none (or it cannot be read).
    /// The bitmap is DETACHED from the file — the caller owns it and the file is not held open, so
    /// the next capture can overwrite it while this one is still on screen.</summary>
    public static Bitmap? Load(string process)
    {
        if (process.Length == 0) return null;
        try
        {
            var path = PathFor(process);
            if (!File.Exists(path)) return null;

            // Through a byte[] on purpose: Bitmap.FromFile keeps the file locked for the life of
            // the image, and this one has to stay writable.
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var loaded = new Bitmap(ms);
            return new Bitmap(loaded);
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] cached frame for {process} unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps this frame as the one for that program, replacing whatever was there.</summary>
    public static void Save(string process, Bitmap frame)
    {
        if (process.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Dir);
            // Via a temp file: a half-written PNG left by a crash mid-save would be a frame that
            // never loads again, and the user has no way to tell that is what happened.
            var path = PathFor(process);
            var tmp = path + ".tmp";
            frame.Save(tmp, ImageFormat.Png);
            File.Move(tmp, path, overwrite: true);
            Prune();
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] could not keep the frame for {process}: {ex.Message}");
        }
    }

    /// <summary>Drops the oldest frames past <see cref="Keep"/>.</summary>
    private static void Prune()
    {
        try
        {
            foreach (var stale in new DirectoryInfo(Dir).GetFiles("*.png")
                                                        .OrderByDescending(f => f.LastWriteTimeUtc)
                                                        .Skip(Keep))
                stale.Delete();
        }
        catch { /* a frame too many is not worth a word to the user */ }
    }
}
