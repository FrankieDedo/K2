using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Raw USB-HID access to the Mountain Everest 60 keyboard (VID 0x3282,
/// PID 0x0005 ANSI / 0x0006 ISO) — no <c>Everest360_USB.dll</c>.
///
/// <para>
/// <b>Why raw HID, not the SDK.</b> Base Camp's own Everest 60 SDK wrapper
/// (class <c>BaseCamp.Service.Helpers.Everest60</c> in
/// <c>Everest360_USB.dll</c>, see <c>_reference/Everest_SDK_signatures.txt</c>)
/// passes almost every struct as an opaque <c>IntPtr</c> instead of a typed
/// struct — the wire layout behind those pointers has never been
/// reverse-engineered, and doing so would need the same multi-session
/// USB-capture effort as Everest Max's <c>EffData</c>/<c>BlockData</c> (see
/// CHANGELOG 2026-05-30). The RGB lighting protocol, however, is ALREADY
/// reverse-engineered and published by the BaseCampLinux community project
/// (<c>devices/everest60/controller.py</c>, itself cross-referencing
/// OpenRGB's MountainKeyboard60Controller): plain HID <b>Feature Reports</b>
/// on interface 2, no vendor DLL involved. Porting that known-good protocol
/// is far cheaper and safer than guessing struct layouts, and it needs no
/// non-redistributable DLL at all for this feature — see
/// <see cref="Everest60Protocol"/> for the command layer built on top of
/// this transport.
/// </para>
///
/// <para>
/// <b>NOT covered yet:</b> key remapping / macros / Fn-layer (SDK
/// <c>ChangeKey</c>/<c>ChangeFnKey</c>/<c>SetFullMacroData</c>/...) — the
/// Everest 60's firmware remap protocol isn't reverse-engineered by any
/// known source (BaseCampLinux explicitly flags this as unimplemented in
/// its <c>docs/CONTROL_INTERFACE.md</c>). Needs a dedicated USB capture of
/// Windows Base Camp performing a key remap before it can be added.
/// </para>
/// </summary>
internal static class Everest60HidNative
{
    public const ushort VID = 0x3282;
    public const ushort PidAnsi = 0x0005;
    public const ushort PidIso = 0x0006;

    /// <summary>Feature report size: 1 report-ID byte (always 0x00) + 64 data bytes.</summary>
    public const int ReportSize = 65;

    /// <summary>Interface 2 = the vendor lighting/command channel (BaseCampLinux: <c>INTERFACE = 2</c>).</summary>
    private const string InterfaceMarker = "mi_02";

    public readonly record struct FoundDevice(string Path, ushort Pid);

    /// <summary>Enumerates the Everest 60's interface-2 HID collection, if connected.</summary>
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
                if (attrs.ProductID != PidAnsi && attrs.ProductID != PidIso)
                    continue;

                string lower = path.ToLowerInvariant();
                if (!lower.Contains(InterfaceMarker)) continue;

                log?.Invoke($"[Ev60Native] found {lower[..Math.Min(80, lower.Length)]}…");
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
    /// reports are stateless control transfers — BaseCampLinux itself opens
    /// and closes the handle per call, no persistent session needed.</summary>
    public static SafeFileHandle? Open(string path, Action<string>? log = null)
    {
        try { return OpenHandle(path, throwOnFail: true); }
        catch (Exception ex) { log?.Invoke($"[Ev60Native] open failed: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Sends a 65-byte feature report and reads it back, retrying up to
    /// <paramref name="retries"/> times until the echoed command byte
    /// (resp[1]) matches what was sent — mirrors BaseCampLinux's
    /// <c>_send()</c>, which found the device occasionally busy on the
    /// first attempt. <paramref name="delayMs"/> defaults to 50 (matching
    /// BaseCampLinux's conservative inter-command delay for lighting
    /// WRITES) but a real Base Camp USB capture of the paginated color
    /// readback (opcode 0x28, see <see cref="Everest60Protocol.ReadColorData"/>)
    /// showed Base Camp itself firing consecutive read pages with well under
    /// 1ms between them — pass a smaller value for read-only polling loops
    /// where 50ms×N would be too slow.
    /// </summary>
    public static byte[]? SendFeature(SafeFileHandle h, byte[] report65, int retries = 3, int delayMs = 50)
    {
        if (report65.Length != ReportSize)
            throw new ArgumentException($"report must be {ReportSize} bytes", nameof(report65));
        byte cmd = report65[1];
        byte[]? last = null;
        for (int attempt = 0; attempt < retries; attempt++)
        {
            if (!HidD_SetFeature(h, report65, report65.Length))
            {
                Thread.Sleep(delayMs);
                continue;
            }
            Thread.Sleep(delayMs);

            var resp = new byte[ReportSize];
            if (HidD_GetFeature(h, resp, resp.Length))
            {
                last = resp;
                if (resp.Length >= 2 && resp[1] == cmd) return resp;
            }
            Thread.Sleep(delayMs);
        }
        return last;
    }

    // ================================================================
    // P/Invoke (same shape as EverestHidNative/DpHidNative — hid.dll + setupapi.
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
