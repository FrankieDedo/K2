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
        var resp = Everest60HidNative.SendFeature(h, req, delayMs: 15, log: log);
        // Diagnostic (2026-07-22, see Everest60Service.WithDevice's doc comment):
        // distinguish WHY a caller sees "not present" — SendFeature giving up
        // (null), a well-formed reply for the wrong command (retries all
        // landed on stale/interleaved traffic — echo mismatch), vs a
        // genuinely all-zero data region. All three currently collapse to
        // the same bool/null from here, which was hiding which one is
        // actually happening on real hardware.
        if (resp is null)
        {
            log?.Invoke("[Ev60] ReadNumpadPresent: SendFeature returned null (no response at all)");
            return null;
        }
        if (resp.Length != Everest60HidNative.ReportSize)
        {
            log?.Invoke($"[Ev60] ReadNumpadPresent: unexpected response length {resp.Length}");
            return null;
        }
        if (resp[1] != 0x20)
        {
            log?.Invoke($"[Ev60] ReadNumpadPresent: echo mismatch, got cmd=0x{resp[1]:X2} instead of 0x20 " +
                        "(another poller's response landed here — contention, not this call failing outright)");
            return null;
        }
        for (int i = 9; i < 61; i++) // wire bytes [8..59]: data region, excludes the constant trailer
            if (resp[i] != 0)
            {
                log?.Invoke("[Ev60] ReadNumpadPresent: present (non-zero data region)");
                return true;
            }
        log?.Invoke("[Ev60] ReadNumpadPresent: not present (data region genuinely all-zero)");
        return false;
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
