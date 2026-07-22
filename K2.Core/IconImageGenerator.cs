using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace K2.Core;

/// <summary>
/// Generates per-key button images automatically when a key's action is "exec" (the
/// target executable's own icon, at the best resolution Windows has for it), "folder"
/// (a real on-disk folder — Windows' own Explorer icon for it, plus its name as a
/// caption, see <see cref="TryGenerateDiskFolderIcon"/>), or a DisplayPad "page" created
/// from the UI (a virtual folder with no filesystem path — a hand-drawn folder glyph,
/// see <see cref="TryGenerateFolderIcon"/>) — used so DisplayPad tiles and Everest
/// numpad display keys get a meaningful picture without the user having to manually
/// pick one. Square canvas, matches the K2 theme's dark background/accent.
/// </summary>
public static class IconImageGenerator
{
    private static readonly Color BackgroundColor = ColorTranslator.FromHtml("#1A1A1E");
    private static readonly Color FolderBackgroundColor = Color.Black;
    private static readonly Color AccentColor     = ColorTranslator.FromHtml("#900000");

    /// <summary>
    /// Renders <paramref name="execPath"/>'s associated icon centered on a size×size
    /// dark canvas, saved as PNG, upright — same convention as any other image file in
    /// K2: the device's physical-mounting counter-rotation is applied later, at upload
    /// time, not baked in here (see <see cref="TryGenerateFolderIcon"/>).
    /// </summary>
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

    /// <summary>
    /// Renders Base Camp's own DisplayPad folder tile art (<c>Assets/dp_folder_template.png</c>,
    /// embedded — see <see cref="LoadFolderTemplate"/>) tinted to the K2 accent color, plus
    /// <paramref name="name"/> as a caption, on a size×size black canvas, saved as PNG,
    /// upright — for a DisplayPad "page" created from the UI (action "dp_folder"): a
    /// virtual folder with no real filesystem path behind it, so there is no Windows icon
    /// to extract (see <see cref="TryGenerateDiskFolderIcon"/> for an actual on-disk
    /// folder). Falls back to a plain caption-only tile if the template can't be loaded.
    /// </summary>
    public static bool TryGenerateFolderIcon(string name, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);

                DrawFolderTemplate(g, size);
                DrawCaption(g, size, name);
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

    /// <summary>
    /// Renders a hand-drawn "back" glyph (Segoe MDL2 Assets, same icon-font already used for
    /// every other chrome glyph in K2 — see <c>K2Theme.xaml</c>'s <c>K2IconButton</c>) tinted
    /// to the K2 accent color, plus <paramref name="caption"/> as a caption, on a size×size
    /// black canvas, saved as PNG, upright — for a DisplayPad key bound to the "dp_back"
    /// action (both the explicit "Set as Back button" context-menu item and the automatic
    /// default Key #0 of a freshly-opened folder sub-page, see
    /// <c>MainWindow.DisplayPad.cs</c>'s <c>DpEnsureDefaultBackButton</c>). Same caption
    /// layout as <see cref="TryGenerateFolderIcon"/>, so a "back" tile and a "folder" tile
    /// line up.
    /// </summary>
    public static bool TryGenerateBackIcon(string caption, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);

                DrawBackGlyph(g, size);
                DrawCaption(g, size, caption);
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

    /// <summary>
    /// Plain caption-only tile (no glyph): black canvas with <paramref name="caption"/>
    /// centered, larger than <see cref="DrawCaption"/>'s bottom-strip layout since there's
    /// no icon above it competing for space. Used for auto-populated action keys (e.g. the
    /// Spotify profile's media-control tiles) where a real glyph-per-action lookup would be
    /// overkill — the label alone is enough to identify the button.
    /// </summary>
    public static bool TryGenerateCaptionIcon(string caption, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);

                using var font = new Font("Segoe UI", size * 0.16f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.White);
                var rect = new RectangleF(size * 0.08f, 0, size * 0.84f, size);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                };
                g.DrawString(caption, font, brush, rect, format);
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

    /// <summary>Draws the Segoe MDL2 Assets "Back" glyph (U+E72B) centered in the same
    /// square icon area <see cref="TryGenerateFolderIcon"/>/<see cref="TryGenerateDiskFolderIcon"/>
    /// use, so all three auto-generated tile flavors align.</summary>
    private static void DrawBackGlyph(Graphics g, int size)
    {
        var (boxLeft, boxTop, boxSize) = IconBox(size);
        using var font = new Font("Segoe MDL2 Assets", boxSize * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(AccentColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var rect = new RectangleF(boxLeft, boxTop, boxSize, boxSize);
        g.DrawString("", font, brush, rect, format);
    }

    /// <summary>
    /// Renders <paramref name="folderPath"/>'s own Windows Explorer icon (same
    /// shell lookup as <see cref="TryGenerateExecIcon"/> — <see cref="GetBestIcon"/>
    /// works for directories too) + its name as a caption below, on a size×size black
    /// canvas, saved as PNG, upright — for a "folder" action pointing at a real
    /// on-disk directory. Falls back to <see cref="TryGenerateFolderIcon"/>'s hand-drawn
    /// glyph if the shell can't produce an icon for the path (e.g. it no longer exists).
    /// </summary>
    public static bool TryGenerateDiskFolderIcon(string folderPath, int size, string outputPngPath)
    {
        string name = SafeFolderName(folderPath);
        try
        {
            using var icon = GetBestIcon(folderPath, size);
            if (icon is null) return TryGenerateFolderIcon(name, size, outputPngPath);

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);

                var (offsetX, offsetY, iconSize) = IconBox(size);
                g.DrawImage(icon, offsetX, offsetY, iconSize, iconSize);

                DrawCaption(g, size, name);
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

    /// <summary>Folder name caption, centered below the icon area — shared layout
    /// between <see cref="TryGenerateFolderIcon"/> and <see cref="TryGenerateDiskFolderIcon"/>.</summary>
    private static void DrawCaption(Graphics g, int size, string name)
    {
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

    /// <summary>Tight crop of the shape within <c>dp_folder_template.png</c>'s 294×294
    /// canvas (measured once from the source asset, plus a little anti-aliasing padding) —
    /// everything outside this is empty black margin.</summary>
    private static readonly Rectangle FolderTemplateCrop = new(56, 76, 182, 148);

    /// <summary>Icon area shared by both folder-tile flavors — a real on-disk folder's
    /// Windows icon (<see cref="TryGenerateDiskFolderIcon"/>) and this hand-tinted
    /// template (<see cref="TryGenerateFolderIcon"/>) — so a "page" tile and a "real
    /// folder" tile line up at the same size/position on the DisplayPad grid instead of
    /// one looking smaller/lower than the other.</summary>
    private static (float Left, float Top, float Size) IconBox(int size)
    {
        float iconSize = size * 0.56f;
        return ((size - iconSize) / 2f, size * 0.08f, iconSize);
    }

    /// <summary>
    /// Draws Base Camp's own folder-tile art (see <see cref="LoadFolderTemplate"/>),
    /// cropped to its shape and tinted to <see cref="AccentColor"/>, into the same
    /// square <see cref="IconBox"/> <see cref="TryGenerateDiskFolderIcon"/> uses for a
    /// real folder's Windows icon (letterboxed within it, since the folder shape itself
    /// is wider than tall) — see <see cref="TryGenerateFolderIcon"/>.
    /// </summary>
    private static void DrawFolderTemplate(Graphics g, int size)
    {
        using var template = LoadFolderTemplate();
        if (template is null) return; // caption-only fallback — see TryGenerateFolderIcon

        using var cropped = template.Clone(FolderTemplateCrop, template.PixelFormat);
        using var tinted = TintFromBlueChannel(cropped, AccentColor);

        var (boxLeft, boxTop, boxSize) = IconBox(size);

        float shapeAspect = (float)FolderTemplateCrop.Width / FolderTemplateCrop.Height;
        float drawW = boxSize, drawH = drawW / shapeAspect;
        if (drawH > boxSize) { drawH = boxSize; drawW = drawH * shapeAspect; }
        float drawX = boxLeft + (boxSize - drawW) / 2f;
        float drawY = boxTop + (boxSize - drawH) / 2f;

        g.DrawImage(tinted, drawX, drawY, drawW, drawH);
    }

    /// <summary>Loads the embedded <c>Assets/dp_folder_template.png</c> (Base Camp's own
    /// DisplayPad folder-tile art — solid black background, the shape drawn in a fixed
    /// blue), or null if the resource can't be found/read.</summary>
    private static Bitmap? LoadFolderTemplate()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("K2.Core.Assets.dp_folder_template.png");
            if (stream is null) return null;

            // Bitmap(Stream) can lazily reference the stream for later pixel access
            // (a well-known GDI+ gotcha) — copy-construct to fully detach before the
            // `using` above disposes it out from under the caller.
            using var lazy = new Bitmap(stream);
            return new Bitmap(lazy);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recolors a black-background/solid-blue source image to black-background/<paramref name="accent"/>,
    /// using the source's own blue channel as a per-pixel coverage mask (it varies smoothly
    /// from 0 at the background to 255 at the shape's fill, including anti-aliased edges) —
    /// avoids depending on alpha transparency, which <c>dp_folder_template.png</c> doesn't have.
    /// </summary>
    private static Bitmap TintFromBlueChannel(Bitmap source, Color accent)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var src32 = source.PixelFormat == PixelFormat.Format32bppArgb
            ? source : source.Clone(rect, PixelFormat.Format32bppArgb);
        try
        {
            var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(srcData.Stride) * source.Height;
                var buf = new byte[bytes];
                Marshal.Copy(srcData.Scan0, buf, 0, bytes);
                for (int i = 0; i < bytes; i += 4)
                {
                    double coverage = buf[i] / 255.0; // Format32bppArgb byte order: B,G,R,A
                    buf[i]     = (byte)(accent.B * coverage);
                    buf[i + 1] = (byte)(accent.G * coverage);
                    buf[i + 2] = (byte)(accent.R * coverage);
                    buf[i + 3] = 255;
                }
                Marshal.Copy(buf, 0, dstData.Scan0, bytes);
            }
            finally
            {
                src32.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }
        }
        finally
        {
            if (!ReferenceEquals(src32, source)) src32.Dispose();
        }
        return result;
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
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
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
