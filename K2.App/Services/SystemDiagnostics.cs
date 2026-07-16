using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Read-only startup diagnostics logged once per session: OS build (to tell Windows 10
/// from 11 reliably — <see cref="Environment.OSVersion"/> alone is not enough without the
/// app.manifest supportedOS entries already in place) and a HID-level snapshot of every
/// Mountain device (VID 0x3282) Windows currently sees, independent of which SDK/engine
/// K2 ends up using to talk to it.
///
/// The exclusive-open probe answers a question no vendor SDK return code can: is some
/// OTHER process already holding this device's HID handle right now? A query-only open
/// (used elsewhere in this codebase, e.g. DpHidNative.Enumerate) always succeeds even
/// when a rival process has the device open exclusively; only a GENERIC_READ|WRITE +
/// non-shared CreateFile attempt reveals a pre-existing exclusive claim (ERROR_SHARING_
/// VIOLATION / ERROR_ACCESS_DENIED). The probe handle is closed immediately either way,
/// so it never itself becomes the rival claimant for the app's real open that follows.
/// </summary>
internal static class SystemDiagnostics
{
    private const ushort MountainVID = 0x3282;

    private static string DeviceLabel(ushort pid) => pid switch
    {
        0x0001 => "Everest Max",
        0x0005 => "Everest 60 (ANSI)",
        0x0006 => "Everest 60 (ISO)",
        0x0009 => "DisplayPad",
        0x0002 => "Makalu Max",
        0x0003 => "Makalu 67",
        _ => $"unknown PID 0x{pid:X4}",
    };

    public static void LogStartupInfo(Action<string> log)
    {
        try { LogOsInfo(log); } catch (Exception ex) { log($"[SysInfo] OS info failed: {ex.Message}"); }
        try { LogMountainHidDevices(log); } catch (Exception ex) { log($"[SysInfo] HID scan failed: {ex.Message}"); }
    }

    private static void LogOsInfo(Action<string> log)
    {
        var os = Environment.OSVersion.Version;
        string productName = "?", displayVersion = "?";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            productName = key?.GetValue("ProductName") as string ?? "?";
            displayVersion = key?.GetValue("DisplayVersion") as string
                              ?? key?.GetValue("ReleaseId") as string ?? "?";
        }
        catch { /* best-effort */ }

        // Build >= 22000 is Windows 11 even though ProductName on some images still says "Windows 10".
        string family = os.Build >= 22000 ? "Windows 11" : "Windows 10";
        log($"[SysInfo] OS: {productName} ({family}) build {os.Build}.{os.Revision} " +
            $"version {displayVersion}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} OS");
    }

    private static void LogMountainHidDevices(Action<string> log)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devs = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devs == INVALID_HANDLE_VALUE) { log("[SysInfo] HID class enumeration unavailable"); return; }

        int found = 0;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
            {
                string? path = GetInterfacePath(devs, ref ifData);
                if (path is null) continue;

                using var qh = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                                            OPEN_EXISTING, 0, IntPtr.Zero);
                if (qh.IsInvalid) continue;

                var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(qh, ref attrs) || attrs.VendorID != MountainVID) continue;
                qh.Dispose();
                found++;

                // Exclusive probe: share flags = 0. If some other process already holds this
                // interface open (any share mode short of full read+write sharing), this fails.
                using var eh = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                                            OPEN_EXISTING, 0, IntPtr.Zero);
                bool exclusiveOk = !eh.IsInvalid;
                int err = exclusiveOk ? 0 : Marshal.GetLastWin32Error();
                eh.Dispose();

                string shortPath = path.Length > 90 ? path[..90] + "…" : path;
                log($"[SysInfo] HID {DeviceLabel(attrs.ProductID)} (PID 0x{attrs.ProductID:X4}) " +
                    $"exclusiveOpen={(exclusiveOk ? "OK" : $"FAIL win32={err}")} path={shortPath}");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }

        if (found == 0) log("[SysInfo] No Mountain (VID 0x3282) HID devices found by Windows");
    }

    private static string? GetInterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData)
    {
        var devInfo = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
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

    // ================================================================
    // P/Invoke (minimal subset, independent of DpHidNative's private ones)
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

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(Microsoft.Win32.SafeHandles.SafeFileHandle h, ref HIDD_ATTRIBUTES attrs);

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string filename, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
