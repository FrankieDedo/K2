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
    /// address. Ported from controller.py's LEDIDX (per-row comment there), EXCEPT
    /// index 0 (ESC): controller.py's own comment claims ESC is really 21 because
    /// "address 0 has no physical LED... the firmware never lit it" (their issue
    /// #15) — that turned out to be wrong. A real USBPcap capture of official Base
    /// Camp painting ESC red (2026-07-25, <c>_reference/usb_dumps/ev60_red.pcapng</c>,
    /// frame 433: Map entry hw=0x00 r=0xFF g=0x00 b=0x00) shows ESC's true hardware
    /// address is 0 — and every one of the OTHER 63 addresses in this table matches
    /// the capture exactly (diffed programmatically, not eyeballed), so this is a
    /// single-value correction, not a re-derivation. controller.py's own "fix" was
    /// almost certainly masking a DIFFERENT bug (see <see cref="SendCustom"/>'s
    /// <c>pkt[5]</c> doc comment: a hardcoded last-packet count byte, not the real
    /// count, meant leftover zero-padding — which always lands at hw=0 — silently
    /// blacked out ESC moments after it was correctly painted, on every Apply/Fill
    /// All). Moving ESC to an unrelated address (21, actually the "U" key) hid the
    /// symptom instead of fixing the padding bug, and broke ESC in the process.
    /// User report 2026-07-25: "il tasto esc lampeggia il colore giusto e poi si
    /// spegne" — exactly this padding stomp.</summary>
    public static readonly byte[] LedIndex =
    {
        0, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55,
        63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 76,
        84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 97, 99, 56,
        105, 106, 107, 110, 113, 115, 119, 120, 121,
    };

    /// <summary>Side perimeter ring: 44 RGB LEDs, clockwise starting above ESC.</summary>
    public static readonly byte[] SideLedIndex = BuildRange(126, 44);

    /// <summary>
    /// Numpad accessory's OWN perimeter ring: 22 RGB LEDs, clockwise starting
    /// top-left (Top → Right → Bottom → Left, same convention as
    /// <see cref="SideLedIndex"/>) — reverse-engineered 2026-07-24 from a real
    /// USBPcap capture (<c>_reference/usb_dumps/ev60numring.pcapng</c>) of Base
    /// Camp painting each numpad-ring LED individually (#FFFF00, one at a time,
    /// user-confirmed clockwise-from-top-left order), NOT guessed. The 22
    /// addresses are perfectly contiguous with <see cref="SideLedIndex"/>
    /// (126-169) and reach exactly <see cref="ColorEntryCount"/>-1 (191, the
    /// last valid address in the whole 192-entry color space) — every address
    /// 0-191 is now accounted for by <see cref="LedIndex"/>/<see cref="SideLedIndex"/>/
    /// <see cref="NumpadLedIndex"/>/this array combined, with zero gaps left,
    /// which is strong corroborating evidence this is the true range (not
    /// just a plausible-looking guess). The capture's first ~45 addresses
    /// (126-170) arrived in one instantaneous burst (already-applied state
    /// resent wholesale, as Custom mode always does) — only 171-191 showed the
    /// ~200ms human click cadence; 170 is inferred to be the actual top-left
    /// starting LED (perfectly contiguous with 169, and the very first click
    /// of the capture's first Apply necessarily batches with whatever state
    /// already existed). Per-edge split for the click overlay (5 top / 6 right
    /// / 5 bottom / 6 left, <see cref="Models.Everest60KeyboardLayout"/>'s
    /// numpad canvas being taller than wide) is a first-pass proportional
    /// placement — same caveat as <see cref="SideLedIndex"/>'s own UI overlay:
    /// total count and clockwise starting corner are confirmed, the exact
    /// per-edge boundary isn't (the capture has no signal for where a corner
    /// turn happens).
    /// </summary>
    public static readonly byte[] NumpadSideLedIndex = BuildRange(170, 22);

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
    /// side ring. Live preview via <see cref="ReadColorData"/>/<c>GetColorData2</c>
    /// AND paintable via <see cref="SendCustom"/>'s <c>numpadColors</c> param
    /// (2026-07-24) — writing reuses this same confirmed address set, not a
    /// new guess (see that param's doc comment).
    /// </summary>
    public static readonly byte[] NumpadLedIndex =
    {
        38, 39, 40, 41,
        59, 60, 61, 62,
        80, 81, 82,
        101, 102, 103,
        125, 122, 124,
    };

    /// <summary>Offset added to a numpad key's 0-16 <c>KeyDef.NumpadIndex</c>
    /// to get the <c>LedIndex</c> value used as Everest60Store's Keys-table
    /// identity, keeping it disjoint from the main board's real LED indices
    /// (0-63) — the two boards share the same (Profile, LedIndex) primary key
    /// with no separate discriminator column. Key Binding persistence only;
    /// unrelated to any hardware address.</summary>
    public const int NumpadLedIndexBase = 1000;

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
        foreach (var a in NumpadSideLedIndex) set.Add(a);
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
    /// Custom per-key RGB (main 64 keys + optional 44-LED side ring + optional
    /// 17-key numpad accessory + optional 22-LED numpad ring). Mirrors
    /// controller.py's <c>set_lighting_custom()</c>: Begin(0x34) → Map(0x35, 14
    /// IRGB entries per packet) → End(0x36), after activating Custom mode.
    /// <paramref name="numpadColors"/> reuses <see cref="NumpadLedIndex"/> —
    /// the SAME hardware address domain <see cref="ReadColorData"/> already
    /// reads live numpad colors from (see its doc comment: "same addressing
    /// Everest60Protocol already uses to WRITE colors"), so writing those
    /// addresses through this already-confirmed Custom stream mechanism is not
    /// a new guess, just reusing a known address set that was previously
    /// read-only for lack of a UI to drive it. <paramref name="numpadRingColors"/>
    /// reuses <see cref="NumpadSideLedIndex"/> (170-191), confirmed via a real
    /// USBPcap capture (see its doc comment) of Base Camp doing exactly this —
    /// painting the numpad ring through this same Custom stream.
    /// </summary>
    public static void SendCustom(SafeFileHandle h, IReadOnlyList<(byte r, byte g, byte b)> colors,
        int brightnessPct = 100, IReadOnlyList<(byte r, byte g, byte b)>? sideColors = null,
        IReadOnlyList<(byte r, byte g, byte b)>? numpadColors = null,
        IReadOnlyList<(byte r, byte g, byte b)>? numpadRingColors = null,
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
        if (numpadColors != null)
        {
            for (int i = 0; i < NumpadLedIndex.Length; i++)
            {
                (byte r, byte g, byte b) c = i < numpadColors.Count ? numpadColors[i] : ((byte)0, (byte)0, (byte)0);
                stream.Add((NumpadLedIndex[i], c.r, c.g, c.b));
            }
        }
        if (numpadRingColors != null)
        {
            for (int i = 0; i < NumpadSideLedIndex.Length; i++)
            {
                (byte r, byte g, byte b) c = i < numpadRingColors.Count ? numpadRingColors[i] : ((byte)0, (byte)0, (byte)0);
                stream.Add((NumpadSideLedIndex[i], c.r, c.g, c.b));
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
            // pkt[5] is the number of VALID entries in this packet, not a binary
            // more/last flag — confirmed 2026-07-25 from a real USBPcap capture of
            // official Base Camp (_reference/usb_dumps/ev60_red.pcapng): every
            // full packet used 0x0E (=14, matching perPacket) and the one partial
            // final packet used 0x0A (=10, matching its exact 10 real entries out
            // of 192 total addresses), with the packet's OWN trailing bytes past
            // that count left as genuine zero padding the firmware evidently
            // ignores. The previous code hardcoded 0x0A for ANY final packet
            // regardless of its real count — harmless for Base Camp's own 192-entry
            // stream (which always ends with exactly 10 left over) but wrong for
            // K2's shorter ~147-entry stream (ends with 7), so the firmware read 3
            // phantom zero-padded entries as real, hw=0 among them — stomping
            // ESC's LED (address 0, see LedIndex's doc comment) back to black
            // moments after Custom correctly painted it.
            pkt[5] = (byte)count;
            Everest60HidNative.SendFeature(h, pkt);
        }

        Everest60HidNative.SendFeature(h, MakeBuf(0x36));
        log?.Invoke($"[Ev60] SendCustom: {stream.Count} LEDs (keys" +
                    (sideColors != null ? "+side" : "") +
                    (numpadColors != null ? "+numpad" : "") +
                    (numpadRingColors != null ? "+numpadRing" : "") + ")");
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
    /// Numpad accessory position (opcode 0x08) — reverse-engineered
    /// 2026-07-25 from three real Base Camp USB captures
    /// (<c>_reference/usb_dumps/ev60_detach.pcapng</c>, <c>ev60_detach_2.pcapng</c>,
    /// <c>ev60_slow.pcapng</c> — the last one an 8-step attach/detach sequence
    /// at known ~8-10s intervals, right×4 then left×4). This is the SAME
    /// heartbeat Base Camp itself polls every ~200ms (far faster than the
    /// old opcode 0x20 presence check this replaces, which K2 only polled
    /// every 3s). Request: cmd 0x08 + magic, no other payload. Response
    /// echoes cmd+magic, then wire byte 4 (<c>resp[5]</c> — cmd echo at
    /// <c>resp[1]</c>, magic at <c>resp[2..4]</c>) is the position: matched
    /// 1:1, zero exceptions, against all 8 steps of the known sequence
    /// (2/0/2/0/1/0/1/0 for right-attach/detach ×2, left-attach/detach ×2),
    /// flipping on the very next ~200ms poll after each physical action —
    /// which is also exactly <see cref="Ev60NumpadPosition"/>'s own
    /// numbering (None=0/Left=1/Right=2), not a coincidence. The rest of the
    /// response (byte 8 onward) rotates through a few unrelated states every
    /// ~10s regardless of numpad state — some other Base Camp telemetry
    /// sharing this opcode, not numpad data, and deliberately ignored here.
    /// <para>
    /// <b>Not yet tested</b>: whether this opcode stays responsive through
    /// the ~20+s firmware stall a numpad Key Binding write causes on opcode
    /// 0x2C/0x20 (see <c>MainWindow.Everest60.cs</c>'s
    /// <c>_ev60NumpadAbsentStreak</c> doc comment) — the three captures above
    /// were all physical attach/detach, none exercised a binding write. Until
    /// confirmed, the caller keeps the same debounce+grace-period mitigation.
    /// </para>
    /// </summary>
    public static Ev60NumpadPosition? ReadNumpadPosition(SafeFileHandle h, Action<string>? log = null)
    {
        var req = MakeBuf(0x08);
        var resp = Everest60HidNative.SendFeature(h, req, delayMs: 15, log: log);
        if (resp is null)
        {
            log?.Invoke("[Ev60] ReadNumpadPosition: SendFeature returned null (no response at all)");
            return null;
        }
        if (resp.Length != Everest60HidNative.ReportSize)
        {
            log?.Invoke($"[Ev60] ReadNumpadPosition: unexpected response length {resp.Length}");
            return null;
        }
        if (resp[1] != 0x08)
        {
            log?.Invoke($"[Ev60] ReadNumpadPosition: echo mismatch, got cmd=0x{resp[1]:X2} instead of 0x08 " +
                        "(another poller's response landed here — contention, not this call failing outright)");
            return null;
        }
        Ev60NumpadPosition? pos = resp[5] switch
        {
            0 => Ev60NumpadPosition.None,
            1 => Ev60NumpadPosition.Left,
            2 => Ev60NumpadPosition.Right,
            _ => null,
        };
        if (pos is null)
            log?.Invoke($"[Ev60] ReadNumpadPosition: unexpected position byte 0x{resp[5]:X2}");
        else
            log?.Invoke($"[Ev60] ReadNumpadPosition: {pos}");
        return pos;
    }

    /// <summary>Sentinel value for "no action"/disabled — confirmed
    /// 2026-07-22 via a 4th capture (<c>ev60_del.pcapng</c>, Base Camp itself
    /// removing a numpad binding) to be plain <c>255</c> (one byte,
    /// zero-padded to an int32), not <c>0xFFFFFFFF</c> as first misread from
    /// the raw hex (three "ff 00 00 00" int32 fields in an early query
    /// response — each one is 255, not 0xFFFFFFFF). Same value as
    /// <see cref="Everest60RemapData.DisabledKeyId"/>, the main board's own
    /// "reset/disable" sentinel for <c>ChangeKey</c>/<c>ChangeFnKey</c> — one
    /// shared convention across both protocols, not a coincidence.</summary>
    public const int NumpadUnassignedMarker = 255;

    /// <summary>Fixed action-type value K2 writes for any bound numpad key —
    /// arbitrary from K2's point of view (see class doc below).</summary>
    public const int NumpadBoundMarker = 1;

    /// <summary>
    /// Main-board (64-key) Key Binding writes — the counterpart of
    /// <see cref="NumpadKeyBinding"/> for the keyboard itself. Reverse-engineered
    /// 2026-07-27 from two USBPcap captures of Base Camp itself
    /// (<c>_reference/usb_dumps/ev60_disable.pcapng</c> setting the key "3" to Disable,
    /// <c>ev60_def.pcapng</c> putting it back to Default), cross-checked against the
    /// <c>Everest60KeyBidings</c> rows Base Camp wrote at the same instant — which is
    /// what pins the key identity down: the disabled key's DLLKeyId (4) and
    /// DLLMatrixIndex (3) are both small integers, and only the DB row proves the
    /// value on the wire is the <b>DLLKeyId</b>. Same feature-report channel and
    /// <c>46 23 ea</c> magic as everything else in this class; both captures were
    /// otherwise nothing but the cmd 0x08 background poll, so the sequences below are
    /// complete, not excerpts.
    ///
    /// <para><b>Disable</b>: cmd 0x30 (no parameters) as a prologue, then cmd 0x29 with
    /// (int32 DLLKeyId, int32 11) — 11 being Base Camp's own action-type code for
    /// "Disable". Both acked with 1. No commit (cmd 0x2C) and no string parameter
    /// (cmd 0x2B), unlike a numpad binding write.</para>
    ///
    /// <para><b>Restore</b>: cmd 0x30 again, then cmd 0x22 with (int32 DLLKeyId, 255).
    /// This is byte for byte the command <see cref="NumpadKeyBinding.UnassignKey"/>
    /// already sends for the accessory — confirming cmd 0x22 with
    /// <see cref="NumpadUnassignedMarker"/> is one device-wide "reset this key to
    /// factory" call, and settling the older ambiguity around
    /// <see cref="Everest60RemapData.DisabledKeyId"/>: 255 means RESET, never disable
    /// (which is why K2 must not reuse it to switch a key off — a previous session
    /// nearly did).</para>
    ///
    /// <para>Neither sequence is followed by a flash save, so the writes are live-only:
    /// unplugging the keyboard restores factory behaviour regardless of what K2 left
    /// behind. That's the safety net behind <c>MainWindow.Everest60.cs</c>'s
    /// disabled-key bookkeeping.</para>
    /// </summary>
    public static class MainKeyBinding
    {
        /// <summary>Base Camp's action-type code for "Disable", captured as the second
        /// int32 of the cmd 0x29 write.</summary>
        private const int DisableActionCode = 11;

        /// <summary>Prologue both sequences open with (cmd 0x30, no parameters). Base
        /// Camp sends it before every key write; its meaning is unknown and K2 doesn't
        /// need it to be known, only to be reproduced.</summary>
        private static void Prologue(SafeFileHandle h, Action<string>? log)
            => Everest60HidNative.SendFeature(h, MakeBuf(0x30), log: log);

        /// <summary>Switches a main-board key off in firmware: it stops emitting its
        /// keystroke entirely (this is what Base Camp's own "Disable" does).</summary>
        public static void DisableKey(SafeFileHandle h, int dllKeyId, Action<string>? log = null)
        {
            Prologue(h, log);
            var req = MakeBuf(0x29);
            BitConverter.GetBytes(dllKeyId).CopyTo(req, 5);
            BitConverter.GetBytes(DisableActionCode).CopyTo(req, 9);
            var resp = Everest60HidNative.SendFeature(h, req, log: log);
            log?.Invoke($"[Ev60] MainKeyBinding.DisableKey: dllKeyId={dllKeyId} " +
                        $"-> {(resp is { Length: > 1 } && resp[1] == 0x29 ? "ack" : "no-ack")}");
        }

        /// <summary>Puts a main-board key back to its factory function.</summary>
        public static void RestoreKey(SafeFileHandle h, int dllKeyId, Action<string>? log = null)
        {
            Prologue(h, log);
            var req = MakeBuf(0x22);
            BitConverter.GetBytes(dllKeyId).CopyTo(req, 5);
            BitConverter.GetBytes(NumpadUnassignedMarker).CopyTo(req, 9);
            var resp = Everest60HidNative.SendFeature(h, req, log: log);
            log?.Invoke($"[Ev60] MainKeyBinding.RestoreKey: dllKeyId={dllKeyId} " +
                        $"-> {(resp is { Length: > 1 } && resp[1] == 0x22 ? "ack" : "no-ack")}");
        }
    }

    /// <summary>
    /// Numpad Key Binding protocol (query/write/commit/event-poll) —
    /// reverse-engineered 2026-07-22 from three real Base Camp USB captures
    /// (<c>_reference/usb_dumps/ev60_keyconf.pcapng</c>,
    /// <c>ev60_press.pcapng</c>; see CHANGELOG for the full trace), same
    /// feature-report channel/magic as the rest of this class — no new
    /// interface or transport needed.
    ///
    /// <para><b>Identity</b>: the "index" every one of these commands takes
    /// is the numpad key's <b>DLLKeyId</b> (same catalog as
    /// <see cref="Everest60RemapData.KeyCatalog"/>/<c>ChangeKey</c> for the
    /// main 64 keys, extracted for the numpad via a fresh decompile of
    /// <c>Everest60Operations.GetEverest60KeyBindings_English</c> — see
    /// <see cref="Everest60RemapData.NumpadDllKeyId"/>), NOT a LED index or
    /// the 0-16 <c>KeyDef.NumpadIndex</c>/array position. Confirmed against
    /// two independent captures: assigning "7" wrote idx=0x5B=91=DLLKeyId("Numpad 7");
    /// assigning "4" queried idx=0x5C=92=DLLKeyId("Numpad 4") — exact match,
    /// not coincidence.
    /// </para>
    ///
    /// <para><b>Write (<see cref="WriteKeyActionType"/>/<see cref="WriteKeyActionParam"/>/
    /// <see cref="CommitKeyBinding"/>)</b>: captured verbatim assigning
    /// "Open Folder ...\Braccio robotico" to Numpad 7 — cmd 0x2A writes
    /// (dllKeyId, actionTypeValue) as two int32 LE fields; cmd 0x2B writes
    /// the action's string parameter (the real folder path was transmitted
    /// in clear, chunked ≤56 bytes/packet with a 4-byte LE length prefix);
    /// cmd 0x2C commits. <b>K2 does not need to replicate Base Camp's real
    /// action-type numbering</b> (that capture's value 0x3E for "Open
    /// Folder" is Base Camp's own vocabulary) — K2 executes its OWN
    /// K2Action via <c>Ev60ActionHost</c>/<c>ButtonActionEngine</c>
    /// regardless of what this firmware-side value would have meant to Base
    /// Camp, so <see cref="NumpadBoundMarker"/> (an arbitrary non-sentinel
    /// constant) is used for every K2-assigned key. A second capture also
    /// showed a cmd 0x29 with a different 2-int32 shape for a different
    /// action type (Run Program) — confirms the write command family varies
    /// by Base Camp's own action type, which is exactly why K2 doesn't try
    /// to mirror it: cmd 0x2A/0x2B/0x2C is the one fully end-to-end
    /// confirmed sequence (verified it silences the key's raw HID output),
    /// so K2 always uses that one path regardless of the K2Action assigned.
    /// </para>
    ///
    /// <para><b>Unassign (<see cref="UnassignKey"/>)</b>: confirmed 2026-07-22
    /// via a 4th capture (<c>ev60_del.pcapng</c>, Base Camp itself removing a
    /// numpad binding after a prior real-hardware test showed K2's original
    /// guess — writing <see cref="NumpadUnassignedMarker"/> via cmd 0x2A —
    /// did NOT restore the literal keystroke). The capture showed no
    /// distinct write command at all for the removal: the only relevant
    /// traffic was a cmd 0x22 call (dllKeyId, value=255), and the boot-
    /// keyboard HID report for the physical key reappeared shortly after.
    /// So the real mechanism is cmd 0x22 acting as a combined query/reset
    /// (255 = <see cref="Everest60RemapData.DisabledKeyId"/>, the SAME
    /// sentinel already used by the main board's <c>ChangeKey</c>), not a
    /// cmd 0x2A write — see <see cref="UnassignKey"/>'s own doc comment.
    /// </para>
    ///
    /// <para><b>Physical-press detection (<see cref="QueryNumpadKeyEvent"/>,
    /// cmd 0x08)</b>: NOT the same as <c>Effect.Yeti = 0x08</c> above (that's
    /// an unrelated enum value in a completely different command's payload,
    /// pure coincidence of numbering) — cmd 0x08 is Base Camp's own
    /// continuous background status poll (never sent by K2 before this),
    /// whose response happens to carry the last numpad key event inline:
    /// wire response bytes [4]=0x02 (constant), [5]=an incrementing event
    /// counter, [6]=the DLLKeyId of the affected key, [7]=1 (pressed) / 0
    /// (released). Verified across two independent, isolated physical
    /// presses (Base Camp's own remap dialog fully closed, ~20s of idle
    /// around them): the counter/dllKeyId/pressed fields are exactly what
    /// changes, precisely twice, with zero false positives during idle —
    /// this is NOT the same as the also-present cmd 0x07→0x2D exchange
    /// (which turned out to be Base Camp's own housekeeping/list-refresh,
    /// unreliable as a press signal on its own).
    /// </para>
    /// </summary>
    public static class NumpadKeyBinding
    {
        /// <summary>Queries a numpad key's current binding (cmd 0x22) —
        /// diagnostic/logging only, K2 doesn't need the result to decide what
        /// to write. Returns the raw int32 action-type value, or
        /// <see cref="NumpadUnassignedMarker"/> if unbound / on failure.</summary>
        public static int QueryKeyBinding(SafeFileHandle h, int dllKeyId, Action<string>? log = null)
        {
            var req = MakeBuf(0x22);
            BitConverter.GetBytes(dllKeyId).CopyTo(req, 5);
            var resp = Everest60HidNative.SendFeature(h, req, delayMs: 15, log: log);
            if (resp is null || resp[1] != 0x22)
            {
                log?.Invoke($"[Ev60] NumpadKeyBinding.QueryKeyBinding: dllKeyId={dllKeyId} failed");
                return NumpadUnassignedMarker;
            }
            return BitConverter.ToInt32(resp, 9);
        }

        /// <summary>Marks a numpad key as bound (cmd 0x2A: dllKeyId at byte
        /// 5, value at byte 9) — see class doc for why <paramref name="value"/>
        /// doesn't need to mean anything to Base Camp.</summary>
        public static void WriteKeyActionType(SafeFileHandle h, int dllKeyId, int value, Action<string>? log = null)
        {
            var req = MakeBuf(0x2A);
            BitConverter.GetBytes(dllKeyId).CopyTo(req, 5);
            BitConverter.GetBytes(value).CopyTo(req, 9);
            var resp = Everest60HidNative.SendFeature(h, req, log: log);
            log?.Invoke($"[Ev60] NumpadKeyBinding.WriteKeyActionType: dllKeyId={dllKeyId} value={value} " +
                        $"-> {(resp is { Length: > 1 } && resp[1] == 0x2A ? "ack" : "no-ack")}");
        }

        /// <summary>Writes the action's string parameter (cmd 0x2B), chunked
        /// ≤56 bytes/packet with a 4-byte LE length prefix per packet — see
        /// class doc for the one detail (a stray extra byte in the capture's
        /// first, multi-chunk-needing packet) left unresolved since K2's own
        /// placeholder text is short enough to normally need a single
        /// chunk.</summary>
        public static void WriteKeyActionParam(SafeFileHandle h, string text, Action<string>? log = null)
        {
            const int maxChunk = 56;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
            int off = 0;
            do
            {
                int len = Math.Min(maxChunk, bytes.Length - off);
                var req = MakeBuf(0x2B);
                BitConverter.GetBytes(len).CopyTo(req, 5);
                if (len > 0) Array.Copy(bytes, off, req, 9, len);
                Everest60HidNative.SendFeature(h, req, log: log);
                off += len;
            } while (off < bytes.Length);
            log?.Invoke($"[Ev60] NumpadKeyBinding.WriteKeyActionParam: \"{text}\" ({bytes.Length} bytes)");
        }

        /// <summary>Commits a binding written via <see cref="WriteKeyActionType"/>/
        /// <see cref="WriteKeyActionParam"/> (cmd 0x2C, no payload).</summary>
        public static void CommitKeyBinding(SafeFileHandle h, Action<string>? log = null)
        {
            var resp = Everest60HidNative.SendFeature(h, MakeBuf(0x2C), log: log);
            log?.Invoke($"[Ev60] NumpadKeyBinding.CommitKeyBinding -> {(resp is { Length: > 1 } && resp[1] == 0x2C ? "ack" : "no-ack")}");
        }

        /// <summary>Restores a numpad key to its unassigned (literal
        /// keystroke) state — confirmed 2026-07-22 via a 4th capture
        /// (Base Camp itself removing a numpad binding): despite going
        /// through Base Camp's "remove" UI, no distinct write command ever
        /// appeared on the wire — the only relevant traffic was cmd 0x22
        /// (the SAME shape as <see cref="QueryKeyBinding"/>) with
        /// <see cref="NumpadUnassignedMarker"/> (255) as its value, and the
        /// physical key's raw HID boot-keyboard report reappeared moments
        /// later. So cmd 0x22 is a combined query/reset, not a pure read:
        /// harmless on an already-unassigned key (255 stays 255), and it
        /// clears an assigned one. No commit needed — none was seen either.
        /// </summary>
        public static void UnassignKey(SafeFileHandle h, int dllKeyId, Action<string>? log = null)
        {
            var req = MakeBuf(0x22);
            BitConverter.GetBytes(dllKeyId).CopyTo(req, 5);
            BitConverter.GetBytes(NumpadUnassignedMarker).CopyTo(req, 9);
            var resp = Everest60HidNative.SendFeature(h, req, log: log);
            log?.Invoke($"[Ev60] NumpadKeyBinding.UnassignKey: dllKeyId={dllKeyId} " +
                        $"-> {(resp is { Length: > 1 } && resp[1] == 0x22 ? "ack" : "no-ack")}");
        }

        /// <summary>Polls for a numpad key press/release event (cmd 0x08, no
        /// request payload). Returns (counter, dllKeyId, pressed) from the
        /// response, or null on failure — see class doc for the byte layout
        /// and how it was verified.</summary>
        public static (int Counter, int DllKeyId, bool Pressed)? QueryNumpadKeyEvent(SafeFileHandle h, Action<string>? log = null)
        {
            var resp = Everest60HidNative.SendFeature(h, MakeBuf(0x08), delayMs: 5);
            if (resp is null || resp[1] != 0x08)
            {
                log?.Invoke("[Ev60] NumpadKeyBinding.QueryNumpadKeyEvent: request failed");
                return null;
            }
            return (Counter: resp[6], DllKeyId: resp[7], Pressed: resp[8] == 1);
        }
    }
}
