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
