using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace K2.App.Services;

/// <summary>
/// Reads the TEXT printed inside a screen probe's rectangle — a health number, an ammo count, the
/// name of the current weapon — using Windows' own OCR engine (<c>Windows.Media.Ocr</c>).
///
/// <para>
/// <b>Why the OS engine and not a bundled one.</b> It is already installed with the language packs
/// the user has, it costs nothing to ship, and it runs locally. When no OCR language is installed
/// at all the engine simply cannot be created: every read then reports "no text", which the tile
/// draws as a dash — the same honest answer it gives for a game that is not running.
/// </para>
///
/// <para>
/// <b>The crop is upscaled before recognition.</b> A HUD number is often 12-16 px tall, well under
/// what the engine expects; blowing the rectangle up (and greyscaling it) is the difference between
/// "85" and nothing at all. The factor is capped so a large rectangle isn't turned into a bitmap
/// that costs more to recognise than the reading is worth.
/// </para>
/// </summary>
internal static class ScreenTextReader
{
    /// <summary>Shortest side the crop is scaled up to before OCR, in pixels. The engine wants
    /// real letters, not a thumbnail: a 120x70 crop of a perfectly clean "85" reads as nothing,
    /// and the same crop at twice the size reads correctly (measured).</summary>
    private const int TargetShortSide = 100;

    /// <summary>Ceiling on the upscale factor — past this the extra pixels are interpolation, not
    /// information, and only cost time.</summary>
    private const int MaxScale = 6;

    /// <summary>Tolerance at which the colour filter is off — the whole 0-255 range matches, so
    /// filtering would only paint the crop black.</summary>
    public const int MaxTolerance = 255;

    private static readonly object _gate = new();
    private static OcrEngine? _engine;
    private static bool _engineTried;

    /// <summary>True when this machine can OCR at all (an OCR language pack is installed). Used by
    /// the calibration dialog to say so rather than leaving the user guessing why every reading is
    /// a dash.</summary>
    public static bool Available
    {
        get { lock (_gate) return Engine() is not null; }
    }

    /// <summary>The text inside <paramref name="rect"/> of <paramref name="frame"/>, or "" when
    /// there is nothing to read (or no OCR engine). Never throws.</summary>
    /// <param name="refColor">Colour the text is drawn in (<c>0xRRGGBB</c>), used with
    /// <paramref name="tolerance"/> to throw the rest of the frame away before recognising: a HUD
    /// number sits on top of the game's own picture, and the engine reads a clean two-colour crop
    /// far better than it reads white-on-anything.</param>
    /// <param name="tolerance">How far a pixel may be from <paramref name="refColor"/> and still
    /// count as ink, 0-255. At the maximum the filter is skipped entirely — that is the "read
    /// whatever is there" setting.</param>
    public static string Read(Bitmap frame, Rectangle rect, int refColor = 0, int tolerance = MaxTolerance)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return "";
        try
        {
            lock (_gate)
            {
                if (Engine() is not { } engine) return "";

                using var crop = Upscaled(frame, rect);
                if (tolerance < MaxTolerance) IsolateColour(crop, refColor, tolerance);
                using var page = WithQuietZone(crop);
                using var ms = new MemoryStream();
                page.Save(ms, ImageFormat.Bmp);

                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(ms.ToArray());
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
                stream.Seek(0);

                var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
                // The format is NOT incidental. Decoding a 32-bit BMP hands back an alpha mode of
                // "Straight", and the OCR engine returns an EMPTY result for one — no exception,
                // no lines, just nothing, which is the hardest kind of failure to see. Asking the
                // decoder for premultiplied Bgra8 is what makes it read (verified: same crop,
                // "Straight" reads "", premultiplied reads "85").
                using var software = decoder
                    .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                    .AsTask().GetAwaiter().GetResult();
                var result = engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();
                stream.Dispose();

                return (result?.Text ?? "").Trim();
            }
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] OCR failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>The first number in <paramref name="text"/>, or null when it holds none. Both
    /// decimal separators are accepted and thousands separators are dropped: OCR returns whatever
    /// the game printed, and a HUD writes "1,250" or "1.250" depending on where it was localised.</summary>
    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Digits, then optionally ONE separator group. A trailing "/100" (as in "85/100") is left
        // to the next match and ignored — the first number is the reading.
        var m = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        if (!m.Success) return null;

        string s = m.Value.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    /// <summary>Characters a document OCR engine hands back for a digit drawn in a GAME's font, and
    /// the digit each one really was. The one that started this list is the <b>slashed zero</b> —
    /// a 0 with a bar through it, which the engine reads as "Ø" or "O" because that is what it looks
    /// like in a book; the rest are the usual suspects of a squared, condensed HUD face.</summary>
    private static readonly Dictionary<char, char> DigitLookalikes = new()
    {
        ['O'] = '0', ['o'] = '0', ['Ø'] = '0', ['ø'] = '0', ['°'] = '0',
        ['Q'] = '0', ['D'] = '0', ['○'] = '0', ['¤'] = '0',
        ['I'] = '1', ['l'] = '1', ['i'] = '1', ['|'] = '1', ['!'] = '1', ['¦'] = '1',
        ['Z'] = '2', ['z'] = '2',
        ['S'] = '5', ['s'] = '5',
        ['G'] = '6', ['b'] = '6',
        ['T'] = '7', ['?'] = '7',
        ['B'] = '8',
        ['g'] = '9', ['q'] = '9',
    };

    /// <summary>An OCR result read as a NUMBER rather than as words.
    ///
    /// <para>Three things happen, and all three are only safe BECAUSE the probe says "this
    /// rectangle holds a number": a lookalike letter becomes the digit it was (see
    /// <see cref="DigitLookalikes"/> — this is what makes a slashed zero readable), whitespace inside
    /// the number goes away (a HUD's wide letter-spacing makes the engine split "100" into "1 00"),
    /// and anything else that is not part of a number is dropped instead of being left to confuse
    /// <see cref="ParseNumber"/>. A TEXT probe gets none of it: there, a letter is a letter.</para></summary>
    public static string NormalizeNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '/' or '%') sb.Append(c);
            else if (DigitLookalikes.TryGetValue(c, out char digit)) sb.Append(digit);
            // Everything else — spaces, the unit the HUD prints, the engine's own noise — is dropped.
        }
        return sb.ToString();
    }

    /// <summary>The engine, created once. Null when the machine has no OCR language installed —
    /// tried once and remembered, since the answer cannot change while K2 runs.</summary>
    private static OcrEngine? Engine()
    {
        if (_engineTried) return _engine;
        _engineTried = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null) App.WriteLog("[PROBE] no OCR language installed — text readings unavailable");
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] OCR engine unavailable: {ex.Message}");
            _engine = null;
        }
        return _engine;
    }

    /// <summary>Repaints the crop in place as BLACK TEXT ON WHITE: every pixel within
    /// <paramref name="tolerance"/> of <paramref name="refColor"/> becomes the ink, everything
    /// else the paper.
    ///
    /// <para>This is what makes a HUD readable to an OCR engine trained on documents. The game's
    /// own artwork behind the number is not "background noise" to it — it is more shapes to
    /// recognise — and dropping every pixel that isn't the number's own colour removes them
    /// outright instead of hoping the engine ignores them.</para></summary>
    private static void IsolateColour(Bitmap crop, int refColor, int tolerance)
    {
        int refR = (refColor >> 16) & 0xFF, refG = (refColor >> 8) & 0xFF, refB = refColor & 0xFF;
        int tol = Math.Clamp(tolerance, 0, 255);

        var rect = new Rectangle(0, 0, crop.Width, crop.Height);
        var data = crop.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* baseAddr = (byte*)data.Scan0;
                for (int y = 0; y < rect.Height; y++)
                {
                    byte* row = baseAddr + (long)y * data.Stride;
                    for (int x = 0; x < rect.Width; x++)
                    {
                        byte* px = row + (long)x * 4;               // BGRA
                        // Distance on the WORST channel, the same measure the fill probe uses.
                        int dist = Math.Max(Math.Max(Math.Abs(px[2] - refR), Math.Abs(px[1] - refG)),
                                            Math.Abs(px[0] - refB));
                        // A RAMP, not a hard threshold: a pixel dead on the colour is black, one at
                        // the edge of tolerance is pale grey, anything further is paper. That keeps
                        // the glyph's antialiased edge, which is what the engine was trained on —
                        // a hard two-colour cutout reads noticeably worse.
                        byte v = dist <= tol ? (byte)Math.Clamp(dist * 255 / Math.Max(tol, 1), 0, 255) : (byte)255;
                        px[0] = px[1] = px[2] = v;
                        px[3] = 255;
                    }
                }
            }
        }
        finally { crop.UnlockBits(data); }
    }

    /// <summary>Margin of white put around the crop before recognition, in pixels.</summary>
    private const int QuietZone = 20;

    /// <summary>The crop on a white page with a margin around it.
    ///
    /// <para><b>This is not cosmetic.</b> The engine expects text on a page and finds nothing at
    /// all in an image whose glyphs run to the very edge: the identical crop reads "" without a
    /// margin and "85" with one (measured). A HUD rectangle is by definition drawn tight around
    /// its number, so the margin has to be added here.</para></summary>
    private static Bitmap WithQuietZone(Bitmap crop)
    {
        var page = new Bitmap(crop.Width + QuietZone * 2, crop.Height + QuietZone * 2,
                              PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            g.DrawImageUnscaled(crop, QuietZone, QuietZone);
        }
        return page;
    }

    /// <summary>The rectangle, cropped out and blown up. Grey rather than colour: the engine reads
    /// luminance anyway, and a flat grey crop of a coloured HUD is more legible to it than the
    /// original.</summary>
    private static Bitmap Upscaled(Bitmap frame, Rectangle rect)
    {
        // Rounded UP: an integer division floors a 70px side to "no upscale at all", which is
        // exactly the size the engine cannot read.
        int shortSide = Math.Max(1, Math.Min(rect.Width, rect.Height));
        int scale = Math.Clamp((TargetShortSide + shortSide - 1) / shortSide, 1, MaxScale);
        int w = rect.Width * scale, h = rect.Height * scale;

        var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(frame, new Rectangle(0, 0, w, h), rect, GraphicsUnit.Pixel);
        }
        return big;
    }
}
