using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Detects physical mouse BUTTON transitions system-wide via the Windows Raw Input API
/// (RIDEV_INPUTSINK) — same technique as <see cref="RawKeyboardActivityWatcher"/>, but
/// decoding button flags (and filtering by device VID/PID) instead of just detecting "any
/// activity". Added 2026-07-27 to drive the Makalu Max/67 on-screen hotspot's physical-press
/// highlight (MainWindow.Makalu.cs): unlike the keyboard devices, K2 has no vendor SDK/HID
/// channel that reports a Makalu button press back to software — <see cref="MakaluHidNative"/>
/// only ever WRITES remap/RGB/DPI feature reports, it never reads a live button state. Raw
/// Input's mouse collection (a completely different HID interface — mi_00, the standard
/// "boot mouse" — from the vendor config interface K2 already talks to on mi_01) is the only
/// place a real click surfaces.
/// <para>
/// Only reports LEFT/RIGHT/MIDDLE/BACK(XBUTTON1)/FORWARD(XBUTTON2) — the five buttons Windows
/// itself understands, filtered to devices whose Raw Input device path carries the Makalu's
/// VID (<see cref="MakaluHidNative.VID"/>). A button the user has remapped away from its
/// default click function (Settings &gt; Remap) won't fire the corresponding message at all —
/// the firmware sends whatever it was remapped to instead (a keystroke, a DPI shift, ...) — so
/// this can only ever light up a button still acting as a normal mouse click. The Makalu Max's
/// extra buttons (5/6 in MkHotspotPosMax) and the DPI button have no standard OS identity and
/// can't be told apart from a Raw Input mouse report at all; they're invisible here regardless
/// of remap state — a limitation acknowledged when this feature was requested, not a bug.
/// </para>
/// </summary>
internal static class RawMouseActivityWatcher
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEMOUSE = 0;

    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN   = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP     = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN  = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP    = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP   = 0x0020;
    private const ushort RI_MOUSE_BUTTON_4_DOWN      = 0x0040; // XBUTTON1 / Back
    private const ushort RI_MOUSE_BUTTON_4_UP        = 0x0080;
    private const ushort RI_MOUSE_BUTTON_5_DOWN      = 0x0100; // XBUTTON2 / Forward
    private const ushort RI_MOUSE_BUTTON_5_UP        = 0x0200;

    /// <summary>Standard mouse buttons Raw Input can tell apart.</summary>
    public enum MouseButton { Left, Right, Middle, Back, Forward }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>Mirrors the native RAWMOUSE union: usFlags is followed by 2 bytes of
    /// alignment padding before the usButtonFlags/usButtonData pair (the union's other
    /// member, ULONG ulButtons, needs 4-byte alignment) — omitting the explicit pad field
    /// would shift every field after it by 2 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort reserved;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    /// <summary>Per-hDevice memoized "is this a Makalu" check — GetRawInputDeviceInfo does a
    /// real syscall, so this avoids repeating it for every button press from the same mouse
    /// (the handle is stable for as long as the device stays connected).</summary>
    private static readonly Dictionary<IntPtr, bool> s_isMakaluByDevice = new();

    /// <summary>Registers for system-wide mouse raw input on the given window. Call once,
    /// after the window's real HWND exists (OnSourceInitialized) — safe alongside
    /// RawKeyboardActivityWatcher.Register, each RAWINPUTDEVICE registration is independent.</summary>
    public static bool Register(IntPtr hWnd)
    {
        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop Controls
            usUsage     = 0x02, // Mouse
            dwFlags     = RIDEV_INPUTSINK,
            hwndTarget  = hWnd
        };
        bool ok = RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!ok)
            App.WriteLog("[RawMouseActivityWatcher] RegisterRawInputDevices failed: " +
                          Marshal.GetLastWin32Error());
        return ok;
    }

    /// <summary>
    /// Call from the window's WndProc for every message. Invokes <paramref name="onButtonChanged"/>
    /// once per button transition this WM_INPUT report carries (usually 0 or 1), but ONLY for a
    /// device whose Raw Input path carries the Makalu's VID — every other mouse/trackpad on the
    /// system is silently ignored. Pure movement reports (usButtonFlags == 0) skip the device-path
    /// lookup entirely, so normal cursor movement doesn't pay for it.
    /// </summary>
    public static void HandleMessage(int msg, IntPtr lParam, Action<MouseButton, bool> onButtonChanged)
    {
        if (msg != WM_INPUT) return;

        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEMOUSE) return;

            var mouse = Marshal.PtrToStructure<RAWMOUSE>(IntPtr.Add(buffer, (int)headerSize));
            ushort flags = mouse.usButtonFlags;
            if (flags == 0) return; // pure movement/wheel — no button transition, skip the device lookup

            if (!IsMakalu(header.hDevice)) return;

            if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN)   != 0) onButtonChanged(MouseButton.Left,    true);
            if ((flags & RI_MOUSE_LEFT_BUTTON_UP)     != 0) onButtonChanged(MouseButton.Left,    false);
            if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN)  != 0) onButtonChanged(MouseButton.Right,   true);
            if ((flags & RI_MOUSE_RIGHT_BUTTON_UP)    != 0) onButtonChanged(MouseButton.Right,   false);
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) onButtonChanged(MouseButton.Middle,  true);
            if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP)   != 0) onButtonChanged(MouseButton.Middle,  false);
            if ((flags & RI_MOUSE_BUTTON_4_DOWN)      != 0) onButtonChanged(MouseButton.Back,    true);
            if ((flags & RI_MOUSE_BUTTON_4_UP)        != 0) onButtonChanged(MouseButton.Back,    false);
            if ((flags & RI_MOUSE_BUTTON_5_DOWN)      != 0) onButtonChanged(MouseButton.Forward, true);
            if ((flags & RI_MOUSE_BUTTON_5_UP)        != 0) onButtonChanged(MouseButton.Forward, false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsMakalu(IntPtr hDevice)
    {
        if (s_isMakaluByDevice.TryGetValue(hDevice, out bool cached)) return cached;

        bool isMakalu = false;
        try
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)size * 2); // RIDI_DEVICENAME size is in WCHARs
                try
                {
                    uint got = size;
                    if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref got) != unchecked((uint)-1))
                    {
                        string path = (Marshal.PtrToStringUni(buf) ?? "").ToLowerInvariant();
                        string vid = $"vid_{MakaluHidNative.VID:x4}";
                        isMakalu = path.Contains(vid)
                            && (path.Contains($"pid_{MakaluHidNative.PidMakalu67:x4}")
                                || path.Contains($"pid_{MakaluHidNative.PidMakaluMax:x4}"));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { /* best-effort — an unrecognized/removed device just never matches */ }

        // Cap the cache so a churn of unrelated USB devices (each getting its own hDevice
        // over a long session) can't grow this unbounded.
        if (s_isMakaluByDevice.Count > 256) s_isMakaluByDevice.Clear();
        s_isMakaluByDevice[hDevice] = isMakalu;
        return isMakalu;
    }
}
