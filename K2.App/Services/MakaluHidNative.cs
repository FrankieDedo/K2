using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Raw USB-HID access to the Mountain Makalu mouse (VID 0x3282, PID 0x0003
/// Makalu 67 / 0x0002 Makalu Max) — no vendor SDK/DLL exists for this device
/// at all (unlike MacroPad/Everest Max, which wrap <c>MacroPadSDK.dll</c>/
/// <c>SDKDLL.dll</c>). Same transport shape as <see cref="Everest60HidNative"/>
/// (setupapi.dll + hid.dll P/Invoke, no external package): plain HID
/// <b>Feature Reports</b>, 64 bytes, report ID 0xA1, on interface 1.
///
/// <para>
/// Protocol reverse-engineered by the BaseCampLinux community project
/// (<c>devices/makalu67/controller.py</c>, from a Windows USB capture) —
/// lighting, DPI, polling rate/debounce/lift-off/angle-snapping, button
/// remap + sniper. Ported line-for-line in <see cref="MakaluProtocol"/>.
/// </para>
/// </summary>
internal static class MakaluHidNative
{
    public const ushort VID = 0x3282;
    public const ushort PidMakalu67  = 0x0003;
    public const ushort PidMakaluMax = 0x0002;

    /// <summary>Feature report size: 64 bytes, byte[0] = report ID 0xA1 (already
    /// included — unlike Everest 60's separate leading report-ID byte).</summary>
    public const int ReportSize = 64;

    /// <summary>Interface 1 is where Base Camp talks to the mouse (controller.py:
    /// <c>d.get('interface_number') == 1</c>). On Windows the device path for a
    /// composite HID device's Nth USB interface contains <c>&amp;mi_0N&amp;</c>.</summary>
    private const string InterfaceMarker = "mi_01";

    public readonly record struct FoundDevice(string Path, ushort Pid);

    /// <summary>Last known-good device path, so steady-state polling doesn't
    /// need to re-walk every HID interface on the system each time (see
    /// <see cref="FindDevice"/>'s doc comment).</summary>
    private static FoundDevice? _cached;

    /// <summary>
    /// Returns the Makalu's interface-1 HID collection, if connected.
    /// <para>
    /// 2026-07-13: cheaply re-validates <see cref="_cached"/> (a single
    /// CreateFile+HidD_GetAttributes on the one known path) before falling
    /// back to <see cref="FindDeviceUncached"/>'s full
    /// SetupDiGetClassDevsW/SetupDiEnumDeviceInterfaces walk over EVERY HID
    /// interface on the machine. That full walk runs on every single poll
    /// (3s timer) plus several times back-to-back on every profile
    /// reload/apply — since Makalu shares Mountain's VID (0x3282) with every
    /// other K2-supported device, it was opening (query-only) and
    /// immediately closing handles on Everest 60's own device interfaces
    /// just to reject them by PID. Real-hardware report: with a Makalu
    /// connected, Everest 60's SDK reads (<c>GetSubDeviceInfo</c>,
    /// <c>GetColorData2</c> — the only two Everest60SdkNative calls that need
    /// a live HID round-trip, unlike the simple state-toggle calls that kept
    /// succeeding) failed 100% of the time; unplugging the Makalu alone (no
    /// other change) made them succeed every time. Caching removes the
    /// systemic full-tree enumeration from the steady-state path — it only
    /// runs again once the cached path actually stops answering (unplug/
    /// reconnect/enumeration-order change), which is exactly when it's
    /// needed. See CLAUDE.md's "no guessing bit-layout" rule: this is
    /// deliberately NOT a guess at Everest360_USB.dll's internals, just a
    /// reduction of K2's own contribution to shared HID-stack traffic.
    /// </para>
    /// </summary>
    public static FoundDevice? FindDevice(Action<string>? log = null)
    {
        if (_cached is { } cached && StillValid(cached))
            return cached;

        var found = FindDeviceUncached(log);
        _cached = found;
        return found;
    }

    /// <summary>Single CreateFile+HidD_GetAttributes check on one already-known
    /// path — no system-wide enumeration.</summary>
    private static bool StillValid(FoundDevice cached)
    {
        using var h = OpenHandle(cached.Path, throwOnFail: false, queryOnly: true);
        if (h is null || h.IsInvalid) return false;
        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        return HidD_GetAttributes(h, ref attrs) && attrs.VendorID == VID && attrs.ProductID == cached.Pid;
    }

    private static FoundDevice? FindDeviceUncached(Action<string>? log)
    {
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
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != VID)
                    continue;
                if (attrs.ProductID != PidMakalu67 && attrs.ProductID != PidMakaluMax)
                    continue;

                string lower = path.ToLowerInvariant();
                if (!lower.Contains(InterfaceMarker)) continue;

                // Interface 1 exposes SEVERAL HID top-level collections (col01,
                // col02, ... — each its own device path in Windows), only one of
                // which actually declares Feature Reports big enough for our
                // 64-byte report. Matching on "mi_01" alone picks whichever
                // collection Windows enumerates first, which isn't guaranteed
                // stable across boots/reconnects — 2026-07-13: a session that
                // landed on a different collection than before got SetFeature
                // silently rejected (HidD_SetFeature -> false) despite the handle
                // opening fine. Checking FeatureReportByteLength via
                // HidP_GetCaps is the deterministic way to find the right one.
                if (!TryGetFeatureReportLength(h, out int featureLen) || featureLen < ReportSize)
                    continue;

                log?.Invoke($"[MakaluNative] found {lower[..Math.Min(80, lower.Length)]}…");
                return new FoundDevice(path, attrs.ProductID);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return null;
    }

    /// <summary>Reads the collection's declared Feature Report length via the
    /// standard HID capabilities API — works on a handle opened with no access
    /// rights (query-only), since it just reads the cached device descriptor,
    /// no I/O involved.</summary>
    private static bool TryGetFeatureReportLength(SafeFileHandle h, out int length)
    {
        length = 0;
        if (!HidD_GetPreparsedData(h, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            return false;
        try
        {
            if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
                return false;
            length = caps.FeatureReportByteLength;
            return true;
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    /// <summary>Same as <see cref="TryGetFeatureReportLength"/> but for InputReportByteLength —
    /// used by <see cref="FindDpiButtonDevice"/> to pick the DPI button's own collection
    /// (distinct from the Feature-Report-only vendor config collection above).</summary>
    private static bool TryGetInputReportLength(SafeFileHandle h, out int length)
    {
        length = 0;
        if (!HidD_GetPreparsedData(h, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            return false;
        try
        {
            if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS)
                return false;
            length = caps.InputReportByteLength;
            return true;
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    /// <summary>Last known-good DPI-button collection path (mirrors <see cref="_cached"/>'s
    /// caching rationale for the vendor config collection above).</summary>
    private static FoundDevice? _cachedDpi;

    /// <summary>
    /// Finds the Makalu's DPI-BUTTON HID collection — a SEPARATE top-level collection on
    /// the same USB interface (mi_01) as the vendor Feature-Report config channel above, but
    /// its own device path in Windows (col01 vs col02, etc.). Confirmed via USB capture
    /// 2026-07-27 (_reference/usb_dumps/makalu_tasti.pcapng, see MakaluDpiButtonWatcher's
    /// class doc): the DPI button sends a plain 8-byte HID INPUT report on this collection
    /// (03 02 01 00 00 00 00 00, one-shot, no release) — invisible to both the vendor Feature
    /// Report channel (FeatureReportByteLength=0 here) and to Windows' mouse Raw Input
    /// decoding (not a Generic Desktop/Mouse usage). Distinguished from the config collection
    /// by InputReportByteLength: 0 for the config collection (Feature-only), &gt;=
    /// <see cref="MakaluDpiButtonWatcher.InputReportSize"/> for this one.
    /// </summary>
    public static FoundDevice? FindDpiButtonDevice(Action<string>? log = null)
    {
        if (_cachedDpi is { } cached && StillValidInput(cached))
            return cached;

        var found = FindDpiButtonDeviceUncached(log);
        _cachedDpi = found;
        return found;
    }

    private static bool StillValidInput(FoundDevice cached)
    {
        using var h = OpenHandle(cached.Path, throwOnFail: false, queryOnly: true);
        if (h is null || h.IsInvalid) return false;
        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        return HidD_GetAttributes(h, ref attrs) && attrs.VendorID == VID && attrs.ProductID == cached.Pid;
    }

    private static FoundDevice? FindDpiButtonDeviceUncached(Action<string>? log)
    {
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
                if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != VID)
                    continue;
                if (attrs.ProductID != PidMakalu67 && attrs.ProductID != PidMakaluMax)
                    continue;

                string lower = path.ToLowerInvariant();
                if (!lower.Contains(InterfaceMarker)) continue;

                if (!TryGetInputReportLength(h, out int inputLen) || inputLen < 8)
                    continue;

                log?.Invoke($"[MakaluNative] found DPI-button collection {lower[..Math.Min(80, lower.Length)]}…");
                return new FoundDevice(path, attrs.ProductID);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return null;
    }

    /// <summary>Opens the DPI-button collection for a persistent blocking read loop (see
    /// <see cref="MakaluDpiButtonWatcher"/>) — plain synchronous handle, no
    /// FILE_FLAG_OVERLAPPED: a background thread blocks in ReadFile and Dispose()-ing the
    /// handle from another thread is what unblocks/cancels it on Stop().</summary>
    public static SafeFileHandle? OpenForRead(string path, Action<string>? log = null)
    {
        var h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            log?.Invoke($"[MakaluNative] OpenForRead failed (win32={Marshal.GetLastWin32Error()}) for {path}");
            h.Dispose();
            return null;
        }
        return h;
    }

    /// <summary>Blocking synchronous ReadFile — waits for the next HID input report on
    /// <paramref name="h"/> (opened via <see cref="OpenForRead"/>, no overlapped flag).
    /// Returns false on error (including the handle being closed by another thread, which
    /// is how <see cref="MakaluDpiButtonWatcher.Stop"/> cancels a pending read).</summary>
    public static bool ReadReport(SafeFileHandle h, byte[] buf, out int bytesRead)
    {
        bytesRead = 0;
        if (!ReadFile(h, buf, (uint)buf.Length, out uint read, IntPtr.Zero))
            return false;
        bytesRead = (int)read;
        return true;
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

    private static SafeFileHandle? OpenHandle(string path, bool throwOnFail, bool queryOnly = false)
    {
        var h = CreateFileW(path, queryOnly ? 0 : GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            if (throwOnFail)
                throw new InvalidOperationException($"CreateFile failed (win32={Marshal.GetLastWin32Error()}) for {path}");
            h.Dispose();
            return null;
        }
        return h;
    }

    /// <summary>Opens the device for a single request/response cycle. Feature
    /// reports are stateless control transfers — controller.py itself opens
    /// and closes the handle per call, no persistent session needed.</summary>
    public static SafeFileHandle? Open(string path, Action<string>? log = null)
    {
        try { return OpenHandle(path, throwOnFail: true); }
        catch (Exception ex) { log?.Invoke($"[MakaluNative] open failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Sends a 64-byte feature report (byte[0] must already be 0xA1) and reads
    /// back the response — mirrors controller.py's <c>_run_cmd()</c>
    /// (send, sleep 50ms, get_feature_report). Returns the raw response, or
    /// null if either transfer failed.
    /// </summary>
    public static byte[]? SendFeature(SafeFileHandle h, byte[] report64)
    {
        if (report64.Length != ReportSize)
            throw new ArgumentException($"report must be {ReportSize} bytes", nameof(report64));
        if (!HidD_SetFeature(h, report64, report64.Length))
            return null;
        Thread.Sleep(50);

        var resp = new byte[ReportSize];
        resp[0] = MakaluProtocol.ReportId;
        return HidD_GetFeature(h, resp, resp.Length) ? resp : null;
    }

    /// <summary>Sets a feature report with no follow-up read — used for the software-action
    /// "ack" write (<see cref="MakaluProtocol.AckButtonEvent"/>), which is followed by a
    /// DIFFERENT, much larger report ID read (0xA0, ~1KB) rather than the usual same-size
    /// round-trip <see cref="SendFeature"/> assumes.</summary>
    public static bool SetFeatureOnly(SafeFileHandle h, byte[] report) =>
        HidD_SetFeature(h, report, report.Length);

    /// <summary>Reads a feature report of a caller-specified length/report ID — used for the
    /// software-action payload read (report ID 0xA0, confirmed by capture to be far larger
    /// than the fixed 64-byte <see cref="ReportSize"/> every other command here uses).
    /// <paramref name="reportId"/> is written to byte[0] before the call, as Windows' HID
    /// class driver requires.</summary>
    public static byte[]? GetFeatureLarge(SafeFileHandle h, byte reportId, int length)
    {
        var resp = new byte[length];
        resp[0] = reportId;
        return HidD_GetFeature(h, resp, resp.Length) ? resp : null;
    }

    /// <summary>Declared max Feature Report length for the collection <paramref name="h"/> is
    /// open on (<see cref="TryGetFeatureReportLength"/>, public wrapper) — used instead of a
    /// hardcoded buffer size for <see cref="GetFeatureLarge"/>, since the two real captures
    /// backing the software-action payload read disagreed on exact byte count (1041 vs 1049)
    /// depending on context; asking the device its own declared max avoids guessing which.</summary>
    public static int GetMaxFeatureReportLength(SafeFileHandle h) =>
        TryGetFeatureReportLength(h, out int len) ? len : ReportSize;

    // ================================================================
    // P/Invoke (same shape as Everest60HidNative — hid.dll + setupapi.
    // No FILE_FLAG_OVERLAPPED: HidD_SetFeature/GetFeature are synchronous
    // control transfers, no background reader thread needed here.)
    // ================================================================

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
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

    /// <summary>Mirrors Windows' hidpi.h HIDP_CAPS — only FeatureReportByteLength
    /// is actually used, but every field must be present for the layout (and
    /// therefore the offset of that field) to match the native struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
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

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint numberOfBytesToRead,
        out uint numberOfBytesRead, IntPtr overlapped);
}
