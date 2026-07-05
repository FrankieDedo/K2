using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// Application facade over <see cref="EverestSdkNative"/>.
///
/// Exposes the Everest Max keyboard's native SDK as a clean .NET API:
/// driver open/close, device/firmware info, AP mode, profile switching, and
/// a typed event for keys.
///
/// Mirrors the role of <c>MacroPadService</c>, but simpler: the Everest is
/// single-device (no slot enumeration) and doesn't use Windows messages for
/// plug detection — state is queried via <see cref="IsPlugged"/>.
///
/// <para>Keys arrive via callback on an internal SDK thread: the
/// consumer of the <see cref="KeyEvent"/> event is responsible for marshalling
/// to the UI thread.</para>
/// </summary>
public sealed class EverestService : IDisposable
{
    // The delegate must be kept alive in a field: if the GC collected it, the SDK
    // would call a dangling function pointer -> native crash.
    private EverestSdkNative.KEY_CALLBACK? _keyCallback;
    private bool _opened;

    // ---- Native engine (opt-in, AppSettings.EverestNativeEngine) ----------
    // Phase 1: bypasses SDKDLL.dll ONLY for driver open/close + init +
    // the 4 numpad display keys (D1-D4). RGB/numpad icons/Media Dock and the
    // full 171-key matrix (used by K2's remap engine) stay on SDKDLL.dll
    // until later phases land (wire layout not yet confirmed for these —
    // see EverestHidNative.cs). With the flag on, those calls simply
    // fail (SDKDLL isn't open) instead of crashing: they're already all in
    // try/catch with logging.
    private EverestHidNative.Pad? _nativePad;
    private bool UseNativeEngine => K2.Core.AppSettings.EverestNativeEngine;

    /// <summary>Numpad display key (D1-D4) pressed/released — NATIVE ENGINE ONLY.
    /// Only populated when <see cref="UseNativeEngine"/> is true (see Open()).</summary>
    public event EventHandler<(int Button, bool Pressed)>? NumpadButtonEvent;

    // Current profile cached from init: avoids calling GetFWInfo
    // repeatedly (each call is a HID packet that may collide with
    // the DLL's internal polling → native crash 0xC0000005 at +0x5133).
    private int _cachedProfile = 1;

    // Global lock to serialize all calls to SDKDLL.dll.
    // The DLL is not thread-safe: the key callback arrives on an SDK
    // thread, UI calls come from the WPF dispatcher → concurrent access
    // → access violation (native crash 0xC0000005 at SDKDLL.dll+0x5133).
    private readonly object _sdkLock = new();

    // SaveFlash DEBOUNCED: if the user changes effect/speed rapidly,
    // cancels the previous SaveFlash and schedules a new one. Avoids
    // flooding the DLL's internal HID queue with back-to-back commands → crash.
    private CancellationTokenSource? _saveFlashCts;

    /// <summary>Keyboard key pressed or released.</summary>
    public event EventHandler<EverestKeyEventArgs>? KeyEvent;

    /// <summary>Profiles stored on the keyboard.</summary>
    public const int ProfileCount = EverestSdkNative.FW_NUM_PROFILE;

    /// <summary>True if the USB driver was opened successfully and the DLL has not crashed.</summary>
    public bool IsOpen => _opened && !App.SdkCrashRecoveryNeeded;

    /// <summary>
    /// Opens the keyboard's USB driver and registers the key callback.
    /// </summary>
    public bool Open()
    {
        if (_opened) return true;

        if (UseNativeEngine)
            return OpenNative();

        _keyCallback = OnKeyCallback;
        try
        {
            EverestSdkNative.SetKeyCallBack(_keyCallback);
            App.WriteLog("[Everest.Open] SetKeyCallBack registered");
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] SetKeyCallBack threw: " + ex);
        }

        try
        {
            _opened = EverestSdkNative.OpenUSBDriver();
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] OpenUSBDriver threw: " + ex);
            return false;
        }
        App.WriteLog($"[Everest.Open] OpenUSBDriver -> {_opened}");

        // Post-open initialization: Base Camp calls GetFWInfo,
        // GetProfileEffectTable, GetExtendInfo, EnableKeyFunc right after
        // OpenUSBDriver. These reads have internal side effects in the DLL
        // that put the state into "ready for effects". Without them,
        // ChangeEffect/ChangeBlockEffect return True but do NOT emit
        // 14 2C packets on the USB bus (confirmed via USB sniff 2026-06-05:
        // DLL polling shows 0x1C without init vs 0x2B with BC's init).
        if (_opened) InitDllState();

        return _opened;
    }

    /// <summary>
    /// Opens via the native engine (Phase 1, see comment on the <see cref="_nativePad"/> field).
    /// SDKDLL.dll is NEVER loaded on this path: it eliminates at the root the
    /// crash from its timer thread for everything the native engine covers.
    /// </summary>
    private bool OpenNative()
    {
        try
        {
            string? path = EverestHidNative.FindCommandInterfacePath(App.WriteLog);
            if (path is null)
            {
                App.WriteLog("[Everest.Open] (native) MI_03 not found — keyboard not connected?");
                return false;
            }
            var pad = new EverestHidNative.Pad(path, App.WriteLog);
            pad.Open();
            pad.NumpadButtonChanged += (btn, pressed) =>
                NumpadButtonEvent?.Invoke(this, (btn, pressed));
            _nativePad = pad;
            _opened = true;
            App.WriteLog("[Everest.Open] (native) OK — SDKDLL.dll not loaded");
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.Open] (native) threw: " + ex);
            _nativePad?.Dispose();
            _nativePad = null;
            return false;
        }
    }

    /// <summary>
    /// Replicates the initialization calls that Base Camp makes after
    /// OpenUSBDriver. Even though we don't use the returned data, the DLL's
    /// internal side effects prepare the state for ChangeEffect/ChangeBlockEffect.
    /// </summary>
    private void InitDllState()
    {
        try
        {
            var fwInfo = new EverestSdkNative.FWInfo();
            bool fi = EverestSdkNative.GetFWInfo(ref fwInfo);
            if (fi && fwInfo.currentlyProfileIndex >= 1)
                _cachedProfile = fwInfo.currentlyProfileIndex;
            App.WriteLog($"[Everest.Init] GetFWInfo -> {fi}  " +
                $"fwVer=0x{fwInfo.fwVer:X4} profile={fwInfo.currentlyProfileIndex} " +
                $"effectMode={fwInfo.byEffectModeIndex} effectMenu={fwInfo.byEffectMenuIndex}" +
                $" -> cachedProfile={_cachedProfile}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetFWInfo threw: " + ex); }

        try
        {
            var effectMenu = new EverestSdkNative.EffectMenu();
            bool em = EverestSdkNative.GetProfileEffectTable(ref effectMenu);
            App.WriteLog($"[Everest.Init] GetProfileEffectTable -> {em}  " +
                $"profileSize={effectMenu.byProfileSize} effectSize={effectMenu.byEffectSize}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetProfileEffectTable threw: " + ex); }

        try
        {
            var extInfo = new EverestSdkNative.FW_EXTEND_INFO();
            bool ei = EverestSdkNative.GetExtendInfo(ref extInfo);
            App.WriteLog($"[Everest.Init] GetExtendInfo -> {ei}  " +
                $"MMDockPlug={extInfo.byMMDockPlug} NumpadPlug={extInfo.byNumpadPlug}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetExtendInfo threw: " + ex); }

        // GetFWLayout (HID 11 12): BC calls it 2 times during init.
        // From reverse engineering SDKDLL.dll, this is the only function that produces
        // the 0x12 sub-command. Without this, GetColorData doesn't work
        // on a clean boot (without BC having already called it).
        try
        {
            int layout = 0;
            bool fl = EverestSdkNative.GetFWLayout(ref layout);
            App.WriteLog($"[Everest.Init] GetFWLayout -> {fl}  layout={layout}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] GetFWLayout threw: " + ex); }

        try
        {
            bool ek = EverestSdkNative.EnableKeyFunc(true);
            App.WriteLog($"[Everest.Init] EnableKeyFunc(true) -> {ek}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] EnableKeyFunc threw: " + ex); }

        // Forces the firmware out of AP mode (it may have been left in AP
        // from a previous K2/BC session). Without this, ChangeEffect may
        // cause a transient rainbow flash before the effect.
        try
        {
            bool ap = EverestSdkNative.APEnable(false);
            _apEnabled = false;
            App.WriteLog($"[Everest.Init] APEnable(false) -> {ap}");
        }
        catch (Exception ex) { App.WriteLog("[Everest.Init] APEnable(false) threw: " + ex); }
    }

    /// <summary>Closes the USB driver.</summary>
    public void Close()
    {
        if (!_opened) return;
        if (_nativePad is not null)
        {
            try { _nativePad.Dispose(); }
            catch (Exception ex) { App.WriteLog("[Everest.Close] (native) threw: " + ex); }
            _nativePad = null;
            _opened = false;
            App.WriteLog("[Everest.Close] (native) driver closed");
            return;
        }
        try { EverestSdkNative.CloseUSBDriver(); }
        catch (Exception ex) { App.WriteLog("[Everest.Close] threw: " + ex); }
        _opened = false;
        // AP mode is lost when the driver closes: the next Open
        // will need to re-enable it.
        _apEnabled = false;
        App.WriteLog("[Everest.Close] driver closed");
    }

    /// <summary>Version of the SDK's native DLL.</summary>
    public int SdkVersion()
    {
        if (_nativePad is not null) return -1; // native engine: SDKDLL.dll not loaded
        lock (_sdkLock)
        try { return EverestSdkNative.GetDLLVersion(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SdkVersion] threw: " + ex);
            return 0;
        }
    }

    /// <summary>True if the keyboard is connected.</summary>
    public bool IsPlugged()
    {
        if (_nativePad is not null) return _opened;
        lock (_sdkLock)
        try { return EverestSdkNative.IsDevicePlug(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.IsPlugged] threw: " + ex);
            return false;
        }
    }

    /// <summary>Application firmware version.</summary>
    public ushort FirmwareVersion()
    {
        lock (_sdkLock)
        try { return EverestSdkNative.GetDevAppVer(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.FirmwareVersion] threw: " + ex);
            return 0;
        }
    }

    /// <summary>Reads VID/PID and device versions.
    /// <c>internal</c>: exposes a P/Invoke layer type (also internal).</summary>
    internal bool TryGetDeviceInfo(out EverestSdkNative.DevInfo info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetDeviceInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetDeviceInfo] threw: " + ex);
            return false;
        }
    }

    /// <summary>Reads firmware state (current profile/effect).
    /// <c>internal</c>: exposes a P/Invoke layer type (also internal).</summary>
    internal bool TryGetFirmwareInfo(out EverestSdkNative.FWInfo info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetFWInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetFirmwareInfo] threw: " + ex);
            return false;
        }
    }

    /// <summary>Currently active profile on the firmware (1..ProfileCount), 0 if unknown.</summary>
    public int CurrentProfile()
    {
        return TryGetFirmwareInfo(out var fw) ? fw.currentlyProfileIndex : 0;
    }

    /// <summary>
    /// Enables/disables software control (AP mode). Updates the
    /// internal flag: a subsequent <see cref="EnsureApMode"/> knows it needs to
    /// reissue the command if the user disabled AP manually.
    /// </summary>
    public bool APEnable(bool enable)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.APEnable(enable);
            App.WriteLog($"[Everest.APEnable] enable={enable} -> {ok}");
            if (ok) _apEnabled = enable;
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.APEnable] threw: " + ex);
            return false;
        }
    }

    /// <summary>Resets the device.</summary>
    public bool ResetDevice()
    {
        lock (_sdkLock)
        try { return EverestSdkNative.ResetDevice(); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetDevice] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Switches the keyboard's active profile. The second native parameter of
    /// <c>SwitchProfile</c> is not confirmed by metadata: since the keyboard is
    /// single-device we pass 0. To be verified on hardware.
    /// </summary>
    public bool SwitchProfile(int profile)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SwitchProfile(profile, 0);
            App.WriteLog($"[Everest.SwitchProfile] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SwitchProfile] threw: " + ex);
            return false;
        }
    }

    // ---- AP / SW mode ------------------------------------------------------

    /// <summary>
    /// True after the first successful <see cref="EnsureApMode"/>: we remember
    /// not to reissue the command every time (harmless but noisy in the logs).
    /// </summary>
    private bool _apEnabled;

    /// <summary>
    /// Puts the keyboard in AP/SW mode (software control). Required
    /// because <c>ChangeEffect</c> and other lighting commands
    /// applied "soft" by the PC are accepted by the firmware. <c>EnableKeyFunc(true)</c>
    /// is called right after to avoid losing key function during AP.
    /// </summary>
    public bool EnsureApMode()
    {
        if (_apEnabled) return true;
        lock (_sdkLock)
        try
        {
            bool ap = EverestSdkNative.APEnable(true);
            // EnableKeyFunc(true) replicates Base Camp's behavior:
            // without this, in AP mode the keyboard may stop transmitting keys.
            bool keyFn = false;
            try { keyFn = EverestSdkNative.EnableKeyFunc(true); }
            catch (Exception ex2) { App.WriteLog("[Everest.EnsureApMode] EnableKeyFunc threw: " + ex2); }

            App.WriteLog($"[Everest.EnsureApMode] APEnable={ap}  EnableKeyFunc={keyFn}");
            _apEnabled = ap;
            return ap;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.EnsureApMode] threw: " + ex);
            return false;
        }
    }

    // ---- RGB lighting (firmware presets) -------------------------------

    /// <summary>Lighting preset: alias of the native enums.</summary>
    public enum Effect : byte
    {
        Static    = (byte)EverestSdkNative.EffectIndex.Static,
        Breath    = (byte)EverestSdkNative.EffectIndex.Breath,
        Wave      = (byte)EverestSdkNative.EffectIndex.Wave,
        ReactiveA = (byte)EverestSdkNative.EffectIndex.ReactiveA,
        ReactiveB = (byte)EverestSdkNative.EffectIndex.ReactiveB,
        ReactiveC = (byte)EverestSdkNative.EffectIndex.ReactiveC,
        Yeti      = (byte)EverestSdkNative.EffectIndex.Yeti,
        Tornado   = (byte)EverestSdkNative.EffectIndex.Tornado,
        Matrix    = (byte)EverestSdkNative.EffectIndex.Matrix,
        Off       = (byte)EverestSdkNative.EffectIndex.Off,
        /// <summary>Matrix variant: same firmware index (9) but with
        /// byRandColor=16 → random vertical lines of color 2.</summary>
        Matrix2   = 200,
    }

    /// <summary>Effect speed.</summary>
    public enum Speed : byte { Slow = 0, Normal = 1, Fast = 2 }

    /// <summary>Rotation/scroll direction.</summary>
    public enum Direction : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>
    /// Applies a lighting preset to the keyboard.
    /// <para>NOTE — the <c>direction</c> and <c>width</c> parameters were
    /// removed: the CIL dump of Base Camp's <c>MacroPadSDK::getChangeEffect</c>
    /// shows that <c>byDirection</c> and <c>byWidth</c> are always forced to
    /// 255 and the CW/CCW direction is encoded in <c>EffMenuIndex</c> (see
    /// <see cref="EverestSdkNative.EffData.New"/>).</para>
    /// </summary>
    /// <param name="effect">Firmware preset (Wave/Breath/Static/...).</param>
    /// <param name="primary">Primary color (R,G,B).</param>
    /// <param name="secondary">Secondary color (optional, used by multicolor presets).</param>
    /// <param name="tertiary">Third color (optional).</param>
    /// <param name="background">Background color (optional, default black).</param>
    /// <param name="speed">Animation speed.</param>
    /// <param name="brightness">Brightness 0..100 (mapped to firmware steps 0/25/50/75/100).</param>
    /// <param name="randomColor">true to ignore the colors and use random colors instead.</param>
    public bool SetEffect(Effect effect,
                          (byte r, byte g, byte b) primary,
                          (byte r, byte g, byte b)? secondary = null,
                          (byte r, byte g, byte b)? tertiary = null,
                          (byte r, byte g, byte b)? background = null,
                          Speed speed = Speed.Normal,
                          int brightness = 100,
                          bool randomColor = false,
                          int speedByte = -1,
                          int directionByte = -1,
                          int colorCountOverride = -1)
    {
      lock (_sdkLock)
      {
        // 2026-05-29 — HYPOTHESIS TEST: AP mode was WRONG. AP mode (= Software
        // mode) is only for ChangeSWEffect / per-key streaming, where the host PC
        // sends all 171 colors to the firmware every frame. For firmware presets
        // (ChangeEffect) the device MUST be in NORMAL mode: the
        // firmware receives an EffData, stores it in the current slot and
        // renders it itself from its runtime. If we enter AP mode before the
        // ChangeEffect, the firmware "listens" to the command but doesn't apply it
        // because it's waiting for us to drive the individual LEDs.
        //
        // So: NO AP mode around ChangeEffect. If the device was
        // already in AP from a previous session, we force it OFF first.
        if (_apEnabled)
        {
            try
            {
                bool offOk = EverestSdkNative.APEnable(false);
                App.WriteLog($"[Everest.SetEffect] forcing APEnable(false) before ChangeEffect -> {offOk}");
                _apEnabled = false;
            }
            catch (Exception ex2) { App.WriteLog("[Everest.SetEffect] APEnable(false) prep threw: " + ex2); }
        }

        EverestSdkNative.FWColor C((byte, byte, byte) c) => new(c.Item1, c.Item2, c.Item3);
        var bright = QuantizeBrightness(brightness);

        // Per-effect parameters from the external config (everest_rgb.json), re-read
        // on EVERY apply: byAll/bySpeed/byDirection/byWidth/color count can be
        // adjusted and the effect re-applied WITHOUT recompiling.
        var def = EverestRgbConfig.Load().For(effect.ToString());
        App.WriteLog($"[Everest.SetEffect] cfg {effect}: byAll={def.ByAll} bySpeed={def.BySpeed} " +
                     $"byDir={def.ByDirection} byWidth={def.ByWidth} rand={def.ByRandColor} colors={def.ColorCount}");

        // The UI takes precedence (override >= 0); otherwise the config is used.
        int effSpeed = speedByte      >= 0 ? speedByte      : def.BySpeed;
        int effDir   = directionByte  >= 0 ? directionByte  : def.ByDirection;
        int effCount = colorCountOverride >= 0 ? colorCountOverride : def.ColorCount;

        // Wave(4) and Tornado(7) are "block effects": ChangeEffect REJECTS them
        // (discovered via USB sniff 2026-05-30). They go through ChangeBlockEffect,
        // with the BlockData struct (byBlockNum + FWBColor colors pos+rgb).
        if (effect == Effect.Wave || effect == Effect.Tornado)
        {
            bool rainbowB = randomColor || def.ByRandColor != 0;
            // bySpeed: scale 0..100 (0=slow, 100=fast) for both block and non-block.
            // The UI sends 0/25/50/75/100 directly (5 positions).
            // If the JSON has bySpeed >= 0 it's used as an override.
            byte spdB = (byte)(effSpeed >= 0 ? Math.Clamp(effSpeed, 0, 100) : 50);
            byte dirB     = (byte)(effDir >= 0 ? effDir : 0);
            EverestSdkNative.FWColor? c2b = null;
            if (secondary is { } s2) c2b = C(s2);

            var block = EverestSdkNative.BlockData.New(
                eff:       (EverestSdkNative.EffectIndex)effect,
                direction: dirB,
                speed:     spdB,
                lightness: (byte)bright,
                c1:        C(primary),
                c2:        c2b,
                rainbow:   rainbowB);
            try
            {
                // Diagnostic hex dump of the struct BEFORE sending
                App.WriteLog("[Everest.SetEffect] DUMP BlockData(62B): " + DumpBlockData(block));
                bool okB = EverestSdkNative.ChangeBlockEffect(block);
                App.WriteLog($"[Everest.SetEffect] BLOCK eff={effect} dir={dirB} speed={spdB} " +
                             $"rainbow={rainbowB} -> {okB}  (P/Invoke by-value)");
                if (!okB)
                {
                    App.WriteLog("[Everest.SetEffect] P/Invoke returned False, trying Raw...");
                    okB = EverestSdkNative.ChangeBlockEffectRaw(block);
                    App.WriteLog($"[Everest.SetEffect] ChangeBlockEffectRaw fallback -> {okB}");
                }
                // Small delay to give the DLL's internal HID queue time
                // to process the command before SaveFlash arrives.
                Thread.Sleep(50);
                DebouncedSaveFlash();
                return okB;
            }
            catch (Exception exB)
            {
                App.WriteLog("[Everest.SetEffect] ChangeBlockEffect threw: " + exB);
                return false;
            }
        }

        // Matrix2 (enum 200) → same firmware index as Matrix (9)
        // but with forceRandColor16 for the visual variant.
        bool isMatrix2 = effect == Effect.Matrix2;
        var fwIndex = isMatrix2
            ? EverestSdkNative.EffectIndex.Matrix
            : (EverestSdkNative.EffectIndex)effect;

        var data = EverestSdkNative.EffData.New(
            eff:              fwIndex,
            c1:               C(primary),
            c2:               secondary is { } s ? C(s) : null,
            c3:               tertiary  is { } t ? C(t) : null,
            background:       background is { } bg ? C(bg) : null,
            speed:            (EverestSdkNative.SpeedT)speed,
            bright:           bright,
            randomColor:      randomColor || def.ByRandColor != 0,
            byAll:            (byte)def.ByAll,
            byDirection:      (byte)effDir,
            byWidth:          (byte)def.ByWidth,
            colorCount:       effCount,
            speedOverride:    effSpeed,
            forceRandColor16: isMatrix2);
        try
        {
            bool ok = EverestSdkNative.ChangeEffect(data);
            App.WriteLog($"[Everest.SetEffect] eff={effect} speed={speed} bright={bright} -> {ok}");
            App.WriteLog("[Everest.SetEffect] DUMP EffData(62B): " + DumpEffData(data));

            Thread.Sleep(50);
            DebouncedSaveFlash();

            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetEffect] threw: " + ex);
            return false;
        }
      } // lock (_sdkLock)
    }

    /// <summary>
    /// Schedules a debounced SaveFlash: cancels any previous timer
    /// and creates a new one at 300ms. If the user changes effect
    /// or speed rapidly, only one SaveFlash is sent at the end
    /// of the burst — avoids overloading the DLL's HID queue.
    /// </summary>
    private void DebouncedSaveFlash()
    {
        _saveFlashCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveFlashCts = cts;
        var profile = _cachedProfile;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
            }
            catch (TaskCanceledException) { return; }

            lock (_sdkLock)
            {
                try
                {
                    bool ok = EverestSdkNative.SaveFlash(profile);
                    App.WriteLog($"[Everest] SaveFlash({profile}) debounced -> {ok}");

                    // (2026-06-09: removed color stream re-activation post-SaveFlash
                    //  because it caused flickering. To investigate whether SaveFlash
                    //  actually interrupts the color stream.)
                }
                catch (Exception ex) { App.WriteLog("[Everest] SaveFlash threw: " + ex); }
            }
        });
    }


    /// <summary>Hex-dump of BlockData's 62 bytes (diagnostics).</summary>
    private static unsafe string DumpBlockData(EverestSdkNative.BlockData d)
    {
        int sz = sizeof(EverestSdkNative.BlockData);
        byte* src = (byte*)&d;
        var sb = new System.Text.StringBuilder(sz * 3 + 10);
        sb.Append($"{sz}B = ");
        for (int i = 0; i < sz; i++)
        {
            if (i > 0) sb.Append('-');
            sb.Append(src[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>Hex-dump of the struct's 62 bytes (diagnostics).</summary>
    private static string DumpEffData(EverestSdkNative.EffData d)
    {
        int sz = Marshal.SizeOf<EverestSdkNative.EffData>();
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(d, p, fDeleteOld: false);
            byte[] buf = new byte[sz];
            Marshal.Copy(p, buf, 0, sz);
            return $"{sz}B = " + BitConverter.ToString(buf);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Resets the effects to the firmware default.</summary>
    public bool ResetEffects()
    {
        try
        {
            bool ok = EverestSdkNative.ResetEffects();
            App.WriteLog($"[Everest.ResetEffects] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetEffects] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Enables/disables effect synchronization across all profiles.
    /// When active, applying an effect to one profile replicates it
    /// to the other four.
    /// </summary>
    public bool SetSyncAcrossProfiles(bool enable)
    {
        try
        {
            bool ok = EverestSdkNative.SetSyncAcrossProfiles(enable);
            App.WriteLog($"[Everest.SetSyncAcrossProfiles] enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetSyncAcrossProfiles] threw: " + ex);
            return false;
        }
    }

    /// <summary>Reads the current cross-profile sync state.</summary>
    public bool GetSyncAcrossProfiles()
    {
        try
        {
            bool enabled = false;
            return EverestSdkNative.GetSyncAcrossProfiles(ref enabled) && enabled;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetSyncAcrossProfiles] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sets the sync effect (HID 12 [sync] 00 00 [brightness]).
    /// Required to enable the color stream on a clean boot.
    /// </summary>
    public bool SetSyncEffect(bool sync, int brightness)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetSyncEffect(sync, brightness);
            App.WriteLog($"[Everest.SetSyncEffect] sync={sync} bright={brightness} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetSyncEffect] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Saves the current state (effects/colors) to the keyboard's flash.
    /// Profile 1..5 or 6 = ALL_PROFILE. Without a SaveFlash, effects
    /// applied via AP-mode are lost on the next unplug.
    /// </summary>
    public bool SaveFlash(int profile = 6)
    {
        try
        {
            bool ok = EverestSdkNative.SaveFlash(profile);
            App.WriteLog($"[Everest.SaveFlash] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SaveFlash] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Reads the current LED colors from the keyboard, with a non-blocking lock.
    /// If the SDK lock is busy (another operation in progress), returns false
    /// without blocking — the poller can skip a tick with no visible impact.
    /// </summary>
    internal bool TryGetColorData(ref EverestSdkNative.KEYBOARD_COLOR buf)
    {
        if (!System.Threading.Monitor.TryEnter(_sdkLock))
            return false;
        try
        {
            return EverestSdkNative.GetColorData(ref buf);
        }
        catch { return false; }
        finally { System.Threading.Monitor.Exit(_sdkLock); }
    }

    /// <summary>
    /// Raw (IntPtr) variant of GetColorData, with a non-blocking lock.
    /// </summary>
    public bool TryGetColorDataRaw(IntPtr rawBuf)
    {
        if (!System.Threading.Monitor.TryEnter(_sdkLock))
            return false;
        try
        {
            return EverestSdkNative.GetColorDataRaw(rawBuf);
        }
        catch { return false; }
        finally { System.Threading.Monitor.Exit(_sdkLock); }
    }

    /// <summary>
    /// Enables streaming of color reports from the firmware (HID 0x11 0x83).
    /// Call with value=10 before GetColorData, as Base Camp does.
    /// </summary>
    public bool EnableColorStream(int value = 10)
    {
        try
        {
            bool ok = EverestSdkNative.SetVolumeInfo(value);
            App.WriteLog($"[Everest.EnableColorStream] value={value} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.EnableColorStream] threw: " + ex);
            return false;
        }
    }

    /// <summary>Turns the backlight on/off ("main" brightness).</summary>
    public bool SetBacklight(bool on)
    {
        try
        {
            bool ok = EverestSdkNative.SetMainBrightness(on);
            App.WriteLog($"[Everest.SetBacklight] on={on} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBacklight] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Quantizes a percentage 0..100 to the 5 firmware brightness steps
    /// (0/25/50/75/100) — the firmware only accepts these values.
    /// </summary>
    private static EverestSdkNative.BrightT QuantizeBrightness(int pct)
    {
        if (pct <= 12)  return EverestSdkNative.BrightT.B0;
        if (pct <= 37)  return EverestSdkNative.BrightT.B25;
        if (pct <= 62)  return EverestSdkNative.BrightT.B50;
        if (pct <= 87)  return EverestSdkNative.BrightT.B75;
        return EverestSdkNative.BrightT.B100;
    }

    // ==== Numpad Display Keys =================================================

    /// <summary>
    /// Reads extended info from the firmware: Media Dock and Numpad plug
    /// state, current menu, sub-device brightness, etc.
    /// </summary>
    internal bool TryGetExtendInfo(out EverestSdkNative.FW_EXTEND_INFO info)
    {
        info = default;
        lock (_sdkLock)
        try { return EverestSdkNative.GetExtendInfo(ref info); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetExtendInfo] threw: " + ex);
            return false;
        }
    }

    /// <summary>True if the numpad (with display keys) is connected.</summary>
    public bool IsNumpadPlugged()
    {
        return TryGetExtendInfo(out var info) && info.byNumpadPlug != 0;
    }

    /// <summary>True if the Media Dock is connected.</summary>
    public bool IsMMDockPlugged()
    {
        return TryGetExtendInfo(out var info) && info.byMMDockPlug != 0;
    }

    /// <summary>
    /// Raw value of byNumpadPlug (0=not connected, 1=left, 2=right — hypothesis to verify).
    /// </summary>
    public byte NumpadPlugPosition()
    {
        return TryGetExtendInfo(out var info) ? info.byNumpadPlug : (byte)0;
    }

    /// <summary>
    /// Raw value of byMMDockPlug (0=not connected, 1=left, 2=right — hypothesis to verify).
    /// </summary>
    public byte MMDockPlugPosition()
    {
        return TryGetExtendInfo(out var info) ? info.byMMDockPlug : (byte)0;
    }

    /// <summary>
    /// Reads which image is assigned to each of the 4 numpad display keys.
    /// </summary>
    public bool GetDisplayKeyPic(out int d1, out int d2, out int d3, out int d4)
    {
        d1 = d2 = d3 = d4 = 0;
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.GetDisplayKeyPic(ref d1, ref d2, ref d3, ref d4);
            App.WriteLog($"[Everest.GetDisplayKeyPic] -> {ok}  d1={d1} d2={d2} d3={d3} d4={d4}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetDisplayKeyPic] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sets which image to show on each of the 4 numpad display keys.
    /// </summary>
    public bool SetDisplayKeyPic(int d1, int d2, int d3, int d4)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetDisplayKeyPic(d1, d2, d3, d4);
            App.WriteLog($"[Everest.SetDisplayKeyPic] d1={d1} d2={d2} d3={d3} d4={d4} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetDisplayKeyPic] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Uploads an image to a numpad display key (square format 72×72).
    /// </summary>
    /// <param name="imagePathOrBase64">Path or base64 string.</param>
    /// <param name="keyIndex">Display key index (0-3).</param>
    /// <param name="picSlot">Firmware image slot (used as byTargetPic).</param>
    public bool UploadNumpadImage(string imagePathOrBase64, int keyIndex, byte picSlot = 0)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.NumpadSquare,
                picSlot,
                (byte)keyIndex);
            App.WriteLog($"[Everest.UploadNumpadImage] key={keyIndex} slot={picSlot} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadNumpadImage] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Uploads an image to a numpad display key (strip format 128×32).
    /// Alternative attempt — needs USB capture verification of which format
    /// is the right one for your hardware.
    /// </summary>
    public bool UploadNumpadImageStrip(string imagePathOrBase64, int keyIndex, byte picSlot = 0)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.NumpadStrip,
                picSlot,
                (byte)keyIndex);
            App.WriteLog($"[Everest.UploadNumpadImageStrip] key={keyIndex} slot={picSlot} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadNumpadImageStrip] threw: " + ex);
            return false;
        }
    }

    /// <summary>Full reset of the numpad (display keys + state).</summary>
    public bool ResetNumpad()
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ResetNumpad();
            App.WriteLog($"[Everest.ResetNumpad] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetNumpad] threw: " + ex);
            return false;
        }
    }

    // ==== Media Dock (MMDock) =================================================

    /// <summary>
    /// Applies an LED effect to the Media Dock's light bar.
    /// </summary>
    internal bool SetBarEffect(EverestSdkNative.BarData data)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeBarEffect(data);
            App.WriteLog($"[Everest.SetBarEffect] eff={data.byEffectIndex} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBarEffect] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sets static custom colors on the Media Dock's bar (126 LEDs).
    /// </summary>
    internal bool SetBarCustomize(EverestSdkNative.CustomStatic data)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeBarCustomize(data);
            App.WriteLog($"[Everest.SetBarCustomize] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetBarCustomize] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Updates the clock on the Media Dock's display with the current time.
    /// Call periodically (every second, as Base Camp does).
    /// </summary>
    public bool UpdateClock()
    {
        lock (_sdkLock)
        try
        {
            // First read whether the clock is enabled and the format
            bool clockEnabled = false, format24h = false;
            bool gotClock = EverestSdkNative.GetClockInfo(ref clockEnabled, ref format24h);
            if (!gotClock || !clockEnabled) return false;

            var now = DateTime.Now;
            bool ok = EverestSdkNative.SetClockInfo(
                now.Month, now.Day, now.Hour, now.Minute, now.Second,
                clockEnabled, format24h);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UpdateClock] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sends a PC monitoring data point to the Media Dock.
    /// </summary>
    /// <param name="infoType">0=CPU, 1=GPU, 2=Disk, 3=Network, 4=RAM, 5=KeyPressCount.</param>
    /// <param name="value">Value (percentage or count).</param>
    public bool SetPCInfo(int infoType, int value)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetPCInfo(infoType, value);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[Everest.SetPCInfo] type={infoType} threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sends the volume level to the Media Dock (0-100).
    /// NOTE: SetVolumeInfo is also used for EnableColorStream (value=10/0x0A
    /// activates the color stream). For the dock's actual volume, call it
    /// when <c>byMMDockMenuIndex == 65 ('A')</c>.
    /// </summary>
    public bool SetVolume(int volumePercent)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetVolumeInfo(volumePercent);
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetVolume] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Uploads a screensaver image to the Media Dock's display (240×204 px).
    /// </summary>
    public bool UploadMMDockScreensaver(string imagePathOrBase64)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestImageUploader.UploadImage(
                imagePathOrBase64,
                EverestImageUploader.PicTarget.MMDockScreensaver,
                picSlot: 1);
            App.WriteLog($"[Everest.UploadMMDockScreensaver] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.UploadMMDockScreensaver] threw: " + ex);
            return false;
        }
    }

    /// <summary>Full reset of the Media Dock.</summary>
    public bool ResetMMDock()
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ResetMMDock();
            App.WriteLog($"[Everest.ResetMMDock] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetMMDock] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Writes the extended configuration to the firmware (MMDock settings, brightness, etc.).
    /// </summary>
    internal bool SetExtendInfo(EverestSdkNative.FW_EXTEND_INFO info)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SetExtendInfo(info);
            App.WriteLog($"[Everest.SetExtendInfo] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetExtendInfo] threw: " + ex);
            return false;
        }
    }

    // ==== Custom per-key lighting =============================================

    /// <summary>
    /// Switches the firmware to "custom per-key" mode for the given profile.
    /// </summary>
    public bool SwitchToCustomize(int profile)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.SwitchToCustomizeEffect(profile);
            App.WriteLog($"[Everest.SwitchToCustomize] profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SwitchToCustomize] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sends a custom per-key effect to the device.
    /// </summary>
    internal bool SetCustomEffect(int profile, int area, EverestSdkNative.CustomEffect data, bool save = true)
    {
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.ChangeCustomizeEffect(profile, area, data, save);
            App.WriteLog($"[Everest.SetCustomEffect] profile={profile} area={area} save={save} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetCustomEffect] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Reads the current custom effect from the device.
    /// </summary>
    internal bool TryGetCustomEffect(int profile, int area, out EverestSdkNative.CustomEffect data)
    {
        data = new EverestSdkNative.CustomEffect
        {
            data = new EverestSdkNative.CustomData[171]
        };
        lock (_sdkLock)
        try
        {
            bool ok = EverestSdkNative.GetEffCustomizeContent(profile, area, ref data);
            App.WriteLog($"[Everest.GetCustomEffect] profile={profile} area={area} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.GetCustomEffect] threw: " + ex);
            return false;
        }
    }

    public void Dispose() => Close();

    // ---- native callback (SDK thread) ---------------------------------

    private void OnKeyCallback(ushort wMatrix, bool bPressed, uint id)
    {
        try
        {
            // We emit the event without a lock: consumers might
            // call back into other EverestService methods (deadlock).
            // Key logging removed — too noisy in normal use.
            KeyEvent?.Invoke(this, new EverestKeyEventArgs(id, wMatrix, bPressed));
        }
        catch (Exception ex)
        {
            // Never let a managed exception propagate into native code.
            App.WriteLog("[Everest.OnKeyCallback] threw: " + ex);
        }
    }
}

/// <summary>Arguments for the <see cref="EverestService.KeyEvent"/> event.</summary>
public sealed class EverestKeyEventArgs : EventArgs
{
    public EverestKeyEventArgs(uint deviceId, ushort keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }

    /// <summary>Device id reported by the SDK.</summary>
    public uint DeviceId { get; }

    /// <summary>Key matrix index (firmware's physical key index).</summary>
    public ushort KeyMatrix { get; }

    /// <summary>True = pressed, false = released.</summary>
    public bool Pressed { get; }
}
