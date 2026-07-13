using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Raw P/Invoke layer over <c>Everest360_USB.dll</c> — the vendor SDK for the
/// <b>Everest 60</b> keyboard, wrapped in Base Camp by C# class
/// <c>BaseCamp.Service.Helpers.Everest60</c>.
///
/// <para>
/// K2's Everest 60 lighting (<see cref="Everest60Protocol"/>/<see cref="Everest60HidNative"/>)
/// deliberately bypasses this DLL and talks raw HID Feature Reports instead,
/// because the lighting exports (<c>ChangeEffect</c>, <c>ChangeCustomizeEffect</c>, ...)
/// pass opaque <c>IntPtr</c> structs whose layout was never reverse-engineered.
/// The <b>key-remap</b> exports below turned out to be a different story: plain
/// <c>int</c>/<c>bool</c> parameters, no struct marshaling — confirmed by
/// decompiling <c>BaseCamp.UI.dll</c>'s <c>Everest60Operations.SetEV60KeyBingingInHW</c>
/// (2026-07-11, see CHANGELOG for the full trace). Only the remap-relevant
/// subset is declared here (not the ~60 lighting/macro/image exports the
/// struct-opaque note above still applies to) — <b>plus one exception</b>:
/// <see cref="GetColorData2"/>, a live LED-color READBACK export that takes
/// a plain <c>(IntPtr, ushort)</c> buffer, not an opaque struct. It powers the
/// on-screen LED preview (<see cref="Everest60LedColorPoller"/>) the same way
/// this class's key-remap exports power Key Binding — see its own doc comment
/// for the decompile trace.
/// </para>
///
/// <para>
/// <b>Wire semantics</b> (from the same decompile trace):
/// <list type="bullet">
///   <item><c>ChangeKey(srcDLLKeyId, targetDLLKeyId)</c> — remaps a physical key
///         to another key's identity. <c>255</c> = reset/disable.</item>
///   <item><c>ChangeFnKey(srcDLLKeyId, targetDLLKeyId)</c> — same, but for the
///         key's Fn-layer (secondary) function. Same DLLKeyId space as ChangeKey.</item>
///   <item><c>ChangeShortcutKey(srcDLLKeyId, targetDLLKeyId, modifierMask)</c> —
///         multi-modifier shortcuts. Modifier bits: ctrl=1, shift=2, alt=4, win=8
///         (e.g. Win+L → ChangeShortcutKey(id, 39, 8)).</item>
///   <item><c>SetSingleMacroContent(srcDLLKeyId, type, code, 0)</c> — media/OS
///         keys; <c>type=3</c>, <c>code</c> 1-7 for volume/mute/play-pause/track/stop.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>IMPORTANT — bitness.</b> Like <c>SDKDLL.dll</c>/<c>MacroPadSDK.dll</c>,
/// <c>Everest360_USB.dll</c> is native 32-bit: the process must be x86
/// (<c>K2.App</c> is). Distribution follows the same non-redistributable
/// pattern (resolved next to a Base Camp install via
/// <see cref="NativeDependencyResolver"/> — not shipped by K2).
/// </para>
/// </summary>
internal static class Everest60SdkNative
{
    private const string Dll = "Everest360_USB.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    /// <summary>USB identifiers and versions of the device (same shape as
    /// EverestSdkNative.DevInfo — both wrap the same Mountain SDK family).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DevInfo
    {
        public ushort vid;
        public ushort pid;
        public ushort fwVer;
        public ushort bootloadVer;
    }

    /// <summary>Delegate invoked by the SDK on every key press/release —
    /// identical signature/convention to EverestSdkNative.KEY_CALLBACK
    /// (confirmed: EV60MessagePumpManager and MessagePumpManager/Everest Max
    /// share the same KEY_CALLBACK type in the SDK metadata).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint ID);

    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern int GetDLLVersion();

    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool OpenUSBDriver(IntPtr hWnd);

    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void CloseUSBDriver();

    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsDevicePlug();

    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern ushort GetDevAppVer();

    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetDeviceInfo(ref DevInfo devInfo);

    /// <summary>
    /// Reads back the keyboard's current LED colors (live hardware readback —
    /// confirmed to exist despite the lighting-writes-via-raw-HID split noted
    /// in this class's doc comment). Confirmed via decompile 2026-07-11 of
    /// <c>BaseCamp.Service.exe</c>'s <c>Everest60.GetColorData</c> wrapper:
    /// it allocates an unmanaged 576-byte buffer, calls
    /// <c>GetColorData2(ptr, 576)</c>, copies the bytes out on success, frees
    /// the buffer. 576 / 3 = 192 RGB triplets (<see cref="ColorBufferSize"/>),
    /// indexed by the SAME firmware LED hardware address used to WRITE colors
    /// (<c>Everest60Protocol.LedIndex</c> for the 64 main keys,
    /// <c>SideLedIndex</c> for the 44-LED side ring). Cross-checked against
    /// <c>BaseCamp.UI.dll</c>'s <c>EverestMiniController.GetColorData</c>
    /// websocket handler ("EverestMini" = Base Camp's internal name for the
    /// Everest 60), which allocates the identical <c>FWColor[192]</c> and
    /// polls it in a plain 300ms loop with NO priming/warm-up call — unlike
    /// Everest Max, which needs <c>SetSyncEffect</c>/<c>EnableColorStream</c>
    /// before <c>GetColorData</c> returns anything. This export needs none of
    /// that (also confirmed by its absence from this class's export list).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetColorData2(IntPtr data, ushort size);

    /// <summary>Buffer size (bytes) for <see cref="GetColorData2"/> — 192 RGB
    /// triplets, exactly as Base Camp itself allocates.</summary>
    public const int ColorBufferSize = 576;

    /// <summary>
    /// Reports a sub-device's firmware version and position. For the
    /// numpad accessory, <paramref name="subDeviceIndex"/>=1 is the only
    /// value Base Camp itself ever passes (confirmed via decompile of
    /// <c>Everest60::Everest60GetSubDeviceInfo</c>, 2026-07-11 — see
    /// CHANGELOG); <paramref name="position"/> comes back 0=not attached,
    /// 1=left, 2=right (confirmed via <c>Everest60Operations.GetEverest60NumPadStatus</c>
    /// in BaseCamp.UI.dll, which reads exactly those three values).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetSubDeviceInfo(int subDeviceIndex, ref int fwVer, ref int position);

    /// <summary>Enables/disables software control (AP mode) — called before
    /// key-function programming, mirroring EverestSdkNative.APEnable.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool APEnable([MarshalAs(UnmanagedType.I1)] bool bEnable);

    /// <summary>Enables "key function" reporting — must be true for
    /// ChangeKey/ChangeFnKey/ChangeShortcutKey programming to take effect
    /// (same role as EverestSdkNative.EnableKeyFunc for Everest Max).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EnableKeyFunc([MarshalAs(UnmanagedType.I1)] bool enable);

    /// <summary>Registers the global callback for key events. The delegate
    /// must be kept alive by the caller (GC-pinned field), otherwise the SDK
    /// calls a dangling pointer once collected.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK callback);

    /// <summary>Remaps a physical key (main layer) to another key's identity.
    /// <paramref name="targetDLLKeyId"/> = 255 resets/disables the key.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeKey(int srcDLLKeyId, int targetDLLKeyId);

    /// <summary>Remaps the Fn-layer (secondary) function of a physical key.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeFnKey(int srcDLLKeyId, int targetDLLKeyId);

    /// <summary>Binds a physical key to a modifier+key shortcut.
    /// <paramref name="modifierMask"/>: ctrl=1, shift=2, alt=4, win=8 (combinable).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeShortcutKey(int srcDLLKeyId, int targetDLLKeyId, int modifierMask);

    /// <summary>Binds a physical key to a media/OS action.
    /// <paramref name="type"/>=3 for media; <paramref name="code"/> 1-7 for
    /// volume up/down/mute/play-pause/prev/next/stop (order not fully
    /// confirmed — see Everest60RemapData). Last parameter always 0
    /// (confirmed from the SetSingleMacroContent call sites).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSingleMacroContent(int srcDLLKeyId, int type, int code, int reserved);

    /// <summary>Resets all key bindings to factory defaults.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetKeys();

    /// <summary>Persists the current key bindings to the keyboard's flash
    /// memory (survives unplug/restart, same role as SaveFlash elsewhere).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SaveFlash(int reserved);
}
