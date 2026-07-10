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

    /// <summary>Enumerates the Makalu's interface-1 HID collection, if connected.</summary>
    public static FoundDevice? FindDevice(Action<string>? log = null)
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

                log?.Invoke($"[MakaluNative] found {lower[..Math.Min(80, lower.Length)]}…");
                return new FoundDevice(path, attrs.ProductID);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return null;
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

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
