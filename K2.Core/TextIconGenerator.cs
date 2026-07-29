using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace K2.Core;

/// <summary>
/// Renders a key image from plain text: either on a solid color background, or
/// overlaid on top of an already-loaded image. Used by the "Add text…" editor
/// (<see cref="TextIconDialog"/>) shared by DisplayPad and Everest numpad display
/// key dialogs. Auto-shrinks the font to fit the canvas and draws a contrasting
/// outline around the text so it stays legible over busy backgrounds.
/// </summary>
public static class TextIconGenerator
{
    /// <summary>
    /// Where the text sits within the icon (9-position anchor, PowerPoint-style), each with
    /// a small fixed padding from the edge (see <see cref="DrawFittedText"/>'s margin) rather
    /// than a free pixel coordinate — simpler to reason about and immune to the source icon
    /// being replaced/rescaled later. <see cref="MiddleCenter"/> is the historical default
    /// (text always centered) kept for callers that don't care.
    /// </summary>
    public enum TextAnchor
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
    }

    /// <summary>
    /// Renders the icon in memory. Caller owns the returned <see cref="Bitmap"/>
    /// (dispose it). Returns null only on unexpected rendering failure.
    /// </summary>
    public static Bitmap? TryRenderTextIcon(
        string text,
        int size,
        Color textColor,
        Color? backgroundColor,
        string? baseImagePath,
        string? fontFamily = null,
        float? fontSize = null,
        TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        try
        {
            var canvas = new Bitmap(size, size);
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (!string.IsNullOrEmpty(baseImagePath) && File.Exists(baseImagePath))
                {
                    byte[] bytes = File.ReadAllBytes(baseImagePath);
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    g.DrawImage(src, 0, 0, size, size);
                }
                else
                {
                    g.Clear(backgroundColor ?? Color.Black);
                }

                DrawFittedText(g, text ?? string.Empty, size, textColor, fontFamily, fontSize, anchor);
            }
            return canvas;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Renders and saves the icon as PNG. See <see cref="TryRenderTextIcon"/>.</summary>
    public static bool TryGenerateTextIcon(
        string text,
        int size,
        string outputPngPath,
        Color textColor,
        Color? backgroundColor,
        string? baseImagePath,
        string? fontFamily = null,
        float? fontSize = null,
        TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        using var canvas = TryRenderTextIcon(text, size, textColor, backgroundColor, baseImagePath, fontFamily, fontSize, anchor);
        if (canvas is null) return false;

        try
        {
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
    /// Word-wraps <paramref name="text"/> inside a fixed-margin region anchored per
    /// <paramref name="anchor"/> (8% of the canvas from each edge — a few px at DisplayPad/
    /// Everest display-key sizes, plenty for a legend), shrinking the font until it fits,
    /// then draws it with a thin outline (auto black/white based on the text color's
    /// luminance) for legibility over any background.
    /// <paramref name="preferredFontSize"/> (pixels) is the STARTING size for the shrink
    /// loop — never grown beyond, only shrunk further if the text doesn't fit — so a
    /// user-chosen size can never overflow the canvas. Null picks the previous default
    /// (42% of the canvas). <paramref name="familyName"/> null/invalid falls back to the
    /// GDI+ default (no crash — <see cref="Font"/>'s constructor silently substitutes a
    /// fallback family rather than throwing on an unknown name).
    /// </summary>
    private static void DrawFittedText(Graphics g, string text, int size, Color color,
        string? familyName, float? preferredFontSize, TextAnchor anchor)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string family = string.IsNullOrWhiteSpace(familyName) ? "Segoe UI" : familyName;

        var rect = new RectangleF(size * 0.08f, size * 0.08f, size * 0.84f, size * 0.84f);
        var (hAlign, vAlign) = AnchorToAlignment(anchor);
        using var format = new StringFormat
        {
            Alignment = hAlign,
            LineAlignment = vAlign,
            Trimming = StringTrimming.EllipsisWord,
        };

        // Shrink loop must reject BOTH overflow axes — checking only Height (as before)
        // let an unbroken long word (no space to wrap on) pass at a large font size even
        // though its measured Width already spilled past the box, which is what made
        // "auto-fit" look like it was always picking too big a size.
        const float minFontSize = 8f;
        float fontSize = preferredFontSize ?? size * 0.42f;
        if (fontSize < minFontSize) fontSize = minFontSize;
        Font? fitFont = null;
        for (; fontSize >= minFontSize; fontSize -= 1f)
        {
            var candidate = CreateFont(family, fontSize);
            SizeF measured = g.MeasureString(text, candidate, (int)rect.Width, format);
            if (measured.Height <= rect.Height && measured.Width <= rect.Width)
            {
                fitFont = candidate;
                break;
            }
            candidate.Dispose();
        }
        fitFont ??= CreateFont(family, minFontSize);

        using (fitFont)
        using (var path = new GraphicsPath())
        {
            path.AddString(text, fitFont.FontFamily, (int)fitFont.Style, fitFont.Size, rect, format);

            float luminance = (0.299f * color.R + 0.587f * color.G + 0.114f * color.B) / 255f;
            Color outline = luminance > 0.55f ? Color.Black : Color.White;
            float strokeWidth = Math.Max(1.5f, size * 0.02f);

            using var pen = new Pen(outline, strokeWidth) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    /// <summary>Maps a <see cref="TextAnchor"/> to the GDI+ horizontal/vertical
    /// <see cref="StringAlignment"/> pair that positions text within the fixed-margin
    /// rect built by <see cref="DrawFittedText"/> — the margin itself is what gives the
    /// "padding from the edge" look for every non-center anchor.</summary>
    private static (StringAlignment H, StringAlignment V) AnchorToAlignment(TextAnchor anchor) => anchor switch
    {
        TextAnchor.TopLeft      => (StringAlignment.Near,   StringAlignment.Near),
        TextAnchor.TopCenter    => (StringAlignment.Center, StringAlignment.Near),
        TextAnchor.TopRight     => (StringAlignment.Far,    StringAlignment.Near),
        TextAnchor.MiddleLeft   => (StringAlignment.Near,   StringAlignment.Center),
        TextAnchor.MiddleRight  => (StringAlignment.Far,    StringAlignment.Center),
        TextAnchor.BottomLeft   => (StringAlignment.Near,   StringAlignment.Far),
        TextAnchor.BottomCenter => (StringAlignment.Center, StringAlignment.Far),
        TextAnchor.BottomRight  => (StringAlignment.Far,    StringAlignment.Far),
        _                       => (StringAlignment.Center, StringAlignment.Center),
    };

    /// <summary>
    /// Prefers Bold (matches the previous fixed look), but some installed families have
    /// no bold face — GDI+'s <see cref="Font"/> constructor throws for a style a family
    /// doesn't support (unlike an unknown family name, which it silently substitutes),
    /// so this falls back to Regular rather than letting the whole render fail.
    /// </summary>
    private static Font CreateFont(string family, float size)
    {
        try { return new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel); }
    }
}
