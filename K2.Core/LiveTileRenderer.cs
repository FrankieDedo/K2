using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;

namespace K2.Core;

/// <summary>
/// Tiles whose CONTENT changes on its own: the DisplayPad clock faces (<c>dp_clock</c>), the
/// PC monitor gauges (<c>dp_sysmon</c>) and the speed-test readouts (<c>dp_speedtest</c>).
///
/// <para>
/// Kept apart from <see cref="IconImageGenerator"/> on purpose: everything there renders ONCE
/// per action+style and is cached under a fingerprinted file name, while these are re-rendered
/// as often as once a second and overwrite a single per-key file (see
/// <c>DpLiveTileService</c>). The LOOK is shared though — same black rounded tile, same accent
/// color, same per-key <see cref="IconStyleScope"/> overrides (background/text color, font,
/// caption) — by calling into that class's tile helpers rather than re-deriving them here, so
/// a live key sits next to the static ones without looking foreign.
/// </para>
///
/// <para>
/// Base Camp has no equivalent feature to stay compatible with: its own CPU/RAM/GPU/disk/
/// network readouts and its clock exist ONLY as Everest Max Media Dock / Display Dial pages
/// drawn by the keyboard's firmware (see <c>MainWindow.MediaDock.cs</c>), never as DisplayPad
/// keys. So the vocabulary below is K2's own — nothing to map from a Base Camp profile.
/// </para>
/// </summary>
public static class LiveTileRenderer
{
    // ─────────────────────────── Clock ───────────────────────────

    /// <summary>Renders one clock tile. <paramref name="mode"/> is the stored action value of a
    /// <c>dp_clock</c> key (see <see cref="ActionTypeHelper.ClockModes"/>):
    /// <list type="bullet">
    /// <item><c>analog</c> — round face with hour/minute hands and an accent second hand;</item>
    /// <item><c>digital24</c>/<c>digital12</c> — "14:35" on one line;</item>
    /// <item><c>vert24</c>/<c>vert12</c> — hours above, minutes below (a taller, bigger read on
    /// a square key than the horizontal one);</item>
    /// <item><c>hours</c>/<c>hours12</c>/<c>minutes</c>/<c>seconds</c> — a single number, so
    /// three adjacent keys can spell out one clock;</item>
    /// <item><c>date</c> — day number over the short month name.</item>
    /// </list>
    /// <paramref name="caption"/> is drawn only when non-empty (the key's "with text" choice is
    /// resolved by the caller, same rule as every other tile).</summary>
    public static bool TryRenderClock(string? mode, DateTime now, string caption, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                switch ((mode ?? "").ToLowerInvariant())
                {
                    case "analog":
                        DrawAnalogFace(g, size, now, caption.Length > 0);
                        break;
                    case "vert24":
                        DrawTwoLines(g, size, now.ToString("HH", CultureInfo.InvariantCulture),
                                     now.ToString("mm", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "vert12":
                        DrawTwoLines(g, size, now.ToString("hh", CultureInfo.InvariantCulture),
                                     now.ToString("mm", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "digital12":
                        DrawBigText(g, size, now.ToString("hh:mm", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "hours":
                        DrawBigText(g, size, now.ToString("HH", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "hours12":
                        DrawBigText(g, size, now.ToString("hh", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "minutes":
                        DrawBigText(g, size, now.ToString("mm", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "seconds":
                        DrawBigText(g, size, now.ToString("ss", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                    case "date":
                        DrawTwoLines(g, size, now.Day.ToString(CultureInfo.InvariantCulture),
                                     now.ToString("MMM", CultureInfo.CurrentCulture).ToUpperInvariant(),
                                     caption.Length > 0, secondLineSmall: true);
                        break;
                    default:   // "digital24" and anything unrecognized
                        DrawBigText(g, size, now.ToString("HH:mm", CultureInfo.InvariantCulture), caption.Length > 0);
                        break;
                }

                if (caption.Length > 0) IconImageGenerator.DrawCaption(g, size, caption);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>The text a clock key shows RIGHT NOW, without rendering anything — the
    /// change-detector the live service uses to skip an upload when nothing moved (a
    /// minutes/date key re-renders identically 59 times out of 60). The analog face is the one
    /// mode whose picture changes every second regardless, hence its seconds-resolution key.</summary>
    public static string ClockStamp(string? mode, DateTime now) => (mode ?? "").ToLowerInvariant() switch
    {
        "analog"    => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        "seconds"   => now.ToString("ss", CultureInfo.InvariantCulture),
        "minutes"   => now.ToString("mm", CultureInfo.InvariantCulture),
        "hours"     => now.ToString("HH", CultureInfo.InvariantCulture),
        "hours12"   => now.ToString("hh", CultureInfo.InvariantCulture),
        "date"      => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "digital12" or "vert12" => now.ToString("hh:mm", CultureInfo.InvariantCulture),
        _           => now.ToString("HH:mm", CultureInfo.InvariantCulture),
    };

    // ─────────────────────── Gauges (sysmon / speedtest) ───────────────────────

    /// <summary>
    /// Renders a metric tile: a 270° ring filled to <paramref name="fraction"/> (0..1) with the
    /// value in the middle and the optional caption below.
    /// <para>
    /// Pass <c>null</c> for a reading with no meaningful full scale — a throughput in MB/s, a
    /// ping in ms — and NO ring is drawn at all: the number simply gets the whole tile. An empty
    /// ring around such a value would read as "0%", which is exactly the wrong thing to say
    /// about a number that isn't a percentage.
    /// </para>
    /// </summary>
    public static bool TryRenderGauge(string valueText, double? fraction, string caption,
                                      int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                bool withCaption = caption.Length > 0;

                if (fraction is double f)
                {
                    float ring = size * (withCaption ? 0.60f : 0.74f);
                    float left = (size - ring) / 2f;
                    float top = withCaption ? size * 0.06f : (size - ring) / 2f;
                    float thickness = Math.Max(3f, size * 0.075f);

                    var rect = new RectangleF(left + thickness / 2f, top + thickness / 2f,
                                              ring - thickness, ring - thickness);

                    // Track first, then the filled sweep on top — 270° starting bottom-left, the
                    // orientation every desktop gauge uses, so "full" reads as "all the way round".
                    using (var track = new Pen(Dim(TextColor, 0.25f), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(track, rect, 135f, 270f);

                    float sweep = (float)(Math.Clamp(f, 0d, 1d) * 270d);
                    if (sweep > 0.5f)
                        using (var pen = new Pen(IconImageGenerator.TileAccent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawArc(pen, rect, 135f, sweep);

                    // The value goes in the ring's INNER area, not its bounding box: a 3-4
                    // character reading ("100%", "73%") drawn across the full box runs straight
                    // through the arc on both sides.
                    float inner = ring * 0.62f;
                    DrawFitted(g, valueText,
                               new RectangleF(left + (ring - inner) / 2f, top + (ring - inner) / 2f, inner, inner),
                               size * 0.30f, TextColor);
                }
                else
                {
                    DrawBigText(g, size, valueText, withCaption);
                }

                if (withCaption) IconImageGenerator.DrawCaption(g, size, caption);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    // ─────────────────────────── Drawing helpers ───────────────────────────

    /// <summary>Text/hand color — the per-key "Edit icon" text color when set, white otherwise
    /// (the same default <see cref="IconImageGenerator.DrawCaption"/> uses).</summary>
    private static Color TextColor => IconStyleScope.OverrideText ?? Color.White;

    private static Graphics NewGraphics(Bitmap canvas, int size)
    {
        var g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(IconImageGenerator.TileBackground);
        IconImageGenerator.ClipToRoundedTile(g, size);
        return g;
    }

    private static bool Save(Bitmap canvas, string outputPngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);
        canvas.Save(outputPngPath, ImageFormat.Png);
        return true;
    }

    private static Color Dim(Color c, float factor) =>
        Color.FromArgb((int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

    /// <summary>One number/short string as big as it can be drawn in the tile's icon area —
    /// the whole point of a single-value key is that it's readable across the desk.</summary>
    private static void DrawBigText(Graphics g, int size, string text, bool withCaption)
    {
        float height = withCaption ? size * 0.62f : size;
        DrawFitted(g, text, new RectangleF(size * 0.04f, 0, size * 0.92f, height), size * 0.62f, TextColor);
    }

    /// <summary>Two stacked values (hours over minutes, day over month) sharing the tile — the
    /// "vertical clock" the three-key layouts are built from.</summary>
    private static void DrawTwoLines(Graphics g, int size, string top, string bottom,
                                     bool withCaption, bool secondLineSmall = false)
    {
        float height = withCaption ? size * 0.62f : size;
        float half = height / 2f;
        DrawFitted(g, top, new RectangleF(size * 0.04f, 0, size * 0.92f, half), size * 0.44f, TextColor);
        DrawFitted(g, bottom, new RectangleF(size * 0.04f, half, size * 0.92f, half),
                   size * (secondLineSmall ? 0.30f : 0.44f),
                   secondLineSmall ? IconImageGenerator.TileAccent : TextColor);
    }

    /// <summary>Draws <paramref name="text"/> centered in <paramref name="rect"/>, shrinking from
    /// <paramref name="startPx"/> until it fits both ways. Same shrink-don't-truncate rule as
    /// <see cref="IconImageGenerator"/>'s captions, but sized for a headline value rather than a
    /// label, and with a hard floor so a pathological string still renders something.</summary>
    private static void DrawFitted(Graphics g, string text, RectangleF rect, float startPx, Color color)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        for (float px = startPx; px >= 8f; px -= 1f)
        {
            using var font = IconImageGenerator.CaptionFont(px);
            SizeF measured = g.MeasureString(text, font);
            if (measured.Width <= rect.Width && measured.Height <= rect.Height)
            {
                g.DrawString(text, font, brush, rect, format);
                return;
            }
        }
        using var smallest = IconImageGenerator.CaptionFont(8f);
        g.DrawString(text, smallest, brush, rect, format);
    }

    /// <summary>
    /// Analog face: a thin accent rim, four cardinal ticks, white hour/minute hands and an
    /// accent second hand — no numerals, which at 102 px would be a grey smudge.
    /// </summary>
    private static void DrawAnalogFace(Graphics g, int size, DateTime now, bool withCaption)
    {
        float box = withCaption ? size * 0.60f : size * 0.88f;
        float cx = size / 2f;
        float cy = withCaption ? size * 0.06f + box / 2f : size / 2f;
        float r = box / 2f;

        var accent = IconImageGenerator.TileAccent;
        using (var rim = new Pen(accent, Math.Max(1.5f, size * 0.025f)))
            g.DrawEllipse(rim, cx - r, cy - r, box, box);

        using (var tick = new Pen(Dim(TextColor, 0.7f), Math.Max(1.5f, size * 0.022f)))
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6d;
                float outer = r * 0.86f;
                float inner = r * (i % 3 == 0 ? 0.66f : 0.76f);
                g.DrawLine(tick,
                    cx + (float)(Math.Sin(a) * inner), cy - (float)(Math.Cos(a) * inner),
                    cx + (float)(Math.Sin(a) * outer), cy - (float)(Math.Cos(a) * outer));
            }

        double hourAngle = (now.Hour % 12 + now.Minute / 60d) * Math.PI / 6d;
        double minuteAngle = (now.Minute + now.Second / 60d) * Math.PI / 30d;
        double secondAngle = now.Second * Math.PI / 30d;

        DrawHand(g, cx, cy, hourAngle, r * 0.48f, Math.Max(2f, size * 0.045f), TextColor);
        DrawHand(g, cx, cy, minuteAngle, r * 0.72f, Math.Max(1.5f, size * 0.032f), TextColor);
        DrawHand(g, cx, cy, secondAngle, r * 0.78f, Math.Max(1f, size * 0.018f), accent);

        float hub = Math.Max(2f, size * 0.03f);
        using var hubBrush = new SolidBrush(accent);
        g.FillEllipse(hubBrush, cx - hub / 2f, cy - hub / 2f, hub, hub);
    }

    private static void DrawHand(Graphics g, float cx, float cy, double angle, float length, float width, Color color)
    {
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx, cy,
                   cx + (float)(Math.Sin(angle) * length),
                   cy - (float)(Math.Cos(angle) * length));
    }
}
