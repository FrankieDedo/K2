using System;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Wire protocol for the Makalu 67/Max mouse, ported line-for-line from
/// BaseCampLinux's <c>devices/makalu67/controller.py</c> (protocol
/// reverse-engineered from a Windows USB capture — report ID 0xA1, 64-byte
/// HID Feature Reports on interface 1, response report ID 0xA0). Transport
/// in <see cref="MakaluHidNative"/>.
/// </summary>
internal static class MakaluProtocol
{
    public const byte ReportId = 0xA1;
    public const byte RespId   = 0xA0;

    private const byte CmdLighting    = 0x0C;
    private const byte CmdPollingRate = 0x0D; // also carries debounce/lift-off/angle-snap/DPI-set sub-commands
    private const byte CmdDpi         = 0x0B; // GET (sub 0x07 = Read_profile_data)
    private const byte CmdRemap       = 0x0A;

    public enum Effect : byte
    {
        Off          = 0,
        Static       = 1,
        Rainbow      = 2,
        Breathing    = 5,
        RgbBreathing = 6,
        Responsive   = 7,
        Yeti         = 8,
        Custom       = 0x0F,
    }

    public const int DpiMin67 = 50;
    public const int DpiMinMax = 100;
    public const int DpiMax = 19000;
    public const int DpiStep = 50;

    /// <summary>Rounds to the nearest valid DPI step (always a multiple of 50 —
    /// the firmware's actual granularity). Used for the wire-level clamp in
    /// <see cref="SetAllDpi"/>.</summary>
    public static int QuantizeDpi(int dpi) => (int)Math.Round(dpi / (double)DpiStep) * DpiStep;

    /// <summary>Rounds to the nearest step of a coarser, tiered grid (50 below
    /// 4000, 100 between 4000-10000, 500 above) — every result is still a
    /// multiple of 50, so it's wire-compatible with <see cref="QuantizeDpi"/>.
    /// Used by the DPI sliders (main levels + sniper) so dragging across the
    /// full 50-19000 range doesn't require 380 micro-steps at the low end.</summary>
    public static int QuantizeDpiTiered(int dpi)
    {
        int step = dpi < 4000 ? 50 : dpi < 10000 ? 100 : 500;
        return (int)Math.Round(dpi / (double)step) * step;
    }

    public static readonly int[] DebounceValuesMs = { 2, 4, 6, 8, 10, 12 };

    /// <summary>Function code (category, code) for button remap, keyed by internal name.</summary>
    public static readonly (string Name, byte Category, byte Code)[] RemapFunctions =
    {
        ("left",         0x00, 0x01),
        ("right",        0x00, 0x02),
        ("middle",       0x00, 0x04),
        ("back",         0x00, 0x08),
        ("forward",      0x00, 0x10),
        ("dpi+",         0x09, 0xF1),
        ("dpi-",         0x09, 0xF3),
        ("scroll_up",    0x01, 0x01),
        ("scroll_down",  0x01, 0xFF),
        ("disabled",     0xFF, 0x01),
    };

    private static byte[] NewBuf()
    {
        var b = new byte[MakaluHidNative.ReportSize];
        b[0] = ReportId;
        return b;
    }

    private static bool Ack(byte[]? resp) => resp is { Length: > 0 } && resp[0] == RespId;

    // ---------------------------------------------------------------
    // Lighting
    // ---------------------------------------------------------------

    /// <summary>Preset effect (Off/Static/Rainbow/Breathing/RgbBreathing/Responsive/Yeti).
    /// <paramref name="param1"/>/<paramref name="param2"/> mirror controller.py's CLI
    /// "code"/"code2" forms: param1=direction byte, param2=speed byte (0 slow/1 medium/2 fast).
    /// <paramref name="secondary"/> is the 2nd color used by dual-color effects
    /// (Breathing/Yeti — controller.py's "code2"), written at buf[20..23].</summary>
    public static bool SetLighting(SafeFileHandle h, Effect effect, byte r = 0, byte g = 0, byte b = 0,
        int brightnessPct = 100, byte param1 = 0, byte param2 = 0, (byte r, byte g, byte b)? secondary = null)
    {
        var buf = NewBuf();
        buf[1]  = CmdLighting;
        buf[5]  = 0x01;
        buf[16] = (byte)effect;
        buf[17] = r; buf[18] = g; buf[19] = b;
        buf[41] = (byte)Math.Clamp(brightnessPct, 0, 100);
        buf[42] = param1;
        buf[43] = param2;
        if (secondary is { } s)
        {
            buf[20] = s.r; buf[21] = s.g; buf[22] = s.b; buf[23] = 0;
        }
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    /// <summary>Per-LED custom colors (effect 0x0F). <paramref name="leds"/> must have
    /// exactly 8 entries, physical layout: 0=top-left … 3=bottom-left, 4=bottom-right … 7=top-right.</summary>
    public static bool SetLightingCustom(SafeFileHandle h, (byte r, byte g, byte b)[] leds, int brightnessPct = 100)
    {
        if (leds.Length != 8) throw new ArgumentException("leds must have exactly 8 entries", nameof(leds));
        var buf = NewBuf();
        buf[1]  = CmdLighting;
        buf[5]  = 0x01;
        buf[16] = (byte)Effect.Custom;
        for (int i = 0; i < 8; i++)
        {
            buf[17 + i * 3] = leds[i].r;
            buf[18 + i * 3] = leds[i].g;
            buf[19 + i * 3] = leds[i].b;
        }
        buf[41] = (byte)Math.Clamp(brightnessPct, 0, 100);
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    // ---------------------------------------------------------------
    // Polling rate / debounce / lift-off / angle snapping
    // ---------------------------------------------------------------

    public static bool SetPollingRate(SafeFileHandle h, int hz)
    {
        byte code = hz switch { 1000 => 0x01, 500 => 0x02, 250 => 0x04, 125 => 0x08,
            _ => throw new ArgumentException($"Invalid polling rate {hz}") };
        var buf = NewBuf();
        buf[1] = CmdPollingRate; buf[2] = 0x01; buf[5] = 0x01; buf[6] = code;
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    public static bool SetDebounce(SafeFileHandle h, int ms)
    {
        if (Array.IndexOf(DebounceValuesMs, ms) < 0)
            throw new ArgumentException($"Invalid debounce {ms}ms");
        var buf = NewBuf();
        buf[1] = CmdPollingRate; buf[2] = 0x02; buf[5] = 0x01; buf[6] = (byte)ms;
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    public static bool SetAngleSnapping(SafeFileHandle h, bool enabled)
    {
        var buf = NewBuf();
        buf[1] = CmdPollingRate; buf[2] = 0x03; buf[5] = 0x01; buf[6] = (byte)(enabled ? 1 : 0);
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    public static bool SetLiftOff(SafeFileHandle h, bool high)
    {
        var buf = NewBuf();
        buf[1] = CmdPollingRate; buf[2] = 0x04; buf[5] = 0x01; buf[6] = (byte)(high ? 1 : 0);
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    // ---------------------------------------------------------------
    // DPI
    // ---------------------------------------------------------------

    public const int DpiLevelCountMin = 1;
    public const int DpiLevelCountMax = 5;

    /// <summary>Reads all 5 DPI level slots + the currently active level (0-based)
    /// + how many of those 5 slots are actually active (<c>dpi_level_num</c>,
    /// resp[21] — the count the physical DPI-cycle button on the mouse steps
    /// through; documented in controller.py's <c>get_dpi</c> byte-offset
    /// comment but never read here until now, always assumed to be 5).</summary>
    public static (int[] Levels, int Active, int Count)? GetDpi(SafeFileHandle h, int dpiMin)
    {
        var buf = NewBuf();
        buf[1] = CmdDpi; buf[2] = 0x07; buf[5] = 0x01;
        var resp = MakaluHidNative.SendFeature(h, buf);
        if (resp is null || resp.Length < 43) return null;

        int count  = Math.Clamp((int)resp[21], DpiLevelCountMin, DpiLevelCountMax);
        int active = Math.Clamp(resp[22] - 1, 0, 4); // resp[22] is 1-based
        var levels = new int[5];
        for (int i = 0; i < 5; i++)
        {
            int lo = resp[23 + i * 4], hi = resp[24 + i * 4];
            int dpi = lo | (hi << 8);
            levels[i] = Math.Clamp(dpi, dpiMin, DpiMax);
        }
        return (levels, active, count);
    }

    /// <summary>Writes all 5 DPI level slots + active level (1-based) + how many
    /// of those slots are active (<paramref name="levelCount"/>, 1-5 —
    /// <c>dpi_level_num</c>, the DPI_T struct field controlling how many
    /// levels the physical DPI-cycle button steps through) to every profile
    /// (ALL_PROFILE=6, same as controller.py's <c>set_all_dpi</c>). The wire
    /// format always carries exactly 5 slots regardless of
    /// <paramref name="levelCount"/> — unused trailing slots just keep
    /// whatever value <paramref name="dpiList"/> gives them.</summary>
    public static bool SetAllDpi(SafeFileHandle h, int[] dpiList, int activeLevel1Based, int dpiMin, int levelCount = 5)
    {
        if (dpiList.Length != 5) throw new ArgumentException("dpiList must have exactly 5 values", nameof(dpiList));
        var buf = NewBuf();
        buf[1] = CmdPollingRate; buf[2] = 0x0A; buf[5] = 6;
        buf[6] = (byte)Math.Clamp(levelCount, DpiLevelCountMin, DpiLevelCountMax);
        buf[7] = (byte)Math.Clamp(activeLevel1Based, 1, 5);
        for (int i = 0; i < 5; i++)
        {
            int dpi = (int)Math.Round(Math.Clamp(dpiList[i], dpiMin, DpiMax) / (double)DpiStep) * DpiStep;
            byte lo = (byte)(dpi & 0xFF), hi = (byte)((dpi >> 8) & 0xFF);
            buf[16 + i * 4]     = lo; buf[16 + i * 4 + 1] = hi; // X
            buf[16 + i * 4 + 2] = lo; buf[16 + i * 4 + 3] = hi; // Y (same as X)
        }
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    // ---------------------------------------------------------------
    // Button remap + sniper
    // ---------------------------------------------------------------

    public static bool SetButtonRemap(SafeFileHandle h, int buttonIndex1Based, string functionName)
    {
        int fi = Array.FindIndex(RemapFunctions, f => f.Name == functionName.ToLowerInvariant());
        if (fi < 0) throw new ArgumentException($"Unknown function '{functionName}'");
        var (_, category, code) = RemapFunctions[fi];
        var buf = NewBuf();
        buf[1] = CmdRemap; buf[5] = 0x01; buf[6] = (byte)buttonIndex1Based;
        buf[16] = category; buf[17] = code; buf[22] = 0x0F;
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }

    /// <summary>While held, switches to <paramref name="sniperDpi"/>; released
    /// restores the active profile DPI automatically (firmware-side).</summary>
    public static bool SetButtonSniper(SafeFileHandle h, int buttonIndex1Based, int sniperDpi, int dpiMin)
    {
        int dpi = (int)Math.Round(Math.Clamp(sniperDpi, dpiMin, DpiMax) / (double)DpiStep) * DpiStep;
        var buf = NewBuf();
        buf[1] = CmdRemap; buf[5] = 0x01; buf[6] = (byte)buttonIndex1Based;
        buf[16] = 0x0C; buf[17] = 0x01;
        buf[18] = (byte)(dpi & 0xFF); buf[19] = (byte)((dpi >> 8) & 0xFF); // X
        buf[20] = (byte)(dpi & 0xFF); buf[21] = (byte)((dpi >> 8) & 0xFF); // Y
        buf[22] = 0x0F;
        return Ack(MakaluHidNative.SendFeature(h, buf));
    }
}
