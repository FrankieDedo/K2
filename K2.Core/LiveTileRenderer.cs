using System;
using System.Collections.Generic;
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

                // A clock's value layout isn't rearranged for a caption (clock captions are
                // rare), so keep a plain bottom strip rather than the gauge's big two-line one.
                if (caption.Length > 0) IconImageGenerator.DrawCaption(g, size, caption, topFrac: 0.72f);
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

                // WITH a caption: the value sits in the tile's UPPER part and the caption gets
                //   the lower ~half — a bigger, up-to-two-line label.
                // WITHOUT: the value is centred on the whole tile and there's no caption strip.
                float valueBottom = withCaption ? size * ValueBottomWithCaption : size;

                if (fraction is double f)
                {
                    float ring = size * (withCaption ? 0.46f : 0.76f);
                    float left = (size - ring) / 2f;
                    float top = withCaption ? size * 0.04f : (size - ring) / 2f;
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
                    // through the arc on both sides. Framed by the ring, so the whole string is
                    // centred as one (no digit-only trick).
                    float inner = ring * 0.62f;
                    DrawFitted(g, valueText,
                               new RectangleF(left + (ring - inner) / 2f, top + (ring - inner) / 2f, inner, inner),
                               size * 0.30f, TextColor, sizeReference: RingValueSizeReference);
                }
                else
                {
                    // A bare number (temperature / throughput): the DIGITS are centred in this
                    // region (both axes), a trailing °/% hanging off to the right; the digit
                    // size is FIXED per length class so "61" and "40" render identically.
                    DrawCenteredValue(g, valueText,
                        new RectangleF(size * 0.06f, 0, size * 0.88f, valueBottom), size, TextColor);
                }

                if (withCaption)
                    IconImageGenerator.DrawCaption(g, size, caption,
                        topFrac: CaptionTopWithText, startFontScale: CaptionFontScaleWithText);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Segmented variant of <see cref="TryRenderGauge"/>: the same 270° dial, but cut into
    /// <paramref name="segments"/> discrete blocks that light up one by one instead of a
    /// continuous sweep — the way a game's own AP pips or a gauge's tick marks read.
    ///
    /// <para>Every segment is drawn twice: dim for the track, bright for the ones that are
    /// filled, so an empty dial still shows how many there ARE. That matters for the small
    /// counts (3 action points), where "2 of 3" has to be legible at a glance; a large count
    /// (health) is passed a fixed dozen ticks instead, since a 35-block dial would just be a
    /// dashed ring.</para>
    ///
    /// <para><paramref name="accent"/> is passed in rather than taken from the ambient icon
    /// theme: these dials belong to a game profile and wear the GAME's colour, which is the
    /// whole point of the tile reading as part of the game's own HUD.</para>
    /// </summary>
    /// <param name="edgeInset">Fraction of the tile kept clear on every side — see the same
    /// parameter on <see cref="TryRenderSpeedTile"/>.</param>
    /// <param name="dialScale">Dial size relative to the stock layout. Anything but 1 (or a non-zero
    /// <paramref name="edgeInset"/>) switches to a layout where the dial takes whatever height the
    /// inset and a compact one-strip caption leave, instead of the fixed upper half.</param>
    public static bool TryRenderSegmentedGauge(string valueText, double? fraction, string caption,
                                               int size, string outputPngPath,
                                               Color accent, int segments,
                                               TileEmphasis emphasis = TileEmphasis.ShadowLitCore,
                                               float edgeInset = 0f, float dialScale = 1f)
    {
        if (edgeInset > 0f || Math.Abs(dialScale - 1f) > 0.001f)
            return TryRenderLargeDial(valueText, fraction, caption, size, outputPngPath, accent, segments,
                                      emphasis, edgeInset, dialScale);

        try
        {
            segments = Math.Clamp(segments, 1, 24);
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                bool withCaption = caption.Length > 0;

                if (fraction is double f)
                {
                    float ring = size * (withCaption ? 0.46f : 0.76f);
                    float left = (size - ring) / 2f;
                    float top = withCaption ? size * 0.04f : (size - ring) / 2f;
                    float thickness = Math.Max(3f, size * 0.075f);
                    var rect = new RectangleF(left + thickness / 2f, top + thickness / 2f,
                                              ring - thickness, ring - thickness);

                    DrawSegmentedDial(g, rect, thickness, segments, f, accent, emphasis, size);
                    float inner = ring * 0.62f;
                    DrawFitted(g, valueText,
                               new RectangleF(left + (ring - inner) / 2f, top + (ring - inner) / 2f, inner, inner),
                               size * 0.30f, TextColor, sizeReference: RingValueSizeReference);
                }
                else
                {
                    DrawCenteredValue(g, valueText,
                        new RectangleF(size * 0.06f, 0, size * 0.88f,
                                       withCaption ? size * ValueBottomWithCaption : size),
                        size, TextColor);
                }

                if (withCaption)
                    DrawSegmentedCaption(g, size, caption, emphasis);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>The <see cref="TryRenderSegmentedGauge"/> layout for a bigger dial: the caption is a
    /// single strip at the bottom, the dial is centred in the height above it, and nothing comes
    /// closer to the key's edge than <paramref name="edgeInset"/>.</summary>
    private static bool TryRenderLargeDial(string valueText, double? fraction, string caption,
                                           int size, string outputPngPath, Color accent, int segments,
                                           TileEmphasis emphasis, float edgeInset, float dialScale)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                caption = IconStyleScope.OverrideCaption ?? caption;
                bool withCaption = caption.Length > 0;
                float inset = size * edgeInset;
                float innerW = size - 2 * inset;
                float captionH = withCaption ? size * 0.16f : 0f;
                float available = size - 2 * inset - captionH;

                float ring = Math.Min(size * (withCaption ? 0.46f : 0.76f) * dialScale, Math.Min(available, innerW));
                float left = (size - ring) / 2f;
                float top = inset + (available - ring) / 2f;
                float thickness = Math.Max(3f, size * 0.075f * Math.Min(dialScale, 1.25f));

                if (fraction is double f)
                {
                    var rect = new RectangleF(left + thickness / 2f, top + thickness / 2f,
                                              ring - thickness, ring - thickness);
                    DrawSegmentedDial(g, rect, thickness, Math.Clamp(segments, 1, 24), f, accent, emphasis, size);
                    float innerBox = ring * 0.62f;
                    DrawFitted(g, valueText,
                               new RectangleF(left + (ring - innerBox) / 2f, top + (ring - innerBox) / 2f, innerBox, innerBox),
                               size * 0.30f * dialScale, TextColor, sizeReference: RingValueSizeReference);
                }
                else
                {
                    DrawCenteredValue(g, valueText, new RectangleF(inset, inset, innerW, available), size, TextColor);
                }

                if (withCaption)
                    DrawFitted(g, caption, new RectangleF(inset, size - inset - captionH, innerW, captionH),
                               size * 0.15f, IconStyleScope.OverrideText ?? Color.White);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>Widths (as a multiple of the segment's own thickness) and alphas of the halo
    /// passes, widest and faintest first. Three strokes rather than a real blur: on a 102 px key
    /// the difference is not visible and this costs three DrawArc calls instead of a full-tile
    /// convolution.
    ///
    /// <para>Tuned TIGHT: the innermost pass is nearly opaque and barely wider than the block
    /// itself, so the white sits right on the edge of the lit segment and falls away fast. A wide
    /// faint cloud reads as fog on a key this small — the light has to look like it comes off the
    /// segment, not like the segment is behind frosted glass.</para></summary>
    private static readonly (float WidthScale, int Alpha)[] GlowPasses =
        { (1.62f, 46), (1.34f, 120), (1.14f, 225) };

    /// <summary>Draws the 270 degree segmented dial — the shared half of every tile that carries
    /// one: the unlit track, the shadow under the lit blocks, their colour, and the lit core
    /// inside them. Split out of <see cref="TryRenderSegmentedGauge"/> when the squad tile needed
    /// the same dial around a different centre, so the two can never drift apart.</summary>
    private static void DrawSegmentedDial(Graphics g, RectangleF rect, float thickness,
                                          int segments, double? fraction, Color accent,
                                          TileEmphasis emphasis, int size)
    {
        if (fraction is not double f) return;
        segments = Math.Clamp(segments, 1, 24);

            // The gap eats into each segment's own share, so more segments means thinner
            // blocks rather than a dial that grows past its 270°.
            float share = 270f / segments;
            float gap = Math.Min(share * 0.30f, 7f);
            float block = share - gap;

            // How many are lit: rounded, but never rounded away — any non-zero reading
            // keeps at least one block, because "1 HP left" must not look like death.
            int lit = (int)Math.Round(Math.Clamp(f, 0d, 1d) * segments);
            if (f > 0 && lit == 0) lit = 1;

            using var dim = new Pen(Dim(accent, 0.22f), thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            using var on = new Pen(accent, thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

            // ORDER MATTERS, and it is the whole of this method's shape:
            //   1. every block's shadow — lit AND unlit, so the dial is seated on the frame art as
            //      one object instead of only where it happens to be lit;
            //   2. the unlit track;
            //   3. the lit blocks and their cores.
            // Drawing a lit block's shadow after the unlit ones would smear it across its dark
            // neighbours; underneath everything, it darkens the art and nothing else.
            if (emphasis is TileEmphasis.Shadow or TileEmphasis.ShadowLitCore)
                foreach (var (widthScale, alpha) in ShadowPasses)
                {
                    using var shadow = new Pen(Color.FromArgb(alpha, Color.Black), thickness * widthScale)
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    for (int i = 0; i < segments; i++)
                        g.DrawArc(shadow, rect, 135f + i * share + gap / 2f, block);
                }

            for (int i = lit; i < segments; i++)
                g.DrawArc(dim, rect, 135f + i * share + gap / 2f, block);

            // The old white halo, kept as a mode.
            if (emphasis == TileEmphasis.WhiteHalo)
            {
                Color halo = TowardsWhite(accent, 0.88f);
                foreach (var (widthScale, alpha) in GlowPasses)
                {
                    using var glow = new Pen(Color.FromArgb(alpha, halo), thickness * widthScale)
                        { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    for (int i = 0; i < lit; i++)
                        g.DrawArc(glow, rect, 135f + i * share + gap / 2f, block);
                }
            }

            // The block's own colour, full width.
            for (int i = 0; i < lit; i++)
                g.DrawArc(on, rect, 135f + i * share + gap / 2f, block);

            // The lit core: progressively thinner, progressively whiter strokes down the
            // MIDDLE of the same arc. Nothing is drawn outside the block, so the light
            // cannot bridge the dial's gaps — the pips stay countable however bright the
            // centre gets, which is what an outward bloom could not promise.
            if (emphasis == TileEmphasis.ShadowLitCore)
            {
                // Inset along the arc as well as across it: without this the core runs
                // right into the block's short edges and the white reads as a bar with
                // blue sides rather than a light sitting inside a blue block.
                //
                // Measured in the ARC's own geometry, not as a share of the block: the
                // pull-back that has to happen is half the stroke's width, and that is a
                // fixed number of pixels whatever the block's length — a percentage would
                // leave a 12-block dial's short segments touching their edges while
                // over-trimming a 3-block one. Capped so a very short block keeps a core.
                float radius = (rect.Width + rect.Height) / 4f;
                float degPerPixel = (float)(180d / Math.PI / Math.Max(radius, 1f));
                float insetDeg = thickness / 2f * degPerPixel;
                float inset = Math.Min(insetDeg * CoreEndInsetScale, block * CoreMaxInsetShare);

                // The core's SHAPE follows the block's. A long block (a dial of three)
                // is a bar lying along the arc: the light keeps its length and narrows
                // across the thickness, which is what makes it read as a lit tube.
                //
                // A short block (a dial of twelve) is nearly square, and a bar of light
                // inside it points the wrong way whichever axis you pick — so there the
                // core shrinks on BOTH axes at once: a smaller square of light floating
                // inside the block, fading out on every side.
                float arcLength = block / degPerPixel;
                bool squareBlock = arcLength < thickness * SquareBlockRatio;

                float usableSweep = block - inset * 2f;
                float usableWidth = Math.Max(thickness - inset / degPerPixel * 2f, 1f);

                // One pad-pixel of extra reach on a square core, centre and falloff
                // alike. Expressed as a fraction of the tile rather than a literal 1f:
                // the same tile is also drawn at preview sizes, where a hard-coded pixel
                // would be a different amount of light.
                float widen = size / (float)DpPixelReference;

                foreach (var (scale, alpha) in CorePasses)
                {
                    // Radially the falloff is allowed to spill one pixel PAST the block,
                    // top and bottom. Nothing sits above or below a segment — its
                    // neighbours are beside it along the arc — so the light can breathe
                    // there without blurring two pips into one.
                    float penWidth = squareBlock
                        ? Math.Min(usableWidth * scale + widen * 2f, thickness + widen * 2f)
                        : thickness * scale;
                    float sweep = squareBlock
                        ? Math.Min(usableSweep * scale + widen * degPerPixel, block)
                        : usableSweep;
                    // Both axes shrink around the block's own centre, so the light stays
                    // centred in it instead of creeping towards one end.
                    float lead = gap / 2f + inset + (usableSweep - sweep) / 2f;

                    // A square core shrinks on two axes at once, so each pass leaves far
                    // less ink than the bar's does and the ladder as a whole comes out
                    // pale. The boost buys back the brightness WITHOUT shortening the
                    // ladder, which is what keeps the falloff soft.
                    int ink = squareBlock
                        ? Math.Min((int)(alpha * SquareCoreBoost), 255)
                        : alpha;
                    using var core = new Pen(Color.FromArgb(ink, CoreLight(accent)),
                                             Math.Max(penWidth, 0.6f))
                    {
                        // Flat caps on a square core: rounded ones turn the little block
                        // of light into a dot as it shrinks.
                        StartCap = squareBlock ? LineCap.Flat : LineCap.Round,
                        EndCap   = squareBlock ? LineCap.Flat : LineCap.Round,
                    };
                    for (int i = 0; i < lit; i++)
                        g.DrawArc(core, rect, 135f + i * share + lead, sweep);
                }
            }
    }

    /// <summary>
    /// The squad-member tile: one soldier's health AND action points on a single key.
    ///
    /// <para>Health is the segmented dial plus the number, pushed to the LEFT of the dial's
    /// middle with a heart under it — the heart sits in the arc's own gap at the bottom, which is
    /// otherwise dead space, so the reading gains an icon without giving up any room. Action
    /// points are three horizontal bars stacked on the right, lit one per point: on a key this
    /// small a second dial would be unreadable, while bars are countable at a glance.</para>
    ///
    /// <para>Everything wears the same treatment as the dial — shadow underneath, a soft lit core
    /// inside — only along a straight line instead of an arc, so the two halves of the tile read
    /// as one object.</para>
    /// </summary>
    public static bool TryRenderUnitTile(string healthText, double? healthFraction,
                                         int actionPoints, int maxActionPoints,
                                         string caption, int size, string outputPngPath,
                                         Color accent, bool withDial = true,
                                         TileEmphasis emphasis = TileEmphasis.ShadowLitCore)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                bool withCaption = caption.Length > 0;

                // The readout lives in the tile's upper band, above the caption strip.
                float bandTop = size * 0.04f;
                float bandHeight = (withCaption ? size * SegCaptionTop : (float)size) - bandTop;

                // Left column: the health reading. Right column: the action-point bars. The
                // split is fixed rather than measured so a three-digit health and a one-digit
                // one put their bars in the same place — a row of these keys sits side by side
                // on the pad and the bars have to line up across them.
                float leftWidth = size * 0.58f;

                if (withDial)
                {
                    // The dial keeps the health company on the left. It is allowed to run a
                    // little TALLER than the band — the caption strip below starts with its own
                    // padding, so the extra height costs nothing — because the band's height,
                    // not the column's width, is what was holding the dial small.
                    float ring = Math.Min(leftWidth * 1.02f, bandHeight * 1.08f);
                    float left = size * DialLeftMargin + (leftWidth - ring) / 2f;
                    float top = bandTop + (bandHeight - ring) / 2f;
                    float thickness = Math.Max(2.5f, size * 0.062f);
                    var rect = new RectangleF(left + thickness / 2f, top + thickness / 2f,
                                              ring - thickness, ring - thickness);
                    DrawSegmentedDial(g, rect, thickness, HealthDialTicks,
                                      healthFraction, accent, emphasis, size);

                    // The number gets the dial's whole inner width and sits low in it, the way
                    // the plain gauge tiles read — the heart then tucks into the arc's gap
                    // underneath instead of pushing the reading up.
                    // No heart any more: the number owns the dial's inside, so it is drawn as
                    // big as the dial allows. DrawFitted only shrinks when the string does not
                    // fit, which for this box means from three digits up — two digits always
                    // come out at the full size, and that is the common case.
                    // Centred on the DIAL, both axes: the arc's gap is at the bottom, so a box
                    // pushed up to clear it (as it was when the heart lived there) now just reads
                    // as a number sitting high in its own circle.
                    float inner = ring * 0.80f;
                    var numberBox = new RectangleF(left + (ring - inner) / 2f,
                                                   top + (ring - inner) / 2f, inner, inner);
                    DrawFitted(g, healthText, numberBox, size * 0.50f, TextColor,
                               boostPx: 0f, semibold: true, sizeReference: "100");

                }
                else
                {
                    // No dial: the number gets the whole left column and the heart sits under it.
                    var numberBox = new RectangleF(size * 0.045f, bandTop, leftWidth, bandHeight);
                    DrawFitted(g, healthText, numberBox, size * 0.50f, TextColor,
                               boostPx: 0f, semibold: true, sizeReference: "100");
                }

                // Three bars, equally spaced, lit from the top down.
                int bars = Math.Max(maxActionPoints, 1);
                float barLeft = size * 0.045f + leftWidth + size * 0.03f;
                float barRight = size * 0.93f;
                float barThickness = Math.Max(2f, size * 0.075f);
                float span = bandHeight * 0.62f;
                float firstY = bandTop + (bandHeight - span) / 2f;
                float step = bars > 1 ? span / (bars - 1) : 0f;

                // Same three phases as the dial — all the shadows, then the dark bars, then
                // the lit ones — so a lit bar's shadow never lands on top of the bar above it.
                for (int i = 0; i < bars; i++)
                    DrawBarShadow(g, barLeft, barRight, BarY(i), barThickness, emphasis);
                for (int i = 0; i < bars; i++)
                    DrawBarBody(g, barLeft, barRight, BarY(i), barThickness,
                                accent, i < actionPoints, emphasis, size);

                float BarY(int i) => bars > 1 ? firstY + i * step : bandTop + bandHeight / 2f;

                if (withCaption) DrawSegmentedCaption(g, size, caption, emphasis);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>How far in from the tile's left edge the health dial starts. Smaller than the
    /// right-hand margin on purpose: the dial is the reading the eye goes to first, and pulling
    /// it outwards buys the action-point bars the room they need on the other side.</summary>
    private const float DialLeftMargin = 0.02f;

    /// <summary>The dark pad under one action-point bar. Drawn for EVERY bar, lit or not, and
    /// for all of them before any bar body — see the call site: a shadow painted after its
    /// neighbours would darken them instead of the art.</summary>
    private static void DrawBarShadow(Graphics g, float x0, float x1, float y, float thickness,
                                      TileEmphasis emphasis)
    {
        if (emphasis is not (TileEmphasis.Shadow or TileEmphasis.ShadowLitCore)) return;

        foreach (var (widthScale, alpha) in ShadowPasses)
            using (var shadow = new Pen(Color.FromArgb(alpha, Color.Black), thickness * widthScale)
                       { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(shadow, x0, y, x1, y);
    }

    /// <summary>One action-point bar: the same colour and lit-core stack the dial's blocks carry,
    /// so the two halves of the tile belong together. An unlit bar is the dim track alone,
    /// exactly like an unlit block.</summary>
    private static void DrawBarBody(Graphics g, float x0, float x1, float y, float thickness,
                                    Color accent, bool lit, TileEmphasis emphasis, int size)
    {
        if (!lit)
        {
            using var dim = new Pen(Dim(accent, 0.22f), thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            g.DrawLine(dim, x0, y, x1, y);
            return;
        }

        using (var on = new Pen(accent, thickness) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            g.DrawLine(on, x0, y, x1, y);

        if (emphasis != TileEmphasis.ShadowLitCore) return;

        // Same ladder as the dial's long blocks: the light narrows across the stroke and stops
        // short of the ends, so it reads as something lit inside the bar rather than a shorter
        // bar drawn on top of it.
        float widen = size / (float)DpPixelReference;
        float inset = thickness / 2f + widen;
        foreach (var (scale, alpha) in CorePasses)
        {
            using var core = new Pen(Color.FromArgb(alpha, CoreLight(accent)),
                                     Math.Max(thickness * scale, 0.6f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(core, x0 + inset, y, x1 - inset, y);
        }
    }

    /// <summary>Blocks in the health dial of a squad tile. Health runs to dozens, so it gets a
    /// fixed scale of ticks rather than one block per point — the same count
    /// <c>DpLiveTileService</c> passes for a standalone health gauge, kept in step by hand
    /// because the two live on opposite sides of the K2.Core/K2.App line.</summary>
    private const int HealthDialTicks = 12;

    /// <summary>An ability tile: the game's own ability icon over the profile's frame, with the
    /// ability name underneath.
    ///
    /// <para>The icon is a PNG on disk, not something read from the game — the artwork lives in
    /// the game's IoStore container, which nothing here can open. See
    /// <c>ZeroCompanyClient.AbilityIconPath</c> for where the files are expected and what happens
    /// when one is missing: the tile falls back to the caption alone, which is exactly the tile
    /// this replaces, so a half-populated icon folder degrades one key at a time.</para>
    ///
    /// <para>The icon is tinted with the tile's accent when it is a flat white/alpha glyph, and
    /// drawn as-is when it has colour of its own — decided by <paramref name="tint"/>.</para>
    /// </summary>
    /// <param name="iconScale">Fraction of the usual icon box to draw into. 1 for a real ability
    /// icon; smaller for the placeholder art of an EMPTY slot, which should read as furniture
    /// inside the frame rather than as another ability.</param>
    /// <param name="iconOpacity">Alpha the icon is drawn at, for the same reason — an empty slot's
    /// glyph is faded so a full row of real abilities still stands out from the gaps in it. This
    /// is the ONE place the "don't dim the artwork" rule above is deliberately broken: here the
    /// dimness IS the meaning, because there is no ability behind the picture at all.</param>
    public static bool TryRenderAbilityTile(string? iconPath, string caption, bool lit,
                                            int size, string outputPngPath, Color accent,
                                            bool tint = false,
                                            TileEmphasis emphasis = TileEmphasis.ShadowLitCore,
                                            float iconScale = 1f, float iconOpacity = 1f,
                                            bool pixelArt = false, string? badge = null, double? wear = null)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                // The icon owns the whole key: no caption, and only enough margin to keep it off
                // the frame art's lit border. The game's icons carry their own padding inside the
                // 512x512 square, so the inset here is deliberately small — anything larger and
                // the glyph reads as a stamp floating in the middle of the tile.
                bool withCaption = caption.Length > 0;
                float box = size * IconTileFill * iconScale;
                var rect = new RectangleF((size - box) / 2f, (size - box) / 2f, box, box);

                if (iconPath is { Length: > 0 } && File.Exists(iconPath))
                {
                    // Pixel art (a Minecraft item) must stay crisp: bicubic smears a 16-px texture.
                    if (pixelArt)
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                    }
                    // The box is square; the PNG need not be. The game's own ability icons are
                    // (128x128), but the placeholder art is not, and stretching it to the square
                    // made a squat, fat silhouette. Fit inside the box and keep the ratio.
                    // The icon is drawn as it is. Whether the ability is usable is said by the
                    // FRAME behind it — the profile ships a lit and an unlit one and the painter
                    // picks between them — so dimming the artwork on top would say it twice, and
                    // badly: a washed-out icon reads as a broken render, not as "unavailable".
                    using var icon = new Bitmap(iconPath);
                    if (icon.Width != icon.Height && icon.Width > 0 && icon.Height > 0)
                    {
                        float k = Math.Min(box / icon.Width, box / icon.Height);
                        float w = icon.Width * k, h = icon.Height * k;
                        rect = new RectangleF((size - w) / 2f, (size - h) / 2f, w, h);
                    }

                    if (tint || iconOpacity < 1f)
                    {
                        var matrix = new System.Drawing.Imaging.ColorMatrix
                        {
                            Matrix00 = tint ? accent.R / 255f : 1f,
                            Matrix11 = tint ? accent.G / 255f : 1f,
                            Matrix22 = tint ? accent.B / 255f : 1f,
                            Matrix33 = iconOpacity,
                        };
                        using var attrs = new System.Drawing.Imaging.ImageAttributes();
                        attrs.SetColorMatrix(matrix);
                        g.DrawImage(icon,
                                    new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height),
                                    0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attrs);
                    }
                    else
                    {
                        g.DrawImage(icon, new Rectangle((int)rect.X, (int)rect.Y,
                                                        (int)rect.Width, (int)rect.Height));
                    }
                }
                // With no icon file the tile is its caption alone — which is exactly the tile
                // this replaces, so an icon folder that only covers some abilities degrades one
                // key at a time instead of all at once.

                if (withCaption) DrawSegmentedCaption(g, size, caption, emphasis);

                // A game's own slot decorations, drawn the way its GUI draws them: a wear bar under
                // the item (green to red) and the stack count in the bottom-right corner.
                if (wear is double w01)
                {
                    float barW = size * 0.56f, barH = Math.Max(3f, size * 0.035f);
                    float bx = (size - barW) / 2f, by = size * 0.86f;
                    g.FillRectangle(Brushes.Black, bx, by, barW, barH);
                    int red = (int)(255 * Math.Clamp(1 - w01, 0, 1)), green = (int)(255 * Math.Clamp(w01, 0, 1));
                    using var wb = new SolidBrush(Color.FromArgb(red, green, 0));
                    g.FillRectangle(wb, bx, by, (float)(barW * Math.Clamp(w01, 0, 1)), barH);
                }
                if (badge is { Length: > 0 })
                {
                    using var font = new Font("Segoe UI", size * 0.2f, FontStyle.Bold, GraphicsUnit.Pixel);
                    var sz = g.MeasureString(badge, font);
                    float tx = size * 0.9f - sz.Width, ty = size * 0.88f - sz.Height;
                    using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                    g.DrawString(badge, font, shadow, tx + size * 0.015f, ty + size * 0.015f);
                    g.DrawString(badge, font, Brushes.White, tx, ty);
                }
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>A scroll arrow, for the key that pages a profile's action rows up or down.
    ///
    /// <para>Drawn rather than shipped as art: it has to wear the profile's accent and the same
    /// shadow/lit-core treatment as everything else on the page, and a PNG would have to be
    /// re-exported for every profile that ever wants one.</para>
    ///
    /// <para><paramref name="enabled"/> false draws the arrow dimmed — a key that has nowhere to
    /// scroll says so instead of vanishing, which would leave a hole in the panel.</para></summary>
    /// <param name="artPath">The profile's own arrow PNG. When it is there the tile is that
    /// picture — the profile ships art that matches its frame, and a drawn triangle next to it
    /// would look like a placeholder. The polygon below stays as the fallback for a profile that
    /// ships no arrows.</param>
    public static bool TryRenderScrollArrow(bool down, bool enabled, int size, string outputPngPath,
                                            Color accent, string? artPath = null,
                                            TileEmphasis emphasis = TileEmphasis.ShadowLitCore)
    {
        try
        {
            bool drawnFromArt = false;
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                if (artPath is { Length: > 0 } && File.Exists(artPath))
                {
                    // The PNG exactly as it is: no tint and no dimming. The art is drawn for this
                    // key and already says whether it is available; touching its alpha here only
                    // made it look like a rendering fault.
                    using var art = new Bitmap(artPath);
                    float box = size * IconTileFill;
                    g.DrawImage(art, new Rectangle((int)((size - box) / 2f), (int)((size - box) / 2f),
                                                   (int)box, (int)box));

                    drawnFromArt = true;
                }

                if (!drawnFromArt) DrawArrowShape(g, size, down, enabled, accent, emphasis);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>The drawn fallback arrow, for a profile that ships none of its own.</summary>
    private static void DrawArrowShape(Graphics g, int size, bool down, bool enabled,
                                       Color accent, TileEmphasis emphasis)
    {
        {
                float w = size * 0.44f, h = size * 0.26f;
                float cx = size / 2f, cy = size / 2f;
                float tipY = down ? cy + h / 2f : cy - h / 2f;
                float baseY = down ? cy - h / 2f : cy + h / 2f;

                using var path = new GraphicsPath();
                path.AddPolygon(new[]
                {
                    new PointF(cx - w / 2f, baseY),
                    new PointF(cx + w / 2f, baseY),
                    new PointF(cx, tipY),
                });

                Color ink = enabled ? accent : Dim(accent, 0.35f);

                if (emphasis is TileEmphasis.Shadow or TileEmphasis.ShadowLitCore)
                    foreach (var (widthScale, alpha) in ShadowPasses)
                        using (var shadow = new Pen(Color.FromArgb(alpha, Color.Black),
                                                    size * 0.03f * widthScale)
                                   { LineJoin = LineJoin.Round })
                            g.DrawPath(shadow, path);

                using (var fill = new SolidBrush(ink)) g.FillPath(fill, path);

                if (enabled && emphasis == TileEmphasis.ShadowLitCore)
                    using (var core = new SolidBrush(Color.FromArgb(110, CoreLight(accent))))
                    {
                        using var inner = new GraphicsPath();
                        inner.AddPolygon(new[]
                        {
                            new PointF(cx - w / 4f, baseY + (down ? h * 0.16f : -h * 0.16f)),
                            new PointF(cx + w / 4f, baseY + (down ? h * 0.16f : -h * 0.16f)),
                            new PointF(cx, tipY + (down ? -h * 0.18f : h * 0.18f)),
                        });
                        g.FillPath(core, inner);
                    }
        }
    }

    /// <summary>How a live tile's ink is separated from the art behind it.</summary>
    public enum TileEmphasis
    {
        /// <summary>Near-white halo hugging the ink.</summary>
        WhiteHalo,
        /// <summary>Only a soft dark shadow behind the ink — contrast, no extra light.</summary>
        Shadow,
        /// <summary>A dark shadow behind everything, and a WHITE CORE running down the middle
        /// of each lit segment that falls away towards its edges — a block lit from inside, like
        /// a neon tube, instead of a block with light spilling around it.</summary>
        ShadowLitCore,
    }

    /// <summary>Widths and alphas of the dark pad drawn under a lit segment. Wider and much
    /// softer than the light passes: a shadow that is visible AS a shadow has already failed.</summary>
    private static readonly (float WidthScale, int Alpha)[] ShadowPasses =
        { (3.40f, 40), (2.75f, 62), (2.15f, 92), (1.70f, 125), (1.35f, 160) };

    /// <summary>The lit core: fractions of the segment's own thickness, narrowing as they get
    /// whiter, so the centre line reaches pure white and the colour takes back over within a
    /// pixel or two. All strictly INSIDE the block — see the call site.</summary>
    /// <summary>The lit core, as fractions of the segment's own thickness with the alpha each
    /// pass adds. MANY faint passes rather than a few strong ones: the alphas accumulate, so a
    /// long ladder of thin layers is what produces a smooth falloff from white to the block's
    /// colour, while three heavy ones produce visible steps and a hard-edged white bar.</summary>
    private static readonly (float WidthScale, int Alpha)[] CorePasses =
    {
        (0.98f,  6), (0.92f,  8), (0.86f, 10), (0.80f, 12), (0.74f, 14),
        (0.68f, 17), (0.62f, 20), (0.55f, 24), (0.48f, 29), (0.41f, 35),
        // The innermost rungs — the ones that make the bright centre — are deliberately 15%
        // lighter than the ladder's curve would put them: the centre reads as a soft hot spot
        // rather than a solid white dot, while the outer rungs keep the falloff wide.
        (0.34f, 37), (0.27f, 46), (0.20f, 60), (0.13f, 81), (0.08f, 106),
    };

    /// <summary>A block counts as "square" — and gets a square core rather than a bar — once its
    /// length along the arc drops below this multiple of its thickness.</summary>
    private const float SquareBlockRatio = 1.35f;

    /// <summary>Opacity multiplier for a square core — see the call site for why it needs one.</summary>
    private const float SquareCoreBoost = 1.75f;

    /// <summary>The tile size the pixel-denominated tweaks above were judged at — the DisplayPad's
    /// own icon edge. Anything measured in "pixels" is scaled by size/this so it looks the same
    /// on the hardware and in the config dialog's larger preview.</summary>
    private const int DpPixelReference = 102;

    /// <summary>The colour the lit core burns to at its centre: the accent pushed most of the
    /// way to pure cyan and then part of the way to white — a light cyan rather than white, so
    /// the hot spot still belongs to the tile's own hue instead of bleaching out of it.</summary>
    private static Color CoreLight(Color accent) =>
        TowardsWhite(Blend(accent, Color.FromArgb(0, 255, 255), 0.60f), 0.45f);

    /// <summary>Linear mix of two colours, <paramref name="amount"/> of the way from
    /// <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        from.R + (int)((to.R - from.R) * amount),
        from.G + (int)((to.G - from.G) * amount),
        from.B + (int)((to.B - from.B) * amount));

    /// <summary>Multiplier on the "half a stroke width" pull-back at each end of the core (see
    /// the call site, where it is turned into degrees of arc).</summary>
    private const float CoreEndInsetScale = 1.15f;

    /// <summary>Ceiling on that pull-back, as a share of the block's own sweep, so a very short
    /// segment is not trimmed out of existence.</summary>
    private const float CoreMaxInsetShare = 0.34f;

    /// <summary>Pushes a colour towards white by <paramref name="amount"/> — how the cyan accent
    /// becomes the near-white the halo is made of, without hard-coding a second colour that would
    /// stop matching if the profile's accent ever changed.</summary>
    private static Color TowardsWhite(Color c, float amount) => Color.FromArgb(
        c.R + (int)((255 - c.R) * amount),
        c.G + (int)((255 - c.G) * amount),
        c.B + (int)((255 - c.B) * amount));

    /// <summary>The tile's caption over a soft white halo, so a name reads against the profile's
    /// own frame art instead of merging into it.
    ///
    /// <para>The halo is the SAME caption drawn as a white silhouette on a transparent layer,
    /// blurred and composited underneath — same font, same wrap, same shrink-to-fit, because it
    /// goes through the very same drawing call. Doing it as a layer rather than as offset copies
    /// is what keeps it a glow instead of a bold outline.</para></summary>
    private static void DrawSegmentedCaption(Graphics g, int size, string caption,
                                             TileEmphasis emphasis)
    {
        try
        {
            if (emphasis is TileEmphasis.Shadow or TileEmphasis.ShadowLitCore)
            {
                // A ladder from widest-and-faintest to narrowest-and-densest. The shadow has to
                // reach well past the letters AND be nearly solid where it touches them, and one
                // blurred layer cannot do both — the wide radius that gives the reach is the same
                // one that thins out the middle. Each rung adds a little more darkness closer in,
                // which is what makes the falloff gradual instead of a grey patch with an edge.
                // The two widest rungs carry most of the weight now. They act FAR from the
                // glyphs, so leaning on them darkens the art all around the label — under the
                // last line included, where the frame art is brightest — without hardening the
                // rim against the letters, which is what the narrow rungs below do.
                Underlay(g, size, caption, Color.Black, Math.Max(1, size / 5),  gain: 1.25f, times: 2);
                Underlay(g, size, caption, Color.Black, Math.Max(1, size / 9),  gain: 1.15f, times: 1);
                Underlay(g, size, caption, Color.Black, Math.Max(1, size / 14), gain: 1.10f, times: 1);
                Underlay(g, size, caption, Color.Black, Math.Max(1, size / 21), gain: 1.60f, times: 1);
                Underlay(g, size, caption, Color.Black, Math.Max(1, size / 32), gain: 2.40f, times: 1);
            }

            if (emphasis == TileEmphasis.WhiteHalo)
                Underlay(g, size, caption, Color.White, Math.Max(1, size / 52), gain: 3.2f, times: 2);
        }
        catch { /* the underlay is decoration: a failure here must not cost us the caption */ }

        DrawCompactCaption(g, size, caption, null);
    }

    /// <summary>The caption strip of a segmented tile: higher up the key and with tighter leading
    /// than <c>IconImageGenerator.DrawCaption</c>'s.
    ///
    /// <para>Two lines of a name ("LUCO" / "BRONC") are one label, and the font's own line height
    /// leaves them looking like two separate captions; GDI+ has no leading control, so the lines
    /// are placed by hand at <see cref="CaptionLeading"/> of the font size. Everything else is
    /// kept faithful to the shared helper — the same font, the same shrink-to-fit, the same
    /// per-key "Edit icon" overrides — so these tiles still sit next to the others.</para></summary>
    private static void DrawCompactCaption(Graphics g, int size, string caption, Color? colorOverride)
    {
        caption = IconStyleScope.OverrideCaption ?? caption;
        if (caption.Length == 0) return;

        string[] lines = caption.Split('\n');
        // The usable width is narrower than the tile: the frame art has a lit border, and a
        // caption that runs up against it looks cramped however well it fits geometrically.
        var strip = new RectangleF(size * 0.09f, size * SegCaptionTop,
                                   size * 0.82f, size * (SegCaptionBottom - SegCaptionTop));

        float start = (float)(IconStyleScope.OverrideFontSize
            ?? (Math.Max(9f, size * 0.13f) + 4f) * CaptionFontScaleWithText);

        float fontSize = start;
        for (; fontSize >= 8f; fontSize -= 1f)
        {
            using var probe = IconImageGenerator.CaptionFont(fontSize);
            bool fits = lines.Length * fontSize * CaptionLeading <= strip.Height;
            foreach (string line in lines)
                fits &= g.MeasureString(line, probe).Width <= strip.Width;
            if (fits) break;
        }

        using var font = IconImageGenerator.CaptionFont(fontSize);
        using var brush = new SolidBrush(colorOverride ?? IconStyleScope.OverrideText ?? Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center,
                                              LineAlignment = StringAlignment.Center };

        float step = fontSize * CaptionLeading;
        float top = strip.Y + (strip.Height - lines.Length * step) / 2f;
        for (int i = 0; i < lines.Length; i++)
            g.DrawString(lines[i], font, brush,
                         new RectangleF(strip.X, top + i * step, strip.Width, step), format);
    }

    /// <summary>Caption strip of a segmented tile — a few points higher than the shared one, so
    /// the label sits closer to its dial and away from the frame art's bottom edge.</summary>
    /// <summary>How much of the key's edge an ability icon covers. The rest is the margin that
    /// keeps it clear of the profile frame's glowing border.</summary>
    private const float IconTileFill = 0.86f;

    private const float SegCaptionTop    = 0.425f;
    private const float SegCaptionBottom = 0.90f;

    /// <summary>Line height as a multiple of the font size. Below 1 the ascenders and descenders
    /// of adjacent lines would start to touch.</summary>
    private const float CaptionLeading = 1.02f;

    /// <summary>Draws the caption as a flat silhouette of <paramref name="colour"/> on its own
    /// layer, blurs it and composites it under the real text <paramref name="times"/> times.
    /// Going through the very same <see cref="DrawCompactCaption"/> is what keeps font, layout
    /// and shrink-to-fit identical, so the underlay can never drift from the letters it belongs
    /// to.</summary>
    private static void Underlay(Graphics g, int size, string caption, Color colour,
                                 int radius, float gain, int times)
    {
        // The layer is BIGGER than the tile and the caption is drawn inset into it. The bottom
        // line of a caption sits close to the tile's edge, so a blur on a same-size layer runs
        // out of pixels exactly there and the shadow fades away under the last line — which is
        // the one place it is most needed, since that edge is where the frame art is brightest.
        int margin = radius + 2;
        using var layer = new Bitmap(size + margin * 2, size + margin * 2);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.SmoothingMode = SmoothingMode.HighQuality;
            lg.TextRenderingHint = TextRenderingHint.AntiAlias;
            lg.TranslateTransform(margin, margin);
            DrawCompactCaption(lg, size, caption, colour);
        }
        BoxBlurAlpha(layer, radius, gain, colour);
        for (int i = 0; i < times; i++) g.DrawImage(layer, -margin, -margin);
    }

    /// <param name="gain">Multiplies the blurred alpha before it is written back (clamped at
    /// full opacity). A gain above 1 is what turns a soft falloff into a solid rim: the pixels
    /// nearest the ink saturate to white while the far tail still fades out.</param>
    /// <param name="ink">The flat colour the blurred layer is tinted with — the blur only
    /// carries alpha, so the silhouette's colour is written back here.</param>
    private static void BoxBlurAlpha(Bitmap bmp, int radius, float gain, Color ink)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            var alpha = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    alpha[y * w + x] = buf[y * stride + x * 4 + 3];

            var tmp = new float[w * h];
            int span = radius * 2 + 1;

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int k = -radius; k <= radius; k++)
                        sum += alpha[y * w + Math.Clamp(x + k, 0, w - 1)];
                    tmp[y * w + x] = sum / span;
                }

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int k = -radius; k <= radius; k++)
                        sum += tmp[Math.Clamp(y + k, 0, h - 1) * w + x];

                    int a = (int)Math.Clamp(sum / span * gain, 0f, 255f);
                    int i = y * stride + x * 4;
                    buf[i]     = ink.B;                       // BGRA order
                    buf[i + 1] = ink.G;
                    buf[i + 2] = ink.R;
                    buf[i + 3] = (byte)a;
                }

            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    // Live-tile layout when a generated caption is shown: value up top, a tall caption strip
    // below it in a bigger font that can wrap to two lines.
    private const float ValueBottomWithCaption   = 0.52f;   // value region is [0 .. 0.52·size]
    private const float CaptionTopWithText       = 0.50f;   // caption strip is [0.50 .. 0.98·size]
    private const float CaptionFontScaleWithText = 1.5f;

    /// <summary>
    /// Draws a short reading ("58°", "73%", "45", "12.4M") centred in <paramref name="region"/>.
    /// <list type="bullet">
    /// <item>Centring is on the NUMERIC CORE only — a trailing <c>°</c>/<c>%</c> is drawn
    ///   afterwards as a small superscript to the right and is NOT counted, so the digits sit
    ///   dead-centre (both axes) and the mark hangs off the right, still fully visible.</item>
    /// <item>The digit size is FIXED per length class (chosen from a reference string of the
    ///   same clamped length made of the widest digit), so "61" and "40" — every 2-digit
    ///   reading — render at exactly the same size.</item>
    /// <item>All positioning is from real <see cref="GraphicsPath"/> ink bounds, not GDI+
    ///   string metrics (which pad the degree sign's right bearing and a full line's descender —
    ///   why a metrics-centred "58°" reads as high and to the left).</item>
    /// </list>
    /// <paramref name="tileSize"/> is the whole tile edge, so the digit box is a fixed fraction
    /// of the KEY regardless of how tall <paramref name="region"/> is (captioned vs not).
    /// </summary>
    private static void DrawCenteredValue(Graphics g, string text, RectangleF region, int tileSize, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Split off a trailing unit mark that shouldn't influence centring.
        string mark = "";
        char lastCh = text[^1];
        if ((lastCh == '°' || lastCh == '%') && text.Length > 1)
        {
            mark = lastCh.ToString();
            text = text[..^1];
        }

        using var probe = IconImageGenerator.CaptionFont(10f);
        FontFamily family = probe.FontFamily;
        int style = (int)probe.Style;
        using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        using var brush = new SolidBrush(color);

        // Digit box: a fixed fraction of the KEY (clamped to the region so it can't overflow a
        // shrunk captioned region), leaving room on the right for the mark and margin all round.
        float coreMaxW = Math.Min(tileSize * 0.50f, region.Width * 0.92f);
        float coreMaxH = Math.Min(tileSize * 0.40f, region.Height * 0.92f);

        // FIXED em from a reference of the same (clamped) length, widest digit — so every
        // reading in a length class comes out identical; 1/2 digits share one size.
        string sizeRef = new string('8', Math.Clamp(text.Length, 2, 4));
        float em = FitEm(sizeRef, family, style, sf, coreMaxW, coreMaxH);

        using var core = new GraphicsPath();
        core.AddString(text, family, style, em, new PointF(0f, 0f), sf);
        RectangleF cb = core.GetBounds();
        if (cb.Width <= 0f || cb.Height <= 0f) return;

        using (var m = new Matrix())
        {
            m.Translate(region.X + region.Width / 2f - (cb.X + cb.Width / 2f),
                        region.Y + region.Height / 2f - (cb.Y + cb.Height / 2f));
            core.Transform(m);
        }
        g.FillPath(brush, core);
        cb = core.GetBounds();   // bounds after the translate

        if (mark.Length == 0) return;

        // The mark: ~58% of the digit em, its TOP aligned with the digits' top (a superscript),
        // just right of them, clamped so it can't run off the tile.
        using var mp = new GraphicsPath();
        mp.AddString(mark, family, style, em * 0.58f, new PointF(0f, 0f), sf);
        RectangleF mb = mp.GetBounds();
        if (mb.Width <= 0f || mb.Height <= 0f) return;

        float mx = cb.Right + tileSize * 0.012f - mb.X;
        float maxMx = region.Right - mb.Width - mb.X;
        if (mx > maxMx) mx = maxMx;
        using var mm = new Matrix();
        mm.Translate(mx, cb.Y - mb.Y);
        mp.Transform(mm);
        g.FillPath(brush, mp);
    }

    /// <summary>Largest AddString em size at which <paramref name="refText"/>'s path bounds fit
    /// <paramref name="maxW"/> × <paramref name="maxH"/> — the fixed size for its length class.</summary>
    private static float FitEm(string refText, FontFamily family, int style, StringFormat sf,
                               float maxW, float maxH)
    {
        for (float em = maxH * 2.2f; em >= 6f; em -= 1f)
        {
            using var p = new GraphicsPath();
            p.AddString(refText, family, style, em, new PointF(0f, 0f), sf);
            RectangleF b = p.GetBounds();
            if (b.Width > 0f && b.Height > 0f && b.Width <= maxW && b.Height <= maxH) return em;
        }
        return 6f;
    }

    /// <summary>Widest string each of the speed-test tile's three bands ever has to hold across
    /// ping/download/upload — used to pick ONE font size per band up front (see
    /// <see cref="TryRenderSpeedTile"/>'s remarks) rather than letting each tile's own, often
    /// shorter, text auto-grow to fill the band and end up a different size from its siblings.</summary>
    private const string SpeedValueSizeReference = "888.8";

    /// <summary>The widest reading a dial's centre normally holds, so every dial value keeps one
    /// size (see <c>DrawFitted</c>'s sizeReference).</summary>
    private const string RingValueSizeReference = "100%";
    private const string SpeedCaptionSizeReference = "download";
    private const string SpeedUnitSizeReference = "Mbps";

    /// <summary>
    /// Speed-test variant of <see cref="TryRenderGauge"/>: caption strip above ("ping" /
    /// "upload" / "download"), value in the middle, unit strip below ("ms" / "Mbps") — three
    /// stacked bands instead of TryRenderGauge's caption-below layout.
    /// <para>
    /// The caption and unit strips are always sized from the widest text that band ever has to
    /// hold (<see cref="SpeedCaptionSizeReference"/>, <see cref="SpeedUnitSizeReference"/>), so
    /// "ping"/"ms" render at the same size as their longer siblings "download"/"Mbps" instead
    /// of auto-growing to fill their own box. The value, by contrast, sizes itself from its OWN
    /// text via <paramref name="ownValueSize"/> — up/down share a size the way the strips do
    /// (<see cref="SpeedValueSizeReference"/>), but ping's few digits are allowed to fill the
    /// tile the way a lone reading normally would. <paramref name="valueTopPad"/> nudges the
    /// caption+value block down by that fraction of the tile (unit strip unaffected) — ping uses
    /// it to keep its bigger self-sized number clear of the top edge.
    /// </para>
    /// <para>
    /// The ring (shown mid-run, before a leg has its own number yet) is sized to the band left
    /// between the two text strips rather than the whole tile.
    /// </para>
    /// </summary>
    /// <param name="edgeInset">Fraction of the tile kept clear on every side — for a profile whose
    /// frame art runs close to the key's edge, where edge-to-edge caption and unit strips would sit
    /// on top of the frame. 0 keeps the original full-bleed layout.</param>
    public static bool TryRenderSpeedTile(string valueText, double? fraction, string caption,
                                          string unitText, int size, string outputPngPath,
                                          bool ownValueSize = false, float valueTopPad = 0f,
                                          float edgeInset = 0f, string? valueSizeReference = null)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                bool withCaption = caption.Length > 0;
                bool withUnit = unitText.Length > 0;
                float inset = size * edgeInset;
                // The strips give up a little height when inset, so the value in the middle keeps
                // most of its room instead of paying for both margins alone.
                float band = edgeInset > 0 ? 0.20f : 0.22f;
                float captionH = withCaption ? size * band : 0f;
                float unitH = withUnit ? size * band : 0f;
                float pad = size * valueTopPad;
                float midTop = inset + captionH + pad;
                float midHeight = size - 2 * inset - captionH - unitH - pad;
                float innerW = size - 2 * inset;

                var captionRect = new RectangleF(inset, inset + pad, innerW, captionH);
                var unitRect = new RectangleF(inset, size - inset - unitH, innerW, unitH);
                var valueRect = new RectangleF(inset + innerW * 0.04f, midTop, innerW * 0.92f, midHeight);

                if (fraction is double f)
                {
                    float ring = Math.Min(innerW * 0.9f, midHeight) * 0.92f;
                    float left = (size - ring) / 2f;
                    float top = midTop + (midHeight - ring) / 2f;
                    float thickness = Math.Max(3f, size * 0.075f);

                    var rect = new RectangleF(left + thickness / 2f, top + thickness / 2f,
                                              ring - thickness, ring - thickness);

                    using (var track = new Pen(Dim(TextColor, 0.25f), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(track, rect, 135f, 270f);

                    float sweep = (float)(Math.Clamp(f, 0d, 1d) * 270d);
                    if (sweep > 0.5f)
                        using (var pen = new Pen(IconImageGenerator.TileAccent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawArc(pen, rect, 135f, sweep);

                    float inner = ring * 0.62f;
                    DrawFitted(g, valueText,
                               new RectangleF(left + (ring - inner) / 2f, top + (ring - inner) / 2f, inner, inner),
                               size * 0.30f, TextColor, sizeReference: RingValueSizeReference);
                }
                else
                {
                    // One size per key: from the caller's reference when there is one, else the
                    // shared "888.8" — never from the value alone, which made a clock grow and
                    // shrink as its digits changed. ownValueSize only drops the shared reference.
                    string reference = valueSizeReference ?? (ownValueSize ? valueText : SpeedValueSizeReference);
                    float valuePx = FitFontSize(g, reference, valueRect, size * 0.62f);
                    DrawFitted(g, valueText, valueRect, valuePx, TextColor, sizeReference: reference);
                }

                if (withCaption)
                {
                    float captionPx = FitFontSize(g, SpeedCaptionSizeReference, captionRect, size * 0.16f);
                    DrawFitted(g, caption, captionRect, captionPx, TextColor);
                }
                if (withUnit)
                {
                    float unitPx = FitFontSize(g, SpeedUnitSizeReference, unitRect, size * 0.20f);
                    DrawFitted(g, unitText, unitRect, unitPx, TextColor);
                }
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>One row of <see cref="TryRenderBarsTile"/>: a label on the left, the value on the
    /// right, and a bar under both. A null fraction draws an empty track (value not known).</summary>
    /// <param name="ShowBar">False for a row that is only label and value (X/Y/Z coordinates).</param>
    public readonly record struct BarRow(string Label, string Value, double? Fraction, Color Color, bool ShowBar = true);

    /// <summary>The widest value a bar row normally shows — rows are sized from it, not from their
    /// current value, so "9" and "19" hearts are drawn the same size.</summary>
    private const string BarValueSizeReference = "-88888";

    /// <summary>
    /// Several readings stacked on one key — Minecraft's health and hunger, or its level with the
    /// XP bar and the armour under it: a big number on top when <paramref name="bigText"/> is given,
    /// then one row per <paramref name="rows"/> entry, each a label/value line over a bar.
    ///
    /// <para>Bars are drawn in each row's own colour (the game's: red hearts, brown drumsticks,
    /// green XP), not the tile accent — on a key that carries two readings, colour is what tells
    /// them apart at a glance. Labels and values use the tile's text colour like every other
    /// tile, so the page still reads as one panel.</para>
    /// </summary>
    /// <param name="valueSizeReference">The widest value any row shows ("88" for Minecraft's 0-20
    /// bars, "-8888" for coordinates): row text is sized from label + this, so it is as big as the
    /// key allows without ever changing size with the value.</param>
    public static bool TryRenderBarsTile(string? bigText, Color bigColor, IReadOnlyList<BarRow> rows,
                                         int size, string outputPngPath, float edgeInset = 0f,
                                         string valueSizeReference = BarValueSizeReference)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                float inset = size * Math.Max(edgeInset, 0.06f);
                float innerW = size - 2 * inset;
                float top = inset, bottom = size - inset;

                // A row with neither label nor value is a bare bar (the XP bar under the level):
                // it takes a thin slice, and the text rows share the rest.
                static bool BarOnly(BarRow r) => r.Label.Length == 0 && r.Value.Length == 0;
                int textRows = 0, barRows = 0;
                foreach (var r in rows) { if (BarOnly(r)) barRows++; else textRows++; }

                if (bigText is { Length: > 0 })
                {
                    float bigH = (bottom - top) * (textRows > 1 ? 0.36f : 0.48f);
                    DrawFitted(g, bigText, new RectangleF(inset, top, innerW, bigH), size * 0.40f, bigColor,
                               sizeReference: "888");
                    top += bigH;
                }
                if (rows.Count == 0) return Save(canvas, outputPngPath);

                float barRowH = size * 0.10f;
                float rowH = textRows > 0 ? (bottom - top - barRows * barRowH) / textRows : 0f;
                float labelPx = size * 0.30f;
                // ONE size for every text row of the key, from its longest label: rows that differ
                // in size read as different kinds of thing, and none may change with its value.
                float rowPx = labelPx;
                foreach (var r in rows)
                {
                    if (BarOnly(r)) continue;
                    float h = r.ShowBar ? rowH * 0.60f : rowH * 0.9f;
                    rowPx = Math.Min(rowPx, FitFontSize(g, r.Label + "  " + valueSizeReference,
                                                        new RectangleF(0, 0, innerW, h), labelPx));
                }
                foreach (var row in rows)
                {
                    if (BarOnly(row))
                    {
                        float bh = Math.Max(4f, barRowH * 0.6f);
                        float by = top + (barRowH - bh) / 2f;
                        using (var track = new SolidBrush(Dim(row.Color, 0.28f)))
                            g.FillRectangle(track, inset, by, innerW, bh);
                        if (row.Fraction is double bf && bf > 0)
                            using (var fill = new SolidBrush(row.Color))
                                g.FillRectangle(fill, inset, by, (float)(innerW * Math.Clamp(bf, 0, 1)), bh);
                        top += barRowH;
                        continue;
                    }
                    float textH = row.ShowBar ? rowH * 0.60f : rowH * 0.9f;
                    float barH = Math.Max(3f, Math.Min(rowH * 0.24f, size * 0.07f));
                    var labelRect = new RectangleF(inset, top, innerW, textH);
                    using (var font = IconImageGenerator.CaptionFont(rowPx))
                    using (var brush = new SolidBrush(TextColor))
                    {
                        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                        g.DrawString(row.Label, font, brush, labelRect, left);
                        g.DrawString(row.Value, font, brush, labelRect, right);
                    }

                    if (row.ShowBar)
                    {
                        float barY = top + textH + (rowH - textH - barH) * 0.35f;
                        using (var track = new SolidBrush(Dim(row.Color, 0.28f)))
                            g.FillRectangle(track, inset, barY, innerW, barH);
                        if (row.Fraction is double f && f > 0)
                            using (var fill = new SolidBrush(row.Color))
                                g.FillRectangle(fill, inset, barY, (float)(innerW * Math.Clamp(f, 0, 1)), barH);
                    }
                    top += rowH;
                }
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>Every digit replaced by "8", the widest in proportional faces — so a value's size
    /// depends on how many digits it has, never on which ones.</summary>
    private static string DigitsAsEights(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsDigit(chars[i])) chars[i] = '8';
        return new string(chars);
    }

    /// <summary>What a value is measured as: the wider (at a probe size) of the text and the
    /// reference, both with digits as "8". See <see cref="DrawFitted"/>'s sizeReference.</summary>
    private static string StableMeasure(string text, string? reference, Graphics g, Func<float, Font> face)
    {
        string t = DigitsAsEights(text);
        if (reference is not { Length: > 0 }) return t;
        string r = DigitsAsEights(reference);
        using var probe = face(40f);
        return g.MeasureString(r, probe).Width > g.MeasureString(t, probe).Width ? r : t;
    }

    /// <summary>The largest font size (down to the same 8px floor <see cref="DrawFitted"/>
    /// uses) at which <paramref name="text"/> fits <paramref name="rect"/> both ways — the
    /// measuring half of <see cref="DrawFitted"/>'s shrink loop, split out so a band's size can
    /// be decided from a reference string instead of the text actually being drawn.</summary>
    private static float FitFontSize(Graphics g, string text, RectangleF rect, float startPx)
    {
        for (float px = startPx; px >= 8f; px -= 1f)
        {
            using var font = IconImageGenerator.CaptionFont(px);
            SizeF measured = g.MeasureString(DigitsAsEights(text), font);
            if (measured.Width <= rect.Width && measured.Height <= rect.Height) return px;
        }
        return 8f;
    }

    // ─────────────────────── Elite Dangerous status ───────────────────────

    /// <summary>What an Elite Dangerous status tile (<c>dp_edstatus</c>) is showing.</summary>
    public enum EdState
    {
        /// <summary>Game not running, or its status file never written — the tile says so
        /// instead of claiming the gear is up.</summary>
        Unknown,
        /// <summary>The state's bit is clear.</summary>
        Off,
        /// <summary>The state's bit is set.</summary>
        On,
        /// <summary>Set, and it's something the pilot needs to react to (overheating, low fuel,
        /// interdiction, flight assist off).</summary>
        Alarm,
    }

    // Cockpit palette, taken from the game's own HUD rather than the tile accent: these keys sit
    // on the desk next to the screen and reading as "part of the cockpit" is the entire point.
    private static readonly Color EdDefaultAccent = Color.FromArgb(255, 122, 20);   // ED orange
    private static readonly Color EdAlarm         = Color.FromArgb(232,  66, 46);
    private static readonly Color EdUnknown       = Color.FromArgb( 70,  70, 74);

    /// <summary>Hue the Elite tiles are drawn in. The caller sets it — the whole profile changes
    /// colour with the ship's flight-assist state (see <c>GameProfileTheme</c>), so the accent
    /// cannot be a constant here. Null falls back to the game's own orange.
    ///
    /// <para>Deliberately a plain static: every render of this service is serialised on the tile
    /// timer's single thread (see <c>DpLiveTileService.PushDevice</c>), so there is no window in
    /// which two renders would disagree about the colour.</para></summary>
    public static Color? EdAccentOverride { get; set; }

    private static Color EdAccent => EdAccentOverride ?? EdDefaultAccent;

    // "Lit" is the accent brightened towards white, "unlit" the same hue heavily dimmed — derived
    // rather than hard-coded so a new accent needs one colour, not three.
    private static Color EdLit => Lighten(EdAccent, 0.42f);
    private static Color EdUnlit => Dim(EdAccent, 0.42f);

    private static Color Lighten(Color c, float t) => Color.FromArgb(
        (int)(c.R + (255 - c.R) * t),
        (int)(c.G + (255 - c.G) * t),
        (int)(c.B + (255 - c.B) * t));

    private static Color EdColor(EdState s) => s switch
    {
        EdState.On      => EdLit,
        EdState.Alarm   => EdAlarm,
        EdState.Off     => EdUnlit,
        _               => EdUnknown,
    };

    /// <summary>
    /// Renders one boolean cockpit-state tile: a square annunciator that is hollow when the
    /// state is off and filled when it's on — the same shape the game's own dashboard uses for
    /// LANDING GEAR / CARGO SCOOP, so the pad reads like an extension of the panel.
    /// <para><see cref="EdState.Unknown"/> draws a grey hollow square with a dash: the game
    /// isn't running, and a tile that looked "off" would be indistinguishable from a real
    /// retracted gear.</para>
    /// </summary>
    public static bool TryRenderEdStatus(EdState state, string caption, int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                // NO annunciator shape: an Elite tile is a backlit HUD label over the
                // profile's cockpit art, and nothing else. The rounded square this used to draw
                // (hollow when off, filled when on) sat on top of that art and read as a second,
                // competing widget — dropped on request 2026-09-05. The state is still legible:
                // ApplyEdStyle picks the lit/dim background and caption colour for it, and the
                // flight-assist key its red/green pair.
                //
                // NOTE the shape of this method: the Save() MUST stay outside the Graphics'
                // using block. GDI+ keeps the bitmap locked while a Graphics is attached to it,
                // so saving from in here throws — and the catch below would swallow it into a
                // silent "tile never renders", which is exactly what an early return did.
                IconImageGenerator.DrawCenteredText(g, size, caption);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    /// <summary>
    /// Renders the SYS/ENG/WEP power distribution as three vertical bars, the cockpit's own
    /// gauge. The game reports pips in HALF units (0..8 for the four-pip maximum), which is why
    /// each bar is drawn as four segments that can each be half-lit.
    /// </summary>
    public static bool TryRenderEdPips(int sys, int eng, int wep, string caption,
                                       int size, string outputPngPath)
    {
        try
        {
            using var canvas = new Bitmap(size, size);
            using (var g = NewGraphics(canvas, size))
            {
                bool withCaption = caption.Length > 0;
                float areaTop = size * 0.08f;
                float areaBottom = size * (withCaption ? 0.52f : 0.80f);
                float areaHeight = areaBottom - areaTop;

                float barW = size * 0.16f;
                float gap = size * 0.10f;
                float totalW = barW * 3 + gap * 2;
                float x0 = (size - totalW) / 2f;

                int[] halves = { Math.Clamp(sys, 0, 8), Math.Clamp(eng, 0, 8), Math.Clamp(wep, 0, 8) };
                string[] labels = { "S", "E", "W" };

                const int segs = 4;
                float segGap = areaHeight * 0.06f;
                float segH = (areaHeight - segGap * (segs - 1)) / segs;

                for (int b = 0; b < 3; b++)
                {
                    float x = x0 + b * (barW + gap);
                    for (int s = 0; s < segs; s++)
                    {
                        // Segments fill from the BOTTOM, like the cockpit gauge.
                        float y = areaBottom - segH - s * (segH + segGap);
                        var seg = new RectangleF(x, y, barW, segH);

                        int halvesIntoThisSeg = halves[b] - s * 2;      // 2 halves per segment
                        using var dimBrush = new SolidBrush(EdUnlit);
                        g.FillRectangle(dimBrush, seg);

                        if (halvesIntoThisSeg > 0)
                        {
                            float frac = halvesIntoThisSeg >= 2 ? 1f : 0.5f;
                            var lit = new RectangleF(seg.X, seg.Bottom - seg.Height * frac,
                                                     seg.Width, seg.Height * frac);
                            using var onBrush = new SolidBrush(EdLit);
                            g.FillRectangle(onBrush, lit);
                        }
                    }

                    // Bar letter directly under its bar — SYS/ENG/WEP won't fit at this width.
                    if (!withCaption)
                        DrawFitted(g, labels[b],
                                   new RectangleF(x, areaBottom + size * 0.02f, barW, size * 0.16f),
                                   size * 0.16f, EdLit);
                }

                if (withCaption)
                    IconImageGenerator.DrawCaption(g, size, caption,
                        topFrac: CaptionTopWithText, startFontScale: CaptionFontScaleWithText);
            }
            return Save(canvas, outputPngPath);
        }
        catch { return false; }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
        IconImageGenerator.PaintTileBackground(g, size);
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
    /// the whole point of a single-value key is that it's readable across the desk. With a
    /// caption below, the value is centred in nearly the whole tile (not just the strip above
    /// the caption) so a short reading like "54°" sits mid-tile instead of hugging the top edge;
    /// the caption's own strip (<see cref="IconImageGenerator.DrawCaption"/>, top-aligned at
    /// y≈0.68) still clears it since the shrink-to-fit text is far shorter than this band.</summary>
    private static void DrawBigText(Graphics g, int size, string text, bool withCaption)
    {
        float height = withCaption ? size * 0.90f : size;
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
    /// <param name="boostPx">Drawn this much larger than the size that measured as fitting.
    /// A point of deliberate overflow: inside a dial the reading can crowd its box by a pixel
    /// and still look right, and stepping the shrink loop by whole pixels otherwise costs a
    /// visible amount of size on a 102 px key.</param>
    /// <param name="semibold">Force the stock semibold face instead of the key's own font.</param>
    /// <param name="sizeReference">The widest text this spot normally shows ("100%", "88:88"). The
    /// size is the one that fits BOTH it and the text, so a reading that changes keeps one size
    /// instead of growing and shrinking with its own width — "9%" draws as big as "73%". Either
    /// way digits are measured as "8", the widest, so "11:11" and "08:08" come out the same.</param>
    private static void DrawFitted(Graphics g, string text, RectangleF rect, float startPx, Color color,
                                   float boostPx = 0f, bool semibold = false, string? sizeReference = null)
    {
        Font Face(float px) =>
            semibold ? IconImageGenerator.SemiboldFont(px) : IconImageGenerator.CaptionFont(px);

        // Measure the wider of text and reference in digit-normalised form; draw the real text.
        string measureText = StableMeasure(text, sizeReference, g, Face);

        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        for (float px = startPx; px >= 8f; px -= 1f)
        {
            using var font = Face(px);
            SizeF measured = g.MeasureString(measureText, font);
            if (measured.Width <= rect.Width && measured.Height <= rect.Height)
            {
                using var drawn = Face(px + boostPx);
                // The boosted string is drawn into a box grown to match. GDI+ CLIPS a
                // NoWrap string to its rectangle, so drawing the bigger face into the
                // original box silently ate the last digit ("24" came out as "2").
                var grown = RectangleF.Inflate(rect, boostPx * 3f, boostPx * 3f);
                g.DrawString(text, drawn, brush, grown, format);
                return;
            }
        }
        using var smallest = Face(8f);
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
