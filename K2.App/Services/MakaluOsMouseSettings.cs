using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Applies Makalu "Sensitivity"/"Click Speed" to Windows itself, NOT to the mouse
/// firmware. Confirmed via decompile (2026-07-29): neither setting appears anywhere
/// in <c>makalu_67_dll.dll</c>'s wrapper (<c>BaseCamp.Service.Helpers.Makalu67</c> —
/// its <c>SENSOR_T</c> struct only has polling_rate/button_response_time/
/// angle_snapping/lod/dpi) nor in K2's own raw-HID <see cref="MakaluProtocol"/>. The
/// strings <c>SystemParametersInfo</c>/<c>SPI_SETMOUSESPEED</c>/
/// <c>SPI_SETDOUBLECLICKTIME</c> sit right next to "Sensitivity"/"ClickSpeed" inside
/// Base Camp's own <c>BaseCamp.UI.exe</c> — Base Camp is simply changing the Windows
/// Control Panel mouse settings when a profile with these fields loads, same as any
/// other mouse utility.
///
/// UNVERIFIED: Base Camp's own DB/XML stores both as a 0-11 integer (confirmed via
/// <c>BaseCamp.Data.MakaluSetting</c>'s constructor defaults, Sensitivity=10/
/// ClickSpeed=0, and 3 real profile rows seen so far), but the exact formula Base
/// Camp uses to turn that 0-11 value into the real Win32 setting lives in a
/// single-file-bundled host (<c>BaseCamp.UI.exe</c>) that couldn't be decompiled —
/// the linear mapping below is K2's own reasonable approximation, not a confirmed
/// match to Base Camp's own curve. Low risk either way: this never touches the
/// physical device, worst case is a pointer-speed/double-click feel that doesn't
/// match Base Camp's exactly.
/// </summary>
internal static class MakaluOsMouseSettings
{
    private const uint SPI_SETMOUSESPEED = 0x0071;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetDoubleClickTime(uint uMilliseconds);

    public const int ScaleMin = 0;
    public const int ScaleMax = 11;

    /// <summary>0-11 (Base Camp's own scale) -> Windows' native 1-20 pointer-speed range.</summary>
    public static bool ApplySensitivity(int sensitivity0To11)
    {
        int clamped = Math.Clamp(sensitivity0To11, ScaleMin, ScaleMax);
        int windowsSpeed = 1 + (int)Math.Round(clamped * 19.0 / ScaleMax); // 0->1, 11->20
        IntPtr p = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(p, windowsSpeed);
            return SystemParametersInfo(SPI_SETMOUSESPEED, 0, p, SPIF_SENDCHANGE);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>0-11 (Base Camp's own scale, 0=slowest) -> Windows' double-click
    /// threshold in ms. Windows' own Control Panel slider covers roughly 900ms
    /// (slow) down to 200ms (fast) — used as the interpolation endpoints.</summary>
    public static bool ApplyClickSpeed(int clickSpeed0To11)
    {
        int clamped = Math.Clamp(clickSpeed0To11, ScaleMin, ScaleMax);
        uint ms = (uint)Math.Round(900 - clamped * (900.0 - 200.0) / ScaleMax);
        return SetDoubleClickTime(ms);
    }
}
