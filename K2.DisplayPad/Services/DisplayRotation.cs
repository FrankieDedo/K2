using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace K2.DisplayPad.Services;

/// <summary>
/// Physical orientation in which the DisplayPad is mounted.
/// The value indicates how many degrees CLOCKWISE the device is rotated
/// relative to its native position (horizontal strip, icons upright).
/// </summary>
public enum DisplayRotation
{
    /// <summary>Native position: 2-row x 6-column grid.</summary>
    None = 0,
    /// <summary>Device mounted rotated 90 degrees clockwise.</summary>
    Cw90 = 90,
    /// <summary>Device mounted upside down (180 degrees, same 2x6 grid).</summary>
    Cw180 = 180,
    /// <summary>Device mounted rotated 270 degrees clockwise (= 90 counter-clockwise).</summary>
    Cw270 = 270,
}

/// <summary>
/// Geometry of the DisplayPad button grid and rotation logic.
///
/// Native PHYSICAL layout: 2 rows x 6 columns, indices 0..11
/// <code>
///     0  1  2  3  4  5
///     6  7  8  9 10 11
/// </code>
///
/// "Visual slot" = position of the cell in K2's on-screen grid,
/// which is re-oriented to mirror the device as it is mounted rotated.
///
/// The internal MODEL (<see cref="Models.ButtonCell"/>.Index, matrix map,
/// <see cref="StateStore"/>) ALWAYS stays in PHYSICAL indices: rotation
/// only affects (a) how cells are arranged on screen and (b) the pixels
/// of the icon uploaded to the firmware. This way button handling, actions
/// and persistence don't need to be touched.
/// </summary>
public static class DisplayPadLayout
{
    /// <summary>Rows of the native physical grid.</summary>
    public const int PhysRows = 2;
    /// <summary>Columns of the native physical grid.</summary>
    public const int PhysCols = 6;
    /// <summary>Total number of buttons (FW_NUM_KEY).</summary>
    public const int ButtonCount = PhysRows * PhysCols; // 12

    /// <summary>On-screen grid size for the given rotation.
    /// At 90/270 the 2x6 strip becomes 6x2; at 0/180 it stays 2x6.</summary>
    public static (int Rows, int Cols) VisualGrid(DisplayRotation r) =>
        r is DisplayRotation.Cw90 or DisplayRotation.Cw270 ? (PhysCols, PhysRows) : (PhysRows, PhysCols);

    /// <summary>
    /// Permutation table: for each visual slot (0..11, in the reading
    /// order of the on-screen grid) returns the PHYSICAL index of the
    /// button to display in that position.
    /// </summary>
    public static int[] PhysicalForVisual(DisplayRotation r)
    {
        var (_, vCols) = VisualGrid(r);
        var map = new int[ButtonCount];
        for (int pr = 0; pr < PhysRows; pr++)
        for (int pc = 0; pc < PhysCols; pc++)
        {
            int phys = pr * PhysCols + pc;
            int vr, vc;
            switch (r)
            {
                // Device rotated 90 CW: the physical top-left corner goes to top-right.
                case DisplayRotation.Cw90:
                    vr = pc;
                    vc = PhysRows - 1 - pr;
                    break;
                // Device rotated 270 CW (= 90 CCW): top-left goes to bottom-left.
                case DisplayRotation.Cw270:
                    vr = PhysCols - 1 - pc;
                    vc = pr;
                    break;
                // Device rotated 180: top-left goes to bottom-right.
                case DisplayRotation.Cw180:
                    vr = PhysRows - 1 - pr;
                    vc = PhysCols - 1 - pc;
                    break;
                default:
                    vr = pr;
                    vc = pc;
                    break;
            }
            map[vr * vCols + vc] = phys;
        }
        return map;
    }

    /// <summary>Short label for the UI.</summary>
    public static string Label(DisplayRotation r) => r switch
    {
        DisplayRotation.Cw90  => "90°",
        DisplayRotation.Cw180 => "180°",
        DisplayRotation.Cw270 => "270°",
        _                     => "0°",
    };

    /// <summary>Converts the value saved in the DB ("0"/"90"/"180"/"270") into the enum.</summary>
    public static DisplayRotation Parse(string? s) => s switch
    {
        "90"  => DisplayRotation.Cw90,
        "180" => DisplayRotation.Cw180,
        "270" => DisplayRotation.Cw270,
        _     => DisplayRotation.None,
    };
}

/// <summary>
/// Rotates icon PNGs before uploading them to the firmware.
///
/// DisplayPad icons are 102x102 px (square, verified on BaseCamp
/// profiles): a 90/270 rotation is lossless and doesn't change the
/// dimensions, so no re-fit is needed.
///
/// The angle applied to the IMAGE is OPPOSITE to the device's, so that
/// (device rotation) + (icon pre-rotation) = an upright icon for whoever
/// looks at the pad mounted rotated.
///
/// Rotated files are cached on disk; the cache key includes the original
/// path, the modification date and the angle, so the cache
/// auto-invalidates whenever the source image changes.
/// </summary>
public static class IconRotator
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2.DisplayPad", "rotated");

    /// <summary>Clockwise angle to apply TO THE IMAGE to compensate for the
    /// device's mounting.</summary>
    private static int ImageAngleCw(DisplayRotation r) => r switch
    {
        DisplayRotation.Cw90  => 270, // device 90 CW  -> icon rotated 90 CCW
        DisplayRotation.Cw180 => 180, // device 180    -> icon rotated 180 (self-opposite)
        DisplayRotation.Cw270 => 90,  // device 270 CW -> icon rotated 90 CW
        _                     => 0,
    };

    /// <summary>
    /// Returns the path of a rotated PNG ready for upload.
    /// If the rotation is <see cref="DisplayRotation.None"/>, the path
    /// doesn't exist, or the rotation fails, returns the original path.
    /// </summary>
    public static string ResolveForUpload(string? originalPath, DisplayRotation r)
    {
        if (string.IsNullOrEmpty(originalPath) || !File.Exists(originalPath))
            return originalPath ?? "";

        int angle = ImageAngleCw(r);
        if (angle == 0) return originalPath;

        try
        {
            Directory.CreateDirectory(CacheDir);
            string cached = Path.Combine(CacheDir, CacheName(originalPath, angle));
            if (File.Exists(cached)) return cached;

            // Loaded via MemoryStream: this way the source file doesn't stay
            // locked and we can work on an independent copy.
            byte[] bytes = File.ReadAllBytes(originalPath);
            using (var ms  = new MemoryStream(bytes))
            using (var src = new Bitmap(ms))
            using (var bmp = new Bitmap(src))
            {
                bmp.RotateFlip(angle switch
                {
                    90  => RotateFlipType.Rotate90FlipNone,
                    180 => RotateFlipType.Rotate180FlipNone,
                    _   => RotateFlipType.Rotate270FlipNone,
                });
                bmp.Save(cached, ImageFormat.Png);
            }
            return cached;
        }
        catch
        {
            // On error, better to load the unrotated icon than to
            // leave the button empty.
            return originalPath;
        }
    }

    private static string CacheName(string path, int angle)
    {
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(path).Ticks; } catch { /* best effort */ }
        var key  = $"{path}|{mtime}|{angle}";
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(hash.Length * 2 + 8);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        sb.Append("_r").Append(angle).Append(".png");
        return sb.ToString();
    }
}
