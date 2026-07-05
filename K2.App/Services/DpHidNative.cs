using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Raw USB-HID access to the Mountain DisplayPad (VID 0x3282, PID 0x0009) — no SDK DLL.
///
/// Protocol reverse-engineered by the BaseCampLinux project (see BaseCampLinux/devices/
/// displaypad/panel.py, itself based on JeLuF/mountain-displaypad and the decompiled
/// MountainDisplayPadWorker.exe):
/// <list type="bullet">
/// <item>Command interface (USB MI_03): 64-byte reports. INIT = <c>11 80 00 00 01</c>,
///   image-start = <c>21 00 00 00 [key] 3d 00 00 65 65</c>, brightness = <c>12 03 00 00 [pct]</c>.
///   Key events arrive as input reports with byte0 = 0x01: bits of byte 42 = K1..K7,
///   bits of byte 47 = K8..K12 (state bitmap, rising edge = press).</item>
/// <item>Display interface (USB MI_01): icon pixels. Payload = 306-byte zero header +
///   102×102×3 BGR pixels, zero-padded to 31744 bytes, streamed in report-sized chunks.
///   Device answers <c>21 00 00</c> (ready for pixels) and <c>21 00 FF</c> (done) on the
///   command interface.</item>
/// </list>
/// On Windows both interfaces are ordinary HID collections (DisplayPadSDK.dll itself only
/// imports hid.dll + setupapi — no WinUSB), so everything goes through CreateFile/
/// ReadFile/WriteFile with unnumbered reports (buffer byte 0 = report ID 0, wire data
/// follows). All byte offsets below refer to WIRE data, i.e. Windows buffer index - 1.
/// </summary>
internal static class DpHidNative
{
    public const ushort VID = 0x3282;
    public const ushort PID = 0x0009;
    public const int IconSize = 102;
    public const int IconBytes = IconSize * IconSize * 3;   // 31212, BGR
    public const int PanelW = 800;                           // full panel LCD, from BC sniff
    public const int PanelH = 240;                           //   (cmd 21 00 00 01: w-1=799, h-1=239)
    public const int PanelBytes = PanelW * PanelH * 3;       // 576000, BGR

    // ================================================================
    // Enumeration: pair the command (MI_03) and display (MI_01) HID
    // collections that belong to the same physical DisplayPad.
    // ================================================================

    internal sealed class PadInterfaces
    {
        public string GroupKey = "";     // parent USB composite device instance ID (stable per pad)
        public string? CmdPath;
        public string? DisplayPath;
        public int CmdOutLen, CmdInLen, DisplayOutLen;
    }

    /// <summary>Enumerates connected DisplayPads, grouped per physical device and
    /// sorted by GroupKey so IDs stay stable across sessions.</summary>
    public static List<PadInterfaces> Enumerate(Action<string>? log = null)
    {
        var groups = new Dictionary<string, PadInterfaces>(StringComparer.OrdinalIgnoreCase);
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero,
                                           DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) return new();
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                string? path = GetInterfacePath(devs, ref ifData, ref devInfo);
                if (path is null) continue;

                // Query-only access: HidD_GetAttributes/GetPreparsedData work without
                // GENERIC_READ/WRITE, and this never fails with ACCESS_DENIED on busy devices.
                using var h = OpenHandle(path, throwOnFail: false, queryOnly: true);
                if (h is null || h.IsInvalid) continue;

                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != VID || attrs.ProductID != PID)
                    continue;
                if (!HidD_GetPreparsedData(h, out IntPtr prep)) continue;
                HIDP_CAPS caps;
                try { if (HidP_GetCaps(prep, out caps) != HIDP_STATUS_SUCCESS) continue; }
                finally { HidD_FreePreparsedData(prep); }

                string key = GetParentKey(devInfo.DevInst) ?? SerialKey(h) ?? "single";
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = new PadInterfaces { GroupKey = key };

                // An interface can expose several HID collections; picking the wrong one
                // corrupts the pixel stream (short reports) or hangs commands. Rules:
                //  - display = the collection with the LARGEST output report (pixel pipe;
                //    the SDK streams 1024-byte chunks, so expect ~1025);
                //  - command = 64-byte in+out collection, preferring the MI_03 interface.
                string lower = path.ToLowerInvariant();
                if (caps.OutputReportByteLength > 500)
                {
                    if (caps.OutputReportByteLength > g.DisplayOutLen)
                    {
                        g.DisplayPath = path;
                        g.DisplayOutLen = caps.OutputReportByteLength;
                    }
                }
                else if (caps.OutputReportByteLength >= 64 && caps.InputReportByteLength >= 64)
                {
                    bool better = g.CmdPath is null ||
                                  (lower.Contains("mi_03") && !g.CmdPath.ToLowerInvariant().Contains("mi_03"));
                    if (better)
                    {
                        g.CmdPath = path;
                        g.CmdOutLen = caps.OutputReportByteLength;
                        g.CmdInLen = caps.InputReportByteLength;
                    }
                }
                log?.Invoke($"[DpNative] HID {lower[..Math.Min(70, lower.Length)]}… out={caps.OutputReportByteLength} in={caps.InputReportByteLength} key={key}");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }

        return groups.Values
            .Where(g => g.CmdPath is not null && g.DisplayPath is not null)
            .OrderBy(g => g.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Walks up two levels (HID collection → USB interface → USB composite
    /// device) to get an instance ID shared by all interfaces of one physical pad.</summary>
    private static string? GetParentKey(uint devInst)
    {
        uint cur = devInst;
        for (int hop = 0; hop < 2; hop++)
            if (CM_Get_Parent(out cur, cur, 0) != 0) return null;
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(cur, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    private static string? SerialKey(SafeFileHandle h)
    {
        var buf = new byte[256];
        if (!HidD_GetSerialNumberString(h, buf, buf.Length)) return null;
        string s = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    internal static SafeFileHandle? OpenHandle(string path, bool throwOnFail, bool queryOnly = false)
    {
        // R/W handles are opened OVERLAPPED so every I/O gets an explicit timeout —
        // a synchronous HID Read/WriteFile can block forever (e.g. endpoint NAKing,
        // wrong collection picked), which froze K2's startup on the UI thread.
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

    /// <summary>
    /// Overlapped Read/WriteFile with a hard timeout. Returns false on timeout or error;
    /// a transfer that completes while being cancelled is still treated as success.
    /// The buffer is pinned for the whole operation (the kernel writes into it after the
    /// initial call returns ERROR_IO_PENDING).
    /// </summary>
    internal static bool Transfer(SafeFileHandle h, byte[] buf, int len, bool write,
                                  int timeoutMs, out int done)
    {
        done = 0;
        using var evt = new ManualResetEvent(false);
        IntPtr ovl = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf,
            System.Runtime.InteropServices.GCHandleType.Pinned);
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
                    evt.WaitOne(2000);   // wait for the cancellation (or late completion) to be reaped
                    // If the transfer actually completed in the race window, keep the data.
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
    // One open physical DisplayPad
    // ================================================================

    internal sealed class Pad : IDisposable
    {
        private readonly PadInterfaces _info;
        private readonly Action<string> _log;
        private SafeFileHandle _cmd = null!;
        private SafeFileHandle _disp = null!;
        private Thread? _reader;
        private volatile bool _stop;
        private readonly object _ioLock = new();
        private readonly ConcurrentQueue<byte[]> _resp = new();
        private readonly SemaphoreSlim _respSignal = new(0);
        private readonly byte[] _prevKeys = new byte[64];

        /// <summary>(keyIndex 0-11, pressed)</summary>
        public event Action<int, bool>? KeyChanged;

        public string GroupKey => _info.GroupKey;

        // Key-event bit map (wire offsets): byte 42 bits 1..7 = K1..K7, byte 47 bits 0..4 = K8..K12.
        private static readonly (int Byte, byte Mask)[] KeyMap =
            new byte[] { 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 }.Select(m => (42, m))
            .Concat(new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10 }.Select(m => (47, m)))
            .ToArray();

        public Pad(PadInterfaces info, Action<string> log)
        {
            _info = info;
            _log = log;
        }

        public void Open()
        {
            _cmd = OpenHandle(_info.CmdPath!, throwOnFail: true)!;
            _disp = OpenHandle(_info.DisplayPath!, throwOnFail: true)!;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "DpNativeReader" };
            _reader.Start();
            try
            {
                Init(attempts: 2);
            }
            catch (InvalidOperationException)
            {
                // The pad ignores commands if a previous session died mid-image-transfer:
                // its display pipe still waits for the remaining pixel chunks and the
                // command endpoint stalls until an internal timeout (~20 s+). Complete
                // the pending transfer with zero-filled chunks to unwedge it, then retry.
                _log($"[DpNative] {GroupKey}: INIT silent — flushing display pipe (recover from interrupted transfer)");
                FlushDisplayPipe();
                Init(attempts: 2);
            }
        }

        // ---- init handshake -------------------------------------------------

        private void Init(int attempts)
        {
            var init = new byte[64];
            init[0] = 0x11; init[1] = 0x80; init[4] = 0x01;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(500);
                DrainResponses();
                WriteCmd(init);
                if (WaitResp(d => d.Length >= 2 && d[0] == 0x11 && d[1] == 0x80, 2500) is not null)
                {
                    _log($"[DpNative] init OK ({GroupKey})");
                    return;
                }
            }
            throw new InvalidOperationException("DisplayPad did not respond to INIT");
        }

        /// <summary>
        /// Writes up to one full image payload of zeros to the display pipe. If the
        /// firmware is stuck waiting for the rest of an interrupted transfer, this
        /// completes it (it paints a partial black icon, repainted right after by the
        /// profile reload). If the pad is NOT stuck the endpoint won't accept the data —
        /// the first write times out and we stop immediately, so this costs one write
        /// timeout at most on a healthy device.
        /// </summary>
        private void FlushDisplayPipe()
        {
            // Worst case pending transfer is a full-panel image (563 × 1024 bytes).
            int flushMax = (PanelBytes + 1023) / 1024 * 1024;
            int chunk = Math.Max(64, _info.DisplayOutLen - 1);
            var buf = new byte[_info.DisplayOutLen];
            int sent = 0;
            for (int off = 0; off < flushMax; off += chunk)
            {
                if (!Transfer(_disp, buf, buf.Length, write: true, 1500, out _)) break;
                sent++;
            }
            _log($"[DpNative] {GroupKey}: display pipe flush — {sent} chunk(s) accepted");
        }

        // ---- icon upload ----------------------------------------------------

        /// <summary>
        /// Uploads one 102×102 BGR icon to a key (live). Thread-safe, serialized.
        /// ALWAYS transfers — no content dedup. The firmware occasionally repaints the
        /// panel from its flash-stored icons (physical profile key, wake, reconnect),
        /// which the native engine never updates: a "the device already shows this"
        /// cache would then refuse to repair the stale/corrupted flash image. BC and
        /// BaseCampLinux also re-upload unconditionally on every reload.
        /// If the handshake fails, re-INITs the command interface once and retries.
        /// </summary>
        public bool UploadIcon(int keyIndex, byte[] bgr)
        {
            if (bgr.Length != IconBytes)
                throw new ArgumentException($"expected {IconBytes} BGR bytes, got {bgr.Length}");
            lock (_ioLock)
            {
                if (UploadIconLocked(keyIndex, bgr)) return true;
                Resync($"key {keyIndex}");
                return UploadIconLocked(keyIndex, bgr);
            }
        }

        /// <summary>
        /// Recovers a desynchronized transfer state machine. If a stream fails halfway
        /// (or a stale READY made us stream before the device processed the START), the
        /// firmware is left waiting for pixel chunks and will attribute the NEXT stream
        /// to the WRONG key — icons appear shifted from then on. Completing the pending
        /// transfer with zero chunks + re-INIT realigns command/pixel pipes.
        /// </summary>
        private void Resync(string what)
        {
            _log($"[DpNative] {what}: RESYNC (flush pending transfer + re-init)");
            FlushDisplayPipe();
            try { Init(attempts: 2); }
            catch (Exception ex) { _log($"[DpNative] resync re-init failed: {ex.Message}"); }
        }

        private bool UploadIconLocked(int keyIndex, byte[] bgr)
        {
            // Icon start command (from BC sniff): 21 00 00 00 [key] 3D 00 00 [w-1] [h-1]
            // where 0x3D = 61 = number of 512-byte pixel blocks (61×512 ≥ 31212).
            var start = new byte[64];
            start[0] = 0x21; start[4] = (byte)keyIndex;
            start[5] = 0x3d; start[8] = 0x65; start[9] = 0x65;
            return StreamLocked(start, bgr, $"key {keyIndex}", settleMs: 4);
        }

        /// <summary>
        /// Uploads a full-panel 800×240 BGR image (BC's SetPanelImage / UploadLogo).
        /// Command from BC sniff: 21 00 00 01 [blocks LE16=0x0465] 00 00 00 00 [w-1 LE16=799]
        /// [h-1 LE16=239], then 306-byte header + 576000 pixels padded to 563×1024.
        /// Pass null for a black (blank) panel. BC sends this — after an INIT — before
        /// reloading the profile icons on every profile change.
        /// </summary>
        public bool UploadPanel(byte[]? bgr)
        {
            bgr ??= new byte[PanelBytes];
            if (bgr.Length != PanelBytes)
                throw new ArgumentException($"expected {PanelBytes} BGR bytes, got {bgr.Length}");
            var start = new byte[64];
            start[0] = 0x21; start[3] = 0x01;
            start[4] = 0x65; start[5] = 0x04;     // 0x0465 = 1125 pixel blocks × 512 = 576000
            start[10] = 0x1F; start[11] = 0x03;   // width-1  = 799
            start[12] = 0xEF; start[13] = 0x00;   // height-1 = 239
            lock (_ioLock)
            {
                if (StreamLocked(start, bgr, "panel", settleMs: 4)) return true;
                Resync("panel");
                return StreamLocked(start, bgr, "panel", settleMs: 4);
            }
        }

        /// <summary>
        /// Shared streaming core, aligned byte-for-byte and pacing-wise with what Base
        /// Camp's DLL does on the wire (verified on the user's full USBPcap capture):
        /// <list type="bullet">
        /// <item>START on the cmd pipe → the device replies with a FULL ECHO of the
        ///   command (including key index) = READY. Matched strictly against the echo,
        ///   so a stale response can never be mistaken for this transfer's READY.</item>
        /// <item>Pixels start at OFFSET 0 of the stream — NO 306-byte header. (The 306
        ///   header in BaseCampLinux is a reverse-engineering artifact: BC's real
        ///   streams have nonzero pixel bytes as early as offset 54. The bogus header
        ///   shifted every icon down one row and desynced the byte count.)</item>
        /// <item>Chunks are paced at ~250 µs apart (BC capture: p50=250, mean=254 µs —
        ///   blasting them at xHCI burst speed overruns the firmware FIFO and corrupts
        ///   single icons at random). Busy-wait, as Sleep granularity is too coarse.</item>
        /// <item>DONE = 21 00 FF FF…, then a ~4 ms settle (BC sends the next command
        ///   ~3.3 ms after DONE).</item>
        /// </list>
        /// </summary>
        private bool StreamLocked(byte[] startCmd, byte[] pixels, string what, int settleMs)
        {
            DrainResponses();
            WriteCmd(startCmd);
            // READY = full echo of the start command (first 10 bytes are plenty unique).
            bool IsEcho(byte[] d)
            {
                if (d.Length < 10) return false;
                for (int i = 0; i < 10; i++) if (d[i] != startCmd[i]) return false;
                return true;
            }
            if (WaitResp(IsEcho, 10_000) is null)
            {
                _log($"[DpNative] {what}: no READY echo");
                return false;
            }

            int total = (pixels.Length + 1023) / 1024 * 1024;   // icon: 31744, panel: 576512
            var payload = new byte[total];
            Buffer.BlockCopy(pixels, 0, payload, 0, pixels.Length);
            int chunk = Math.Max(64, _info.DisplayOutLen - 1);
            var buf = new byte[_info.DisplayOutLen];
            long gapTicks = System.Diagnostics.Stopwatch.Frequency / 4000;   // 250 µs
            long lastWrite = 0;
            for (int off = 0; off < payload.Length; off += chunk)
            {
                Array.Clear(buf, 0, buf.Length);
                int n = Math.Min(chunk, payload.Length - off);
                Buffer.BlockCopy(payload, off, buf, 1, n);
                if (lastWrite != 0)
                    while (System.Diagnostics.Stopwatch.GetTimestamp() - lastWrite < gapTicks)
                        Thread.SpinWait(20);
                lastWrite = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!Transfer(_disp, buf, buf.Length, write: true, 2000, out int written) ||
                    written != buf.Length)
                {
                    _log($"[DpNative] {what}: display write failed/short at off={off} " +
                         $"(written={written}/{buf.Length}, win32={Marshal.GetLastWin32Error()})");
                    return false;
                }
            }

            bool ok = WaitResp(d => d[0] == 0x21 && d[1] == 0x00 && d[2] == 0xFF, 20_000) is not null;
            if (!ok) _log($"[DpNative] {what}: transfer not confirmed");
            if (ok && settleMs > 0) Thread.Sleep(settleMs);
            return ok;
        }

        /// <summary>Re-runs the INIT handshake (BC does this before every profile repaint).</summary>
        public bool Reinit()
        {
            lock (_ioLock)
            {
                try { Init(attempts: 2); return true; }
                catch (Exception ex) { _log($"[DpNative] re-init failed: {ex.Message}"); return false; }
            }
        }

        // ---- brightness -----------------------------------------------------

        public bool SetBrightness(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            var msg = new byte[64];
            msg[0] = 0x12; msg[1] = 0x03; msg[4] = (byte)percent;
            lock (_ioLock)
            {
                DrainResponses();
                if (!WriteCmd(msg)) return false;
                // The device echoes the command when done (57 ms in the BC capture) —
                // wait for it so the echo can't linger into the next handshake and the
                // firmware isn't hit with a 0x21 while still applying brightness.
                WaitResp(d => d.Length >= 2 && d[0] == 0x12 && d[1] == 0x03, 800);
                return true;
            }
        }

        // ---- plumbing -------------------------------------------------------

        private bool WriteCmd(byte[] wire64)
        {
            var buf = new byte[Math.Max(65, _info.CmdOutLen)];
            Buffer.BlockCopy(wire64, 0, buf, 1, Math.Min(wire64.Length, buf.Length - 1));
            if (!Transfer(_cmd, buf, buf.Length, write: true, 2000, out _))
            {
                _log($"[DpNative] cmd write failed/timeout (win32={Marshal.GetLastWin32Error()})");
                return false;
            }
            return true;
        }

        private void ReaderLoop()
        {
            var buf = new byte[Math.Max(65, _info.CmdInLen)];
            while (!_stop)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!Transfer(_cmd, buf, buf.Length, write: false, 1000, out int read) || read < 2)
                {
                    if (_stop) return;
                    // Fast failure = handle/device error (a timeout takes the full 1000ms):
                    // back off instead of spinning.
                    if (sw.ElapsedMilliseconds < 100) Thread.Sleep(200);
                    continue;
                }
                // wire data = buf[1..read]
                if (buf[1] == 0x01 && read >= 49)
                    DecodeKeys(buf);
                else
                {
                    var wire = new byte[read - 1];
                    Buffer.BlockCopy(buf, 1, wire, 0, wire.Length);
                    // At Verbose, dump every non-key packet: needed to learn the exact
                    // READY/DONE response structure (does it echo the key index? are
                    // there extra status packets?) for stricter handshake matching.
                    if (K2.Core.AppSettings.LogLevel == K2.Core.K2LogLevel.Verbose)
                        _log($"[DpNative] rx {Convert.ToHexString(wire, 0, Math.Min(12, wire.Length))} ({GroupKey[^3..]})");
                    _resp.Enqueue(wire);
                    _respSignal.Release();
                }
            }
        }

        private void DecodeKeys(byte[] winBuf)
        {
            for (int k = 0; k < KeyMap.Length; k++)
            {
                var (bi, mask) = KeyMap[k];
                bool now = (winBuf[bi + 1] & mask) != 0;   // +1: Windows report-ID byte
                bool before = (_prevKeys[bi] & mask) != 0;
                if (now != before)
                    KeyChanged?.Invoke(k, now);
            }
            Buffer.BlockCopy(winBuf, 1, _prevKeys, 0, Math.Min(64, winBuf.Length - 1));
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
            // Let an in-flight icon upload finish (≤3 s) before closing the handles:
            // killing a transfer halfway leaves the firmware waiting for the missing
            // chunks and the pad ignores commands for the next session (see Open()).
            bool got = false;
            try { got = Monitor.TryEnter(_ioLock, 3000); } catch { }
            try
            {
                try { if (_cmd is { IsInvalid: false, IsClosed: false }) CancelIoEx(_cmd, IntPtr.Zero); } catch { }
                try { _reader?.Join(500); } catch { }
                try { _cmd?.Dispose(); } catch { }
                try { _disp?.Dispose(); } catch { }
            }
            finally { if (got) Monitor.Exit(_ioLock); }
        }
    }

    // ================================================================
    // P/Invoke
    // ================================================================

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
            NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    private static string? GetInterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData,
                                            ref SP_DEVINFO_DATA devInfo)
    {
        SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, IntPtr.Zero, 0, out int size, ref devInfo);
        if (size <= 0) return null;
        IntPtr detail = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 (4+2) on x86.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(devs, ref ifData, detail, size, out _, ref devInfo))
                return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

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

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, uint flags);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr prep);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr prep);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr prep, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int len);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    // Overlapped-only signatures: lpNumberOfBytes must be NULL when an OVERLAPPED is
    // used (per WriteFile/ReadFile docs); the count comes from GetOverlappedResult.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, IntPtr writtenMustBeZero, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, IntPtr readMustBeZero, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
