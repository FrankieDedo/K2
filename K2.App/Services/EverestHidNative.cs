using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Raw USB-HID access to the Mountain Everest Max keyboard (VID 0x3282, PID 0x0001) —
/// no SDKDLL.dll. Mirrors <see cref="DpHidNative"/>'s approach for the DisplayPad.
///
/// <para>
/// <b>Why this exists.</b> SDKDLL.dll's internal timer thread has a chronic stack-write
/// bug (see App.xaml.cs VehCore / memory "sdkdll-crash-veh-skip") that we can mitigate
/// but not fix at the source (closed-source 3rd-party DLL). Talking to the keyboard's
/// custom protocol directly over HID removes SDKDLL.dll from the process entirely for
/// whatever surface this class covers.
/// </para>
///
/// <para>
/// <b>Interface.</b> <c>Get-PnpDevice</c> on a real device (2026-07-05) confirms MI_03
/// enumerates as <c>HID\VID_3282&amp;PID_0001&amp;MI_03</c>, <c>Class=HIDClass</c>,
/// vendor-defined usage — an ordinary HID collection, exactly like the DisplayPad's
/// command interface. No WinUSB, no driver install. MI_00/MI_02 (the actual keyboard
/// typing, claimed by <c>kbdhid</c>) and MI_01's COL01-03 (consumer/system controls +
/// mouse, claimed by <c>mouhid</c>/OS) are untouched by this class — SDKDLL.dll's
/// custom protocol (RGB, macros, numpad display keys, Media Dock) all lives on MI_03.
/// </para>
///
/// <para>
/// <b>Protocol source.</b> Cross-validated from two independent sources that agree on
/// the command IDs: BaseCampLinux's <c>emax_controller.py</c> (VID/PID/EP/pkt size,
/// init sequence <c>11 12</c> → <c>11 14</c>, numpad D1-D4 button bit-map at wire byte
/// 42) and K2's own SDKDLL.dll reverse-engineering comments in
/// <see cref="EverestSdkNative"/> (e.g. "GetFWLayout = HID 11 12", "14 2C" for
/// ChangeEffect/ChangeBlockEffect, "11 83" for the color-stream toggle).
/// </para>
///
/// <para>
/// <b>NOT yet covered (see memory/task "Everest nativo — Fase 3/4"):</b> the FULL
/// 171-key keyboard matrix event layout used by K2's existing key-remap engine is
/// NOT confirmed by either source above — emax_controller.py only ever inspects wire
/// byte 42 for the 4 numpad buttons, never the rest of the packet. Guessing at that
/// bit layout risks silently breaking remapping, so <see cref="Pad.NumpadButtonChanged"/>
/// only exposes D1-D4 for now; the full matrix needs a dedicated USB capture (press
/// several distinct keys, diff the packets) before it can be added safely. Until then,
/// <c>EverestService</c> keeps using SDKDLL.dll's key callback for full-keyboard events
/// even when the native engine is enabled for connectivity/RGB.
/// </para>
/// </summary>
internal static class EverestHidNative
{
    public const ushort VID = 0x3282;
    public const ushort PID = 0x0001;
    public const int PktSize = 64;

    // ================================================================
    // Enumeration: find the MI_03 vendor-defined HID collection.
    // ================================================================

    /// <summary>Enumerates the Everest Max's MI_03 command interface, if connected.</summary>
    public static string? FindCommandInterfacePath(Action<string>? log = null)
    {
        string? best = null;
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero,
                                           DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) return null;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                string? path = GetInterfacePath(devs, ref ifData, ref devInfo);
                if (path is null) continue;

                using var h = OpenHandle(path, throwOnFail: false, queryOnly: true);
                if (h is null || h.IsInvalid) continue;

                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != VID || attrs.ProductID != PID)
                    continue;

                string lower = path.ToLowerInvariant();
                log?.Invoke($"[EvNative] HID {lower[..Math.Min(80, lower.Length)]}…");

                // MI_03 = the vendor-defined command interface (confirmed via
                // Get-PnpDevice: Class=HIDClass, no kbdhid/mouhid claiming it).
                // MI_00/MI_02 (keyboard) and MI_01 (consumer/system/mouse) are
                // claimed by other OS class drivers and irrelevant here.
                if (lower.Contains("mi_03"))
                    best = path;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return best;
    }

    private static string? GetInterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData,
                                            ref SP_DEVINFO_DATA devInfo)
    {
        SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out int size, ref devInfo);
        if (size <= 0) return null;
        IntPtr detail = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, size, out _, ref devInfo))
                return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    internal static SafeFileHandle? OpenHandle(string path, bool throwOnFail, bool queryOnly = false)
    {
        var h = CreateFileW(path, queryOnly ? 0 : GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            queryOnly ? 0 : FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            if (throwOnFail)
                throw new InvalidOperationException($"CreateFile failed (win32={Marshal.GetLastWin32Error()}) for {path}");
            h.Dispose();
            return null;
        }
        return h;
    }

    /// <summary>Overlapped Read/WriteFile with a hard timeout (see DpHidNative.Transfer).</summary>
    internal static bool Transfer(SafeFileHandle h, byte[] buf, int len, bool write,
                                  int timeoutMs, out int done)
    {
        done = 0;
        using var evt = new ManualResetEvent(false);
        IntPtr ovl = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            var no = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
            Marshal.StructureToPtr(no, ovl, false);
            bool ok = write
                ? WriteFile(h, buf, len, IntPtr.Zero, ovl)
                : ReadFile(h, buf, len, IntPtr.Zero, ovl);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return false;
                if (!evt.WaitOne(timeoutMs))
                {
                    CancelIoEx(h, ovl);
                    evt.WaitOne(2000);
                    return GetOverlappedResult(h, ovl, out done, false) && done > 0;
                }
            }
            return GetOverlappedResult(h, ovl, out done, false);
        }
        finally
        {
            pin.Free();
            Marshal.FreeHGlobal(ovl);
        }
    }

    // ================================================================
    // One open Everest Max command interface (MI_03)
    // ================================================================

    internal sealed class Pad : IDisposable
    {
        private readonly string _path;
        private readonly Action<string> _log;
        private SafeFileHandle _cmd = null!;
        private Thread? _reader;
        private volatile bool _stop;
        private readonly object _ioLock = new();
        private readonly ConcurrentQueue<byte[]> _resp = new();
        private readonly SemaphoreSlim _respSignal = new(0);
        private byte _prevNumpadBits;

        /// <summary>(buttonIndex 0-3 = D1-D4, pressed). See class remarks: the full
        /// 171-key matrix is NOT covered yet — only the 4 numpad display buttons.</summary>
        public event Action<int, bool>? NumpadButtonChanged;

        // Wire byte 42 (see BaseCampLinux emax_controller.py BTN_LOOKUP), bits for D1-D4.
        private static readonly (int WireByte, byte Mask)[] BtnMap =
            { (42, 0x02), (42, 0x04), (42, 0x08), (42, 0x10) };

        public Pad(string path, Action<string> log)
        {
            _path = path;
            _log = log;
        }

        public void Open()
        {
            _cmd = OpenHandle(_path, throwOnFail: true)!;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "EvNativeReader" };
            _reader.Start();
            Init(attempts: 2);
        }

        /// <summary>
        /// Init handshake: <c>11 12</c> (BaseCampLinux "Init"; matches SDKDLL.dll's own
        /// GetFWLayout comment: "HID 11 12") followed by <c>11 14</c> (BaseCampLinux
        /// sends this right after, and again as a readiness poll before display/CPU
        /// updates — meaning here as "are you ready").
        /// </summary>
        private void Init(int attempts)
        {
            var cmd1 = new byte[PktSize];
            cmd1[0] = 0x11; cmd1[1] = 0x12;
            var cmd2 = new byte[PktSize];
            cmd2[0] = 0x11; cmd2[1] = 0x14;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(500);
                DrainResponses();
                if (!WriteCmd(cmd1)) continue;
                // BaseCampLinux does not strictly validate the response content for
                // these two — it just reads-and-discards with a timeout. We do the same,
                // just confirming SOMETHING came back (echo/ack) before proceeding.
                WaitResp(_ => true, 1500);
                if (!WriteCmd(cmd2)) continue;
                WaitResp(_ => true, 1500);
                // NB: an `11 80 00 00 01` packet (seen in Base Camp's startup,
                // evicon.pcapng) was briefly sent here as a candidate "suppress firmware
                // default display-key actions" flag — REMOVED same day: it did not stop
                // the double execution and coincided with icon uploads/resets no longer
                // refreshing the LCDs (unknown semantics, likely needs a companion
                // command we haven't captured). Do not re-add without a capture of BC
                // assigning an action to a defaulted key.
                _log($"[EvNative] init OK");
                return;
            }
            throw new InvalidOperationException("Everest Max did not respond to INIT (11 12 / 11 14)");
        }

        /// <summary>Re-runs the INIT handshake (e.g. after a resync).</summary>
        public bool Reinit()
        {
            lock (_ioLock)
            {
                try { Init(attempts: 2); return true; }
                catch (Exception ex) { _log($"[EvNative] re-init failed: {ex.Message}"); return false; }
            }
        }

        /// <summary>
        /// Sends a raw 64-byte command on MI_03 and returns the first response that
        /// satisfies <paramref name="match"/>, or null on timeout. For Phase 2 (RGB)
        /// commands (<c>14 2C ...</c> ChangeEffect/ChangeBlockEffect payloads etc.).
        /// </summary>
        public byte[]? SendCommand(byte[] wire64, Func<byte[], bool> match, int timeoutMs = 2000)
        {
            lock (_ioLock)
            {
                DrainResponses();
                if (!WriteCmd(wire64)) return null;
                return WaitResp(match, timeoutMs);
            }
        }

        private static byte[] Cmd(params byte[] head)
        {
            var wire = new byte[PktSize];
            Buffer.BlockCopy(head, 0, wire, 0, head.Length);
            return wire;
        }

        /// <summary>
        /// Sends a command and waits for its own ECHO ack (a response whose first byte
        /// equals the command's) — NOT just any response. The device continuously streams
        /// LED-color packets (byte0 = 0x02-0x0A/0x11, from the color poller's 11 83
        /// reads), so a permissive wait returns instantly on unrelated traffic and the
        /// next command gets fired within microseconds — real Base Camp paces multi-packet
        /// sequences ~150ms apart precisely by waiting each command's echo (evprofiles.pcapng:
        /// every OUT on ep 0x05 is answered by an IN on ep 0x84 starting with the same
        /// byte). Without this, the ResetDisplayKeyPic sequence was accepted ("True") but
        /// visibly ignored by the firmware (user report 2026-07-19).
        /// </summary>
        private bool SendCmdAcked(byte[] wire64, int timeoutMs = 1200)
        {
            byte b0 = wire64[0];
            var resp = SendCommand(wire64, r => r.Length > 0 && r[0] == b0, timeoutMs);
            // Full cmd/echo hex at Verbose: the echo's payload may carry a status the
            // firmware uses to say "received but refused" — needed to diagnose sequences
            // that ack fine yet visibly do nothing (display-key reset, 2026-07-19).
            if (K2.Core.AppSettings.LogLevel == K2.Core.K2LogLevel.Verbose)
                _log($"[EvNative] cmd  {Convert.ToHexString(wire64, 0, 12)} -> " +
                     (resp is null ? "TIMEOUT" : $"echo {Convert.ToHexString(resp, 0, Math.Min(12, resp.Length))}"));
            return resp is not null;
        }

        /// <summary>
        /// Claims a display-key press on behalf of the host software, replicating what
        /// Base Camp does after EVERY key report (evicon.pcapng, 2026-07-19): read
        /// status (<c>11 00</c>) then send <c>11 02 00 01 [profile] [wMatrix] 02</c>
        /// (response <c>FF AA FF</c>). Without this claim the firmware executes the
        /// key's own stored/default action AUTONOMOUSLY (standalone mode) — the cause of
        /// the "double action" the user saw with K2: firmware fired the default AND K2's
        /// engine fired the configured action on the same press (BC's captures show only
        /// the custom action firing while it acks every press; with BC closed only the
        /// default fires). Cross-checked against the older evprofiles.pcapng session end,
        /// which contains <c>11 02 00 01 03 50 02</c> = same shape with profile 3 active
        /// and wMatrix 0x50 = 80 = display key D2.
        /// </summary>
        public void AckKeyPress(int profile, byte wMatrix)
        {
            SendCommand(Cmd(0x11, 0x00), r => r.Length > 1 && r[0] == 0x11 && r[1] == 0x00, 400);
            SendCommand(Cmd(0x11, 0x02, 0x00, 0x01, (byte)profile, wMatrix, 0x02),
                        r => r.Length > 0 && r[0] == 0xFF, 400);
        }

        /// <summary>
        /// Writes a display key's ACTION BINDING into the firmware — the write that marks
        /// the key as "custom" so the firmware STOPS executing its own built-in default
        /// action on press (the root of the "double action" bug: K2 never wrote bindings,
        /// so the firmware kept firing defaults alongside K2's software action). Captured
        /// from real Base Camp (evicon.pcapng 2026-07-19: assigning a URL action to D2 =
        /// <c>12 08 00 01</c> then <c>17 AB 13 00 02 "https://google..."</c>; evprofiles
        /// .pcapng: multi-chunk exe paths, e.g. <c>17 AD 3C 01 01 "C:\...Telegra"</c> +
        /// <c>17 AD 05 03 "m.exe"</c>). Wire format:
        ///   first chunk:  17 [0xAA+key] [1+dataLen] [flag] [type] [ASCII data ≤59]
        ///   next chunks:  17 [0xAA+key] [dataLen]   [flag]        [ASCII data ≤60]
        /// flag: 00 = single chunk, 01 = first of many, 03 = final (02 = middle, inferred).
        /// type: 01 = executable path, 02 = URL. For K2 action types the firmware has no
        /// concept of, callers pass a synthetic type-01 payload — the point is flipping
        /// the key to custom mode, execution stays in K2 either way.
        /// </summary>
        public bool WriteDisplayKeyBinding(int keyIndex, byte type, string payload)
        {
            byte key = (byte)(0xAA + keyIndex);
            byte[] data = Encoding.ASCII.GetBytes(payload ?? "");

            bool ok = SendCmdAcked(Cmd(0x12, 0x08, 0x00, 0x01));

            int off = 0;
            bool first = true;
            while (first || off < data.Length)
            {
                int cap = first ? 59 : 60;
                int n = Math.Min(cap, data.Length - off);
                bool last = off + n >= data.Length;

                var pkt = new byte[PktSize];
                pkt[0] = 0x17;
                pkt[1] = key;
                if (first)
                {
                    pkt[2] = (byte)(n + 1);                    // len counts the type byte
                    pkt[3] = last ? (byte)0x00 : (byte)0x01;
                    pkt[4] = type;
                    Buffer.BlockCopy(data, off, pkt, 5, n);
                }
                else
                {
                    pkt[2] = (byte)n;
                    pkt[3] = last ? (byte)0x03 : (byte)0x02;
                    Buffer.BlockCopy(data, off, pkt, 4, n);
                }
                ok &= SendCmdAcked(pkt);
                off += n;
                first = false;
            }

            _log($"[EvNative] WriteDisplayKeyBinding(key={keyIndex}, type=0x{type:X2}, len={data.Length}) -> {ok}");
            return ok;
        }

        /// <summary>
        /// Switches the keyboard's active firmware profile (1-5): wire cmd
        /// <c>14 00 00 00 [profile] 01</c>. Captured from real Base Camp
        /// (evprofiles.pcapng, 2026-07-19): the user's 5 UI profile switches
        /// (3→2→3→1→2→3) appear as exactly this packet with byte4 = target profile and
        /// NOTHING else — the per-profile NDK pictures/lighting swap instantly because
        /// they already live in that profile's flash slot. Replaces the SDKDLL
        /// SwitchProfile call in native-engine mode, where SDKDLL has no OpenUSBDriver
        /// state and its True return was unverifiable.
        /// </summary>
        public bool SwitchProfile(int profile)
        {
            var ok = SendCmdAcked(Cmd(0x14, 0x00, 0x00, 0x00, (byte)profile, 0x01));
            _log($"[EvNative] SwitchProfile({profile}) -> {ok}");
            return ok;
        }

        /// <summary>
        /// Restores display key <paramref name="keyIndex"/> (0-3) of firmware profile
        /// <paramref name="profile"/> (1-5) to its factory-default artwork. Sequence
        /// captured from real Base Camp deleting one icon:
        ///   12 08 00 01 → 14 20 [AA+key] 00 FF (x2) → 13 42 + key mask
        ///   → 12 08 00 01 → 14 20 [AA+key] 00 FF (x2) → 12 00 00 00 32
        /// The 13 42 command carries ONE KEY-BITMASK FIELD PER PROFILE, at byte index
        /// 4 + (profile - 1) — NOT a single mask acting on the active profile. Cross-
        /// confirmed by two independent captures (2026-07-19): deleting D1..D4 while BC
        /// sat on profile 3 produced masks 01/02/04/08 at byte 6 (evprofiles.pcapng),
        /// while deleting D3 on profile 2 produced mask 04 at byte 5 (evicon.pcapng).
        /// An earlier revision hardcoded the mask at byte 6, so every reset silently
        /// targeted PROFILE 3's picture slots regardless of the profile being edited —
        /// acked by the firmware, invisible to the user (report 2026-07-19).
        /// </summary>
        public bool ResetDisplayKeyPic(int keyIndex, int profile)
        {
            if (profile is < 1 or > 5)
            {
                _log($"[EvNative] ResetDisplayKeyPic: invalid profile {profile}");
                return false;
            }

            byte key = (byte)(0xAA + keyIndex);
            var reset = Cmd(0x13, 0x42);
            reset[4 + (profile - 1)] = (byte)(1 << keyIndex);

            bool ok = true;
            void Send(byte[] wire) => ok &= SendCmdAcked(wire);

            Send(Cmd(0x12, 0x08, 0x00, 0x01));
            Send(Cmd(0x14, 0x20, key, 0x00, 0xFF));
            Send(Cmd(0x14, 0x20, key, 0x00, 0xFF));
            Send(reset);
            Send(Cmd(0x12, 0x08, 0x00, 0x01));
            Send(Cmd(0x14, 0x20, key, 0x00, 0xFF));
            Send(Cmd(0x14, 0x20, key, 0x00, 0xFF));
            Send(Cmd(0x12, 0x00, 0x00, 0x00, 0x32));

            _log($"[EvNative] ResetDisplayKeyPic(key={keyIndex}, profile={profile}) -> {ok}");
            return ok;
        }

        /// <summary>Sends the 126 main-keycap LED colors via the raw-HID channel:
        /// zone switch (<c>11 01 00 02 02 02</c>) then the 7 positional page packets
        /// (<see cref="EverestSideLedProtocol.BuildKeycapPackets"/>) — replaces
        /// SDKDLL.dll's <c>ChangeCustomizeEffect</c>, which produced NO wire traffic at
        /// all on real hardware (evmax_fillall_k2.pcapng, 2026-07-22).
        /// <paramref name="wireColors"/> indexed 0-132 (only 0-125 meaningful),
        /// 0xRRGGBB per entry; <paramref name="brightness"/> 0-100.</summary>
        public bool SendKeycapColors(int[] wireColors, byte brightness = 100)
        {
            bool ok = SwitchZoneToCustom(EverestSideLedProtocol.ZoneKeycaps);
            foreach (var pkt in EverestSideLedProtocol.BuildKeycapPackets(wireColors, brightness))
                ok &= SendCmdAcked(pkt);
            _log($"[EvNative] SendKeycapColors -> {ok}");
            return ok;
        }

        /// <summary>
        /// Sends the <c>11 01 00 zone 02 02</c> zone switch and consumes its ENTIRE
        /// response burst. The command doesn't ack with a single echo: the device dumps
        /// the zone's current custom colors as one page packet per page
        /// (<c>11 01 00 zone page ...</c>, page counting DOWN to 00 — 7 packets for
        /// zone 02, 3 for zone 05; see evmax_anchors_bc.pcapng #4253-#4271). Waiting for
        /// the LAST page (page byte 00) makes WaitResp dequeue-and-discard all the
        /// earlier ones. Matching only the first packet (plain <see cref="SendCmdAcked"/>)
        /// left 6 stale <c>11 xx</c> packets queued, which later got mistaken for
        /// <c>11 83</c> module-status replies — numpad/media dock showed as disconnected
        /// and every exchange timed out into retries (the "K2 slow + modules gone"
        /// regression, first hardware test of this path 2026-07-22).
        /// </summary>
        private bool SwitchZoneToCustom(byte zone)
        {
            var resp = SendCommand(
                EverestSideLedProtocol.BuildZoneSwitchPacket(zone),
                r => r.Length >= 5 && r[0] == 0x11 && r[1] == 0x01 && r[3] == zone && r[4] == 0x00,
                1200);
            _log($"[EvNative] SwitchZoneToCustom(zone=0x{zone:X2}) -> {resp is not null}");
            return resp is not null;
        }

        /// <summary>
        /// Sends the 45 border ("side") LED colors — <see cref="EverestSideLedProtocol"/>,
        /// NOT covered by SDKDLL.dll's Custom-mode struct (that one only ever carried the
        /// 126 keycap LEDs). Prefixed by the zone-05 switch (<c>11 01 00 05 02 02</c>),
        /// exactly as Base Camp does before every ring burst (2026-07-22 captures).
        /// <paramref name="wireColors"/> is indexed 0-44 by WIRE index
        /// (see <see cref="EverestSideLedProtocol.MainOrder"/>/<c>NumpadOrder</c> to
        /// translate from physical border-square position), 0xRRGGBB per entry.
        /// </summary>
        public bool SendSideLedColors(int[] wireColors, byte brightness = 0xFF)
        {
            bool ok = SwitchZoneToCustom(EverestSideLedProtocol.ZoneSideRing);
            foreach (var pkt in EverestSideLedProtocol.BuildSideLedPackets(wireColors, brightness))
                ok &= SendCmdAcked(pkt);
            _log($"[EvNative] SendSideLedColors -> {ok}");
            return ok;
        }

        /// <summary>Enables Custom mode / sets overall brightness for the border LEDs
        /// (<c>14 2C 0A</c>) — call before <see cref="SendSideLedColors"/>.</summary>
        public bool EnableCustomLighting(byte brightness = 100)
        {
            bool ok = SendCmdAcked(EverestSideLedProtocol.BuildEnablePacket(brightness));
            _log($"[EvNative] EnableCustomLighting(brightness={brightness}) -> {ok}");
            return ok;
        }

        /// <summary>Persists the current Custom lighting (keys + border) to flash slot 6
        /// (<c>13 55 00 00 06</c>) so it survives a power cycle.</summary>
        public bool PersistCustomLighting()
        {
            bool ok = SendCmdAcked(EverestSideLedProtocol.BuildPersistPacket());
            _log($"[EvNative] PersistCustomLighting -> {ok}");
            return ok;
        }

        private bool WriteCmd(byte[] wire64)
        {
            // Windows HID buffer = report-ID byte (0, unnumbered) + wire data.
            var buf = new byte[PktSize + 1];
            Buffer.BlockCopy(wire64, 0, buf, 1, Math.Min(wire64.Length, PktSize));
            if (!Transfer(_cmd, buf, buf.Length, write: true, 2000, out _))
            {
                _log($"[EvNative] cmd write failed/timeout (win32={Marshal.GetLastWin32Error()})");
                return false;
            }
            return true;
        }

        private void ReaderLoop()
        {
            var buf = new byte[PktSize + 1];
            while (!_stop)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!Transfer(_cmd, buf, buf.Length, write: false, 1000, out int read) || read < 2)
                {
                    if (_stop) return;
                    if (sw.ElapsedMilliseconds < 100) Thread.Sleep(200);
                    continue;
                }
                // wire data = buf[1..read] (Windows report-ID prefix stripped)
                if (buf[1] == 0x01 && read >= 44)
                    DecodeNumpadButtons(buf);

                var wire = new byte[read - 1];
                Buffer.BlockCopy(buf, 1, wire, 0, wire.Length);
                if (K2.Core.AppSettings.LogLevel == K2.Core.K2LogLevel.Verbose)
                    _log($"[EvNative] rx {Convert.ToHexString(wire, 0, Math.Min(12, wire.Length))}");
                _resp.Enqueue(wire);
                _respSignal.Release();
            }
        }

        private void DecodeNumpadButtons(byte[] winBuf)
        {
            // +1: Windows report-ID prefix byte.
            byte bits = winBuf[42 + 1];
            for (int i = 0; i < BtnMap.Length; i++)
            {
                var (_, mask) = BtnMap[i];
                bool now = (bits & mask) != 0;
                bool before = (_prevNumpadBits & mask) != 0;
                if (now != before)
                    NumpadButtonChanged?.Invoke(i, now);
            }
            _prevNumpadBits = bits;
        }

        private byte[]? WaitResp(Func<byte[], bool> match, int timeoutMs)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                int wait = (int)Math.Max(1, deadline - Environment.TickCount64);
                if (!_respSignal.Wait(Math.Min(wait, 250))) continue;
                while (_resp.TryDequeue(out var d))
                    if (match(d)) return d;
            }
            return null;
        }

        private void DrainResponses()
        {
            while (_resp.TryDequeue(out _)) { }
            while (_respSignal.CurrentCount > 0) _respSignal.Wait(0);
        }

        public void Dispose()
        {
            _stop = true;
            try { if (_cmd is { IsInvalid: false, IsClosed: false }) CancelIoEx(_cmd, IntPtr.Zero); } catch { }
            try { _reader?.Join(500); } catch { }
            try { _cmd?.Dispose(); } catch { }
        }
    }

    // ================================================================
    // P/Invoke (identical to DpHidNative — hid.dll + setupapi, no WinUSB)
    // ================================================================

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid gClass, string? enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid gClass,
        uint index, ref SP_DEVICE_INTERFACE_DATA ifData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData,
        IntPtr detail, int detailSize, out int required, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, IntPtr writtenMustBeZero, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, IntPtr readMustBeZero, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
