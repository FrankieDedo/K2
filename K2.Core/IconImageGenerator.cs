using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace K2.Core;

/// <summary>
/// Generates per-key button images automatically when a key's action is "exec" (the
/// target executable's own icon, at the best resolution Windows has for it) or "folder"
/// (a flat folder glyph with the folder's name as a caption) — used so DisplayPad tiles
/// and Everest numpad display keys get a meaningful picture without the user having to
/// manually pick one. Square canvas, matches the K2 theme's dark background/accent.
/// </summary>
public static class IconImageGenerator
{
    private static readonly Color BackgroundColor = ColorTranslator.FromHtml("#1A1A1E");
    private static readonly Color AccentColor     = ColorTranslator.FromHtml("#900000");
    private const string SegoeMdl2 = "Segoe MDL2 Assets";
    private const string FolderGlyph = ""; // "OpenFolderHorizontal" glyph

    /// <summary>Renders <paramref name="execPath"/>'s associated icon centered on a size×size dark canvas, saved as PNG.</summary>
    public static bool TryGenerateExecIcon(string execPath, int size, string outputPngPath)
    {
        try
        {
            using var icon = GetBestIcon(execPath, size);
            if (icon is null) return false;

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(BackgroundColor);

                int iconSize = (int)(size * 0.72);
                int offset = (size - iconSize) / 2;
                g.DrawImage(icon, offset, offset, iconSize, iconSize);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
            canvas.Save(outputPngPath, ImageFormat.Png);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Renders a flat folder glyph + the folder's display name on a size×size dark canvas, saved as PNG.</summary>
    public static bool TryGenerateFolderIcon(string folderPath, int size, string outputPngPath)
    {
        try
        {
            string name = SafeFolderName(folderPath);

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(BackgroundColor);

                float glyphSize = size * 0.5f;
                using (var glyphFont = new Font(SegoeMdl2, glyphSize, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var glyphBrush = new SolidBrush(AccentColor))
                {
                    var glyphBounds = g.MeasureString(FolderGlyph, glyphFont);
                    float gx = (size - glyphBounds.Width) / 2f;
                    float gy = size * 0.12f;
                    g.DrawString(FolderGlyph, glyphFont, glyphBrush, gx, gy);
                }

                float labelSize = Math.Max(9f, size * 0.13f);
                using var labelFont = new Font("Segoe UI", labelSize, FontStyle.Regular, GraphicsUnit.Pixel);
                using var labelBrush = new SolidBrush(Color.White);
                var rect = new RectangleF(size * 0.06f, size * 0.68f, size * 0.88f, size * 0.28f);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.LineLimit,
                };
                g.DrawString(name, labelFont, labelBrush, rect, format);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
            canvas.Save(outputPngPath, ImageFormat.Png);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeFolderName(string folderPath)
    {
        try
        {
            var name = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return folderPath; // root paths like "C:\" have no file name
        }
        catch
        {
            return folderPath;
        }
    }

    /// <summary>
    /// Best-quality icon available for <paramref name="path"/>: tries the Shell's
    /// "jumbo" image factory (up to 256×256, sharp at any DisplayPad/numpad tile size),
    /// falling back to the small associated icon (~32×32, then upscaled) if the shell
    /// call fails (e.g. exotic file systems, missing shell extension).
    /// </summary>
    private static Bitmap? GetBestIcon(string path, int size)
    {
        try
        {
            var guid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory);
            if (factory is not null)
            {
                try
                {
                    factory.GetImage(new SIZE(size, size), SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hBitmap);
                    if (hBitmap != IntPtr.Zero)
                    {
                        try { return Image.FromHbitmap(hBitmap); }
                        finally { DeleteObject(hBitmap); }
                    }
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
        }
        catch
        {
            // Shell image factory unavailable/failed: fall through to the classic API.
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            return icon?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    // ---- Shell interop (IShellItemImageFactory::GetImage, SIIGBF_ICONONLY) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a20b1a396a3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
