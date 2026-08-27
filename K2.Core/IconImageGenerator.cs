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
    private static readonly Color DefaultBackgroundColor = Color.Black;
    private static readonly Color DefaultFolderBackgroundColor = Color.Black;

    /// <summary>Tile background — the per-key override pushed by <see cref="IconStyleScope"/>
    /// when the user picked a background color for this icon ("Edit icon"), otherwise the
    /// stock look. Read per render (a property, not the former constant field) so a scope
    /// pushed around a single TryGenerate* call is honoured without touching its signature.</summary>
    private static Color BackgroundColor => IconStyleScope.OverrideBg ?? DefaultBackgroundColor;

    /// <summary>Same as <see cref="BackgroundColor"/> for the black-tile flavours (folder,
    /// back, glyph, nav, Google Home).</summary>
    private static Color FolderBackgroundColor => IconStyleScope.OverrideBg ?? DefaultFolderBackgroundColor;

    /// <summary>Read live (not cached) so newly-generated icons follow the current
    /// Settings &gt; Accent color &gt; Icon color (default: same as the app-wide accent
    /// theme; can also be pinned to K2 Red / Mountain Blue / White independently — see
    /// AppSettings.IconColorTheme). Icons generated before a change are not
    /// retroactively repainted, same as every other one-shot GDI+ render in this class.</summary>
    private static Color AccentColor
    {
        get
        {
            var c = ResolveIconColor();
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }

    /// <summary>The tile background/accent as <see cref="LiveTileRenderer"/> sees them —
    /// the live-updating clock/monitor tiles are drawn by that class (their content changes
    /// every second, so they can't go through the cached TryGenerate* path) but must look
    /// like they came out of this one: same black rounded tile, same accent, same per-key
    /// <see cref="IconStyleScope"/> overrides.</summary>
    internal static Color TileBackground => FolderBackgroundColor;

    /// <inheritdoc cref="TileBackground"/>
    internal static Color TileAccent => AccentColor;

    /// <summary>Accent color to tint a "full color" Base Camp gallery icon with (see
    /// <see cref="TryGenerateGalleryIcon"/>). White is a fine accent for the hand-drawn glyph
    /// tiles (a light line on the dark tile background) but breaks <see cref="TintBlueHueToAccent"/>:
    /// with a white target hue/saturation are meaningless (both 0) and lightness only pushes
    /// every recolored pixel further TOWARD white, washing the icon's own shading out against
    /// the gallery art's own white highlights/background — user report 2026-08-25. Black is the
    /// closest fixed color that still reads as a real recolor (matches the tile's own background)
    /// instead of erasing the icon, so it's substituted only for this one tinting step.</summary>
    private static Color GalleryTintColor => AccentColor is { R: 255, G: 255, B: 255 } ? Color.Black : AccentColor;

    private static System.Windows.Media.Color ResolveIconColor()
    {
        string theme = AppSettings.IconColorTheme;
        if (string.IsNullOrEmpty(theme))
            return Services.AccentCatalog.Resolve(AppSettings.AccentTheme).Accent;
        if (theme == "White")
            return System.Windows.Media.Colors.White;
        return Services.AccentCatalog.Resolve(theme).Accent;
    }

    /// <summary>Same ratio as CropEditor's KeyCornerRadiusRatio (kept independent rather than
    /// shared since the two classes live in different projects/assemblies) — the physical
    /// DisplayPad tile/Everest numpad display key is a square LCD with no mechanical bezel
    /// crop, so Base Camp's rounded-icon look only exists if the corner pixels themselves are
    /// painted over with the background color. Every auto-generated icon below clips its
    /// drawing to this rounded rect (right after clearing the full square) so it matches the
    /// same baked-corner look CropEditor already applies to user-picked/cropped images.</summary>
    private const float KeyCornerRadiusRatio = 0.18f;

    /// <summary>Clips <paramref name="g"/>'s subsequent drawing to a centered size×size
    /// rounded rect — call right after <see cref="Graphics.Clear"/> (which ignores the
    /// clip region and always fills the whole bitmap) so the cut corners fall through to
    /// the background color already painted there.</summary>
    internal static void ClipToRoundedTile(Graphics g, int size)
    {
        float radius = size * KeyCornerRadiusRatio;
        using var path = RoundedRectPath(0, 0, size, size, radius);
        g.SetClip(path);
    }

    /// <summary>Builds a rounded-rectangle path — same construction as CropEditor's own
    /// private helper of the same name (GDI+ has no RadiusX/RadiusY shorthand like WPF's
    /// RectangleGeometry).</summary>
    private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

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
            // The stored "exec" value may carry the batch terminal marker — strip it.
            using var icon = GetBestIcon(ExecActionPayload.PathOf(execPath), size);
            if (icon is null) return false;

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(BackgroundColor);
                ClipToRoundedTile(g, size);

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
    /// Renders a flat folder silhouette in the K2 accent color plus <paramref name="name"/>
    /// as a caption, on a size×size black canvas, saved as PNG, upright — for a DisplayPad
    /// "page" created from the UI (action "dp_folder"): a virtual folder with no real
    /// filesystem path behind it, so there is no Windows icon to extract (see
    /// <see cref="TryGenerateDiskFolderIcon"/> for an actual on-disk folder).
    /// </summary>
    public static bool TryGenerateFolderIcon(string name, int size, string outputPngPath, bool showCaption = true)
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
                ClipToRoundedTile(g, size);

                DrawFlatFolder(g, size, centered: !showCaption);
                if (showCaption) DrawCaption(g, size, name);
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
    /// Renders the flat "back" arrow (<see cref="NavShape.Back"/>, the same shape the emoji
    /// browser's own back key uses) tinted to the K2 accent color, plus
    /// <paramref name="caption"/> as a caption, on a size×size
    /// black canvas, saved as PNG, upright — for a DisplayPad key bound to the "dp_back"
    /// action (both the explicit "Set as Back button" context-menu item and the automatic
    /// default Key #0 of a freshly-opened folder sub-page, see
    /// <c>MainWindow.DisplayPad.cs</c>'s <c>DpEnsureDefaultBackButton</c>). Same caption
    /// layout as <see cref="TryGenerateFolderIcon"/>, so a "back" tile and a "folder" tile
    /// line up.
    /// </summary>
    public static bool TryGenerateBackIcon(string caption, int size, string outputPngPath, bool showCaption = true)
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
                ClipToRoundedTile(g, size);

                var (backLeft, backTop, backSize) = IconBox(size, centered: !showCaption);
                DrawNavShape(g, new RectangleF(backLeft, backTop, backSize, backSize), NavShape.Back);
                if (showCaption) DrawCaption(g, size, caption);
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
                ClipToRoundedTile(g, size);

                using var brush = new SolidBrush(Color.White);
                var rect = new RectangleF(size * 0.08f, 0, size * 0.84f, size);
                DrawWrappedShrunkText(g, caption, rect, size * 0.16f, brush, StringAlignment.Center);
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
    /// Renders a Google Home device tile: the device's own Material icon, rasterized from
    /// home.google.com itself and cached by <see cref="Services.GoogleHomeIconCatalog"/>
    /// (already white-on-transparent, and drawn as-is — unlike the folder template it is NOT
    /// tinted to the K2 accent), plus <paramref name="caption"/> below — same <see cref="IconBox"/>/
    /// <see cref="DrawCaption"/> layout as the folder and back tiles, so a Google Home key
    /// lines up with the rest of the grid. Falls back to a caption-only tile when no glyph has
    /// been cached for the device (it never was captured, or the ligature failed to render —
    /// see <c>GoogleHomeJs.renderIcons</c>), which is why this takes an icon NAME rather than a
    /// path: "no icon" is an expected, non-exceptional case here.
    /// </summary>
    public static bool TryGenerateGoogleHomeIcon(string? iconName, string caption, int size, string outputPngPath, bool showCaption = true)
    {
        string? glyphPath = Services.GoogleHomeIconCatalog.TryGetCachedPng(iconName);
        if (glyphPath is null)
            return showCaption && TryGenerateCaptionIcon(caption, size, outputPngPath);

        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);
                ClipToRoundedTile(g, size);

                using (var glyph = LoadDetached(glyphPath))
                {
                    if (glyph is null)
                        return showCaption && TryGenerateCaptionIcon(caption, size, outputPngPath);
                    var (boxLeft, boxTop, boxSize) = IconBox(size, centered: !showCaption);
                    g.DrawImage(glyph, boxLeft, boxTop, boxSize, boxSize);
                }

                if (showCaption) DrawCaption(g, size, caption);
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
    /// Renders one of Base Camp's own pre-made gallery icons (<c>K2.App/Assets/IconGallery/**</c>,
    /// extracted from Mountain's <c>wwwroot/images/</c> — see DISTRIBUTION.md) onto a size×size
    /// tile. Unlike <see cref="TryGenerateFolderIcon"/>, the source art already IS the full
    /// tile (Mountain's own background baked in edge-to-edge, no separate caption needed) — this
    /// just letterbox-fills it and clips the baked rounded corners like every other generator.
    /// <paramref name="tintToAccent"/> runs <see cref="TintBlueHueToAccent"/> first, best-effort
    /// on every source image regardless of whether it's flat blue-on-white or mixed black/white/
    /// blue art: every gallery folder sampled during this feature's investigation shares
    /// Mountain's brand blue (#0044FF, coincidentally identical to the "Mountain Blue" accent
    /// theme) as either the background or an accent detail, so the same hue-based recolor works
    /// across the whole set instead of needing a per-folder special case.
    /// </summary>
    public static bool TryGenerateGalleryIcon(string sourceImagePath, int size, string outputPngPath, bool tintToAccent = true)
    {
        try
        {
            using var source = LoadBitmapWithRetry(sourceImagePath);
            if (source is null) return false;
            using var tinted = tintToAccent ? TintBlueHueToAccent(source, GalleryTintColor) : new Bitmap(source);

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(BackgroundColor);
                ClipToRoundedTile(g, size);
                g.DrawImage(tinted, 0, 0, size, size);
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
    /// Loads a bitmap from a file path, retrying briefly on failure — <c>new Bitmap(path)</c>
    /// can transiently throw a generic <see cref="ArgumentException"/> ("Parameter is not
    /// valid") when the file is momentarily locked by something else touching it (observed
    /// while stress-testing this method against freshly-copied files: antivirus/indexer
    /// scanning a just-written file is the usual cause). The gallery's own shipped assets are
    /// long-settled by the time a real user clicks anything, so this is mostly a defensive
    /// safety net for edge cases (a fresh install, a file the user just dropped in) rather
    /// than something expected to fire often.
    /// </summary>
    private static Bitmap? LoadBitmapWithRetry(string path, int attempts = 3, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return new Bitmap(path); }
            catch when (i < attempts - 1) { System.Threading.Thread.Sleep(delayMs); }
        }
        return null;
    }

    /// <summary>
    /// Converts a source bitmap of ANY pixel format to a fresh 32bppArgb copy via
    /// <see cref="Graphics.DrawImage(Image, int, int, int, int)"/> — deliberately NOT
    /// <c>Bitmap.Clone(Rectangle, PixelFormat)</c>, which is documented to be unreliable when
    /// converting FROM an indexed/palette format (several of the gallery's own source PNGs
    /// decode as 8bpp-indexed, e.g. "1_calc.png") and was the root cause of a fatal
    /// (uncatchable) CLR crash the first time this feature was tried against a real gallery
    /// folder — Clone-based format conversion corrupted the native heap instead of throwing a
    /// normal, catchable exception. DrawImage's blit-based conversion is the standard safe
    /// path for this.
    /// </summary>
    private static Bitmap NormalizeTo32bppArgb(Bitmap source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return copy;
    }

    /// <summary>Circular distance in degrees between two hues (0..360), e.g. 350° and 10°
    /// are 20° apart, not 340°.</summary>
    private static float HueDistance(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    /// <summary>HSL -&gt; RGB, alpha passed through unchanged. <see cref="Color.GetHue"/>/
    /// <see cref="Color.GetSaturation"/>/<see cref="Color.GetBrightness"/> are HSL-space in
    /// System.Drawing (despite "Brightness" in the name) — this is their inverse, needed
    /// because .NET has no built-in HSL constructor for <see cref="Color"/>.</summary>
    private static Color FromAhsl(byte alpha, float h, float s, float l)
    {
        static byte ToByte(float v01) => (byte)Math.Clamp(MathF.Round(v01 * 255f), 0f, 255f);

        if (s <= 0f)
        {
            byte gray = ToByte(l);
            return Color.FromArgb(alpha, gray, gray, gray);
        }

        float c = (1f - Math.Abs(2f * l - 1f)) * s;
        float hp = h / 60f;
        float x = c * (1f - Math.Abs(hp % 2f - 1f));
        (float r1, float g1, float b1) = hp switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _    => (c, 0f, x),
        };
        float m = l - c / 2f;
        return Color.FromArgb(alpha, ToByte(r1 + m), ToByte(g1 + m), ToByte(b1 + m));
    }

    /// <summary>
    /// Remaps a source pixel's HSL lightness so the "pure blue" core of an antialiased blue↔white
    /// or blue↔black blend (source L=0.5 — confirmed by construction: lightening/darkening pure
    /// Mountain blue #0044FF toward white or black keeps hue AND saturation exactly constant at
    /// 224°/1.0, only L moves) lands EXACTLY on <paramref name="accentL"/> instead of staying at
    /// 0.5 — piecewise-linear through (0→0), (0.5→accentL), (1→1) so the true white/black
    /// endpoints (and the antialiased gradient toward them) are untouched, only the "how blue"
    /// axis in between is rescaled. This is what makes a flat-blue background render as the
    /// EXACT accent RGB (e.g. K2 Red's own #900000, not a brighter same-lightness red) — a
    /// direct user request ("come rosso delle icone puoi usare proprio il #900000 di K2?").
    /// </summary>
    private static float RemapLightness(float sourceL, float accentL) =>
        sourceL <= 0.5f ? sourceL / 0.5f * accentL : accentL + (sourceL - 0.5f) / 0.5f * (1f - accentL);

    /// <summary>
    /// Recolors the blue hue band of <paramref name="source"/> to <paramref name="accent"/>'s
    /// exact hue/saturation/lightness (see <see cref="RemapLightness"/> for why lightness is
    /// remapped rather than copied verbatim) — for gallery art that mixes blue with
    /// black/white line art or other brand colors, not just flat blue-on-black. Grayscale pixels (the black/white part of the art — near-zero
    /// saturation) are naturally left untouched by the saturation gate below, no explicit
    /// black/white detection needed. See <see cref="TryGenerateGalleryIcon"/>.
    /// </summary>
    private static Bitmap TintBlueHueToAccent(Bitmap source, Color accent)
    {
        const float BlueHueCenter = 224f;   // hue of Mountain's brand blue #0044FF
        const float BlueHueTolerance = 45f; // covers anti-aliased drift toward white/black
        const float MinSaturation = 0.15f;  // gates out near-gray (black/white) pixels

        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var src32 = source.PixelFormat == PixelFormat.Format32bppArgb
            ? source : NormalizeTo32bppArgb(source);
        try
        {
            float accentHue = accent.GetHue();
            float accentSat = accent.GetSaturation();
            float accentL = accent.GetBrightness();
            var srcData = src32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(srcData.Stride) * source.Height;
                var buf = new byte[bytes];
                Marshal.Copy(srcData.Scan0, buf, 0, bytes);
                for (int i = 0; i < bytes; i += 4)
                {
                    // Format32bppArgb byte order: B,G,R,A
                    var px = Color.FromArgb(buf[i + 3], buf[i + 2], buf[i + 1], buf[i]);
                    float sat = px.GetSaturation();
                    if (sat < MinSaturation || HueDistance(px.GetHue(), BlueHueCenter) > BlueHueTolerance)
                        continue; // leave this pixel's bytes as copied from source

                    float remappedL = RemapLightness(px.GetBrightness(), accentL);
                    var recolored = FromAhsl(buf[i + 3], accentHue, accentSat, remappedL);
                    buf[i]     = recolored.B;
                    buf[i + 1] = recolored.G;
                    buf[i + 2] = recolored.R;
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

    /// <summary>Loads a PNG fully into memory — <c>Bitmap(string)</c> keeps the file locked for
    /// the bitmap's lifetime, which would block re-rendering the same cached glyph later.</summary>
    private static Bitmap? LoadDetached(string path)
    {
        try
        {
            using var lazy = new Bitmap(path);
            return new Bitmap(lazy);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Draws an arbitrary Segoe MDL2 Assets glyph, thickened, in the shared
    /// <see cref="IconBox"/> area and tinted to the accent color — used by
    /// <see cref="TryGenerateGlyphIcon"/>, i.e. by every <see cref="Services.ActionIconFallback"/>
    /// default tile. Shapes K2 draws itself (folder, back, the emoji browser's scroll keys)
    /// go through <see cref="DrawNavShape"/>/<see cref="DrawFlatFolder"/> instead.</summary>
    private static void DrawGlyph(Graphics g, int size, string glyph, bool centered = false)
    {
        var (boxLeft, boxTop, boxSize) = IconBox(size, centered);
        var rect = new RectangleF(boxLeft, boxTop, boxSize, boxSize);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var brush = new SolidBrush(AccentColor);

        // Segoe MDL2 Assets is a hairline outline font: drawn as-is at 102 px the strokes are
        // about two pixels wide and all but vanish on the panel. Taking the glyph as a PATH
        // and stroking it with a round-joined pen ON TOP of the fill thickens every stroke
        // uniformly and softens the corners, which is what gives these tiles the same flat,
        // chunky look as the hand-drawn shapes in DrawNavShape.
        try
        {
            using var family = new FontFamily("Segoe MDL2 Assets");
            using var path = new GraphicsPath();
            path.AddString(glyph, family, (int)FontStyle.Regular, boxSize * 0.72f, rect, format);
            using var pen = new Pen(AccentColor, boxSize * GlyphStrokeRatio)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        catch
        {
            // Font missing, or GDI+ refusing to hand out its outlines: a thin glyph still
            // beats an empty tile.
            using var font = new Font("Segoe MDL2 Assets", boxSize * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString(glyph, font, brush, rect, format);
        }
    }

    /// <summary>How much of the icon box the glyph-thickening stroke adds — see
    /// <see cref="DrawGlyph"/>. Tuned by eye against the busiest glyphs in
    /// <see cref="Services.ActionIconFallback"/> (calculator, keyboard): any thicker and their
    /// internal detail fills in.</summary>
    private const float GlyphStrokeRatio = 0.034f;

    /// <summary>
    /// Same tile as <see cref="TryGenerateBackIcon"/> but with an arbitrary Segoe MDL2 Assets
    /// <paramref name="glyph"/> (a one-character string) and an optional <paramref name="caption"/>
    /// below it. Used by <see cref="Services.ActionIconFallback"/> for the "no art anywhere else"
    /// default tile of an action key. Pass an empty caption for a glyph-only tile.
    /// </summary>
    public static bool TryGenerateGlyphIcon(string glyph, string caption, int size, string outputPngPath, bool showCaption = true)
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
                ClipToRoundedTile(g, size);

                bool drawCaption = showCaption && !string.IsNullOrEmpty(caption);
                DrawGlyph(g, size, glyph, centered: !drawCaption);
                if (drawCaption) DrawCaption(g, size, caption);
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

    /// <summary>Flat navigation shapes drawn by <see cref="TryGenerateNavIcon"/>.</summary>
    public enum NavShape
    {
        /// <summary>Solid left arrow (head + shaft) — "go back one level".</summary>
        Back,
        /// <summary>Thick X — "close/dismiss".</summary>
        Close,
        /// <summary>Scroll triangles. Which pair is used depends on how the panel is
        /// mounted: up/down on an unrotated 2×6 pad, left/right on a 90°/270° one.</summary>
        Up, Down, Left, Right,
    }

    /// <summary>
    /// Same tile flavour as <see cref="TryGenerateBackIcon"/> (black rounded square, accent
    /// color, optional caption below) but with a hand-drawn FLAT shape instead of an icon-font
    /// glyph: the Segoe MDL2 chevrons are thin outlines that all but vanish on a 102 px key,
    /// so the emoji browser's own navigation keys use solid shapes with softened corners
    /// (triangles for scrolling, a filled arrow for back, a thick cross for close).
    /// Pass an empty caption for a shape-only tile — it then gets a bigger, vertically
    /// centered shape instead of the caption layout's top-aligned <see cref="IconBox"/>.
    /// </summary>
    public static bool TryGenerateNavIcon(NavShape shape, string caption, int size, string outputPngPath)
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
                ClipToRoundedTile(g, size);

                RectangleF box;
                if (string.IsNullOrEmpty(caption))
                {
                    float side = size * 0.58f;
                    box = new RectangleF((size - side) / 2f, (size - side) / 2f, side, side);
                }
                else
                {
                    var (boxLeft, boxTop, boxSize) = IconBox(size);
                    box = new RectangleF(boxLeft, boxTop, boxSize, boxSize);
                    DrawCaption(g, size, caption);
                }

                DrawNavShape(g, box, shape);
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

    /// <summary>Paints one <see cref="NavShape"/> filled in the accent color inside
    /// <paramref name="box"/>. Corners are softened by stroking the same path with a
    /// round-joined pen of the same color on top of the fill — cheaper and more even than
    /// building per-corner arcs, and the stroke's half-width is why the polygons below stop
    /// short of the box edges.</summary>
    private static void DrawNavShape(Graphics g, RectangleF box, NavShape shape)
    {
        float b = Math.Min(box.Width, box.Height);
        float x = box.X, y = box.Y;
        using var brush = new SolidBrush(AccentColor);
        using var pen = new Pen(AccentColor, b * 0.13f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (shape == NavShape.Close)
        {
            using var bar = new Pen(AccentColor, b * 0.20f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(bar, x + b * 0.24f, y + b * 0.24f, x + b * 0.76f, y + b * 0.76f);
            g.DrawLine(bar, x + b * 0.76f, y + b * 0.24f, x + b * 0.24f, y + b * 0.76f);
            return;
        }

        if (shape == NavShape.Back)
        {
            // Head + shaft, so "back" stays readable next to the plain scroll triangles.
            using var head = new GraphicsPath();
            head.AddPolygon(new[]
            {
                new PointF(x + b * 0.12f, y + b * 0.50f),
                new PointF(x + b * 0.50f, y + b * 0.20f),
                new PointF(x + b * 0.50f, y + b * 0.80f),
            });
            g.FillPath(brush, head);
            g.DrawPath(pen, head);

            using var shaft = new GraphicsPath();
            shaft.AddRectangle(new RectangleF(x + b * 0.46f, y + b * 0.40f, b * 0.40f, b * 0.20f));
            g.FillPath(brush, shaft);
            g.DrawPath(pen, shaft);
            return;
        }

        PointF[] triangle = shape switch
        {
            NavShape.Up   => new[] { new PointF(x + b * 0.50f, y + b * 0.22f), new PointF(x + b * 0.84f, y + b * 0.72f), new PointF(x + b * 0.16f, y + b * 0.72f) },
            NavShape.Down => new[] { new PointF(x + b * 0.50f, y + b * 0.78f), new PointF(x + b * 0.16f, y + b * 0.28f), new PointF(x + b * 0.84f, y + b * 0.28f) },
            NavShape.Left => new[] { new PointF(x + b * 0.22f, y + b * 0.50f), new PointF(x + b * 0.72f, y + b * 0.16f), new PointF(x + b * 0.72f, y + b * 0.84f) },
            _             => new[] { new PointF(x + b * 0.78f, y + b * 0.50f), new PointF(x + b * 0.28f, y + b * 0.84f), new PointF(x + b * 0.28f, y + b * 0.16f) },
        };

        using var path = new GraphicsPath();
        path.AddPolygon(triangle);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Renders <paramref name="folderPath"/>'s own Windows Explorer icon (same
    /// shell lookup as <see cref="TryGenerateExecIcon"/> — <see cref="GetBestIcon"/>
    /// works for directories too) + its name as a caption below, on a size×size black
    /// canvas, saved as PNG, upright — for a "folder" action pointing at a real
    /// on-disk directory. Falls back to <see cref="TryGenerateFolderIcon"/>'s hand-drawn
    /// glyph if the shell can't produce an icon for the path (e.g. it no longer exists).
    /// </summary>
    public static bool TryGenerateDiskFolderIcon(string folderPath, int size, string outputPngPath, bool showCaption = true)
    {
        string name = SafeFolderName(folderPath);
        try
        {
            using var icon = GetBestIcon(folderPath, size);
            if (icon is null) return TryGenerateFolderIcon(name, size, outputPngPath, showCaption);

            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(FolderBackgroundColor);
                ClipToRoundedTile(g, size);

                var (offsetX, offsetY, iconSize) = IconBox(size, centered: !showCaption);
                g.DrawImage(icon, offsetX, offsetY, iconSize, iconSize);

                if (showCaption) DrawCaption(g, size, name);
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
    internal static void DrawCaption(Graphics g, int size, string name)
    {
        // Both the size and the color can be overridden per key from "Edit icon" — the size
        // acts as a STARTING size, since DrawWrappedShrunkText still shrinks from there when
        // the text doesn't fit the caption strip ("segue le attuali regole").
        float labelSize = (float)(IconStyleScope.OverrideFontSize ?? Math.Max(9f, size * 0.13f) + 4f);
        using var labelBrush = new SolidBrush(IconStyleScope.OverrideText ?? Color.White);
        // The user's own wording wins over whatever the generator derived (folder name,
        // device name, action summary) — see IconStyleScope.OverrideCaption.
        name = IconStyleScope.OverrideCaption ?? name;
        var rect = new RectangleF(size * 0.06f, size * 0.68f, size * 0.88f, size * 0.28f);
        DrawWrappedShrunkText(g, name, rect, labelSize, labelBrush, StringAlignment.Near);
    }

    /// <summary>
    /// Draws <paramref name="text"/> centered horizontally in <paramref name="rect"/>,
    /// word-wrapping across as many lines as fit and shrinking the font (down to
    /// <see cref="MinLabelFontSize"/>) rather than truncating with an ellipsis — a long
    /// folder/action name reads better on two smaller lines than cut short with "…".
    /// The ellipsis trimming stays wired up only as a last-resort safety net for the
    /// pathological case where even the smallest font doesn't fit (e.g. a single very
    /// long unbroken word).
    /// </summary>
    private static void DrawWrappedShrunkText(Graphics g, string text, RectangleF rect,
        float startFontSize, Brush brush, StringAlignment lineAlignment)
    {
        for (float fontSize = startFontSize; fontSize >= MinLabelFontSize; fontSize -= 1f)
        {
            using var candidate = CaptionFont(fontSize);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = lineAlignment,
                FormatFlags = StringFormatFlags.LineLimit,
            };
            SizeF measured = g.MeasureString(text, candidate, (int)rect.Width, format);
            if (measured.Height <= rect.Height)
            {
                g.DrawString(text, candidate, brush, rect, format);
                return;
            }
        }

        using var fallbackFont = CaptionFont(MinLabelFontSize);
        using var fallbackFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = lineAlignment,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };
        g.DrawString(text, fallbackFont, brush, rect, fallbackFormat);
    }

    private const float MinLabelFontSize = 7f;

    /// <summary>"Segoe UI Semibold" resolved once, or null when that family isn't installed.
    /// It has to be probed through <see cref="FontFamily"/>'s constructor (which throws for an
    /// unknown family) because <see cref="Font"/>'s constructor SILENTLY substitutes a default
    /// family instead of failing — so a plain <c>new Font("Segoe UI Semibold", ...)</c> would
    /// quietly render in Microsoft Sans Serif on a machine without it.</summary>
    private static readonly FontFamily? SemiboldFamily = TryGetFamily("Segoe UI Semibold");

    private static FontFamily? TryGetFamily(string name)
    {
        try { return new FontFamily(name); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Font used by EVERY auto-generated caption/label in this class — semibold, by
    /// explicit user request: on Windows "Segoe UI Semibold" is a separate FAMILY, not a
    /// <see cref="FontStyle"/>, so the weight can't be asked for through the style flags.
    /// Falls back to Segoe UI Bold (the closest available weight) when it isn't installed.</summary>
    internal static Font CaptionFont(float sizePx)
    {
        // Per-key font picked in "Edit icon" (see IconStyleScope); an unusable family name
        // falls through to the stock face rather than throwing mid-render.
        if (IconStyleScope.OverrideFontFamily is string family)
        {
            try { return new Font(family, sizePx, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch { }
        }
        return SemiboldFamily is not null
            ? new Font(SemiboldFamily, sizePx, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font("Segoe UI", sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    /// <summary>Icon area shared by every generated tile flavor — a real on-disk folder's
    /// Windows icon (<see cref="TryGenerateDiskFolderIcon"/>), the hand-drawn folder/nav
    /// shapes, the thickened MDL2 glyphs and a captioned emoji alike — so they all line up
    /// at the same size/position on the DisplayPad grid instead of one looking smaller or
    /// lower than the others.</summary>
    /// <summary>Pass <paramref name="centered"/> true (no caption drawn below) to vertically
    /// center the box on the tile instead of the default top-offset layout that leaves room
    /// for <see cref="DrawCaption"/>'s bottom strip.</summary>
    private static (float Left, float Top, float Size) IconBox(int size, bool centered = false)
    {
        float iconSize = size * 0.56f;
        float top = centered ? (size - iconSize) / 2f : size * 0.08f;
        return ((size - iconSize) / 2f, top, iconSize);
    }

    /// <summary>
    /// Draws a flat folder silhouette — a rounded body with a tab stub on top-left, filled in
    /// <see cref="AccentColor"/> — letterboxed into the same square <see cref="IconBox"/>
    /// <see cref="TryGenerateDiskFolderIcon"/> uses for a real folder's Windows icon, so a
    /// "page" tile and a "real folder" tile line up. Replaces the outline art K2 used to tint
    /// out of Base Camp's own <c>dp_folder_template.png</c>: that shape was a hairline outline
    /// that disappeared on a 102 px key, and hand-drawing it also drops the dependency on a
    /// Base Camp asset. Same fill+round-joined-stroke construction as
    /// <see cref="DrawNavShape"/>.
    /// </summary>
    private static void DrawFlatFolder(Graphics g, int size, bool centered = false)
    {
        var (boxLeft, boxTop, boxSize) = IconBox(size, centered);
        // The folder shape is wider than tall, so it is letterboxed in the square icon box.
        float w = boxSize, h = boxSize * 0.80f;
        float x = boxLeft, y = boxTop + (boxSize - h) / 2f;

        float stroke = h * 0.13f;
        float inset = stroke / 2f;                 // the stroke grows the shape outwards
        float tabH = h * 0.18f;

        using var brush = new SolidBrush(AccentColor);
        using var pen = new Pen(AccentColor, stroke)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // Tab first, body second: the body's own rounded corners then win where they overlap.
        // Tab length: this is the RECT, and the round-joined stroke below grows it by a
        // further half-stroke on each side, so the drawn tab is wider than the rect —
        // 0.32 draws ~24 px of the 57 px shape at the DisplayPad's 102 px tile. Tuned
        // against the real panel over three rounds of user feedback (2026-08-23):
        // 0.42 (~53%, read as a second slab) -> 0.22 (18 px, too stubby) -> 0.27
        // (21 px, still short) -> 0.32.
        using (var tab = new GraphicsPath())
        {
            tab.AddRectangle(new RectangleF(x + inset, y + inset, w * 0.32f, tabH * 2f));
            g.FillPath(brush, tab);
            g.DrawPath(pen, tab);
        }
        using (var body = new GraphicsPath())
        {
            body.AddRectangle(new RectangleF(x + inset, y + tabH + inset, w - stroke, h - tabH - stroke));
            g.FillPath(brush, body);
            g.DrawPath(pen, body);
        }
    }

    /// <summary>The exact caption <see cref="TryGenerateDiskFolderIcon"/> bakes into the tile
    /// for a real on-disk folder — exposed so the key-config dialogs can prefill "Add/Edit
    /// text" with it instead of stacking new text on top of the already-captioned default
    /// icon.</summary>
    public static string GetDiskFolderCaption(string folderPath) => SafeFolderName(folderPath);

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
