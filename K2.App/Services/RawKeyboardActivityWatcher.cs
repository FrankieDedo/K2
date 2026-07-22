using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Detects physical keyboard activity system-wide via the Windows Raw Input
/// API (RIDEV_INPUTSINK), independent of any vendor keyboard SDK.
/// <para>
/// Added 2026-07-20 for Everest Max's backlight auto-off: on real hardware,
/// SDKDLL.dll's KeyEvent callback (the normal source of physical key events,
/// see MainWindow.Everest.cs's HandleEverestKey) stops firing permanently
/// after the very first native SDK call issued by the idle timer — tried
/// locking, avoiding SetMainBrightness, and raising dispatcher priority,
/// none of which helped, meaning the vendor SDK's internal callback thread
/// dies regardless of which call or priority triggers it. Raw Input reads
/// standard HID keyboard reports straight from Windows, not the vendor's
/// proprietary channel, so "wake on keypress" keeps working even if
/// SDKDLL.dll's own event pipe is dead. Only used to drive
/// BacklightIdleTimer.RegisterActivity() (wake detection) — actual key
/// remapping/actions still go through the normal SDK KeyEvent path.
/// </para>
/// </summary>
internal static class RawKeyboardActivityWatcher
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEKEYBOARD = 1;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    /// <summary>Registers for system-wide keyboard raw input on the given window.
    /// Call once, after the window's real HWND exists (OnSourceInitialized).</summary>
    public static bool Register(IntPtr hWnd)
    {
        var rid = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop Controls
            usUsage     = 0x06, // Keyboard
            dwFlags     = RIDEV_INPUTSINK,
            hwndTarget  = hWnd
        };
        bool ok = RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!ok)
            App.WriteLog("[RawKeyboardActivityWatcher] RegisterRawInputDevices failed: " +
                          Marshal.GetLastWin32Error());
        return ok;
    }

    /// <summary>Call from the window's WndProc for every message. Returns true if this
    /// message is a raw keyboard input event (i.e. real physical key activity).</summary>
    public static bool IsKeyboardInput(int msg, IntPtr lParam)
    {
        if (msg != WM_INPUT) return false;

        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return false;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
                return false;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            return header.dwType == RIM_TYPEKEYBOARD;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
