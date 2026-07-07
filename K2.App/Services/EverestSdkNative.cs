using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Raw P/Invoke layer over <c>SDKDLL.dll</c> (the native C++ library for the
/// <b>Everest Max</b> keyboard).
///
/// <para>
/// <b>Note on the DLL.</b> Base Camp has <i>two</i> Everest wrappers:
/// <list type="bullet">
///   <item><c>BaseCamp.Service.Helpers.Everest</c>   → binds to
///         <c>SDKDLL.dll</c>         → <b>Everest Max</b> keyboard (this module)</item>
///   <item><c>BaseCamp.Service.Helpers.Everest60</c> → binds to
///         <c>Everest360_USB.dll</c> → <b>Everest 60</b> keyboard (60%, Fn-heavy)</item>
/// </list>
/// Signatures extracted from ECMA-335 metadata of <c>BaseCamp.Service.exe</c>
/// (parser: <c>outputs/everest_meta.py</c>, dump:
/// <c>_reference/Everest_SDK_signatures.txt</c>); the P/Invoke map
/// (class → DLL) retrieved with <c>outputs/dotnet_pinvoke_dump.py</c>.
/// All exported functions use <c>__cdecl</c>; structs
/// <c>DevInfo</c>/<c>FWInfo</c> are <c>Sequential, Pack=1</c>.
/// </para>
///
/// <para>
/// Only the "skeleton" subset needed by the action module is declared here:
/// driver open/close, device/firmware info, AP mode, profile switching,
/// key callbacks. The ~60 functions for RGB effects / macros / images
/// (ChangeEffect, SetFullMacroData, SetDisplayKeyPic, ...) will be added
/// when needed.
/// </para>
///
/// <para>
/// Differences from MacroPad: the Everest is <b>single-device</b>
/// (no slot 1..N) and <c>OpenUSBDriver</c> <b>does not take an HWND</b> in
/// the Base Camp wrapper — plug events are detected by polling
/// <see cref="IsDevicePlug"/>, not via Windows messages.
/// </para>
///
/// <para>
/// <b>IMPORTANT — bitness.</b> <c>SDKDLL.dll</c> is native 32-bit: the process
/// must be x86 (<c>K2.App</c> is).
/// </para>
/// </summary>
internal static class EverestSdkNative
{
    private const string Dll = "SDKDLL.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    /// <summary>Profiles stored on the keyboard (same as other Mountain devices).</summary>
    public const int FW_NUM_PROFILE = 5;

    // ---- Struct hardware (Sequential, Pack=1) ------------------------------

    /// <summary>USB identifiers and versions of the device.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DevInfo
    {
        public ushort vid;
        public ushort pid;
        public ushort fwVer;
        public ushort bootloadVer;
    }

    /// <summary>Firmware state: version, profiles, current effect indices.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWInfo
    {
        public ushort fwVer;
        public ushort wUndef;
        public byte sizeProfile;
        public byte byEffectModeIndex;
        public byte currentlyProfileIndex;
        public byte byEffectMenuIndex;
    }

    /// <summary>Row of the effect table for a profile.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EffectTable
    {
        public byte curIndex;
        public byte byEffSize;
    }

    /// <summary>Effect table for all profiles (used by GetProfileEffectTable).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EffectMenu
    {
        public byte byProfileSize;
        public byte byEffectSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public EffectTable[] table;
    }

    /// <summary>Extended brightness for sub-device (bar/numpad/...).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FW_EXTEND_BRIGHTNESS
    {
        public byte byDevType;
        public byte byBrightness;
    }

    /// <summary>Extended firmware information (MMDock, Numpad, PixelShift).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FW_EXTEND_INFO
    {
        public byte byMMDockPlug;
        public byte byMMDockShowMenu;
        public byte byMMDockMenuIndex;
        public FWColor MMDockColor;
        public byte byMMDockScreenSetup;
        public ushort wMMDockScreenSaver;
        public ushort wMMDockTurnOff;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] byMMDockShowProfile;
        public byte byNumpadPlug;
        public byte byPixelShiftTime;
        public byte byMMDockDBClick;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public FW_EXTEND_BRIGHTNESS[] exBrightness;
    }

    // ---- Key callbacks -------------------------------------------------------

    /// <summary>
    /// Delegate invoked by the SDK on every key press/release.
    /// Mountain SDK family signature (identical to MacroPad): <c>__stdcall</c>
    /// convention, parameters (key matrix, pressed/released, id).
    /// Called on an SDK internal thread.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint ID);

    // ---- Exported functions (__cdecl) ----------------------------------------

    /// <summary>Native SDK DLL version.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern int GetDLLVersion();

    /// <summary>
    /// Opens the keyboard USB driver. Unlike MacroPad/DisplayPad,
    /// does NOT require an HWND.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool OpenUSBDriver();

    /// <summary>Closes the USB driver.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void CloseUSBDriver();

    /// <summary>True if the keyboard is connected.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsDevicePlug();

    /// <summary>Firmware application version.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern ushort GetDevAppVer();

    /// <summary>Reads the device VID/PID and versions.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetDeviceInfo(ref DevInfo devInfo);

    /// <summary>Reads the firmware state (current profile, effect indices).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetFWInfo(ref FWInfo fwInfo);

    /// <summary>Reads the effect table for all profiles.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetProfileEffectTable(ref EffectMenu effectMenu);

    /// <summary>
    /// Reads the keyboard layout from the firmware (HID <c>11 12</c>).
    /// Base Camp calls it twice during init; without this call
    /// <c>GetColorData</c> returns no data on a clean boot.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetFWLayout(ref int layout);

    /// <summary>Reads extended firmware information (MMDock, Numpad, etc.).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetExtendInfo(ref FW_EXTEND_INFO extendInfo);

    /// <summary>Enables/disables software control (AP mode).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool APEnable([MarshalAs(UnmanagedType.I1)] bool bEnable);

    /// <summary>Resets the device.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetDevice();

    /// <summary>
    /// Switches the active profile. The first parameter is the profile index;
    /// the meaning of the second is NOT confirmed by metadata — since the
    /// keyboard is single-device, 0 is passed (see <c>EverestService.SwitchProfile</c>).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SwitchProfile(int profile, int reserved);

    /// <summary>
    /// Registers the global callback for key events. The delegate must be
    /// kept alive by the caller (see <see cref="EverestService"/>),
    /// otherwise the GC collects it and the SDK calls a dangling pointer.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK callback);

    // =======================================================================
    // RGB lighting (system presets)
    // =======================================================================
    //
    // Structs and indices derived from BaseCamp.Service.exe metadata:
    //  - enum EFF_INDEX (BaseCamp.Service.Service / Common)
    //  - struct FWColor / EffData (Pack=1, array sizes from FieldMarshal)
    // The numbers come from `_reference/tools/dotnet_pinvoke_dump.py` /
    // `dotnet_enum_dump.py` / `dotnet_marshalas.py`: no guessed signatures.
    // All Mountain firmware effect structs are fixed at 62 bytes.
    // -----------------------------------------------------------------------

    /// <summary>Numeric index of the lighting preset (Base Camp's EFF_INDEX enum).
    /// Matches the values written to the firmware.</summary>
    public enum EffectIndex : byte
    {
        Static    = 0,
        Breath    = 1,
        ReactiveA = 3,
        Wave      = 4,
        ReactiveB = 5,
        Yeti      = 6,
        Tornado   = 7,
        Matrix    = 9,
        Custom    = 10,
        ReactiveC = 11,
        Off       = 12,
    }

    /// <summary>Speed (firmware SPEED_T enum).</summary>
    public enum SpeedT : byte { Slow = 0, Normal = 1, Fast = 2 }

    /// <summary>Rotation direction (firmware DIRECTION_T enum).</summary>
    public enum DirectionT : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>Brightness (firmware BRIGHT_T enum: 0/25/50/75/100).</summary>
    public enum BrightT : byte { B0 = 0, B25 = 25, B50 = 50, B75 = 75, B100 = 100 }

    /// <summary>Firmware RGB triplet (3 bytes, Pack=1).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWColor
    {
        public byte r, g, b;

        public FWColor(byte r, byte g, byte b) { this.r = r; this.g = g; this.b = b; }

        /// <summary>Builds from a 0xRRGGBB integer (e.g. <c>0x900000</c>).</summary>
        public static FWColor FromRgb(int rgb) =>
            new((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }

    /// <summary>
    /// "ChangeEffect" payload for the keyboard's main presets.
    /// Pack=1, total size 62 bytes.
    ///
    /// <para><b>BLITTABLE (2026-05-30).</b> The 3 colors and the 43-byte tail
    /// used to be <c>FWColor[]</c>/<c>byte[]</c> with <c>[MarshalAs(ByValArray)]</c>:
    /// this makes the struct NOT blittable, and by-value marshalling on x86
    /// (cdecl) did not copy the 62 bytes correctly onto the stack — result:
    /// <c>ChangeEffect</c> returned True but <b>emitted no packet</b>
    /// on the bus (USB sniff comparison: Base Camp sends <c>14 2C ...</c>, K2 nothing,
    /// while <c>APEnable</c>/<c>SaveFlash</c> with scalar parameters did go out).
    /// Now the colors are inline fields and the tail a <c>fixed</c> buffer: the struct
    /// is entirely blittable and the by-value pass is an exact copy.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct EffData
    {
        public byte byEffectIndex;   // EffectIndex
        public byte byAll;            // 1 = applies to all keys
        public byte bySpeed;          // SpeedT (enum value 0/1/2)
        public byte byLightness;      // BrightT
        public byte byRandColor;      // 1 = random colors
        public byte byDirection;      // DirectionT (forced to 0xFF: see getChangeEffect)
        public byte byWidth;          // wave width (forced to 0xFF: see getChangeEffect)

        /// <summary>Main effect colors (3 inline RGB triplets).</summary>
        public FWColor colorLv0;
        public FWColor colorLv1;
        public FWColor colorLv2;

        /// <summary>Effect background color.</summary>
        public FWColor bkColor;

        /// <summary>Firmware command tail (43 bytes, zero for standard presets).
        /// Inline <c>fixed</c> buffer -> blittable struct.</summary>
        public fixed byte byData[43];

        /// <summary>
        /// Creates an <see cref="EffData"/> for a preset. Layout from CIL dump of
        /// <c>MacroPadSDK::getChangeEffect</c> (2026-06-07):
        ///
        /// <para><b>1 color (Type==0):</b> colorLv[0]=c1, byRandColor=0.</para>
        /// <para><b>2 colors (Type==1):</b>
        ///   - bkColor path (Reactive/Yeti/Matrix): colorLv[0]=c1, bkColor=c2, byRandColor=0.
        ///   - colorLv path (Breath): colorLv[0]=c1, colorLv[1]=c2, byRandColor=16.
        /// </para>
        /// <para><b>Rainbow:</b> byRandColor=2.</para>
        ///
        /// <para><paramref name="forceRandColor16"/> forces byRandColor=16 + colorLv path
        /// even for effects that normally use bkColor (e.g. Matrix 2).</para>
        /// </summary>
        public static EffData New(EffectIndex eff,
                                   FWColor c1, FWColor? c2 = null, FWColor? c3 = null,
                                   FWColor? background = null,
                                   SpeedT speed = SpeedT.Normal,
                                   BrightT bright = BrightT.B100,
                                   bool randomColor = false,
                                   byte byAll = 1,
                                   byte byDirection = 0xFF,
                                   byte byWidth = 0xFF,
                                   int colorCount = 3,
                                   int speedOverride = -1,
                                   bool forceRandColor16 = false)
        {
            bool isOff  = eff == EffectIndex.Off;
            FWColor zero = default;
            byte spd = speedOverride >= 0 ? (byte)speedOverride : (byte)speed;

            // From decompiled BC + empirical tests:
            //   - Breath: 2nd color in colorLv[1], byRandColor=16
            //   - Reactive/Yeti/Matrix: 2nd color in bkColor, byRandColor=0
            //   - forceRandColor16: "Matrix 2" variant → colorLv[1] + byRandColor=16
            bool usesBkColor = eff == EffectIndex.ReactiveA
                            || eff == EffectIndex.ReactiveB
                            || eff == EffectIndex.ReactiveC
                            || eff == EffectIndex.Yeti
                            || eff == EffectIndex.Matrix;

            if (forceRandColor16) usesBkColor = false;

            bool hasTwoColors = !isOff && !randomColor && c2.HasValue;

            byte randColor;
            if (randomColor)
                randColor = 2;
            else if (hasTwoColors && !usesBkColor)
                randColor = 16;   // 0x10: multi-color gradient (from BC)
            else
                randColor = 0;

            FWColor lv0 = isOff ? zero : c1;
            FWColor lv1, bk;

            if (hasTwoColors && usesBkColor)
            {
                // Reactive/Yeti/Matrix: 2nd color in bkColor
                lv1 = zero;
                bk  = c2!.Value;
            }
            else if (hasTwoColors)
            {
                // Breath (or forced Matrix2): 2nd color in colorLv[1]
                lv1 = c2!.Value;
                bk  = isOff ? zero : (background ?? zero);
            }
            else
            {
                lv1 = (isOff || colorCount < 2) ? zero : (c2 ?? zero);
                bk  = isOff ? zero : (background ?? zero);
            }

            return new EffData
            {
                byEffectIndex = (byte)eff,
                byAll         = byAll,
                bySpeed       = spd,
                byLightness   = (byte)bright,
                byRandColor   = randColor,
                byDirection   = byDirection,
                byWidth       = byWidth,
                colorLv0      = lv0,
                colorLv1      = lv1,
                colorLv2      = (isOff || colorCount < 3) ? zero : (c3 ?? zero),
                bkColor       = bk,
            };
        }
    }

    /// <summary>Live color state of the whole keyboard (171 LEDs).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct KEYBOARD_COLOR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 171)]
        public FWColor[] color;
    }

    // ==== Structs for Numpad Display Keys + Media Dock ==========================

    /// <summary>
    /// Info for uploading an image to a sub-device (numpad display key or
    /// MMDock screensaver). The <c>pImageData</c> field is a pointer to RGB565 data
    /// allocated via <see cref="System.Runtime.InteropServices.Marshal.AllocHGlobal"/>.
    /// <para>Resolutions (from decompiled UploadImageInHw):</para>
    /// <list type="bullet">
    ///   <item><c>byTargetDev=0, byTargetPic=1</c>: MMDock screensaver → 240×204 px</item>
    ///   <item><c>byTargetDev=0, byTargetPic&gt;1</c>: numpad display key strip → 128×32 px</item>
    ///   <item><c>byTargetDev=1</c>: numpad display key square → 72×72 px</item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PicUpdateInfo
    {
        public IntPtr pImageData;
        public uint   dwDataLength;
        public byte   byTargetDev;
        public byte   byTargetPic;
        public byte   byTargetSubItem;
    }

    /// <summary>
    /// LED effect for the Media Dock bar. Same family as EffData but with
    /// a single <c>FWColor colorLv</c> (not an array) + 52 bytes <c>byData</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BarData
    {
        public byte   byEffectIndex;
        public byte   byAll;
        public byte   bySpeed;
        public byte   byLightness;
        public byte   byRandColor;
        public byte   byDirection;
        public byte   byWidth;
        public FWColor colorLv;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 52)]
        public byte[] byData;
    }

    // ── Custom per-key lighting (keyboard: 171 LEDs) ──────────────────

    /// <summary>Single LED in the custom effect: matrix index + RGB color.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CustomData
    {
        public byte   byMatrix;
        public FWColor color;
    }

    /// <summary>Custom per-key effect for the keyboard (171 LEDs).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CustomEffect
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 171)]
        public CustomData[] data;
    }

    /// <summary>Static custom colors for the Media Dock bar (126 LEDs).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CustomStatic
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public FWColor[] color;
    }

    /// <summary>Effect + custom read-back from the Media Dock bar.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BarReadData
    {
        public byte         byEffectValue;
        public CustomStatic custom;
    }

    /// <summary>Equalizer data for the Media Dock display (21 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EQ_DATA
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 21)]
        public byte[] byData;
    }

    /// <summary>Applies a "simple" preset (Static/Breath/Reactive/Yeti/Matrix/Off).
    /// NOTE: does NOT accept Wave(4)/Tornado(7) — those are "block effects" and go
    /// via <see cref="ChangeBlockEffect"/> (the native function rejects indices 4/5/7 here).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeEffect(EffData data);

    /// <summary>
    /// Calls <c>ChangeEffect</c> via a native function pointer, bypassing
    /// P/Invoke. Mirror of <see cref="ChangeBlockEffectRaw"/>: allocates 62B
    /// in native memory and calls the function pointer.
    /// Used to diagnose whether the issue is in P/Invoke or in the DLL.
    /// </summary>
    public static unsafe bool ChangeEffectRaw(EffData data)
    {
        nint hDll = GetLoadedSdkDll();
        if (hDll == 0) return false;

        nint proc = System.Runtime.InteropServices.NativeLibrary.GetExport(hDll, "ChangeEffect");
        if (proc == 0) return false;

        IntPtr buf = Marshal.AllocHGlobal(62);
        try
        {
            byte* src = (byte*)&data;
            byte* dst = (byte*)buf;
            for (int i = 0; i < 62; i++) dst[i] = src[i];

            var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte>)proc;
            byte result = fn(buf);
            return result != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // =======================================================================
    // Block effect (Wave / Tornado) — ChangeBlockEffect(BlockData)
    // =======================================================================
    // Discovered 2026-05-30 via USB sniff: Wave(EFF_INDEX 4) and Tornado(7) do NOT
    // go through ChangeEffect (which rejects them) but through ChangeBlockEffect. On
    // the bus the `14 2C` payload has an extra `byBlockNum` byte at pos.7 and the colors
    // are FWBColor (pos+rgb, 4 bytes) instead of FWColor (rgb, 3 bytes).
    // BlockData layout (Pack=1, 62B) from dotnet_struct_dump/marshalas:
    //   header 8B + colorLv FWBColor[2] + undef FWBColor[5] + bkColor FWColor + 23B.

    /// <summary>Firmware "block" color triplet: position/level + RGB (4 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWBColor
    {
        public byte pos, r, g, b;
        public FWBColor(byte pos, byte r, byte g, byte b) { this.pos = pos; this.r = r; this.g = g; this.b = b; }
    }

    /// <summary>
    /// "ChangeBlockEffect" payload for block effects (Wave/Tornado).
    /// Pack=1, 62 bytes, BLITTABLE (inline fields + fixed buffer).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct BlockData
    {
        public byte byEffectIndex;
        public byte byAll;
        public byte bySpeed;
        public byte byLightness;
        public byte byRandColor;
        public byte byDirection;
        public byte byWidth;
        public byte byBlockNum;

        public FWBColor colorLv0;   // colorLv[2]
        public FWBColor colorLv1;
        public FWBColor undefA0;    // undef[5]
        public FWBColor undefA1;
        public FWBColor undefA2;
        public FWBColor undefA3;
        public FWBColor undefA4;
        public FWColor  bkColor;    // 3 byte
        public fixed byte tail[23]; // undef2[23]

        /// <summary>
        /// Builds a BlockData replicating Base Camp's wire format (single color):
        /// <c>byBlockNum=1</c>; <c>byWidth</c>/<c>byRandColor</c> = 0 (mono) or 2
        /// (rainbow); <c>colorLv0 = {pos=lightness, primary RGB}</c>;
        /// <c>colorLv1 = {pos=lightness, secondary RGB}</c> if present, otherwise
        /// marker <c>{pos=0xFF,0,0,0}</c> (as seen in captures).
        /// </summary>
        public static BlockData New(EffectIndex eff, byte direction, byte speed, byte lightness,
                                     FWColor c1, FWColor? c2 = null, bool rainbow = false)
        {
            var d = new BlockData
            {
                byEffectIndex = (byte)eff,
                byAll         = 0,
                bySpeed       = speed,
                byLightness   = lightness,
                byDirection   = direction,
                bkColor       = default,
            };

            // Layout from decompiled BC (MacroPadSDK.getChangeBlockEffect):
            //   1 color   -> byRand=0, byWidth=0(?), byBlockNum=1, colorLv[0]={0,r,g,b}
            //   2 colors  -> byRand=16(0x10), byWidth=2, byBlockNum=1,
            //                colorLv[0]={0,r,g,b}, colorLv[1]={0,r,g,b}
            //   rainbow   -> byRand=2, byWidth=2(?), byBlockNum=0, no stop
            if (rainbow)
            {
                d.byRandColor = 2; d.byWidth = 2; d.byBlockNum = 0;
            }
            else if (c2 is { } s)
            {
                // 2 colors: from decompiled BC (getChangeBlockEffect):
                //   byRandColor=16 (0x10), byBlockNum=1, byWidth=2
                //   colorLv[0] = {pos=0, r,g,b}, colorLv[1] = {pos=0, r,g,b}
                //   HexToFWBColor does NOT set pos (stays 0 from initobj).
                d.byRandColor = 16; d.byWidth = 2; d.byBlockNum = 1;
                d.colorLv0 = new FWBColor(0, c1.r, c1.g, c1.b);
                d.colorLv1 = new FWBColor(0, s.r,  s.g,  s.b);
            }
            else
            {
                // 1 color: byWidth=2 (set globally in BC), pos=0
                d.byRandColor = 0; d.byWidth = 2; d.byBlockNum = 1;
                d.colorLv0 = new FWBColor(0, c1.r, c1.g, c1.b);
            }
            return d;
        }
    }

    /// <summary>Applies a "block" effect (Wave/Tornado) via by-value P/Invoke.
    /// Re-enabled after discovering the previous failure was caused by
    /// missing InitDllState, not by the struct passing (2026-06-05).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeBlockEffect(BlockData data);

    /// <summary>
    /// Calls <c>ChangeBlockEffect</c> via a native function pointer, completely
    /// bypassing the .NET 8 P/Invoke marshaler. The blittable
    /// <see cref="BlockData"/> is passed by-value directly to x86 cdecl
    /// machine code, with no managed intermediaries.
    ///
    /// <para><b>History of failed attempts on .NET 8 x86:</b>
    /// (1) blittable BlockData via DllImport → True, wire stale;
    /// (2) ref BlockData → False;
    /// (3) BlockDataRaw ByValArray byte[62] → True, wire stale;
    /// (4) BlockDataManaged (BC-style managed array) → True, wire stale;
    /// (5) EffData as transport (EntryPoint=ChangeBlockEffect) → True, wire stale.
    /// All P/Invoke paths produce the same result. Here we use
    /// <c>delegate* unmanaged[Cdecl]</c> as a last resort.</para>
    /// </summary>
    /// <summary>
    /// Calls <c>ChangeBlockEffect</c> by passing a pointer to native memory.
    /// Hypothesis: .NET Framework (used by BC) allocates native memory for
    /// non-blittable structs and passes a POINTER, not the 62 bytes on the stack.
    /// The DLL might internally expect a pointer.
    /// </summary>
    public static unsafe bool ChangeBlockEffectRaw(BlockData data)
    {
        nint hDll = GetLoadedSdkDll();
        if (hDll == 0) return false;

        nint proc = System.Runtime.InteropServices.NativeLibrary.GetExport(hDll, "ChangeBlockEffect");
        if (proc == 0) return false;

        // Allocate 62 bytes of NATIVE (non-GC) memory and copy the struct
        IntPtr buf = Marshal.AllocHGlobal(62);
        try
        {
            byte* src = (byte*)&data;
            byte* dst = (byte*)buf;
            for (int i = 0; i < 62; i++) dst[i] = src[i];

            // Call with a single pointer (4 bytes on the stack on x86)
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, byte>)proc;
            byte result = fn(buf);
            return result != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static nint GetLoadedSdkDll()
    {
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(Dll, out nint h))
            return h;
        return 0;
    }

    /// <summary>Resets the current effects to the firmware default.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetEffects();

    /// <summary>
    /// Activates "customize effect" mode for the given profile.
    /// In the Base Camp USB capture this corresponds to HID command
    /// <c>14 00 00 00 01 01</c> (report 0x14, sub 0x00, profile=1).
    /// Required to enable color streaming (GetColorData).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SwitchToCustomizeEffect(int profile);

    /// <summary>
    /// Applies a custom per-key effect to the given profile.
    /// Signature: <c>ChangeCustomizeEffect(int profile, int area, CustomEffect data, bool save)</c>.
    /// Area: 0=keyboard, 1=numpad (to be confirmed).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeCustomizeEffect(
        int profile, int area, CustomEffect data,
        [MarshalAs(UnmanagedType.I1)] bool save);

    /// <summary>
    /// Reads the current custom effect for the given profile and area.
    /// Signature: <c>GetEffCustomizeContent(int profile, int area, ref CustomEffect data)</c>.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetEffCustomizeContent(
        int profile, int area, ref CustomEffect data);

    /// <summary>
    /// Syncs the effect of the given profile with another: second
    /// parameter = source profile. Metadata signature:
    /// <c>SetSyncEffect(bool, int)</c>.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncEffect(
        [MarshalAs(UnmanagedType.I1)] bool enable, int profile);

    /// <summary>Enables effect synchronization across all profiles.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] bool enable);

    /// <summary>Reads whether cross-profile synchronization is enabled.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] ref bool enable);

    /// <summary>
    /// Sends HID command 0x11 0x83 with the specified value (0-100).
    /// Base Camp calls it with value=10 (0x0A) before starting GetColorData
    /// polling: this activates the color report stream from the firmware.
    /// Misleading name in the SDK — nothing to do with volume.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetVolumeInfo(int value);

    /// <summary>Reads the current LED colors for the entire keyboard (171 LEDs).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetColorData(ref KEYBOARD_COLOR data);

    /// <summary>
    /// Raw variant of GetColorData: passes an IntPtr to a pre-allocated buffer.
    /// Used for diagnostics to rule out struct marshalling issues.
    /// The buffer must be at least 513 bytes (171 × 3 FWColor).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "GetColorData")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetColorDataRaw(IntPtr data);

    /// <summary>Sets the global "main" brightness (backlight on/off).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetMainBrightness([MarshalAs(UnmanagedType.I1)] bool enable);

    /// <summary>
    /// Enables normal key functionality during AP mode (firmware <c>bool</c>
    /// parameter: true = keys keep typing even in SW mode).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EnableKeyFunc([MarshalAs(UnmanagedType.I1)] bool enable);

    /// <summary>
    /// Saves the current state (effects/colors) to the keyboard flash.
    /// Without a SaveFlash, effects applied via AP-mode are lost on the
    /// next unplug. Parameter = profile (1..5) or 6 = ALL_PROFILE.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SaveFlash(int profile);

    /// <summary>
    /// Sets the "Game Mode" key-lock bitmask. Bit layout confirmed by
    /// decompiling <c>EverestOperations.SaveSettings</c> in Base Camp's own
    /// BaseCamp.UI.dll: it builds a 4-char binary string in the order
    /// "AltTab Win AltF4 Shift" and parses it with <c>Convert.ToInt32(s, 2)</c> —
    /// i.e. bit0=DisableShift(+Tab), bit1=DisableAltF4, bit2=DisableWin, bit3=DisableAltTab.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetGameMode(int mode);

    /// <summary>Enables/disables the keyboard's Core indicator LEDs.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetIndicatorLed([MarshalAs(UnmanagedType.I1)] bool enable);

    /// <summary>
    /// Resets the keyboard to factory defaults (parameter observed as always
    /// <c>true</c> in Base Camp's <c>EverestOperations.ResetSettings</c>).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetFlash([MarshalAs(UnmanagedType.I1)] bool full);

    // ==== Numpad Display Keys =================================================

    /// <summary>
    /// Sets which image to show on each of the 4 numpad display keys.
    /// Each parameter is the pic index to display (loaded via StartPicUpdate).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetDisplayKeyPic(int iDisplay1, int iDisplay2, int iDisplay3, int iDisplay4);

    /// <summary>
    /// Reads which image is currently shown on each of the 4 display keys.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetDisplayKeyPic(ref int iDisplay1, ref int iDisplay2, ref int iDisplay3, ref int iDisplay4);

    /// <summary>Full reset of the numpad (display keys + state).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetNumpad();

    /// <summary>
    /// Resets images on the numpad. 5 byte parameters — exact meaning to be
    /// confirmed via USB capture (likely: 1 flag + 4 display key slots).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetNumpadPic(byte b0, byte b1, byte b2, byte b3, byte b4);

    /// <summary>
    /// Uploads an RGB565 image to a sub-device. The <c>pImageData</c> pointer
    /// in <see cref="PicUpdateInfo"/> must point to native memory allocated via
    /// <see cref="System.Runtime.InteropServices.Marshal.AllocHGlobal"/>.
    /// Call <c>Marshal.FreeHGlobal</c> afterwards.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool StartPicUpdate(PicUpdateInfo info);

    // ==== Media Dock (MMDock) =================================================

    /// <summary>Full reset of the Media Dock.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetMMDock();

    /// <summary>Resets the specified Media Dock image.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetMMDockPic(byte picIndex);

    /// <summary>
    /// Applies an LED effect to the Media Dock light bar.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeBarEffect(BarData data);

    /// <summary>
    /// Sets static custom colors (126 LEDs) on the Media Dock bar.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeBarCustomize(CustomStatic data);

    /// <summary>
    /// Reads the current bar effect for a given profile from the Media Dock.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetBarEffectData(int profile, ref BarReadData data);

    /// <summary>
    /// Sends equalizer data to the Media Dock display (21 bytes).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetEQInfo(EQ_DATA data);

    /// <summary>
    /// Updates the clock on the Media Dock display. Base Camp calls this
    /// every second via a timer. The booleans indicate whether the clock
    /// is enabled and whether it uses 24h format.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetClockInfo(int month, int day, int hour, int minute, int second,
        [MarshalAs(UnmanagedType.I1)] bool clockEnabled,
        [MarshalAs(UnmanagedType.I1)] bool format24h);

    /// <summary>
    /// Reads the clock settings from the firmware (enabled? 24h format?).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetClockInfo(
        [MarshalAs(UnmanagedType.I1)] ref bool clockEnabled,
        [MarshalAs(UnmanagedType.I1)] ref bool format24h);

    /// <summary>
    /// Sends a PC monitoring value to the Media Dock. The type (first param)
    /// determines the metric: 0=CPU, 1=GPU, 2=Disk, 3=Network, 4=RAM,
    /// 5=KeyPressCount. The value is the percentage or count.
    /// Activated by the firmware when <c>byMMDockMenuIndex</c> is 97-101 or 113.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetPCInfo(int infoType, int value);

    /// <summary>
    /// Writes the extended configuration to the firmware (MMDock settings,
    /// brightness, screensaver, shown profiles, etc.).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetExtendInfo(FW_EXTEND_INFO extendInfo);
}
