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
}

/// <summary>Direction a bar fills in, for <see cref="ProbeMode.Edge"/>.</summary>
public enum ProbeDir { LeftToRight, RightToLeft, TopToBottom, BottomToTop }

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
}

/// <summary>What a probe reads right now.</summary>
/// <param name="Valid">False when there was no frame to read (program not running, window not in
/// the foreground, capture refused). The tile then shows a dash rather than a confident zero.</param>
/// <param name="Fraction">0..1 for <see cref="ProbeMode.Fill"/>/<see cref="ProbeMode.Edge"/>.</param>
/// <param name="On">The on/off answer for <see cref="ProbeMode.Color"/>.</param>
public readonly record struct ProbeReading(bool Valid, double Fraction, bool On);

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
    public static ProbeReading Read(ScreenProbe? probe)
    {
        if (probe is null) return default;
        try
        {
            lock (_gate)
            {
                var frame = FrameFor(probe.Process);
                if (frame is null) return default;
                return Measure(frame, probe);
            }
        }
        catch (Exception ex)
        {
            App.WriteLog($"[PROBE] \"{probe.Name}\" read failed: {ex.Message}");
            return default;
        }
    }

    /// <summary>Same measurement, but on a frame the caller already has — what the calibration
    /// dialog uses so its preview and the pad agree pixel for pixel.</summary>
    public static ProbeReading Measure(Bitmap frame, ScreenProbe probe)
    {
        var rect = PixelRect(frame.Width, frame.Height, probe);
        if (rect.Width <= 0 || rect.Height <= 0) return default;

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
    private static Bitmap? FrameFor(string process)
    {
        var now = DateTime.UtcNow;
        if (_frames.TryGetValue(process, out var cached) &&
            (now - cached.At).TotalMilliseconds < FrameTtlMs)
            return cached.Frame;

        cached.Frame?.Dispose();
        var hwnd = WindowCapture.FindWindow(process);
        // requireForeground: a live tile must never measure whatever window happens to be sitting
        // on top of the game. See WindowCapture's remarks.
        var frame = WindowCapture.TryCaptureClient(hwnd, requireForeground: true);
        _frames[process] = (frame, now);
        return frame;
    }

    /// <summary>Drops every cached frame — called when the pad stops painting probe tiles, so a
    /// closed game doesn't leave a full-screen bitmap alive.</summary>
    public static void Flush()
    {
        lock (_gate)
        {
            foreach (var (_, entry) in _frames) entry.Frame?.Dispose();
            _frames.Clear();
        }
    }
}
