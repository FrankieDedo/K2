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
    /// Renders the icon in memory. Caller owns the returned <see cref="Bitmap"/>
    /// (dispose it). Returns null only on unexpected rendering failure.
    /// </summary>
    public static Bitmap? TryRenderTextIcon(
        string text,
        int size,
        Color textColor,
        Color? backgroundColor,
        string? baseImagePath)
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

                DrawFittedText(g, text ?? string.Empty, size, textColor);
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
        string? baseImagePath)
    {
        using var canvas = TryRenderTextIcon(text, size, textColor, backgroundColor, baseImagePath);
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
    /// Word-wraps <paramref name="text"/> inside a centered square-ish region, shrinking
    /// the font until it fits, then draws it with a thin outline (auto black/white based
    /// on the text color's luminance) for legibility over any background.
    /// </summary>
    private static void DrawFittedText(Graphics g, string text, int size, Color color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var rect = new RectangleF(size * 0.08f, size * 0.08f, size * 0.84f, size * 0.84f);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord,
        };

        const float minFontSize = 8f;
        float fontSize = size * 0.42f;
        Font? fitFont = null;
        for (; fontSize >= minFontSize; fontSize -= 1f)
        {
            var candidate = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            SizeF measured = g.MeasureString(text, candidate, (int)rect.Width, format);
            if (measured.Height <= rect.Height)
            {
                fitFont = candidate;
                break;
            }
            candidate.Dispose();
        }
        fitFont ??= new Font("Segoe UI", minFontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        using (fitFont)
        using (var path = new GraphicsPath())
        {
            path.AddString(text, fitFont.FontFamily, (int)FontStyle.Bold, fitFont.Size, rect, format);

            float luminance = (0.299f * color.R + 0.587f * color.G + 0.114f * color.B) / 255f;
            Color outline = luminance > 0.55f ? Color.Black : Color.White;
            float strokeWidth = Math.Max(1.5f, size * 0.02f);

            using var pen = new Pen(outline, strokeWidth) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }
}
