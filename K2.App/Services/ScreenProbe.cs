using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace K2.App.Services;

/// <summary>How a probe turns a rectangle of pixels into one reading.</summary>
public enum ProbeMode
{
    /// <summary>Share of pixels in the rectangle matching the reference colour, 0-100%. Shape
    /// independent, so it reads a straight bar, a radial gauge or an odd-shaped icon fill with
    /// the same code — the honest default for a HUD element nobody has measured before.</summary>
    Fill,

    /// <summary>Position of the fill's leading edge along a direction, 0-100%. Truer than
    /// <see cref="Fill"/> for a linear bar whose fill is a gradient (where a colour match only
    /// catches part of the filled area), and wrong for anything that isn't linear.</summary>
    Edge,

    /// <summary>On/off: is the rectangle showing the reference colour at all. For an indicator
    /// that lights up rather than fills — a warning icon, a mode marker.</summary>
    Color,

    /// <summary>The NUMBER printed in the rectangle, read with the OS's OCR engine — a health
    /// value, an ammo count, a speed. Colour and tolerance mean nothing here: what is measured is
    /// the text, not the pixels' hue.</summary>
    Number,

    /// <summary>The TEXT printed in the rectangle, read the same way — a weapon name, a mode, a
    /// zone. Read as a word rather than a value.</summary>
    Text,

    /// <summary>Nothing is measured: the rectangle is COPIED, and shown on the key as a picture —
    /// a tiny live screenshot of a minimap, a portrait, an icon the game swaps out. Colour,
    /// tolerance, direction and invert all mean nothing here; only the rectangle does. Meant for a
    /// picture element (a <c>Mirror</c> reading in the game studio), and the reason it is a MODE
    /// rather than a flag is that it is the answer to the same question as the others: what is
    /// this rectangle for.</summary>
    Capture,
}

/// <summary>Direction a bar fills in, for <see cref="ProbeMode.Edge"/>.</summary>
public enum ProbeDir { LeftToRight, RightToLeft, TopToBottom, BottomToTop }

/// <summary>What a typed POSITION is measured from, on one axis: the near edge, the middle, or the
/// far edge. Only the calibration dialog uses it — the probe itself stores a relative rectangle, and
/// this is the sentence the user says about it ("40 px right of centre", "12 px off the bottom").</summary>
public enum ProbeRef { Start, Center, End }

/// <summary>
/// One user-defined reading taken off the screen: a rectangle of some program's window, a way of
/// interpreting the pixels in it, and a name. This is the whole "custom live tile" idea — a probe
/// is created once in the calibration dialog and any number of DisplayPad keys
/// (<c>dp_screen</c>) can then show it.
///
/// <para>
/// <b>The rectangle is RELATIVE</b> (0..1 of the window's client area), never pixels. A HUD that
/// scales with the window then keeps its probe when the game is moved, when the window is resized
/// and across most resolution changes; storing pixels would have silently pointed the probe at
/// empty screen the first time any of that happened.
/// </para>
/// </summary>
public sealed record ScreenProbe
{
    /// <summary>Stable identity, stored in the key's action value. Never reused.</summary>
    public string Id { get; init; } = "";

    /// <summary>What the user called it — also the tile's default caption.</summary>
    public string Name { get; init; } = "";

    /// <summary>Process to read, no extension (the spelling <c>Process.GetProcessesByName</c>
    /// and the game catalogue both use).</summary>
    public string Process { get; init; } = "";

    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }

    public ProbeMode Mode { get; init; } = ProbeMode.Fill;
    public ProbeDir Dir { get; init; } = ProbeDir.LeftToRight;

    /// <summary>Reference colour as <c>0xRRGGBB</c> — the colour the filled/lit part of the HUD
    /// element is drawn in, picked with the eyedropper on a real frame.</summary>
    public int Color { get; init; }

    /// <summary>How far a pixel may be from <see cref="Color"/> on its worst channel and still
    /// count as a match (0-255). Small values read a flat colour exactly; large ones survive a
    /// gradient or a compression-blurred HUD.</summary>
    public int Tolerance { get; init; } = 60;

    /// <summary>Flips the reading — for an element that EMPTIES as the value grows (a depleting
    /// dark segment, a "damage taken" overlay).</summary>
    public bool Invert { get; init; }

    /// <summary>OCR modes only (<see cref="ProbeMode.Number"/>, <see cref="ProbeMode.Text"/>): read
    /// WHATEVER is printed in the rectangle, instead of keeping only the pixels within
    /// <see cref="Tolerance"/> of <see cref="Color"/> as the ink. The filter is what makes a number
    /// drawn over the game's artwork legible to the engine, and exactly what ruins a line of text
    /// the game draws in two colours or shades as it fades — so it is the user's call, not a
    /// guess made for them.</summary>
    public bool AnyText { get; init; }
}

/// <summary>What a probe reads right now.</summary>
/// <param name="Valid">False when there was no frame to read (program not running, window not in
/// the foreground, capture refused). The tile then shows a dash rather than a confident zero.</param>
/// <param name="Fraction">0..1 for <see cref="ProbeMode.Fill"/>/<see cref="ProbeMode.Edge"/>.</param>
/// <param name="On">The on/off answer for <see cref="ProbeMode.Color"/>.</param>
/// <param name="Text">What an OCR probe read, "" for the pixel-counting modes.</param>
/// <param name="Number">The number in <paramref name="Text"/>, when it holds one.</param>
public readonly record struct ProbeReading(bool Valid, double Fraction, bool On,
                                           string Text = "", double? Number = null);

/// <summary>
/// What the calibration dialog carries over from the LAST probe the user saved: which program they
/// were reading, how big the rectangle was, and what they were measuring its position from.
///
/// <para>Calibrating is repetitive by nature — four readings of the same HUD, one after another — so
/// a new probe that opens on the same game, already the size of the last one, with its frame
/// already captured, starts where the previous one left off instead of at a default nobody wants
/// twice. Its own small file: these are not a probe's properties, they are the dialog's
/// memory.</para>
/// </summary>
internal static class ScreenProbeDefaults
{
    private sealed record Data
    {
        /// <summary>Last program named anywhere. Only used where there is no profile to ask about
        /// — the standalone probe editor reached from a key's own dialog.</summary>
        public string Process { get; init; } = "";

        /// <summary>Profile id → the program its probes were last aimed at. Per profile because
        /// one remembered program for the whole app is wrong the moment a second game exists: a
        /// new reading on the Kerbal profile was being offered War Thunder, purely because that is
        /// what the user happened to calibrate last.</summary>
        public Dictionary<string, string> Processes { get; init; } = new();

        public int Width { get; init; }
        public int Height { get; init; }
        public ProbeRef RefX { get; init; } = ProbeRef.Start;
        public ProbeRef RefY { get; init; } = ProbeRef.Start;
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "screenprobe_last.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object _gate = new();
    private static Data? _cache;

    private static Data Current
    {
        get
        {
            lock (_gate)
            {
                if (_cache is not null) return _cache;
                try
                {
                    _cache = File.Exists(FilePath)
                        ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new Data()
                        : new Data();
                }
                catch (Exception ex)
                {
                    // Nothing here is worth failing over: it is a convenience, so a broken file
                    // just means the next probe starts from the defaults.
                    App.WriteLog($"[PROBE] cannot read \"{FilePath}\": {ex.Message}");
                    _cache = new Data();
                }
                return _cache;
            }
        }
    }

    public static string Process => Current.Process;

    /// <summary>Which program a new probe should start on.
    ///
    /// <para>With a <paramref name="profileKey"/> the answer is that PROFILE's own history and
    /// nothing else: what it was last aimed at, or failing that the program the profile itself
    /// runs on (<paramref name="profileExe"/>), or nothing at all. Deliberately never the
    /// app-wide last program — being handed another game's window is worse than being handed an
    /// empty box, because an empty box is obviously unfinished and a wrong game is not.</para>
    ///
    /// <para>Without a key there is no profile to be specific about (the probe editor opened from
    /// a key's own dialog), and the app-wide last program is the best guess there is.</para></summary>
    public static string ProcessFor(string? profileKey, string? profileExe = null)
    {
        if (string.IsNullOrEmpty(profileKey)) return Current.Process;

        return Current.Processes.TryGetValue(profileKey!, out string? kept) && kept.Length > 0
            ? kept
            : (profileExe ?? "").Trim();
    }

    /// <summary>Last rectangle size in FRAME pixels, or null when nothing has been saved yet (or
    /// what was saved is unusable) — a new probe then keeps its own default size.</summary>
    public static (int W, int H)? Size =>
        Current.Width > 0 && Current.Height > 0 ? (Current.Width, Current.Height) : null;

    public static ProbeRef RefX => Current.RefX;
    public static ProbeRef RefY => Current.RefY;

    /// <param name="profileKey">Profile the probe belongs to, when it belongs to one: that is the
    /// bucket the program is remembered in. The app-wide value is updated either way, since the
    /// keyless callers still need it.</param>
    public static void Remember(string process, int width, int height, ProbeRef refX, ProbeRef refY,
                                string? profileKey = null)
    {
        string clean = (process ?? "").Trim();

        // Copied, not mutated in place: Data is a record handed out by Current, and editing the
        // dictionary under a caller that is reading it is the kind of bug that only shows up on
        // someone else's machine.
        var byProfile = new Dictionary<string, string>(Current.Processes, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(profileKey)) byProfile[profileKey!] = clean;

        var data = new Data
        {
            Process = clean,
            Processes = byProfile,
            Width = Math.Max(width, 0),
            Height = Math.Max(height, 0),
            RefX = refX,
            RefY = refY,
        };
        lock (_gate)
        {
            _cache = data;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(data, Json));
            }
            catch (Exception ex)
            {
                App.WriteLog($"[PROBE] cannot write \"{FilePath}\": {ex.Message}");
            }
        }
    }
}

/// <summary>
/// The probes the user has defined, persisted as one JSON file next to K2's log.
///
/// <para>Deliberately NOT in <c>DisplayPadStore</c>: probes are read by static services (the live
/// tile painter) that have no store instance, and they are not per-device — the same probe can
/// feed keys on two pads. One file, loaded once, rewritten whole on every change; the list is a
/// handful of entries, so nothing here needs to be cleverer than that.</para>
/// </summary>
internal static class ScreenProbeStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2", "K2.App", "screenprobes.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object _gate = new();
    private static List<ScreenProbe>? _cache;

    public static IReadOnlyList<ScreenProbe> All()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<ScreenProbe>>(File.ReadAllText(FilePath), Json)
                      ?? new List<ScreenProbe>()
                    : new List<ScreenProbe>();
            }
            catch (Exception ex)
            {
                // A corrupt file must not take the app down, and must not be silently replaced
                // either — say so in the log and carry on with an empty list.
                App.WriteLog($"[PROBE] cannot read \"{FilePath}\": {ex.Message}");
                _cache = new List<ScreenProbe>();
            }
            return _cache;
        }
    }

    public static ScreenProbe? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(p => p.Id == id);

    /// <summary>Adds a probe, or replaces the one with the same <see cref="ScreenProbe.Id"/>.</summary>
    public static void Save(ScreenProbe probe)
    {
        lock (_gate)
        {
            var list = All().ToList();
            int i = list.FindIndex(p => p.Id == probe.Id);
            if (i >= 0) list[i] = probe; else list.Add(probe);
            Write(list);
        }
    }

    public static void Delete(string id)
    {
        lock (_gate)
        {
            var list = All().Where(p => p.Id != id).ToList();
            Write(list);
        }
    }

    /// <summary>A fresh id. Time-based rather than a GUID so the file stays readable by hand.</summary>
    public static string NewId() => "p" + DateTime.UtcNow.Ticks.ToString("x");

    private static void Write(List<ScreenProbe> list)
    {
        _cache = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] cannot write \"{FilePath}\": {ex.Message}");
        }
    }
}

/// <summary>
/// Reads probes off the screen.
///
/// <para>
/// <b>One capture per window per tick, not one per probe.</b> Four Deadside tiles are four probes
/// on the same window; capturing four times would cost four full-window blits a second for one
/// frame's worth of information. Frames are therefore cached per process for
/// <see cref="FrameTtlMs"/> — long enough for every tile of one repaint to share a frame, short
/// enough that the reading is never visibly stale.
/// </para>
/// </summary>
internal static class ScreenProbeReader
{
    /// <summary>How long a captured frame is reused. The live-tile timer runs at 1 Hz, so this
    /// only ever collapses the probes of ONE tick into one capture.</summary>
    private const int FrameTtlMs = 250;

    /// <summary>Longest side, in pixels, a probe's rectangle is sampled at. A full-resolution
    /// 3440x1440 HUD region can be hundreds of thousands of pixels; the answer to "how much of
    /// this is red" does not change when it is measured on a grid of at most 160 steps, and the
    /// tile timer is not the place to spend milliseconds proving that.</summary>
    private const int MaxSamplesPerAxis = 160;

    /// <summary>Share of matching pixels at which a <see cref="ProbeMode.Color"/> probe reads ON,
    /// and a row/column counts as filled for <see cref="ProbeMode.Edge"/>.</summary>
    private const double OnThreshold = 0.5;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, (Bitmap? Frame, DateTime At)> _frames =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current reading of a probe. Never throws: a probe that cannot be read reports
    /// <see cref="ProbeReading.Valid"/> false.</summary>
    /// <param name="requireForeground">True — the rule for a LIVE tile: the window must be the one
    /// in front, or the pad would measure whatever is sitting on top of the game. False for a
    /// PREVIEW inside K2 (the studio, the key dialog): K2 itself is in front at that moment, and a
    /// preview that always reads "—" is the one thing that makes calibration impossible.</param>
    public static ProbeReading Read(ScreenProbe? probe, bool requireForeground = true)
    {
        if (probe is null) return default;
        try
        {
            lock (_gate)
            {
                var frame = FrameFor(probe.Process, requireForeground);
                if (frame is null) return default;
                return Steady(probe, Measure(frame, probe));
            }
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] \"{probe.Name}\" read failed: {ex.Message}");
            return default;
        }
    }

    /// <summary>Longest side, in pixels, a MIRRORED crop is written at. A DisplayPad key is 102px
    /// and the studio previews at twice that, so anything past this is pixels nobody will ever
    /// see, saved and reloaded once a second.</summary>
    private const int MaxMirrorSide = 256;

    /// <summary>Content stamp of the last crop written to each path, so a mirror of a screen that
    /// is not changing rewrites nothing — and, above it, the pad's own change detection sees the
    /// same stamp and does not re-upload the key once a second for a still picture.</summary>
    private static readonly Dictionary<string, string> _mirrorStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the probe's rectangle to <paramref name="path"/> as a PNG — the pixels
    /// themselves, with nothing measured. This is what a <c>Mirror</c> reading shows.
    ///
    /// <para>A file rather than a bitmap handed back because that is what the tile painter already
    /// takes for a picture: the crop then travels the same road as a PNG the user picked, and
    /// nothing downstream grows an image lifetime to manage. Callers pass DIFFERENT paths for the
    /// live tile and for a preview, so the pad's thread and the studio's never write the same
    /// file.</para></summary>
    /// <returns>A stamp of what the picture now holds — the same string as long as the pixels do
    /// not change — or null when there was nothing to copy.</returns>
    public static string? TryCropToFile(ScreenProbe? probe, bool requireForeground, string path)
    {
        if (probe is null) return null;
        try
        {
            lock (_gate)
            {
                var frame = FrameFor(probe.Process, requireForeground);
                if (frame is null) return null;

                var rect = PixelRect(frame.Width, frame.Height, probe);
                if (rect.Width <= 0 || rect.Height <= 0) return null;

                using var crop = frame.Clone(rect, PixelFormat.Format32bppArgb);
                var scaled = Shrink(crop, MaxMirrorSide);
                byte[] png;
                try
                {
                    using var buffer = new MemoryStream();
                    scaled.Save(buffer, ImageFormat.Png);
                    png = buffer.ToArray();
                }
                finally { if (!ReferenceEquals(scaled, crop)) scaled.Dispose(); }

                string stamp = Fingerprint(png);
                // Identical picture, file still there: leave it alone. The tile above reads the
                // stamp, not the file's timestamp, so nothing downstream notices a skipped write.
                if (_mirrorStamps.TryGetValue(path, out var last) && last == stamp && File.Exists(path))
                    return stamp;

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, png);
                _mirrorStamps[path] = stamp;
                return stamp;
            }
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] \"{probe.Name}\" mirror failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A short hash of the encoded picture (FNV-1a). Not a checksum anyone depends on —
    /// just a value that changes when the pixels do, which is all "has this tile changed" asks.</summary>
    private static string Fingerprint(byte[] data)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in data) { h ^= b; h *= 1099511628211; }
        return h.ToString("x16");
    }

    /// <summary>The bitmap itself when it is already small enough, or a copy scaled to fit
    /// <paramref name="maxSide"/> — which the caller disposes only when it got a copy back.</summary>
    private static Bitmap Shrink(Bitmap source, int maxSide)
    {
        int side = Math.Max(source.Width, source.Height);
        if (side <= maxSide) return source;

        double k = (double)maxSide / side;
        var small = new Bitmap(Math.Max(1, (int)(source.Width * k)),
                               Math.Max(1, (int)(source.Height * k)));
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, small.Width, small.Height);
        }
        return small;
    }

    /// <summary>How hard the ink is isolated, as a factor of the tolerance the user calibrated, when
    /// the calibrated value alone reads nothing.
    ///
    /// <para>A HUD number sits on the GAME, and what is behind it changes: an explosion, a
    /// headlight, a white wall can all bring background pixels within tolerance of the text's own
    /// colour and drown the glyphs — and a frame later they are gone again. So a failed read is
    /// retried with the ink cut HARDER (only pixels nearly dead on the colour survive), then much
    /// harder, then looser, and finally with no colour filter at all. Only ON FAILURE: a frame that
    /// reads fine costs exactly one pass, which is what keeps this affordable once a second.</para></summary>
    private static readonly double[] TolerancePasses = { 0.5, 0.25, 2.0 };

    /// <summary>What an OCR probe reads, with those retries. <paramref name="rect"/> is already the
    /// probe's pixel rectangle.</summary>
    private static ProbeReading MeasureText(Bitmap frame, Rectangle rect, ScreenProbe probe)
    {
        // The colour and tolerance mean the same thing here as everywhere else — "this is the colour
        // that counts" — they just select the INK instead of counting filled pixels.
        int calibrated = probe.AnyText ? ScreenTextReader.MaxTolerance : probe.Tolerance;
        if (ReadOnce(calibrated) is { } first) return first;

        // "Read any text" already means no filter: there is no other contrast left to try.
        if (probe.AnyText) return default;

        foreach (double factor in TolerancePasses)
        {
            int tol = Math.Clamp((int)Math.Round(calibrated * factor), 0, ScreenTextReader.MaxTolerance);
            if (tol == calibrated) continue;
            if (ReadOnce(tol) is { } retry)
            {
                App.WriteLog($"[PROBE] \"{probe.Name}\": read at tolerance {tol} " +
                             $"(the calibrated {calibrated} read nothing on this frame)");
                return retry;
            }
        }

        // Last resort, the whole crop with its colours: a number on a flat panel reads perfectly
        // that way, and it is the only thing left before answering "I don't know".
        return ReadOnce(ScreenTextReader.MaxTolerance) ?? default;

        ProbeReading? ReadOnce(int tolerance)
        {
            string text = ScreenTextReader.Read(frame, rect, probe.Color, tolerance);
            if (text.Length == 0) return null;

            // A NUMBER probe reads its rectangle as a number: the engine's letter-shaped digits
            // (a slashed zero read as "Ø", a squared 1 read as "I") become digits again, and the
            // spaces a wide HUD font makes it insert disappear. A TEXT probe keeps every character
            // exactly as it came — there, a letter really is a letter.
            if (probe.Mode == ProbeMode.Number) text = ScreenTextReader.NormalizeNumber(text);
            if (text.Length == 0) return null;

            double? number = ScreenTextReader.ParseNumber(text);
            // A NUMBER probe that read something with no number in it has not read its reading: the
            // next contrast gets its turn, and if none works the tile says it does not know rather
            // than printing the engine's misfire.
            if (probe.Mode == ProbeMode.Number && number is null) return null;
            return new ProbeReading(true, 0, true, text, number);
        }
    }

    /// <summary>Same measurement, but on a frame the caller already has — what the calibration
    /// dialog uses so its preview and the pad agree pixel for pixel.</summary>
    public static ProbeReading Measure(Bitmap frame, ScreenProbe probe)
    {
        var rect = PixelRect(frame.Width, frame.Height, probe);
        if (rect.Width <= 0 || rect.Height <= 0) return default;

        // A capture probe has no measurement to give: its rectangle is a PICTURE (see
        // ScreenProbeReader.TryCropToFile). Saying "no reading" rather than 0% is what keeps a key
        // pointed at one from printing a confident zero it never measured.
        if (probe.Mode == ProbeMode.Capture) return default;

        // The OCR modes read the rectangle's TEXT, so they leave the pixel counting below alone
        // entirely — there is no colour to match and no fill to measure.
        if (probe.Mode is ProbeMode.Number or ProbeMode.Text)
            return MeasureText(frame, rect, probe);

        int stepX = Math.Max(1, rect.Width / MaxSamplesPerAxis);
        int stepY = Math.Max(1, rect.Height / MaxSamplesPerAxis);

        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int refR = (probe.Color >> 16) & 0xFF, refG = (probe.Color >> 8) & 0xFF, refB = probe.Color & 0xFF;
            int tol = Math.Clamp(probe.Tolerance, 0, 255);

            // Per-line match counts, in the probe's own direction: the Fill answer is their sum,
            // the Edge answer is how far along the lines keep matching. Measuring both from one
            // pass keeps the two modes from disagreeing about the same pixels.
            bool vertical = probe.Dir is ProbeDir.TopToBottom or ProbeDir.BottomToTop;
            int lineCount = vertical ? (rect.Height + stepY - 1) / stepY : (rect.Width + stepX - 1) / stepX;
            var lineHits = new int[Math.Max(lineCount, 1)];
            var linePixels = new int[Math.Max(lineCount, 1)];
            long hits = 0, total = 0;

            unsafe
            {
                byte* baseAddr = (byte*)data.Scan0;
                for (int y = 0, iy = 0; y < rect.Height; y += stepY, iy++)
                {
                    byte* row = baseAddr + (long)y * data.Stride;
                    for (int x = 0, ix = 0; x < rect.Width; x += stepX, ix++)
                    {
                        byte* px = row + (long)x * 4;                 // BGRA
                        bool hit = Math.Abs(px[2] - refR) <= tol &&
                                   Math.Abs(px[1] - refG) <= tol &&
                                   Math.Abs(px[0] - refB) <= tol;
                        int line = vertical ? iy : ix;
                        if (line < lineHits.Length)
                        {
                            linePixels[line]++;
                            if (hit) lineHits[line]++;
                        }
                        total++;
                        if (hit) hits++;
                    }
                }
            }

            if (total == 0) return default;

            double value = probe.Mode == ProbeMode.Edge
                ? EdgePosition(lineHits, linePixels, probe.Dir)
                : (double)hits / total;

            if (probe.Invert) value = 1 - value;
            value = Math.Clamp(value, 0, 1);

            return new ProbeReading(true, value, value >= OnThreshold);
        }
        finally { frame.UnlockBits(data); }
    }

    /// <summary>Where the fill stops, 0..1, scanning from the end the bar fills FROM. Lines are
    /// walked from that end and the run stops at the first line that isn't filled, so a bar with
    /// tick marks or a bright HUD element sitting past the empty part doesn't get counted as
    /// fill.</summary>
    private static double EdgePosition(int[] hits, int[] pixels, ProbeDir dir)
    {
        int n = hits.Length;
        if (n == 0) return 0;
        bool fromEnd = dir is ProbeDir.RightToLeft or ProbeDir.BottomToTop;

        int filled = 0;
        for (int k = 0; k < n; k++)
        {
            int i = fromEnd ? n - 1 - k : k;
            if (pixels[i] == 0) break;
            if ((double)hits[i] / pixels[i] < OnThreshold) break;
            filled++;
        }
        return (double)filled / n;
    }

    /// <summary>The probe's relative rectangle as pixels of this frame, clamped inside it. A
    /// rectangle that would fall entirely outside comes back empty and reads as "no value".</summary>
    public static Rectangle PixelRect(int frameW, int frameH, ScreenProbe probe)
    {
        int x = (int)Math.Round(probe.X * frameW);
        int y = (int)Math.Round(probe.Y * frameH);
        int w = (int)Math.Round(probe.W * frameW);
        int h = (int)Math.Round(probe.H * frameH);

        x = Math.Clamp(x, 0, Math.Max(frameW - 1, 0));
        y = Math.Clamp(y, 0, Math.Max(frameH - 1, 0));
        w = Math.Clamp(w, 0, frameW - x);
        h = Math.Clamp(h, 0, frameH - y);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>A recent frame of that process's window, capturing one if the cached frame has
    /// expired. Null when the window can't be read right now.</summary>
    private static Bitmap? FrameFor(string process, bool requireForeground)
    {
        // The two rules are cached apart: a preview's frame (taken with K2 in front) must never be
        // handed to a live tile, which asked for the game to be the foreground window.
        string key = requireForeground ? process : process + "\u0000bg";

        var now = DateTime.UtcNow;
        if (_frames.TryGetValue(key, out var cached) &&
            (now - cached.At).TotalMilliseconds < FrameTtlMs)
            return cached.Frame;

        cached.Frame?.Dispose();
        var hwnd = WindowCapture.FindWindow(process);
        var frame = WindowCapture.TryCaptureClient(hwnd, requireForeground);

        // A PREVIEW falls back to the frame kept from the last capture, so the studio can show
        // what a tile will look like with the game closed — the same frame the calibration window
        // works on, so the two agree. A LIVE tile never does this: a stale picture on the pad,
        // indistinguishable from a fresh one, would be worse than a dash.
        if (frame is null && !requireForeground) frame = ScreenFrameCache.Load(process);

        _frames[key] = (frame, now);

        // Said once per reason, not once per tick: "my tile shows a dash" is the report this
        // service gets, and the answer is always one of these three.
        if (frame is null)
            NoteOnce(key, hwnd == IntPtr.Zero
                ? $"[PROBE] \"{process}\": no window found"
                : requireForeground
                    ? $"[PROBE] \"{process}\": window is not in the foreground — no reading"
                    : $"[PROBE] \"{process}\": window could not be captured");
        else NoteOnce(key, null);

        return frame;
    }

    /// <summary>Last thing said about each capture key, so a standing condition is logged once
    /// instead of once a second.</summary>
    private static readonly Dictionary<string, string?> _notes = new(StringComparer.Ordinal);

    private static void NoteOnce(string key, string? message)
    {
        if (_notes.TryGetValue(key, out var last) && last == message) return;
        _notes[key] = message;
        if (message is not null) App.WriteLog(message);
    }

    /// <summary>The last reading each probe gave that meant something, when it gave it, and whether
    /// it is being held right now — see <see cref="Steady"/>.</summary>
    private static readonly Dictionary<string, (ProbeReading Reading, DateTime At, bool Holding)> _held =
        new(StringComparer.Ordinal);

    /// <summary>How long a reading survives frames it could not be taken from.</summary>
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(3);

    /// <summary>Keeps the last real answer on the key through a frame the probe could not read.
    ///
    /// <para><b>Why a tile must not blink.</b> There WAS a frame here — the game is running and in
    /// front — so a failed reading means this one frame fought the probe: the background moved under
    /// the number, a tooltip crossed it, the HUD faded through an animation. Answering "I don't know"
    /// for that single tick makes the key flash a dash once a second, which reads as a broken tile
    /// rather than as a busy background. The last value is therefore kept for <see cref="HoldFor"/> —
    /// long enough to ride out a few bad frames, short enough that a value nobody can read any more
    /// does eventually admit it.</para>
    ///
    /// <para>Only on the LIVE path (<see cref="Read"/>). The calibration dialog measures through
    /// <see cref="Measure"/> and gets the naked truth about the frame in front of it, which is the
    /// whole point of a preview.</para></summary>
    private static ProbeReading Steady(ScreenProbe probe, ProbeReading reading)
    {
        var now = DateTime.UtcNow;

        if (reading.Valid)
        {
            if (_held.TryGetValue(probe.Id, out var was) && was.Holding)
                App.WriteLog($"[PROBE] \"{probe.Name}\": readable again");
            _held[probe.Id] = (reading, now, false);
            return reading;
        }

        if (!_held.TryGetValue(probe.Id, out var last) || now - last.At > HoldFor)
        {
            _held.Remove(probe.Id);
            return reading;
        }

        if (!last.Holding)
        {
            App.WriteLog($"[PROBE] \"{probe.Name}\": frame unreadable — holding " +
                         $"\"{last.Reading.Text}\" for up to {HoldFor.TotalSeconds:0}s");
            _held[probe.Id] = (last.Reading, last.At, true);
        }
        return last.Reading;
    }

    /// <summary>Drops every cached frame — called when the pad stops painting probe tiles, so a
    /// closed game doesn't leave a full-screen bitmap alive. The held readings go with them: one
    /// kept from before a game closed, or before the user re-calibrated, is not worth a moment
    /// longer on a key.</summary>
    public static void Flush()
    {
        lock (_gate)
        {
            foreach (var (_, entry) in _frames) entry.Frame?.Dispose();
            _frames.Clear();
            _held.Clear();
        }
    }
}
