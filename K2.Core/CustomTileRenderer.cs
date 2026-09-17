using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace K2.Core;

/// <summary>One piece of a tile, resolved: what it is and where it goes (its
/// <see cref="Element"/>) plus the value it ended up showing. The caller (K2.App, which owns the
/// probes and the profiles) does the reading and the colour inheritance; the painter just
/// draws.</summary>
public sealed record TilePiece
{
    public TileElement Element { get; init; } = new();

    /// <summary>What a value or a label prints.</summary>
    public string Text { get; init; } = "";

    /// <summary>0..1 for an indicator, or null for "no reading" — it then draws its empty track,
    /// because a tile must never fake a confident zero.</summary>
    public double? Fraction { get; init; }

    /// <summary>The PNG a picture wears (the element's own, or the active state's).</summary>
    public string? IconPath { get; init; }

    /// <summary>Ink for text, fill for an indicator — already resolved from the element's own
    /// colour or the profile's.</summary>
    public Color Colour { get; init; } = Color.White;
}

/// <summary>Everything one custom tile needs to be painted, already resolved: no store lookups, no
/// probe reading, no inheritance left to apply.</summary>
public sealed record CustomTilePaint
{
    public Color Background { get; init; } = Color.FromArgb(11, 11, 13);

    /// <summary>Drawn over <see cref="Background"/> when it loads. The tile's BACKGROUND — never
    /// an element: it does not move, does not resize and is always at the back.</summary>
    public string? BackgroundImagePath { get; init; }

    /// <summary>The pieces, in drawing order — the last one is on top.</summary>
    public IReadOnlyList<TilePiece> Pieces { get; init; } = Array.Empty<TilePiece>();
}

/// <summary>
/// Painter for the tiles the user builds in the game studio (<c>dp_custom</c>).
///
/// <para>
/// Separate from <see cref="LiveTileRenderer"/> because the two answer different questions. That
/// one draws K2's OWN tiles: their layout is a design decision, fixed, and only the number in the
/// middle changes. This one draws a tile whose layout IS the user's input — how many pieces, of
/// what kind, where, how big, which on top.
/// </para>
///
/// <para>
/// <b><see cref="Layout"/> is the single source of truth for geometry.</b> The painter draws from
/// it and the studio's preview drags from it, so the box the user grabs is the box the pad will
/// paint.
/// </para>
/// </summary>
public static class CustomTileRenderer
{
    /// <summary>Renders one custom tile to <paramref name="outputPngPath"/>. Never throws: a tile
    /// that cannot be drawn returns false and the caller keeps whatever was on the key.</summary>
    public static bool TryRender(CustomTilePaint paint, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                DrawBackground(g, paint, size);

                foreach (var (piece, rect) in Layout(paint, size))
                {
                    switch (piece.Element.Kind)
                    {
                        case TileElementKind.Icon:
                            DrawIcon(g, piece.IconPath, rect);
                            break;
                        case TileElementKind.Indicator:
                            DrawIndicator(g, piece, rect);
                            break;
                        default:
                            DrawFitted(g, piece.Text, rect, size * (piece.Element.Kind == TileElementKind.Value ? 0.30f : 0.20f),
                                       piece.Colour, AlignmentFor(piece.Element),
                                       piece.Element.FontFamily, piece.Element.FontSize);
                            break;
                    }
                }
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Where each piece lands, in pixels of a tile of <paramref name="size"/>, in drawing order.
    /// A piece with nothing to show — a picture with no PNG, an indicator with no shape, empty
    /// text — is left out entirely.
    /// </summary>
    public static IReadOnlyList<(TilePiece Piece, RectangleF Rect)> Layout(CustomTilePaint paint, int size)
    {
        var drawn = paint.Pieces.Where(HasSomethingToShow).ToList();
        var result = new List<(TilePiece, RectangleF)>(drawn.Count);

        foreach (var piece in drawn)
        {
            var element = piece.Element;
            var rect = element.Kind switch
            {
                TileElementKind.Icon => Place(element, size, IconFill, IconFill),
                TileElementKind.Indicator => IndicatorRect(element, size),
                _ => Place(element, size, TextBandWidth, TextBandHeight),
            };

            // Text pieces that sit on the SAME snap point, none of them resized, share that band
            // instead of printing on top of each other: the first takes the top slice, the next
            // the one below it. That is what makes a reading with a label under it — the way these
            // tiles have always read — come out of two independent elements.
            if (element.Kind is TileElementKind.Value or TileElementKind.Label &&
                element.Snap && element.Box is null)
            {
                var stack = drawn
                    .Where(p => p.Element.Kind is TileElementKind.Value or TileElementKind.Label
                                && p.Element.Snap && p.Element.Box is null
                                && p.Element.Anchor == element.Anchor
                                && Math.Abs(p.Element.Margin - element.Margin) < 0.0001)
                    .ToList();

                if (stack.Count > 1)
                {
                    int slot = stack.IndexOf(piece);
                    float sliceH = rect.Height / stack.Count;
                    rect = new RectangleF(rect.X, rect.Y + slot * sliceH, rect.Width, sliceH);
                }
            }

            result.Add((piece, rect));
        }

        return result;
    }

    private static bool HasSomethingToShow(TilePiece piece) => piece.Element.Kind switch
    {
        TileElementKind.Icon => !string.IsNullOrWhiteSpace(piece.IconPath),
        TileElementKind.Indicator => piece.Element.Shape != CustomIndicatorShape.None,
        _ => (piece.Text ?? "").Length > 0,
    };

    /// <summary>Parses <c>#RRGGBB</c> (or <c>#AARRGGBB</c>), falling back to
    /// <paramref name="fallback"/> for anything else — an empty field, a half-typed value in the
    /// studio's colour box. The studio previews on every keystroke, so this is on the hot path of
    /// someone typing and must not throw.</summary>
    public static Color ParseColor(string? hex, Color fallback)
    {
        string s = (hex ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
        if (s.Length == 6 &&
            int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        if (s.Length == 8 &&
            uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
            return Color.FromArgb((int)argb);
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ─────────────────────────── placement ───────────────────────────

    /// <summary>The rectangle a piece occupies: its size (dragged or default) put where its
    /// placement says — on one of the nine points, kept <see cref="TileElement.Margin"/> from the
    /// edge, or free at its own centre when snapping is off.</summary>
    private static RectangleF Place(TileElement element, int size,
                                    float defaultWidthShare, float defaultHeightShare)
    {
        float w = Sized(element.Box?.Width, size, defaultWidthShare);
        float h = Sized(element.Box?.Height, size, defaultHeightShare);
        var (cx, cy) = Centre(element, size, w, h);
        return new RectangleF(cx - w / 2f, cy - h / 2f, w, h);
    }

    private static (float X, float Y) Centre(TileElement element, int size, float w, float h)
    {
        if (!element.Snap)
            return ((float)(element.X * size), (float)(element.Y * size));

        float margin = size * (float)Math.Clamp(element.Margin, 0d, 0.45d);
        float left = margin + w / 2f, right = size - margin - w / 2f;
        float top = margin + h / 2f, bottom = size - margin - h / 2f;
        float midX = size / 2f, midY = size / 2f;

        // A box wider than the tile has nowhere to go: centre it rather than pushing it out on one
        // side, which is what the clamped edges would otherwise do.
        if (left > right) left = right = midX;
        if (top > bottom) top = bottom = midY;

        float x = element.Anchor switch
        {
            TileAnchor.TopLeft or TileAnchor.MiddleLeft or TileAnchor.BottomLeft => left,
            TileAnchor.TopRight or TileAnchor.MiddleRight or TileAnchor.BottomRight => right,
            _ => midX,
        };
        float y = element.Anchor switch
        {
            TileAnchor.TopLeft or TileAnchor.TopCenter or TileAnchor.TopRight => top,
            TileAnchor.BottomLeft or TileAnchor.BottomCenter or TileAnchor.BottomRight => bottom,
            _ => midY,
        };
        return (x, y);
    }

    /// <summary>A dragged size in tile fractions turned into pixels, or the renderer's own share of
    /// the tile when the user never set one. Clamped so a box cannot be dragged down to nothing
    /// (nor past the tile), which would leave a piece that can't be grabbed again.</summary>
    private static float Sized(double? fraction, int size, float fallbackShare) =>
        fraction is { } f && f > 0
            ? size * (float)Math.Clamp(f, 0.06d, 1.0d)
            : size * fallbackShare;

    private static StringAlignment AlignmentFor(TileElement element) => !element.Snap
        ? StringAlignment.Center
        : element.Anchor switch
        {
            TileAnchor.TopLeft or TileAnchor.MiddleLeft or TileAnchor.BottomLeft => StringAlignment.Near,
            TileAnchor.TopRight or TileAnchor.MiddleRight or TileAnchor.BottomRight => StringAlignment.Far,
            _ => StringAlignment.Center,
        };

    // ─────────────────────────── the layers ───────────────────────────

    private static void DrawBackground(Graphics g, CustomTilePaint paint, int size)
    {
        using (var brush = new SolidBrush(paint.Background))
            g.FillRectangle(brush, 0, 0, size, size);

        if (LoadImage(paint.BackgroundImagePath) is not { } bg) return;
        using (bg) g.DrawImage(bg, new Rectangle(0, 0, size, size));
    }

    /// <summary>How much of the key a picture covers by default. The margin keeps it clear of the
    /// tile's edge, where a DisplayPad key's own bezel eats a pixel or two.</summary>
    private const float IconFill = 0.72f;

    private static void DrawIcon(Graphics g, string? path, RectangleF rect)
    {
        if (LoadImage(path) is not { } icon) return;
        using (icon)
        {
            // Aspect preserved: a wide icon squashed into a square is the one thing that makes a
            // hand-made tile look broken next to K2's own.
            float scale = Math.Min(rect.Width / icon.Width, rect.Height / icon.Height);
            float w = icon.Width * scale, h = icon.Height * scale;
            g.DrawImage(icon, rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
        }
    }

    /// <summary>Thickness of a linear bar and of the ring, as a share of the tile.</summary>
    private const float BarThickness = 0.11f;

    /// <summary>Length of a linear bar, as a share of the tile.</summary>
    private const float BarLength = 0.74f;

    /// <summary>Diameter of the ring, as a share of the tile. Smaller than the tile so a ring on a
    /// corner anchor still fits inside the key.</summary>
    private const float RingSize = 0.52f;

    /// <summary>Alpha of the empty part of an indicator. Drawn always — an empty track is how the
    /// tile shows there IS a scale, which is what tells "0%" apart from "no reading".</summary>
    private const int TrackAlpha = 70;

    /// <summary>Default share of the tile a band of text takes.</summary>
    private const float TextBandHeight = 0.34f;
    private const float TextBandWidth = 0.88f;

    private static RectangleF IndicatorRect(TileElement element, int size)
    {
        if (element.Shape == CustomIndicatorShape.Circular)
            return Place(element, size, RingSize, RingSize);

        bool horizontal = element.Shape == CustomIndicatorShape.LinearHorizontal;
        return Place(element, size,
                     horizontal ? BarLength : BarThickness,
                     horizontal ? BarThickness : BarLength);
    }

    private static void DrawIndicator(Graphics g, TilePiece piece, RectangleF rect)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;

        double f = Math.Clamp(piece.Fraction ?? 0d, 0d, 1d);
        var track = Color.FromArgb(TrackAlpha, piece.Colour);

        if (piece.Element.Shape == CustomIndicatorShape.Circular)
        {
            float thickness = Math.Max(3f, Math.Min(rect.Width, rect.Height) * 0.17f);
            var box = new RectangleF(rect.X + thickness / 2f, rect.Y + thickness / 2f,
                                     rect.Width - thickness, rect.Height - thickness);

            // 270° starting bottom-left: the orientation every desktop gauge uses, and the one K2's
            // own gauge tiles already use — a custom ring must not spin the other way.
            using (var pen = new Pen(track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, box, 135f, 270f);

            if (piece.Fraction is not null && f > 0.002d)
                using (var pen = new Pen(piece.Colour, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, box, 135f, (float)(f * 270d));
            return;
        }

        bool horizontal = piece.Element.Shape == CustomIndicatorShape.LinearHorizontal;
        float radius = (horizontal ? rect.Height : rect.Width) / 2f;

        using (var brush = new SolidBrush(track))
            FillRounded(g, brush, rect, radius);

        if (piece.Fraction is null || f <= 0.002d) return;

        // A horizontal bar fills left to right, a vertical one BOTTOM TO TOP: the direction a gauge
        // is read in, not the direction pixels happen to be numbered in.
        var fill = horizontal
            ? new RectangleF(rect.X, rect.Y, rect.Width * (float)f, rect.Height)
            : new RectangleF(rect.X, rect.Bottom - rect.Height * (float)f, rect.Width, rect.Height * (float)f);

        using (var brush = new SolidBrush(piece.Colour))
            FillRounded(g, brush, fill, Math.Min(radius, Math.Min(fill.Width, fill.Height) / 2f));
    }

    /// <summary>Draws <paramref name="text"/> in <paramref name="box"/>: at
    /// <paramref name="fixedSize"/> when the user pinned one, otherwise as large as it fits,
    /// starting from <paramref name="startSize"/> and shrinking — the same rule K2's own tiles use,
    /// so a custom tile's text sits at the same weight as a shipped one.</summary>
    private static void DrawFitted(Graphics g, string text, RectangleF box, float startSize,
                                   Color color, StringAlignment align, string? family, double fixedSize)
    {
        float fontSize;
        if (fixedSize > 0)
        {
            // A pinned size is honoured as asked — the point of pinning one is that the number
            // stops changing size when the reading goes from "9" to "100".
            fontSize = (float)fixedSize;
        }
        else
        {
            fontSize = Math.Max(8f, startSize);
            for (; fontSize > 8f; fontSize -= 1f)
            {
                using var probe = TileFont(family, fontSize);
                var m = g.MeasureString(text, probe);
                if (m.Width <= box.Width && m.Height <= box.Height) break;
            }
        }

        using var font = TileFont(family, fontSize);
        using var format = new StringFormat
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // A dark drop shadow under every string: the background can be an arbitrary PNG the user
        // dropped in, and white-on-white is the one failure mode a colour picker cannot prevent.
        float off = Math.Max(1f, fontSize * 0.07f);
        using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.DrawString(text, font, shadow, new RectangleF(box.X + off, box.Y + off, box.Width, box.Height), format);
        using (var brush = new SolidBrush(color))
            g.DrawString(text, font, brush, box, format);
    }

    /// <summary>The face a tile's text is drawn in: the family the element asks for when it is
    /// installed on this PC, K2's own otherwise. A family that cannot be created falls back rather
    /// than throwing mid-render — the name is stored as a string and the PC it renders on is not
    /// necessarily the one it was authored on.</summary>
    private static Font TileFont(string? family, float sizePx)
    {
        if (!string.IsNullOrWhiteSpace(family))
        {
            try
            {
                var font = new Font(family, sizePx, FontStyle.Bold, GraphicsUnit.Pixel);
                // System.Drawing SUBSTITUTES silently for an unknown family, so the result has to be
                // checked rather than trusted: a mismatch means the family is not here.
                if (string.Equals(font.FontFamily.Name, family, StringComparison.OrdinalIgnoreCase))
                    return font;
                font.Dispose();
            }
            catch { /* fall through to the stock face */ }
        }
        return IconImageGenerator.CaptionFont(sizePx);
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r, float radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        radius = Math.Max(0f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
        if (radius <= 0.5f) { g.FillRectangle(brush, r); return; }

        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>Loads a PNG into a bitmap the caller owns, or null when the path is empty, gone or
    /// unreadable. Copied off the file handle so the source stays free to be replaced from the
    /// studio while a tile is on screen.</summary>
    private static Bitmap? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var src = new Bitmap(path);
            return new Bitmap(src);
        }
        catch { return null; }
    }

    private static bool Save(Bitmap canvas, string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            canvas.Save(path, ImageFormat.Png);
            return true;
        }
        catch { return false; }
    }
}
