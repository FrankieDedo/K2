using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Detects Everest 60 main-board key transitions via the Windows Raw Input API — same
/// technique as <see cref="RawMouseActivityWatcher"/> (decoding a specific device's reports,
/// filtered by VID/PID, instead of just "any activity" like <see cref="RawKeyboardActivityWatcher"/>).
///
/// <para>
/// <b>Why not the vendor SDK, and why not opening the HID collection directly either.</b>
/// Real-hardware log 2026-07-28: the vendor SDK's KEY_CALLBACK never fires even when
/// APEnable/EnableKeyFunc both report True. The first fix attempt read the board's raw HID
/// boot-keyboard reports directly (interface 0/mi_00, verified against a real USB capture —
/// see the CHANGELOG entry) — but opening that collection with <c>CreateFile</c> fails with
/// win32 error 5 (ACCESS_DENIED): Windows reserves direct read/write access to a keyboard's
/// own HID collection for its class driver (kbdhid.sys), the exact restriction Raw Input
/// exists to work around. <see cref="RawKeyboardActivityWatcher"/> already proves Raw Input
/// itself works fine for this keyboard (it drives the backlight auto-off wake) — this class
/// just also decodes WHICH key and filters to the Everest 60 specifically, instead of only
/// answering "was that any keyboard".
/// </para>
///
/// <para>
/// No new <c>RegisterRawInputDevices</c> call needed: <see cref="RawKeyboardActivityWatcher.Register"/>
/// already registered this process for system-wide keyboard Raw Input (usage page 0x01/usage
/// 0x06) — a second registration for the same usage page/usage would just replace it. This
/// class only needs to decode the SAME WM_INPUT messages already flowing to MainWindow's
/// WndProc, the same way <see cref="RawMouseActivityWatcher.HandleMessage"/> piggybacks off
/// the mouse registration.
/// </para>
///
/// <para>
/// <b>Scan code, not VKey.</b> Raw Input's own <c>VKey</c> field is derived from the scan code
/// via the CURRENTLY ACTIVE keyboard layout — for OEM/punctuation keys that translation
/// genuinely differs by locale (user report 2026-07-28: an Italian-layout key lit up the wrong
/// key on screen). The <c>MakeCode</c>/E0 pair is the actual physical scan code, standard PS/2
/// Set 1, unchanged since the original IBM PC/AT and never locale-dependent — the Raw Input
/// equivalent of the raw HID usage ids Everest Max's native engine already keys off for the
/// same reason (see <see cref="K2.App.Models.EverestWMatrixMap.HidUsageToMatrixId"/>'s doc
/// comment). As a side benefit, scan codes already distinguish LEFT/RIGHT Shift/Ctrl/Alt AND
/// the numpad accessory's Enter/nav-cluster keys from the main board's own (their physical scan
/// codes differ regardless of Num Lock state, unlike the OS-translated VKey) — no separate
/// normalization/disambiguation logic needed here at all, see
/// <see cref="K2.App.Models.Everest60KeyboardLayout.ScanCodeToLedIndex"/>.
/// </para>
/// </summary>
internal static class RawEv60KeyWatcher
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEKEYBOARD = 1;

    private const ushort RI_KEY_BREAK = 0x0001;
    private const ushort RI_KEY_E0 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>Mirrors the native RAWKEYBOARD struct — no manual padding needed, every
    /// field before the ULONG is already a multiple of 4 bytes in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    /// <summary>Per-hDevice memoized "is this the Everest 60" check — same caching shape as
    /// RawMouseActivityWatcher.IsMakalu, same reason (GetRawInputDeviceInfo is a real syscall,
    /// the handle is stable for as long as the device stays connected).</summary>
    private static readonly Dictionary<IntPtr, bool> s_isEv60ByDevice = new();

    /// <summary>
    /// Call from the window's WndProc for every message. Invokes <paramref name="onScanCodeChanged"/>
    /// with (scan code, pressed) for every key transition this WM_INPUT report carries, but ONLY
    /// for a device whose Raw Input path carries the Everest 60's VID/PID — every other keyboard
    /// on the system (including the one you're actually typing on right now) is silently ignored.
    /// The scan code is <c>MakeCode</c>, with <c>0x100</c> added when the E0 extended-key flag is
    /// set (same combined-code convention <see cref="K2.App.Models.Everest60KeyboardLayout.ScanCodeToLedIndex"/>
    /// expects) — look that table up directly, no further translation needed.
    /// </summary>
    public static void HandleMessage(int msg, IntPtr lParam, Action<int, bool> onScanCodeChanged)
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
            if (header.dwType != RIM_TYPEKEYBOARD) return;
            if (!IsEverest60(header.hDevice)) return;

            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));
            // A VKey of 0xFF is Windows' "no mapping / overrun" filler, not a real key —
            // official Raw Input guidance is to discard it, even though this class doesn't
            // otherwise use VKey at all.
            if (kb.VKey == 0xFF) return;

            bool e0 = (kb.Flags & RI_KEY_E0) != 0;
            bool pressed = (kb.Flags & RI_KEY_BREAK) == 0;
            int scanCode = kb.MakeCode + (e0 ? 0x100 : 0);
            onScanCodeChanged(scanCode, pressed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsEverest60(IntPtr hDevice)
    {
        if (s_isEv60ByDevice.TryGetValue(hDevice, out bool cached)) return cached;

        bool isEv60 = false;
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
                        string vid = $"vid_{Everest60HidNative.VID:x4}";
                        isEv60 = path.Contains(vid)
                            && (path.Contains($"pid_{Everest60HidNative.PidAnsi:x4}")
                                || path.Contains($"pid_{Everest60HidNative.PidIso:x4}"));
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { /* best-effort — an unrecognized/removed device just never matches */ }

        // Cap the cache so a churn of unrelated USB devices (each getting its own hDevice
        // over a long session) can't grow this unbounded — same guard as RawMouseActivityWatcher.
        if (s_isEv60ByDevice.Count > 256) s_isEv60ByDevice.Clear();
        s_isEv60ByDevice[hDevice] = isEv60;
        return isEv60;
    }
}
