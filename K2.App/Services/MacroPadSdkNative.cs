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
    /// <para>
    /// <b>2026-07-09 fix:</b> was previously declared with
    /// <c>[MarshalAs(UnmanagedType.ByValArray)] FWColor[] colorLv</c> /
    /// <c>byte[] byData</c> — a non-blittable struct that P/Invoke marshals by
    /// copying into a temporary native buffer on every call. Wave/Tornado
    /// (which use the sibling <see cref="BlockData"/>, declared with INLINE
    /// fields + a <c>fixed</c> buffer — a truly blittable, pass-by-value
    /// struct) worked; every other effect via <c>ChangeEffect</c> did not
    /// (confirmed by the user: not even "Off" did anything) — <c>ChangeEffect</c>
    /// returned <c>true</c> but the wire data was stale, exactly the "P/Invoke
    /// returns True, wire stale" failure mode already documented for the
    /// Everest's <c>BlockData</c> history (see <c>EverestSdkNative.cs</c>).
    /// Switched to the same inline-field/<c>unsafe fixed</c> layout as the
    /// Everest's (now working) <c>EffData</c> struct.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct EffData
    {
        public byte byEffectIndex;   // EffectIndex
        public byte byAll;            // 1 = applies to all keys
        public byte bySpeed;          // SpeedT
        public byte byLightness;      // BrightT
        public byte byRandColor;      // 1 = random colors
        public byte byDirection;      // DirectionT (forced to 255: see getChangeEffect)
        public byte byWidth;          // wave width (forced to 255: see getChangeEffect)

        /// <summary>Main effect colors (3 inline RGB triplets).</summary>
        public FWColor colorLv0;
        public FWColor colorLv1;
        public FWColor colorLv2;

        /// <summary>Effect background color.</summary>
        public FWColor bkColor;

        /// <summary>Firmware command parameter tail (43 bytes, zero for presets).
        /// Inline <c>fixed</c> buffer -&gt; blittable struct.</summary>
        public fixed byte byData[43];

        /// <summary>
        /// Creates an <see cref="EffData"/> exactly replicating Base Camp's
        /// <c>MacroPadSDK::getChangeEffect</c> (CIL dump, re-verified 2026-07-09
        /// against the extracted <c>BaseCamp.UI.dll</c>):
        /// <c>byWidth=255</c> and <c>byDirection=255</c> always, <c>byAll=1</c>,
        /// <c>byData</c> = 43 zeros. <c>bySpeed</c> is the RAW <c>Lighting.Speed</c>
        /// byte (0..100-ish scale, DB default 60) — NOT a 3-value enum — except
        /// for <see cref="EffectIndex.Static"/>/<see cref="EffectIndex.Off"/>,
        /// where BC forces <c>bySpeed=255</c> ("no speed" marker). Rainbow
        /// (<c>byRandColor</c>) is <c>2</c> in BC (not <c>1</c>) when the effect's
        /// color "Type" is random — <see cref="ChangeBlockEffect"/> uses the same
        /// convention. For <see cref="EffectIndex.Off"/>: colors and background
        /// at zero.
        /// </summary>
        public static EffData New(EffectIndex eff,
                                   FWColor c1, FWColor? c2 = null, FWColor? c3 = null,
                                   FWColor? background = null,
                                   byte speed = 60,
                                   BrightT bright = BrightT.B100,
                                   bool randomColor = false)
        {
            bool isOff   = eff == EffectIndex.Off;
            bool noSpeed = isOff || eff == EffectIndex.Static;
            FWColor zero = default;

            // From decompiled BC (getChangeEffect): Reactive/Yeti/Matrix put the
            // 2nd color in bkColor (byRandColor stays 0); Breath (and anything
            // else with a 2nd color) puts it in colorLv1 with byRandColor=16.
            // Mirrors EverestSdkNative.EffData.New's usesBkColor logic exactly
            // (same firmware family).
            bool usesBkColor = eff == EffectIndex.ReactiveA
                             || eff == EffectIndex.ReactiveB
                             || eff == EffectIndex.ReactiveC
                             || eff == EffectIndex.Yeti
                             || eff == EffectIndex.Matrix;

            bool hasTwoColors = !isOff && !randomColor && c2.HasValue;

            byte randColor;
            if (randomColor) randColor = 2;
            else if (hasTwoColors && !usesBkColor) randColor = 16; // 0x10: multi-color gradient
            else randColor = 0;

            FWColor lv0 = isOff ? zero : c1;
            FWColor lv1, bk;
            if (hasTwoColors && usesBkColor)
            {
                lv1 = zero;
                bk  = c2!.Value;
            }
            else if (hasTwoColors)
            {
                lv1 = c2!.Value;
                bk  = isOff ? zero : (background ?? zero);
            }
            else
            {
                lv1 = zero;
                bk  = isOff ? zero : (background ?? zero);
            }

            return new EffData
            {
                byEffectIndex = (byte)eff,
                // Confirmed 0 (not 1) against a real USB capture 2026-07-09
                // (_reference/usb_dumps/macropad.pcapng, "14 2C" packets):
                // byte offset 1 of EffData is 0x00 for every preset Base Camp
                // sent (Static/Breathing/Reactive/Matrix/Yeti/Off). The
                // previous "byAll=1" was never verified and is a likely reason
                // ChangeEffect returned true but never visibly applied.
                byAll         = 0,
                bySpeed       = noSpeed ? (byte)255 : speed,
                byLightness   = (byte)bright,
                byRandColor   = randColor,
                byDirection   = 0xFF,
                byWidth       = 0xFF,
                colorLv0      = lv0,
                colorLv1      = lv1,
                colorLv2      = isOff ? zero : (c3 ?? zero),
                bkColor       = bk,
            };
        }
    }

    // =======================================================================
    // Block effect (Wave / Tornado) — ChangeBlockEffect(BlockData, ID)
    // =======================================================================
    // Discovered 2026-07-09 by decompiling BaseCamp.UI.dll (MacroPadDLLHelper.
    // getChangeBlockEffect, MacroPadOperations.SetMacroPadLighting): exactly
    // like the Everest keyboard (EverestSdkNative.BlockData — same firmware
    // family, same magic numbers), the MacroPad's Wave (EffMenuIndex=Colorwave)
    // and Tornado (EffMenuIndex=Tornado) presets do NOT go through ChangeEffect
    // — Base Camp routes them through ChangeBlockEffect instead. Direction byte
    // codes verified identical to Everest's: Wave 4-way {0,2,4,6}, Tornado
    // CW/CCW {9,10}. This was never ported to the MacroPad module before —
    // ChangeEffect was called unconditionally for every effect including Wave
    // (the UI's default selection), which is the most likely reason the LED
    // effects never appeared to do anything on the MacroPad.

    /// <summary>Firmware "block" color triplet: position/level + RGB (4 bytes).
    /// Identical layout to the Everest's <c>FWBColor</c>.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FWBColor
    {
        public byte pos, r, g, b;
        public FWBColor(byte pos, byte r, byte g, byte b) { this.pos = pos; this.r = r; this.g = g; this.b = b; }
    }

    /// <summary>
    /// "ChangeBlockEffect" payload for block effects (Wave/Tornado).
    /// Pack=1, 62 bytes — identical layout to the Everest's <c>BlockData</c>
    /// (same Mountain firmware family).
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
        /// Builds a BlockData replicating Base Camp's wire format, ported
        /// byte-for-byte from <c>MacroPadDLLHelper::getChangeBlockEffect</c>
        /// (same encoding as the Everest's <c>BlockData.New</c>):
        /// <c>byWidth=2</c> always; 1 color -&gt; byRand=0/byBlockNum=1;
        /// 2 colors -&gt; byRand=16(0x10)/byBlockNum=1; rainbow -&gt;
        /// byRand=2/byBlockNum=0. <paramref name="direction"/> must already be
        /// one of the firmware's valid codes (Wave: 0/2/4/6, Tornado: 9/10).
        /// <para>
        /// Rainbow colorLv0/colorLv1 confirmed 2026-07-10 against a real
        /// capture of Base Camp's own live LED preview
        /// (<c>_reference/usb_dumps/macropad_led.pcapng</c>, "14 2C" packet,
        /// Wave+rainbow): both slots are sent as the sentinel
        /// <c>{pos=0xFF,0,0,0}</c>, not left zeroed.
        /// </para>
        /// </summary>
        public static BlockData New(MacroPadSdkNative.EffectIndex eff, byte direction, byte speed, byte lightness,
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

            if (rainbow)
            {
                d.byRandColor = 2; d.byWidth = 2; d.byBlockNum = 0;
                d.colorLv0 = new FWBColor(0xFF, 0, 0, 0);
                d.colorLv1 = new FWBColor(0xFF, 0, 0, 0);
            }
            else if (c2 is { } s)
            {
                d.byRandColor = 16; d.byWidth = 2; d.byBlockNum = 1;
                d.colorLv0 = new FWBColor(0, c1.r, c1.g, c1.b);
                d.colorLv1 = new FWBColor(0, s.r,  s.g,  s.b);
            }
            else
            {
                d.byRandColor = 0; d.byWidth = 2; d.byBlockNum = 1;
                d.colorLv0 = new FWBColor(0, c1.r, c1.g, c1.b);
            }
            return d;
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

    /// <summary>Applies a "block" effect (Wave/Tornado) to the given device slot.
    /// See <see cref="BlockData"/> — ChangeEffect rejects these two indices.</summary>
    [DllImport(Dll, CallingConvention = Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ChangeBlockEffect(BlockData data, uint ID);

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
