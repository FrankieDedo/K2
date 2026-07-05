using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// "Raw" P/Invoke layer over <c>MacroPadSDK.dll</c> (the native C++ library for the MacroPad).
///
/// Unlike the DisplayPad — which has a managed wrapper
/// (<c>DisplayPad.SDK.dll</c>) — the MacroPad only exposes the native DLL, so
/// here we declare the exported functions directly.
///
/// <para>
/// The signatures were NOT guessed: they were extracted from the ECMA-335
/// metadata of <c>BaseCamp.Service.exe</c> (internal class
/// <c>BaseCamp.Service.Helpers.MacroPadSDK</c>), which is the original Base
/// Camp binary that drives the MacroPad. Verification results:
/// </para>
/// <list type="bullet">
///   <item>all exported functions use the <c>__cdecl</c> convention;</item>
///   <item>the key callback (<see cref="KEY_CALLBACK"/>) uses <c>__stdcall</c>
///         — the <c>UnmanagedFunctionPointer</c> attribute in the original
///         binary is 3 = StdCall;</item>
///   <item><c>DevInfo</c> and <c>FWInfo</c> are <c>Sequential</c> with <c>Pack=1</c>.</item>
/// </list>
///
/// <para>
/// <b>IMPORTANT — bitness.</b> <c>MacroPadSDK.dll</c> is a 32-bit binary:
/// the process loading it MUST be x86 (<c>K2.App.csproj</c> sets
/// <c>PlatformTarget=x86</c>). In a 64-bit process the first P/Invoke
/// would fail with <c>BadImageFormatException</c>.
/// </para>
/// </summary>
internal static class MacroPadSdkNative
{
    private const string Dll = "MacroPadSDK.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    // ---- Firmware constants ---------------------------------------------
    // (const values from the MacroPadSDK class in BaseCamp.Service.exe)

    /// <summary>Maximum slots the SDK can address.</summary>
    public const int MAX_DEV_COUNT = 10;

    /// <summary>Profiles stored on each MacroPad.</summary>
    public const int FW_NUM_PROFILE = 5;

    /// <summary>Physical keys on the MacroPad.</summary>
    public const int FW_NUM_KEY = 12;

    /// <summary>Windows message posted to the HWND on every device plug/unplug.</summary>
    public const int WM_DEVICE_PLUG = 21505;

    /// <summary>Windows message for firmware update progress.</summary>
    public const int WM_FW_PROGRESS = 21506;

    /// <summary>Windows message for key status (keys also arrive via callback).</summary>
    public const int WM_KEY_STATUS = 25600;

    // ---- Hardware structs ----------------------------------------------------

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

    // ---- Key callbacks -------------------------------------------------------

    /// <summary>
    /// Delegate invoked by the SDK on every key press/release.
    /// <c>__stdcall</c> convention. Parameters: key matrix, pressed/released,
    /// device id. Called on an SDK internal thread: the
    /// consumer must marshal to the UI thread.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void KEY_CALLBACK(ushort wMatrix, bool bPressed, uint ID);

    // ---- Exported functions (__cdecl) --------------------------------------

    /// <summary>Native SDK DLL version.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern int GetDLLVersion();

    /// <summary>
    /// Opens the USB driver. <paramref name="handle"/> is the HWND of the window
    /// that will receive the <see cref="WM_DEVICE_PLUG"/> /
    /// <see cref="WM_FW_PROGRESS"/> messages.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool OpenUSBDriver(IntPtr handle);

    /// <summary>Closes the USB driver.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void CloseUSBDriver();

    /// <summary>Number of devices currently known to the SDK.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetDevCount(ref int iDevCount);

    /// <summary>
    /// True if a device is connected in slot <paramref name="ID"/>.
    /// Explicit <c>I1</c> marshalling: this function, in the Mountain SDK
    /// family, returns a 1-byte C++ <c>bool</c> (verified against
    /// the original DisplayPad worker code).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsDevicePlug(uint ID);

    /// <summary>Firmware application version of the device.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern ushort GetDevAppVer(uint ID);

    /// <summary>Reads VID/PID and versions of the device in the given slot.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetDeviceInfo(ref DevInfo devInfo, uint ID);

    /// <summary>Reads the firmware state (current profile, effect indices).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool GetFWInfo(ref FWInfo fwInfo, uint ID);

    /// <summary>
    /// Reads the firmware layout (HID <c>11 12</c>).
    /// Required to enable color streaming (GetColorData).
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetFWLayout(ref int layout, uint ID);

    /// <summary>True if the device is updating its firmware.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool IsUpdating(uint ID);

    /// <summary>
    /// Enables/disables software control (AP mode) of the device.
    /// With AP enabled, the host manages effects and keys in real time.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool APEnable(bool bEnable, uint ID);

    /// <summary>Resets the device.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern bool ResetDevice(uint ID);

    /// <summary>
    /// Registers the global callback for key events. A delegate kept
    /// alive by the caller must be passed (see <see cref="MacroPadService"/>),
    /// otherwise the GC frees it and the SDK calls an invalid pointer.
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    public static extern void SetKeyCallBack(KEY_CALLBACK callback);

    // =======================================================================
    // LED lighting (firmware presets)
    // =======================================================================
    //
    // The signatures are NOT guessed: extracted from ECMA-335 metadata of
    // BaseCamp.Service.exe (class BaseCamp.Service.Helpers.MacroPadSDK), P/Invoke
    // dump via _reference/tools/dotnet_pinvoke_dump.py + signature decoder
    // (2026-05-29). Key result:
    //
    //   DIFFERENCE from the Everest wrapper (SDKDLL.dll, single-device): every
    //   MacroPad function takes a LAST parameter `uint ID` = the device slot
    //   (1..MAX_DEV_COUNT), because the MacroPad is multi-device.
    //   E.g. SwitchProfile(int,int,uint) has 3 parameters (not 2).
    //
    // The EffData struct is the SAME Mountain firmware family as the Everest's:
    // verified byte-for-byte identical (Pack=1, 62B, colorLv ByValArray[3],
    // byData ByValArray[43]) via dotnet_struct_dump.py + dotnet_marshalas.py.
    // The EffData.New constants come from the CIL dump of
    // MacroPadSDK::getChangeEffect (byWidth=255, byDirection=255 always;
    // bySpeed=enum value; byAll=1; byData=43 zeros).
    // -----------------------------------------------------------------------

    /// <summary>Numeric index of the lighting preset (Base Camp's EFF_INDEX
    /// enum, shared across all Mountain devices).</summary>
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
    /// "ChangeEffect" payload for the main presets. Pack=1, 62 bytes total.
    /// Identical to the Everest's EffData struct (same firmware family).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EffData
    {
        public byte byEffectIndex;   // EffectIndex
        public byte byAll;            // 1 = applies to all keys
        public byte bySpeed;          // SpeedT
        public byte byLightness;      // BrightT
        public byte byRandColor;      // 1 = random colors
        public byte byDirection;      // DirectionT (forced to 255: see getChangeEffect)
        public byte byWidth;          // wave width (forced to 255: see getChangeEffect)

        /// <summary>Main effect colors (max 3).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public FWColor[] colorLv;

        /// <summary>Effect background color.</summary>
        public FWColor bkColor;

        /// <summary>Firmware command parameter tail (43 bytes, zeros for presets).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 43)]
        public byte[] byData;

        /// <summary>
        /// Creates an <see cref="EffData"/> exactly replicating Base Camp's
        /// <c>MacroPadSDK::getChangeEffect</c> (CIL dump):
        /// <c>byWidth=255</c> and <c>byDirection=255</c> always, <c>bySpeed</c>
        /// = enum value (0/1/2), <c>byAll=1</c>, <c>byData</c> = 43 zeros.
        /// For <see cref="EffectIndex.Off"/>: colors and background at zero.
        /// </summary>
        public static EffData New(EffectIndex eff,
                                   FWColor c1, FWColor? c2 = null, FWColor? c3 = null,
                                   FWColor? background = null,
                                   SpeedT speed = SpeedT.Normal,
                                   BrightT bright = BrightT.B100,
                                   bool randomColor = false)
        {
            bool isOff  = eff == EffectIndex.Off;
            FWColor zero = default;
            var colors = isOff
                ? new[] { zero, zero, zero }
                : new[] { c1, c2 ?? zero, c3 ?? zero };

            return new EffData
            {
                byEffectIndex = (byte)eff,
                byAll         = 1,
                bySpeed       = (byte)speed,
                byLightness   = (byte)bright,
                byRandColor   = randomColor ? (byte)1 : (byte)0,
                byDirection   = 0xFF,
                byWidth       = 0xFF,
                colorLv       = colors,
                bkColor       = isOff ? zero : (background ?? zero),
                byData        = new byte[43],
            };
        }
    }

    /// <summary>
    /// Switches the active firmware profile (1..5). The second int is reserved (pass 0).
    /// <para>Note: SwitchProfile has 3 parameters on MacroPad (not 2 like Everest), because
    /// MacroPad supports multiple devices — the last uint ID identifies the device slot.</para>
    /// </summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SwitchProfile(int profile, int reserved, uint ID);

    /// <summary>Applies a lighting preset to the given device slot.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeEffect(EffData data, uint ID);

    /// <summary>Resets the current effects to the firmware default.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ResetEffects(uint ID);

    /// <summary>Syncs the effect of the given profile (signature <c>(bool,int,uint)</c>).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncEffect(
        [MarshalAs(UnmanagedType.I1)] bool enable, int profile, uint ID);

    /// <summary>Enables effect synchronization across all profiles.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Reads whether cross-profile synchronization is enabled.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetSyncAcrossProfiles(
        [MarshalAs(UnmanagedType.I1)] ref bool enable, uint ID);

    /// <summary>Sets the global "main" brightness (backlight on/off).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SetMainBrightness(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Keeps key functionality during AP mode (true = keys keep typing).</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool EnableKeyFunc(
        [MarshalAs(UnmanagedType.I1)] bool enable, uint ID);

    /// <summary>Saves the current state to flash. Profile 1..5 or 6 = ALL_PROFILE.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SaveFlash(int profile, uint ID);

    // =====================================================================
    // Live LED color read (for real-time preview)
    // =====================================================================

    /// <summary>Number of LEDs in the MacroPad color buffer (indices 0..125).</summary>
    public const int COLOR_LED_COUNT = 126;

    /// <summary>Current MacroPad LED color buffer (126 RGB triplets).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MACROPAD_COLOR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = COLOR_LED_COUNT)]
        public FWColor[] color;
    }

    /// <summary>Reads the current MacroPad LED colors.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetColorData(ref MACROPAD_COLOR colorData, uint ID);
}
