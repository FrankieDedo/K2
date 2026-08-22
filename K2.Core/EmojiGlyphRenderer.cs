using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.Core;

/// <summary>
/// Renders a single emoji in FULL COLOR, both as a WPF <see cref="ImageSource"/> (emoji
/// picker / previews) and as a PNG key image (display keys — DisplayPad tiles and Everest
/// Max numpad display keys alike).
///
/// Why a hand-rolled font renderer instead of just drawing the character with a font:
/// neither GDI+ (<c>Graphics.DrawString</c>) nor WPF (<c>FormattedText</c>) supports OpenType
/// COLR/CPAL color fonts, so both draw Segoe UI Emoji's MONOCHROME outline layer only —
/// verified empirically before writing this (a rendered 😀 came out with zero saturated
/// pixels in both stacks). Since a black-and-white emoji on a key defeats the point, this
/// parses the two color tables out of <c>seguiemj.ttf</c> directly:
/// <list type="bullet">
///   <item><c>COLR</c> (v0) maps a base glyph to an ordered list of (layer glyph, palette
///   index) pairs — the same glyph drawn several times, one flat color each.</item>
///   <item><c>CPAL</c> holds the palettes those indices point into (BGRA records).</item>
/// </list>
/// The layer OUTLINES themselves still come from WPF (<see cref="GlyphTypeface.GetGlyphOutline"/>),
/// so only the two color tables needed parsing.
///
/// Scope: single-codepoint emoji only. Multi-codepoint sequences (ZWJ families, skin-tone
/// modifiers, country flags) resolve to their composed glyph through the font's GSUB table,
/// which is not parsed here — <see cref="EmojiCatalog"/> is filtered to single-scalar emoji
/// for exactly that reason, so the picker can never offer something this can't draw.
/// </summary>
public static class EmojiGlyphRenderer
{
    /// <summary>Tile background of every generated key PNG — same value as
    /// <see cref="IconImageGenerator"/>'s (that one is a GDI+ <c>System.Drawing.Color</c>,
    /// this one a WPF <c>Media.Color</c>, hence the duplicate rather than a shared constant).</summary>
    private static readonly Color TileBackground = Color.FromRgb(0x1A, 0x1A, 0x1E);

    /// <summary>Same ratio as <see cref="IconImageGenerator"/>'s <c>KeyCornerRadiusRatio</c> —
    /// keeps an emoji tile's baked rounded corners identical to every other generated icon.</summary>
    private const double KeyCornerRadiusRatio = 0.18;

    /// <summary>Fraction of the tile the emoji itself fills (the rest is breathing room, so a
    /// square-ish emoji doesn't touch the baked rounded corners).</summary>
    private const double GlyphFillRatio = 0.82;

    /// <summary>Design-em size the layer outlines are requested at — arbitrary, since
    /// everything is rescaled to the tile afterwards; big enough to keep the geometry's
    /// bounds precise.</summary>
    private const double OutlineEmSize = 100.0;

    // ---- font/table state (loaded once, then reused) ---------------------

    private static readonly object Sync = new();
    private static bool _loaded;
    private static byte[]? _font;
    private static GlyphTypeface? _typeface;
    private static uint _colrOffset;
    private static Color[]? _palette;
    private static readonly Dictionary<int, DrawingGroup?> DrawingCache = new();

    /// <summary>True when Segoe UI Emoji was found and carries usable COLR/CPAL tables —
    /// false on a machine where the font is missing or is an unexpected build, in which case
    /// every method below degrades to "no emoji art" rather than throwing.</summary>
    public static bool IsAvailable
    {
        get { EnsureLoaded(); return _typeface is not null && _palette is not null; }
    }

    /// <summary>The Unicode scalar behind <paramref name="emoji"/> (surrogate pair aware),
    /// with a trailing VARIATION SELECTOR-16 (U+FE0F, the "render as emoji" hint) ignored —
    /// stored action values may or may not carry it depending on where they came from.
    /// Returns null when the string isn't exactly one scalar.</summary>
    public static int? SingleCodepoint(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        string s = emoji.Replace("\uFE0F", "");
        if (s.Length == 0) return null;
        try
        {
            int cp = char.ConvertToUtf32(s, 0);
            int consumed = char.IsHighSurrogate(s[0]) ? 2 : 1;
            return s.Length == consumed ? cp : null;
        }
        catch (ArgumentException) { return null; }   // lone/unpaired surrogate
    }

    /// <summary>True when this emoji has color art available — what
    /// <see cref="EmojiCatalog"/> filters its entries by, so an emoji the installed font
    /// build doesn't cover never shows up in the picker.</summary>
    public static bool HasColorGlyph(int codepoint) => GetDrawing(codepoint) is not null;

    /// <summary>Frozen <see cref="ImageSource"/> for UI use (picker grid cells, previews), or
    /// null when there's no color art. A <see cref="DrawingImage"/> — vector, so one cached
    /// instance renders crisply at any cell size.</summary>
    public static ImageSource? TryGetImage(string? emoji)
    {
        int? cp = SingleCodepoint(emoji);
        if (cp is null) return null;
        var drawing = GetDrawing(cp.Value);
        if (drawing is null) return null;

        var img = new DrawingImage(drawing);
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Writes <paramref name="emoji"/> centered on a size×size dark tile as PNG — the key
    /// image for a display key bound to an "emoji" action. Same contract as every
    /// <c>IconImageGenerator.TryGenerate*</c> method: false (caller keeps whatever image it
    /// already had) rather than an exception when the emoji has no color art.
    /// </summary>
    /// <remarks>Must be called from an STA thread — <see cref="RenderTargetBitmap"/> is a WPF
    /// object; every call site is a dialog running on the UI thread.</remarks>
    public static bool TryGenerateEmojiIcon(string? emoji, int size, string outputPngPath)
    {
        try
        {
            int? cp = SingleCodepoint(emoji);
            if (cp is null || size <= 0) return false;
            var drawing = GetDrawing(cp.Value);
            if (drawing is null) return false;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Paint the full square first, then clip to the rounded tile so the corner
                // pixels themselves carry the background color — same baked-corner look
                // CropEditor bakes into user-picked images (see IconImageGenerator).
                dc.DrawRectangle(new SolidColorBrush(TileBackground), null, new Rect(0, 0, size, size));
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, size, size),
                    size * KeyCornerRadiusRatio, size * KeyCornerRadiusRatio));
                DrawCentered(dc, drawing, size);
                dc.Pop();
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            string? dir = Path.GetDirectoryName(outputPngPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(outputPngPath);
            encoder.Save(fs);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Scales <paramref name="drawing"/> to <see cref="GlyphFillRatio"/> of the tile
    /// and centers it on the glyph's own ink bounds — glyph outlines sit on a baseline with
    /// generous side bearings, so centering the raw box would leave the emoji visibly high.</summary>
    private static void DrawCentered(DrawingContext dc, DrawingGroup drawing, double size)
    {
        Rect b = drawing.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        double scale = Math.Min(size * GlyphFillRatio / b.Width, size * GlyphFillRatio / b.Height);
        dc.PushTransform(new TranslateTransform(
            size / 2 - (b.X + b.Width / 2) * scale,
            size / 2 - (b.Y + b.Height / 2) * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawDrawing(drawing);
        dc.Pop();
        dc.Pop();
    }

    // =====================================================================
    // COLR / CPAL
    // =====================================================================

    /// <summary>The layered color art for one codepoint (cached, frozen), or null when the
    /// font has no glyph for it / no COLR entry (a plain non-emoji character).</summary>
    private static DrawingGroup? GetDrawing(int codepoint)
    {
        lock (Sync)
        {
            if (DrawingCache.TryGetValue(codepoint, out var cached)) return cached;

            DrawingGroup? built = null;
            try { built = BuildDrawing(codepoint); }
            catch (Exception) { built = null; }   // malformed/unexpected font build

            DrawingCache[codepoint] = built;
            return built;
        }
    }

    private static DrawingGroup? BuildDrawing(int codepoint)
    {
        EnsureLoaded();
        if (_font is null || _typeface is null || _palette is null) return null;
        if (!_typeface.CharacterToGlyphMap.TryGetValue(codepoint, out ushort baseGlyph)) return null;

        var layers = ReadLayers(_font, _colrOffset, baseGlyph);
        if (layers.Count == 0) return null;

        var group = new DrawingGroup();
        foreach (var (layerGlyph, paletteIndex) in layers)
        {
            Geometry outline;
            try { outline = _typeface.GetGlyphOutline(layerGlyph, OutlineEmSize, 1.0); }
            catch (Exception) { continue; }
            if (outline.IsEmpty()) continue;

            // 0xFFFF is COLR's "use the text foreground color" sentinel — white here, the
            // only sensible choice on K2's dark tiles.
            Color color = paletteIndex == 0xFFFF || paletteIndex >= _palette.Length
                ? Colors.White : _palette[paletteIndex];

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, outline));
        }

        if (group.Children.Count == 0) return null;
        group.Freeze();
        return group;
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;
            try { Load(); }
            catch (Exception) { _font = null; _typeface = null; _palette = null; }
        }
    }

    private static void Load()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        if (!File.Exists(path)) return;

        byte[] font = File.ReadAllBytes(path);
        var tables = ReadTableDirectory(font);
        if (!tables.TryGetValue("COLR", out uint colr) || !tables.TryGetValue("CPAL", out uint cpal))
            return;

        _typeface   = new GlyphTypeface(new Uri(path));
        _font       = font;
        _colrOffset = colr;
        _palette    = ReadPalette(font, cpal);
    }

    /// <summary>sfnt table directory: 12-byte header (table count at offset 4) followed by
    /// 16-byte records of tag/checksum/offset/length. Only each table's offset is kept.</summary>
    private static Dictionary<string, uint> ReadTableDirectory(byte[] f)
    {
        var tables = new Dictionary<string, uint>(StringComparer.Ordinal);
        int count = U16(f, 4);
        for (int i = 0; i < count; i++)
        {
            int rec = 12 + i * 16;
            if (rec + 16 > f.Length) break;
            tables[Encoding.ASCII.GetString(f, rec, 4)] = U32(f, rec + 8);
        }
        return tables;
    }

    /// <summary>COLR v0: the base-glyph records are sorted by glyph id (spec requirement),
    /// so this binary-searches them and returns the referenced slice of the layer array.
    /// Returns empty for a glyph with no color layers.</summary>
    private static List<(ushort Glyph, ushort Palette)> ReadLayers(byte[] f, uint colr, ushort glyph)
    {
        var result = new List<(ushort, ushort)>();
        int o = (int)colr;
        int numBaseRecords = U16(f, o + 2);
        uint baseRecordsOffset  = U32(f, o + 4);
        uint layerRecordsOffset = U32(f, o + 8);

        int lo = 0, hi = numBaseRecords - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int rec = (int)(colr + baseRecordsOffset) + mid * 6;
            ushort g = (ushort)U16(f, rec);
            if (g < glyph) { lo = mid + 1; continue; }
            if (g > glyph) { hi = mid - 1; continue; }

            int first = U16(f, rec + 2), count = U16(f, rec + 4);
            for (int i = 0; i < count; i++)
            {
                int lr = (int)(colr + layerRecordsOffset) + (first + i) * 4;
                result.Add(((ushort)U16(f, lr), (ushort)U16(f, lr + 2)));
            }
            return result;
        }
        return result;
    }

    /// <summary>CPAL palette 0 (the default palette — Segoe UI Emoji ships exactly one),
    /// whose color records are BGRA bytes.</summary>
    private static Color[] ReadPalette(byte[] f, uint cpal)
    {
        int o = (int)cpal;
        int entries = U16(f, o + 2);
        uint recordsOffset = U32(f, o + 8);
        int firstIndex = U16(f, o + 12);     // colorRecordIndices[0]

        var palette = new Color[entries];
        for (int i = 0; i < entries; i++)
        {
            int p = (int)(cpal + recordsOffset) + (firstIndex + i) * 4;
            palette[i] = Color.FromArgb(f[p + 3], f[p + 2], f[p + 1], f[p]);
        }
        return palette;
    }

    private static int  U16(byte[] f, int o) => (f[o] << 8) | f[o + 1];
    private static uint U32(byte[] f, int o) =>
        ((uint)f[o] << 24) | ((uint)f[o + 1] << 16) | ((uint)f[o + 2] << 8) | f[o + 3];
}
