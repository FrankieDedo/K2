using System;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Wire protocol for the Everest 60's RGB lighting, ported line-for-line from
/// BaseCampLinux's <c>devices/everest60/controller.py</c> (itself derived from
/// OpenRGB's MountainKeyboard60Controller + community USB captures — see
/// <c>BaseCampLinux/docs/CONTROL_INTERFACE.md</c>). HID Feature Reports on
/// interface 2, 65-byte packets (report ID 0x00 + 64 data bytes), magic bytes
/// [2..4] = 46 23 EA on every command. Transport in <see cref="Everest60HidNative"/>.
///
/// Direction byte values for Wave (Right0/Down2/Left4/Up6) and Tornado
/// (CCW9/CW10) line up with Everest Max's own <c>byDirection</c> encoding
/// (see <c>MainWindow.Everest.cs</c> CapsFor/EVEREST_TODO.md) — same firmware
/// family, cross-checked independently by two reverse-engineering efforts.
/// </summary>
internal static class Everest60Protocol
{
    private static readonly byte[] Magic = { 0x46, 0x23, 0xEA };

    public enum Effect : byte
    {
        Static = 0x01,
        Wave = 0x02,
        Tornado = 0x03,
        Breathing = 0x04,
        Reactive = 0x05,
        Custom = 0x07,
        Yeti = 0x08,
        Off = 0x09,
    }

    public enum ColorMode : byte
    {
        Single = 0x00,
        Rainbow = 0x02,
        Dual = 0x10,
    }

    /// <summary>Wave direction wire codes (byte 10 of SendModeDetails).</summary>
    public static readonly (string Label, byte Code)[] WaveDirections =
    {
        ("Right", 0x00), ("Down", 0x02), ("Left", 0x04), ("Up", 0x06),
    };

    /// <summary>Tornado direction wire codes.</summary>
    public static readonly (string Label, byte Code)[] TornadoDirections =
    {
        ("Clockwise", 0x0A), ("Counter-CW", 0x09),
    };

    public const int NumKeys = 64;

    /// <summary>Logical key index (row-major, ANSI 60%) → firmware LED hardware
    /// address. Ported from controller.py's LEDIDX (per-row comment there).</summary>
    public static readonly byte[] LedIndex =
    {
        21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55,
        63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 76,
        84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 97, 99, 56,
        105, 106, 107, 110, 113, 115, 119, 120, 121,
    };

    /// <summary>Side perimeter ring: 44 RGB LEDs, clockwise starting above ESC.</summary>
    public static readonly byte[] SideLedIndex = BuildRange(126, 44);

    /// <summary>
    /// Numpad accessory's 17 keys → firmware LED hardware address, same index
    /// order as <see cref="Models.Everest60KeyboardLayout.Numpad"/> (Num//,*,-,
    /// 7,8,9,+, 4,5,6, 1,2,3,Enter, 0,.). Reverse-engineered 2026-07-12 via a
    /// real USBPcap capture of Base Camp painting each numpad key individually
    /// (user confirmed paint order), NOT guessed — see CHANGELOG for the full
    /// trace. The addresses fall in the unused "row" slots right after the
    /// main board's own keys (e.g. 38-41 sit right after Backspace=34 in
    /// LedIndex's row 0): the numpad shares the same physical PCB row/column
    /// addressing as the main board, just further right — same
    /// firmware-family reasoning already confirmed for the main 64 keys and
    /// side ring. Read-only for now (live preview only, see
    /// MainWindow.Everest60.cs's OnEv60ColorsUpdated) — writing/painting the
    /// numpad is a separate feature, not implemented here.
    /// </summary>
    public static readonly byte[] NumpadLedIndex =
    {
        38, 39, 40, 41,
        59, 60, 61, 62,
        80, 81, 82,
        101, 102, 103,
        125, 122, 124,
    };

    /// <summary>Every hardware LED address accounted for by <see cref="LedIndex"/>/
    /// <see cref="SideLedIndex"/>/<see cref="NumpadLedIndex"/> — used to spot
    /// non-zero colors at UNKNOWN addresses in a <c>GetColorData2</c> readback
    /// (whatever's left is genuinely unexplained padding in the firmware's
    /// address space, not a missed physical LED). Diagnostic only, added
    /// 2026-07-12.</summary>
    public static readonly HashSet<byte> KnownLedAddresses = BuildKnownAddresses();

    private static HashSet<byte> BuildKnownAddresses()
    {
        var set = new HashSet<byte>(LedIndex);
        foreach (var a in SideLedIndex) set.Add(a);
        foreach (var a in NumpadLedIndex) set.Add(a);
        return set;
    }

    private static byte[] BuildRange(int start, int count)
    {
        var a = new byte[count];
        for (int i = 0; i < count; i++) a[i] = (byte)(start + i);
        return a;
    }

    private static byte[] MakeBuf(byte cmd)
    {
        var b = new byte[Everest60HidNative.ReportSize];
        b[1] = cmd;
        b[2] = Magic[0]; b[3] = Magic[1]; b[4] = Magic[2];
        return b;
    }

    /// <summary>Quantizes a 0-100% value to the nearest 25-step (0/25/50/75/100),
    /// matching controller.py's <c>_brightness_val</c>/<c>_speed_val</c>.</summary>
    private static byte Step25(int pct) => (byte)(Math.Clamp((int)Math.Round(pct / 25.0), 0, 4) * 25);

    /// <summary>
    /// SetMode (0x16) + SendModeDetails (0x17): activates an effect and sends
    /// its parameters. Mirrors controller.py's <c>_send_mode()</c>.
    /// </summary>
    public static void SendMode(SafeFileHandle h, Effect effect, int speedPct = 50, int brightnessPct = 100,
        byte r1 = 255, byte g1 = 255, byte b1 = 255, byte r2 = 0, byte g2 = 0, byte b2 = 0,
        ColorMode colorMode = ColorMode.Dual, byte direction = 0, Action<string>? log = null)
    {
        var setMode = MakeBuf(0x16);
        setMode[5] = 1;
        setMode[9] = (byte)effect;
        Everest60HidNative.SendFeature(h, setMode);

        var details = MakeBuf(0x17);
        details[5] = (byte)effect;
        details[7] = Step25(speedPct);
        details[8] = Step25(brightnessPct);
        details[9] = (byte)colorMode;
        details[10] = direction;
        if (colorMode != ColorMode.Rainbow)
        {
            details[12] = r1; details[13] = g1; details[14] = b1;
            if (colorMode == ColorMode.Dual)
            {
                details[15] = r2; details[16] = g2; details[17] = b2;
            }
        }
        var resp = Everest60HidNative.SendFeature(h, details);
        log?.Invoke($"[Ev60] SetMode/Details eff={effect} colorMode={colorMode} dir=0x{direction:X2} " +
                    $"-> {(resp is { Length: > 1 } && resp[1] == 0x17 ? "ack" : "no-ack")}");
    }

    /// <summary>
    /// Custom per-key RGB (main 64 keys + optional 44-LED side ring). Mirrors
    /// controller.py's <c>set_lighting_custom()</c>: Begin(0x34) → Map(0x35, 14
    /// IRGB entries per packet) → End(0x36), after activating Custom mode.
    /// </summary>
    public static void SendCustom(SafeFileHandle h, IReadOnlyList<(byte r, byte g, byte b)> colors,
        int brightnessPct = 100, IReadOnlyList<(byte r, byte g, byte b)>? sideColors = null,
        Action<string>? log = null)
    {
        SendMode(h, Effect.Custom, brightnessPct: brightnessPct, colorMode: ColorMode.Single, log: log);

        var stream = new List<(byte hw, byte r, byte g, byte b)>();
        for (int i = 0; i < LedIndex.Length; i++)
        {
            (byte r, byte g, byte b) c = i < colors.Count ? colors[i] : ((byte)0, (byte)0, (byte)0);
            stream.Add((LedIndex[i], c.r, c.g, c.b));
        }
        if (sideColors != null)
        {
            for (int i = 0; i < SideLedIndex.Length; i++)
            {
                (byte r, byte g, byte b) c = i < sideColors.Count ? sideColors[i] : ((byte)0, (byte)0, (byte)0);
                stream.Add((SideLedIndex[i], c.r, c.g, c.b));
            }
        }

        var begin = MakeBuf(0x34);
        begin[5] = Step25(brightnessPct);
        begin[6] = 0xC0;
        Everest60HidNative.SendFeature(h, begin);

        const int perPacket = 14; // (65 - 9 header bytes) / 4 bytes per IRGB entry
        int idx = 0;
        while (idx < stream.Count)
        {
            var pkt = MakeBuf(0x35);
            int pos = 9, count = 0;
            while (idx < stream.Count && count < perPacket)
            {
                var (hw, r, g, b) = stream[idx];
                pkt[pos] = hw; pkt[pos + 1] = r; pkt[pos + 2] = g; pkt[pos + 3] = b;
                pos += 4; idx++; count++;
            }
            pkt[5] = idx == stream.Count ? (byte)0x0A : (byte)0x0E; // 0x0A=last, 0x0E=more
            Everest60HidNative.SendFeature(h, pkt);
        }

        Everest60HidNative.SendFeature(h, MakeBuf(0x36));
        log?.Invoke($"[Ev60] SendCustom: {stream.Count} LEDs ({(sideColors != null ? "keys+side" : "keys only")})");
    }

    /// <summary>Number of RGB entries in a <see cref="ReadColorData"/> buffer —
    /// same 192-entry address space as <c>Everest60SdkNative.GetColorData2</c>
    /// (see its doc comment), just reached over raw HID instead of the vendor
    /// SDK.</summary>
    public const int ColorEntryCount = 192;

    /// <summary>Max entries per page: (65-byte report - 1 report-ID byte -
    /// 4-byte cmd+magic echo header) / 3 bytes-per-RGB-entry = 20.</summary>
    private const int ColorPageSize = 20;

    /// <summary>
    /// Live LED-color readback (opcode 0x28) — reverse-engineered 2026-07-13
    /// from a real Base Camp USB capture (<c>_reference/usb_dumps/ev60+mak.pcapng</c>,
    /// captured specifically because <c>Everest60SdkNative.GetColorData2</c>/
    /// <c>GetSubDeviceInfo</c> were found to reliably fail whenever a Makalu
    /// mouse was also connected — see CHANGELOG). Base Camp's own traffic
    /// showed this is NOT a separate vendor-SDK code path at all: it's plain
    /// HID Feature Reports on the SAME interface 2 channel already used for
    /// lighting writes (<see cref="SendMode"/>/<see cref="SendCustom"/>),
    /// which — unlike the SDK session — kept working in every single test
    /// even with a Makalu connected. Wire format: request = cmd 0x28 + magic
    /// + int32 LE <c>offset</c> (entry index, byte 4) + int32 LE
    /// <c>count</c> (entries, byte 8, max <see cref="ColorPageSize"/> since
    /// 20 entries × 3 bytes + 4-byte echo header = 64 bytes, exactly one
    /// report). Response echoes cmd+magic then <c>count</c>×3 raw RGB bytes
    /// starting at firmware LED address <c>offset</c> — same addressing
    /// Everest60Protocol already uses to WRITE colors
    /// (<see cref="LedIndex"/>/<see cref="SideLedIndex"/>/<see cref="NumpadLedIndex"/>).
    /// Base Camp reads the full 192-entry space (matching
    /// <c>GetColorData2</c>'s documented 576-byte/192-FWColor buffer) in 10
    /// pages: nine of 20 entries (offsets 0,20,...,160) plus one final page
    /// of 12 (offset 180) — 9×20+12=192, confirmed byte-for-byte against the
    /// capture, not guessed (see CLAUDE.md's reverse-engineering rule).
    /// <paramref name="delayMs"/> defaults far below <see cref="Everest60HidNative.SendFeature"/>'s
    /// normal 50ms: the capture showed Base Camp firing consecutive read
    /// pages under 1ms apart, so a full 10-page sweep needs to stay well
    /// under a slow poll interval — kept at 15ms (not 0-1ms like the
    /// capture) as a safety margin since only one hardware sample exists so
    /// far.
    /// </summary>
    public static bool ReadColorData(SafeFileHandle h, EverestSdkNative.FWColor[] colors, Action<string>? log = null, int delayMs = 15)
    {
        if (colors.Length != ColorEntryCount)
            throw new ArgumentException($"colors must have {ColorEntryCount} entries", nameof(colors));

        for (int offset = 0; offset < ColorEntryCount; offset += ColorPageSize)
        {
            int count = Math.Min(ColorPageSize, ColorEntryCount - offset);
            var req = MakeBuf(0x28);
            BitConverter.GetBytes(offset).CopyTo(req, 5);
            BitConverter.GetBytes(count).CopyTo(req, 9);
            var resp = Everest60HidNative.SendFeature(h, req, delayMs: delayMs);
            if (resp is null || resp.Length < 5 + count * 3 || resp[1] != 0x28)
            {
                log?.Invoke($"[Ev60] ReadColorData: page offset={offset} count={count} failed");
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                int p = 5 + i * 3;
                colors[offset + i] = new EverestSdkNative.FWColor(resp[p], resp[p + 1], resp[p + 2]);
            }
        }
        return true;
    }

    /// <summary>
    /// Numpad accessory presence (opcode 0x20) — reverse-engineered
    /// 2026-07-13 from two real Base Camp USB captures with the accessory
    /// unplugged then plugged (<c>_reference/usb_dumps/ev60stacca.pcapng</c>),
    /// found the same session as <see cref="ReadColorData"/> after
    /// <c>Everest60SdkNative.GetSubDeviceInfo</c> was confirmed to reliably
    /// fail whenever a Makalu is also connected (see CHANGELOG). Request:
    /// cmd 0x20 + magic + int32 LE <c>1</c> at byte 4 (sub-device index,
    /// same convention <c>GetSubDeviceInfo(1, ...)</c> uses). Response
    /// echoes cmd+magic+the same int32, followed by a 52-byte data region
    /// that is ALL ZERO with no numpad attached and full of RGB-triplet-
    /// shaped bytes (matching the numpad's own live LED preview, same
    /// gradient pattern as <see cref="ReadColorData"/>'s output) once
    /// attached — confirmed by diffing the exact same request/response
    /// shape before vs. after the user plugged the accessory mid-capture,
    /// not guessed. A trailing 4-byte tail (<c>05 05 01 02</c>) is constant
    /// in EITHER state (some general status/checksum unrelated to the
    /// accessory) and is deliberately excluded from the presence check —
    /// including it would make every response look "present".
    /// <para>
    /// <b>Left/right side NOT yet determined</b> (only a right-side unit was
    /// available across the sessions that captured this): callers should
    /// treat the returned bool as presence-only and keep whatever side was
    /// last known/assumed until a differential capture with the accessory on
    /// the left is available — see MainWindow.Everest60.cs's caller.
    /// </para>
    /// </summary>
    public static bool? ReadNumpadPresent(SafeFileHandle h, Action<string>? log = null)
    {
        var req = MakeBuf(0x20);
        BitConverter.GetBytes(1).CopyTo(req, 5);
        var resp = Everest60HidNative.SendFeature(h, req, delayMs: 15);
        if (resp is null || resp.Length != Everest60HidNative.ReportSize || resp[1] != 0x20)
        {
            log?.Invoke("[Ev60] ReadNumpadPresent: request failed");
            return null;
        }
        for (int i = 9; i < 61; i++) // wire bytes [8..59]: data region, excludes the constant trailer
            if (resp[i] != 0) return true;
        return false;
    }
}
