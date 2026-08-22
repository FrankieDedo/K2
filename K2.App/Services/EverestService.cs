using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using K2.App.Models;   // EverestWMatrixMap (SetKeyDisabled's VK -> DLLKeyId lookup)

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

    /// <summary>
    /// EffMenuIndex of the last effect applied through <see cref="SetEffect"/> /
    /// <see cref="ApplyEverestCustomLighting"/> — the second half of the firmware's
    /// (profile, lighting-menu-slot) address pair. Cached because a bare
    /// <see cref="SwitchProfile(int)"/> has to name a slot too and the caller usually
    /// doesn't know which one yet (the UI loads the profile's effect only AFTER the
    /// switch). Starts at 1 = Wave, the factory-default slot, which is exactly the value
    /// hardcoded here before 2026-08-22.
    /// </summary>
    private int _cachedMenuIndex = 1;

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

    /// <summary>Window SDKDLL.dll posts its key/dial notifications to — assign the main
    /// window's HWND once it exists (OnSourceInitialized), before the driver is opened.
    /// See <see cref="EverestSdkNative.OpenUSBDriver"/> for why the DLL needs one.
    /// Zero is tolerated (the DLL simply has nowhere to post) so the service stays usable
    /// from contexts without a window.</summary>
    public static IntPtr HostWindow { get; set; } = IntPtr.Zero;

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
            _opened = EverestSdkNative.OpenUSBDriver(HostWindow);
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
            {
                NumpadButtonEvent?.Invoke(this, (btn, pressed));
                // Claim the press like Base Camp does, so the firmware does NOT also run
                // the key's own default action (see Pad.AckKeyPress). D1-D4's wMatrix
                // codes are 71/80/89/98 (steps of 9 — straight from BaseCamp.db's
                // EverestKeyBidings touch rows, confirmed by the 11 02 acks in both BC
                // captures). Dispatched to the pool: this callback runs on the Pad's own
                // reader thread, and AckKeyPress waits for responses that very thread
                // must dequeue — calling it inline would stall the reader until timeout.
                if (!pressed)
                {
                    byte wMatrix = (byte)(71 + 9 * btn);
                    int profile = _cachedProfile;
                    Task.Run(() => { try { pad.AckKeyPress(profile, wMatrix); } catch { /* best effort */ } });
                }
            };
            // Ordinary keys: the vendor SDK's key callback never fires in native-engine
            // mode, so presses come from the NKRO bitmap instead — same KeyEvent, but
            // carrying a HID usage rather than a wMatrix, hence the flag (see
            // EverestKeyEventArgs.FromNativeKeyReport).
            pad.KeyUsageChanged += (usage, pressed) =>
            {
                try { KeyEvent?.Invoke(this, new EverestKeyEventArgs(0, (ushort)usage, pressed, fromNativeKeyReport: true)); }
                catch (Exception ex) { App.WriteLog("[Everest.KeyUsageChanged] threw: " + ex); }
            };
            _nativePad = pad;
            _opened = true;
            App.WriteLog("[Everest.Open] (native) OK");

            // Open SDKDLL.dll's own driver handle as well. The native engine replaces
            // SDKDLL for driver open/close, init and the 4 numpad display keys, but
            // everything else on the Max still goes through SDKDLL (RGB, numpad icons,
            // Media Dock, Display Dial) — see the class header. Those calls need the
            // DLL's handle open.
            //
            // MEASURED 2026-08-22 on the real keyboard, because this used to be
            // documented the other way round ("SDKDLL calls work without OpenUSBDriver"):
            // from a process that never calls OpenUSBDriver, GetExtendInfo/GetFWInfo
            // return false forever (20+ consecutive one-second attempts); issue
            // OpenUSBDriver once and the very next call succeeds and keeps succeeding.
            // With it missing, every SDKDLL-only feature on the Max silently no-ops —
            // most visibly ApplyDialToDevice, which bails out on its opening
            // TryGetExtendInfo and so never writes the Display Dial's own settings
            // (screensaver included) to the firmware.
            //
            // Two transports to one firmware is the intended design here (see
            // _PROJECT_MAP.md), and every SDKDLL call is already serialized under
            // _sdkLock, so this adds a handle, not a race.
            bool sdkOpen = false;
            try { sdkOpen = EverestSdkNative.OpenUSBDriver(HostWindow); }
            catch (Exception ex) { App.WriteLog("[Everest.Open] (native) OpenUSBDriver threw: " + ex); }
            App.WriteLog($"[Everest.Open] (native) SDKDLL OpenUSBDriver(0x{HostWindow.ToInt64():X}) -> {sdkOpen}");

            // Run Base Camp's own post-open init through SDKDLL even on the native path.
            // Rationale (2026-07-19): the display-key reset sequence, replicated byte-for-
            // byte from a real BC capture (evprofiles.pcapng) with identical pacing and
            // firmware echoes (verified against K2's own capture, evdelete.pcapng), is
            // acked by the firmware but visibly IGNORED — while the same bytes work from
            // BC. Both BC captures start with Base Camp already running, so its session
            // init has never been seen on the wire; the one known candidate for the
            // missing device-side state is exactly this init (its doc comment already
            // records that ChangeEffect is silently ignored without it).
            InitDllState();
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
            // The keyboard also reports which lighting menu slot it is currently on —
            // seed the cache with it so the first SwitchProfile of the session names the
            // slot the device is really showing instead of the flat 1 K2 assumed.
            if (fi && fwInfo.byEffectMenuIndex <= 8)
                _cachedMenuIndex = fwInfo.byEffectMenuIndex;
            App.WriteLog($"[Everest.Init] GetFWInfo -> {fi}  " +
                $"fwVer=0x{fwInfo.fwVer:X4} profile={fwInfo.currentlyProfileIndex} " +
                $"effectMode={fwInfo.byEffectModeIndex} effectMenu={fwInfo.byEffectMenuIndex}" +
                $" -> cachedProfile={_cachedProfile} cachedMenu={_cachedMenuIndex}");
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
        //
        // This call is made right after Open() — on the native-engine path
        // (see OpenNative's comment) SDKDLL.dll is not necessarily loaded/ready
        // yet, so it can (and on a fast machine reliably does, per a 2026-08-17
        // hardware report: captured log showed this exact call returning False
        // ~1ms after native open) fail here. _apEnabled must only be cleared on
        // a CONFIRMED success: if we assumed AP was off anyway, the first
        // SetEffect() call right after would skip its own AP-off guard (it also
        // only fires "if (_apEnabled)"), the firmware would still be in AP mode,
        // and ChangeEffect/ChangeBlockEffect would be silently ignored — the
        // keyboard keeps showing whatever AP mode was rendering (observed as
        // wrong speed / effect off / an unrelated "rainbow"-looking pattern)
        // instead of the effect K2 just "successfully" sent. Leaving _apEnabled
        // true on failure makes SetEffect's own guard retry the disable right
        // before the real ChangeEffect call, by which point the DLL is up.
        try
        {
            bool ap = EverestSdkNative.APEnable(false);
            _apEnabled = !ap;
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
            // OpenNative also opens SDKDLL's handle (see its comment) — give it back.
            try { EverestSdkNative.CloseUSBDriver(); }
            catch (Exception ex) { App.WriteLog("[Everest.Close] (native) CloseUSBDriver threw: " + ex); }
            _opened = false;
            _apEnabled = false;
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
        // Used to short-circuit to -1 in native-engine mode on the assumption that
        // SDKDLL.dll wasn't loaded there. It is: the native path opens it too (see
        // OpenNative), so report the real version.
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
    /// Reads back the effect the firmware is currently running for
    /// <paramref name="fwProfile"/> (1-based): the profile's <c>curIndex</c> from the
    /// effect table, then that slot's stored <c>EffData</c>. Two chained reads, exactly
    /// as Base Camp's <c>Common.ChangeEverestBrightness</c>/<c>ChangeEffectOnUI</c> do it.
    /// Used to follow effect/brightness changes the user makes on the Display Dial rather
    /// than in the app — see <c>MainWindow.MediaDock.cs</c>.
    /// <c>internal</c>: exposes a P/Invoke layer type (also internal).
    /// </summary>
    internal bool TryGetCurrentEffect(int fwProfile, out EverestSdkNative.EffData eff)
    {
        eff = default;
        if (fwProfile is < 1 or > 5) return false;
        lock (_sdkLock)
        try
        {
            var menu = new EverestSdkNative.EffectMenu();
            if (!EverestSdkNative.GetProfileEffectTable(ref menu) || menu.table is null) return false;
            byte curIndex = menu.table[fwProfile - 1].curIndex;
            return EverestSdkNative.GetEffectContent(fwProfile, curIndex, ref eff);
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.TryGetCurrentEffect] threw: " + ex);
            return false;
        }
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
    /// Switches the keyboard's active profile. In native-engine mode this sends the
    /// captured wire command directly (see <see cref="EverestHidNative.Pad.SwitchProfile"/>
    /// — evprofiles.pcapng, 2026-07-19) instead of going through SDKDLL, whose
    /// SwitchProfile returned True in this mode without a verifiable hardware effect
    /// (no OpenUSBDriver state). SDKDLL fallback kept for the non-native path.
    /// </summary>
    /// <param name="profile">Firmware profile slot, 1-5.</param>
    /// <param name="effMenuIndex">
    /// Lighting menu slot to make active inside that profile (see
    /// <see cref="MenuIndexFor"/>). -1 (the default) reuses the slot of the last effect
    /// K2 applied — the honest answer when the caller is switching profiles and hasn't
    /// loaded the new profile's effect yet, and never worse than the flat <c>0x01</c>
    /// this argument replaced.
    /// </param>
    public bool SwitchProfile(int profile, int effMenuIndex = -1)
    {
        int menu = effMenuIndex >= 0 ? effMenuIndex : _cachedMenuIndex;
        if (_nativePad is not null)
        {
            // Under _sdkLock even though this doesn't touch SDKDLL: there is only ONE
            // firmware behind the two transports, and a concurrent SDKDLL call — in
            // particular the DEBOUNCED SaveFlash, which fires ~500ms after any lighting
            // change on its own thread — leaves the keyboard mute for seconds, so raw-HID
            // commands issued in that window time out. See FlushSaveFlash.
            lock (_sdkLock)
            {
                bool nok = _nativePad.SwitchProfile(profile, menu);
                if (nok) _cachedProfile = profile;   // keeps AckKeyPress's profile byte honest
                App.WriteLog($"[Everest.SwitchProfile] (native) profile={profile} menu={menu} -> {nok}");
                return nok;
            }
        }

        lock (_sdkLock)
        try
        {
            // The DLL's second parameter is the same EffMenuIndex byte the native wire
            // command carries (SwitchProfile(profile, EffMenuIndex, id) on the MacroPad,
            // confirmed 2026-07-09); it was passed as 0 while its meaning was unknown.
            bool ok = EverestSdkNative.SwitchProfile(profile, menu);
            App.WriteLog($"[Everest.SwitchProfile] profile={profile} menu={menu} -> {ok}");
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
        /// <summary>Per-key custom lighting (paint mode) — UI-only selection in
        /// CbEvEffect, never sent through <see cref="SetEffect"/>: MainWindow.
        /// ApplyCurrentEffect short-circuits on this value and shows the Custom
        /// Lighting panel instead (a separate raw-HID apply path, see
        /// MainWindow.CustomLighting.cs's use of ApplyEverestCustomLighting).</summary>
        Custom    = (byte)EverestSdkNative.EffectIndex.Custom,
    }

    /// <summary>
    /// Effect → <b>EffMenuIndex</b>, the slot of the firmware's per-profile lighting menu
    /// the effect lives in. The firmware addresses lighting as a (profile, menu slot) pair:
    /// <c>14 00 00 00 [profile] [menuIndex]</c> selects it and <c>13 55 00 00 [menuIndex]</c>
    /// persists it — the 5th byte of the save is the MENU SLOT, never the profile.
    ///
    /// <para><b>Capture-confirmed</b> (ev_profile_load.pcapng, 2026-08-22): Base Camp
    /// loading a profile into slot 2 writes all nine slots in the order 0,1,3,4,5,6,7,8,2,
    /// and the effect index carried by each <c>14 2C</c> pins the table down exactly —
    /// 0=Static(0) 1=Wave(4) 2=Tornado(7) 3=Breath(1) 4=ReactiveA(3) 5=Matrix(9)
    /// 6=Custom(10) 7=Yeti(6) 8=Off(12). Identical to the MacroPad's own menu order
    /// (MacroPadService.MenuIndexFor, confirmed 2026-07-09), which is why the same class
    /// of bug — saving to a slot that has nothing to do with the effect just sent —
    /// existed on both devices.</para>
    ///
    /// <para>The three Reactive variants share slot 4: Base Camp's menu has a single
    /// "Reactive" entry, the A/B/C split is K2's own. Matrix2 is K2's visual variant of
    /// Matrix and shares slot 5 for the same reason.</para>
    /// </summary>
    public static int MenuIndexFor(Effect effect) => effect switch
    {
        Effect.Static    => 0,
        Effect.Wave      => 1,
        Effect.Tornado   => 2,
        Effect.Breath    => 3,
        Effect.ReactiveA => 4,
        Effect.ReactiveB => 4,
        Effect.ReactiveC => 4,
        Effect.Matrix    => 5,
        Effect.Matrix2   => 5,
        Effect.Custom    => 6,
        Effect.Yeti      => 7,
        _                => 8,   // Off
    };

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
                // Only trust a confirmed success (see InitDllState's APEnable comment) —
                // a failed call here must keep retrying on the NEXT SetEffect, not silently
                // give up while the firmware may still be in AP mode.
                _apEnabled = !offOk;

                // A CONFIRMED-True APEnable(false) only means the DLL/USB round-trip
                // acked the command — not that the firmware has actually finished
                // leaving AP/software mode internally. Sending ChangeEffect/
                // ChangeBlockEffect immediately after (previously 0 delay — confirmed
                // ~4ms apart in a 2026-08-18 hardware log) can still land while the
                // firmware is mid-transition, so it "listens but doesn't apply" it
                // (same failure mode this whole block exists to avoid) and the keyboard
                // keeps showing its AP-mode idle pattern even though every call here
                // reports success and the bytes we sent were correct. Give the firmware
                // a moment to actually settle before the real command goes out.
                if (offOk) Thread.Sleep(150);
            }
            catch (Exception ex2) { App.WriteLog("[Everest.SetEffect] APEnable(false) prep threw: " + ex2); }
        }

        EverestSdkNative.FWColor C((byte, byte, byte) c) => new(c.Item1, c.Item2, c.Item3);
        var bright = QuantizeBrightness(brightness);

        // Base Camp names the (profile, lighting menu slot) pair before EVERY effect
        // apply, not just when the user switches profile — confirmed on the MacroPad
        // (2026-07-09) and now on the Everest Max (ev_profile_load.pcapng, 2026-08-22:
        // 14 00 00 00 02 <menu> → 14 2C <EffData> → 13 55 00 00 <menu>, nine times).
        // K2 sent neither the switch nor the right save slot here: ChangeEffect landed
        // in whatever slot the firmware happened to be on and the debounced SaveFlash
        // then persisted the PROFILE number as if it were a menu slot. Prime suspect for
        // "the first apply after Open()/SwitchProfile() is silently dropped even though
        // the bytes on the wire are correct" (2026-08-18 reports, currently worked around
        // by EvScheduleStartupEffectResend's brute-force 2s re-apply).
        int menuIndex = MenuIndexFor(effect);
        _cachedMenuIndex = menuIndex;
        SwitchProfile(_cachedProfile, menuIndex);

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
                DebouncedSaveFlash(menuIndex);
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
            DebouncedSaveFlash(menuIndex);

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
    /// <summary>
    /// Runs any PENDING debounced <see cref="EverestSdkNative.SaveFlash"/> right now and
    /// returns once the keyboard is done with it, so a caller about to send a long
    /// firmware sequence (display-key picture upload/reset, binding writes) can be sure a
    /// flash write won't start underneath it.
    ///
    /// <para>Why this exists: a lighting change schedules SaveFlash 500ms later on a
    /// background task, and while the firmware writes flash it answers NOTHING on the
    /// raw-HID channel. Selecting a profile does both — reapply lighting, then push the
    /// display keys — so on 2026-08-21 every one of the reset sequence's 8 commands timed
    /// out (1.2s each, 9.6s per key, all False) while SaveFlash was in flight.</para>
    ///
    /// <para>Taking <see cref="_sdkLock"/> is what actually does the waiting: the
    /// debounced task holds it for the whole SaveFlash call.</para>
    /// </summary>
    public void FlushSaveFlash()
    {
        var cts = _saveFlashCts;
        if (cts is null) return;
        cts.Cancel();                    // stop the pending 500ms delay from firing later
        _saveFlashCts = null;
        var slot = _cachedMenuIndex;     // menu slot, not the profile — see DebouncedSaveFlash
        lock (_sdkLock)                  // blocks until an already-started SaveFlash ends
        {
            try
            {
                bool ok = EverestSdkNative.SaveFlash(slot);
                App.WriteLog($"[Everest] SaveFlash(menu={slot}) flushed -> {ok}");
            }
            catch (Exception ex) { App.WriteLog("[Everest] SaveFlash flush threw: " + ex); }
        }
    }

    private void DebouncedSaveFlash(int effMenuIndex)
    {
        _saveFlashCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveFlashCts = cts;
        // 5th byte of "13 55 00 00 xx" is the EffMenuIndex, NOT the profile: the profile
        // was already named by the SwitchProfile that preceded the effect. Passing
        // _cachedProfile here (as this did until 2026-08-22) saved the effect into a
        // menu slot picked by coincidence — same bug fixed on the MacroPad 2026-07-09,
        // now capture-confirmed for the Everest Max too (see MenuIndexFor).
        var slot = effMenuIndex;
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
                    bool ok = EverestSdkNative.SaveFlash(slot);
                    App.WriteLog($"[Everest] SaveFlash(menu={slot}) debounced -> {ok}");

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

    /// <summary>Sets the "Game Mode" key-lock bitmask (see EverestSdkNative.SetGameMode).</summary>
    public bool SetGameMode(int mode)
    {
        try
        {
            bool ok = EverestSdkNative.SetGameMode(mode);
            App.WriteLog($"[Everest.SetGameMode] mode={mode} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetGameMode] threw: " + ex);
            return false;
        }
    }

    /// <summary>Enables/disables the keyboard's Core indicator LEDs.</summary>
    public bool SetIndicatorLed(bool enable)
    {
        try
        {
            bool ok = EverestSdkNative.SetIndicatorLed(enable);
            App.WriteLog($"[Everest.SetIndicatorLed] enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetIndicatorLed] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Wipes the keyboard's flash back to factory defaults (profiles, key bindings,
    /// display-key artwork, lighting) and drops every host-side cache describing what
    /// the device holds. BLOCKS for ~3.5s: the firmware goes mute while erasing and
    /// only then echoes the command (everest_reset.pcapng, 2026-08-21) — call it from a
    /// background/RunHwBusy thread, never inline on the UI thread.
    ///
    /// Native-engine path sends the captured wire command directly (see
    /// <see cref="EverestHidNative.Pad.ResetFlash"/>): SDKDLL's ResetFlash runs without
    /// OpenUSBDriver state in that mode and would very likely return True while emitting
    /// nothing, the same failure mode already proven for SwitchProfile. SDKDLL fallback
    /// kept for the non-native path.
    ///
    /// This is a TRUE factory reset: unlike Base Camp, which re-pushes its active profile
    /// right after the wipe, K2 leaves the hardware factory-clean — wiping K2's own store
    /// to match is the caller's job (see MainWindow.BtnSettingsFactoryReset_Click).
    /// Afterwards the device is on profile 1 with factory artwork and no bindings, and
    /// AP mode is off.
    /// </summary>
    public bool ResetFlash(bool full = true)
    {
        // A SaveFlash scheduled seconds ago (effect/speed edit) would otherwise land
        // right after the wipe and re-persist exactly what we just erased.
        _saveFlashCts?.Cancel();

        bool ok;
        try
        {
            ok = _nativePad is not null
                ? _nativePad.ResetFlash()
                : EverestSdkNative.ResetFlash(full);
            App.WriteLog($"[Everest.ResetFlash] full={full} native={_nativePad is not null} -> {ok}");
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetFlash] threw: " + ex);
            return false;
        }

        if (!ok) return false;

        // The wipe reboots the firmware's config state: AP mode is gone (leaving
        // _apEnabled true would make EnsureApMode/SetEffect skip their own AP guard and
        // every later lighting command would be silently ignored — same trap documented
        // in InitDllState), and the active profile is back to 1 — on the factory
        // lighting menu slot 1 (Wave), which is what the post-reset read in
        // everest_reset.pcapng shows.
        _apEnabled = false;
        _cachedProfile = 1;
        _cachedMenuIndex = 1;

        // Re-run Base Camp's post-open init, exactly like BC does after its own reset
        // (everest_reset.pcapng: 11 00 / 11 14 / 11 12 / 11 01 reads before it touches
        // the device again) — without it the DLL/device state is stale for the next
        // lighting call.
        InitDllState();
        return true;
    }

    /// <summary>
    /// Clears the ACTIVE profile's stored content (lighting menu slots, display-key
    /// bindings/pictures) without touching the rest of the keyboard — the
    /// <c>13 40 00 00 00</c> command Base Camp sends right before repopulating a profile
    /// it is loading, see <see cref="EverestHidNative.Pad.ResetProfileContent"/> for the
    /// capture and for what is NOT yet verified about its scope. Native-engine only
    /// (SDKDLL has no export for it); the caller must already have switched the device to
    /// the profile it wants wiped.
    /// </summary>
    public bool ResetProfileContent()
    {
        if (_nativePad is null) return false;
        // Same one-firmware-two-transports serialization as SwitchProfile: a debounced
        // SaveFlash in flight leaves the keyboard mute and this command would time out.
        FlushSaveFlash();
        lock (_sdkLock)
        try
        {
            bool ok = _nativePad.ResetProfileContent();
            App.WriteLog($"[Everest.ResetProfileContent] -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ResetProfileContent] threw: " + ex);
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
    /// Without a SaveFlash, effects applied via AP-mode are lost on the next unplug.
    /// <para>The argument is the <b>EffMenuIndex</b> (see <see cref="MenuIndexFor"/>) —
    /// the lighting menu slot to commit, addressed inside whatever profile the last
    /// <see cref="SwitchProfile(int,int)"/> selected. It is NOT a profile number, despite
    /// the DLL export naming it one: <c>13 55 00 00 xx</c> carries the menu slot on the
    /// wire (ev_profile_load.pcapng, 2026-08-22). The default 6 is Custom's slot, which
    /// is what <see cref="EverestSideLedProtocol"/>'s persist packet has always used.</para>
    /// </summary>
    public bool SaveFlash(int effMenuIndex = 6)
    {
        try
        {
            bool ok = EverestSdkNative.SaveFlash(effMenuIndex);
            App.WriteLog($"[Everest.SaveFlash] menu={effMenuIndex} -> {ok}");
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
        lock (_sdkLock)
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
        lock (_sdkLock)
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
    /// Accessory positions with an explicit "couldn't read" answer, unlike
    /// <see cref="NumpadPlugPosition"/>/<see cref="MMDockPlugPosition"/>, which report a
    /// failed GetExtendInfo as 0 = "not connected". That conflation made the numpad (and
    /// with it every display key) vanish from the UI whenever the 3s accessory poll
    /// happened to land inside a firmware-busy window — user report 2026-08-21,
    /// "spariscono i tasti display dall'interfaccia". Callers keep their previous state
    /// when this returns false.
    /// </summary>
    public bool TryGetAccessoryPositions(out byte numpadPos, out byte dockPos)
    {
        if (TryGetExtendInfo(out var info))
        {
            numpadPos = info.byNumpadPlug;
            dockPos   = info.byMMDockPlug;
            return true;
        }
        numpadPos = dockPos = 0;
        return false;
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
    /// <summary>
    /// Sets what an ordinary keyboard key emits: nothing (a "disabled key"), its factory
    /// function, or "claimed by the host" so K2's own action can run without the key also
    /// typing — see <see cref="EverestHidNative.Pad.WriteKeyOutputMode"/>.
    /// <paramref name="matrixId"/> is K2's own VK-code key identity (what
    /// <c>EverestKeyRecord.KeyMatrix</c> stores), translated here to the DLLKeyId the
    /// firmware wants via <see cref="EverestWMatrixMap.MatrixIdToDllKeyId"/>. Returns
    /// false when the key isn't in that catalog or the native engine isn't running —
    /// nothing host-side can suppress a keystroke, so a false here means the key keeps
    /// typing and the caller must not pretend otherwise.
    /// </summary>
    // internal, not public: KeyOutputMode is an internal type (project rule on facade
    // methods that expose internal types).
    internal bool SetKeyOutputMode(int matrixId, EverestHidNative.Pad.KeyOutputMode mode)
    {
        if (_nativePad is null) return false;
        if (!EverestWMatrixMap.MatrixIdToDllKeyId.TryGetValue(matrixId, out int dllKeyId))
        {
            App.WriteLog($"[Everest.SetKeyOutputMode] matrixId={matrixId} not in the DLLKeyId catalog");
            return false;
        }
        try { return _nativePad.WriteKeyOutputMode(dllKeyId, mode); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetKeyOutputMode] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Writes a display key's action binding into the firmware, flipping the key to
    /// "custom" mode so its built-in default action stops firing on press — see
    /// <see cref="EverestHidNative.Pad.WriteDisplayKeyBinding"/>. Only meaningful in
    /// native-engine mode; K2 action types the firmware can't express get an EMPTY
    /// type-01 payload (suppression is the goal, K2 executes the action itself).
    /// A non-empty payload here is NOT inert: type=0x01 makes the firmware actually
    /// try to launch it (like a real Base Camp exec binding), independent of K2 — the
    /// previous "K2:" + actionType placeholder (e.g. "K2:oscmd") made every display
    /// key bound to a non-exec/url action (Calculator, shell commands, ...) pop
    /// Windows' "get an app to open this 'k2' link" dialog on every press, since
    /// "K2:oscmd" parses as a URI with an unregistered "K2" scheme (user report
    /// 2026-08-22). An empty payload gives the firmware nothing to resolve.
    /// </summary>
    public bool WriteNumpadBinding(int keyIndex, string actionType, string? actionValue)
    {
        if (_nativePad is null) return false;
        byte type;
        string payload;
        if (string.Equals(actionType, "exec", StringComparison.Ordinal))
        {
            type = 0x01; payload = actionValue ?? "";
        }
        else if (string.Equals(actionType, "url", StringComparison.Ordinal)
                 && actionValue?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
        {
            type = 0x02; payload = actionValue;
        }
        else
        {
            type = 0x01; payload = "";   // suppression only — must NOT look like a launchable path/URI
        }
        // Serialized against SDKDLL traffic like every other native-transport call —
        // see SwitchProfile's comment.
        try { lock (_sdkLock) return _nativePad.WriteDisplayKeyBinding(keyIndex, type, payload); }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.WriteNumpadBinding] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Uploads an image to a numpad display key (square format 72×72).
    /// </summary>
    /// <param name="imagePathOrBase64">Path or base64 string.</param>
    /// <param name="keyIndex">Display key index (0-3), sent as byTargetSubItem.</param>
    /// <param name="picSlot">Firmware PROFILE number (1-5), sent as byTargetPic — confirmed
    /// via USB capture against real Base Camp (K2/_reference/usb_dumps/evicone.pcapng,
    /// 2026-07-16): each profile stores its own 4 NDK pictures in flash, which is also why
    /// switching the active profile is instant on real hardware (no image re-transfer).</param>
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
    /// Clears a numpad display key's picture. In native-engine mode this restores the
    /// FACTORY-DEFAULT artwork with the exact reset sequence real Base Camp sends when
    /// deleting an icon (see <see cref="EverestHidNative.Pad.ResetDisplayKeyPic"/> —
    /// the 13 42 reset command addresses the target profile's picture slots directly,
    /// one bitmask field per profile). Non-native fallback: upload a solid-black 72×72
    /// image (no SDKDLL call exists to blank a picture slot).
    /// </summary>
    /// <param name="keyIndex">Display key index (0-3).</param>
    /// <param name="picSlot">Firmware PROFILE number (1-5) whose picture slot to clear.</param>
    public bool ClearNumpadImage(int keyIndex, byte picSlot)
    {
        if (_nativePad is not null)
        {
            // Same one-firmware-two-transports serialization as SwitchProfile: this
            // 8-command sequence took 9.6s of pure timeouts on 2026-08-21 because a
            // debounced SaveFlash was writing flash at the same moment.
            lock (_sdkLock)
            {
                bool nok = _nativePad.ResetDisplayKeyPic(keyIndex, picSlot);
                App.WriteLog($"[Everest.ClearNumpadImage] (native reset) key={keyIndex} profile={picSlot} -> {nok}");
                return nok;
            }
        }

        lock (_sdkLock)
        try
        {
            using var black = new System.Drawing.Bitmap(72, 72);
            using (var g = System.Drawing.Graphics.FromImage(black))
                g.Clear(System.Drawing.Color.Black);
            bool ok = EverestImageUploader.UploadBitmap(
                black, EverestImageUploader.PicTarget.NumpadSquare, picSlot, (byte)keyIndex);
            App.WriteLog($"[Everest.ClearNumpadImage] key={keyIndex} slot={picSlot} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ClearNumpadImage] threw: " + ex);
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
    /// Updates the clock on the Media Dock's display with the current time and
    /// format. The dock has its own RTC and ticks on its own, so this is a
    /// *resync*, not a per-second refresh: call it every 30 minutes (the interval
    /// of Base Camp's own <c>Clock_timer</c>) plus on the events that need it
    /// (apply, profile switch, startup). Calling it every second keeps the dock's
    /// idle counter permanently reset, so the screensaver never fires — see
    /// <c>MainWindow.DisplayDial.cs</c>'s <c>_dialClockTimer</c> (2026-08-22).
    /// </summary>
    /// <remarks>
    /// <b>2026-07-15, real Base Camp USB capture (_reference/usb_dumps/evclock.pcapng):</b>
    /// toggling the clock format (12h/24h) in Base Camp's own UI produces ZERO
    /// change to FW_EXTEND_INFO (byMMDockScreenSetup stays constant throughout)
    /// — the format is instead carried on every periodic <c>SetClockInfo</c> call
    /// itself (this method), not through SetExtendInfo. Previously this method
    /// only ever re-sent whatever <c>GetClockInfo</c> reported, so K2's Display
    /// Dial 12h/24h buttons had no way to actually reach the device (nothing
    /// called this method either — see MainWindow.DisplayDial.cs). Now takes the
    /// desired format explicitly and forces the clock on, since nothing else in
    /// K2 ever sets it true.
    /// </remarks>
    public bool UpdateClock(bool format24h)
    {
        lock (_sdkLock)
        try
        {
            var now = DateTime.Now;
            bool ok = EverestSdkNative.SetClockInfo(
                now.Month, now.Day, now.Hour, now.Minute, now.Second,
                clockEnabled: true, format24h);
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
    /// Sends the 45 border ("side") LED colors only — raw HID, separate channel from
    /// <see cref="SetCustomEffect"/> (SDKDLL.dll's struct never carried these, see
    /// <see cref="EverestSideLedProtocol"/>). Prefer <see cref="ApplyEverestCustomLighting"/>
    /// when also sending keycap colors in the same action (avoids a double persist-to-
    /// flash write). Only available in native-engine mode (always on, see
    /// <see cref="K2.Core.AppSettings.EverestNativeEngine"/>) — returns false if the
    /// native pad isn't open. <paramref name="wireColors"/> indexed 0-44 by WIRE index,
    /// 0xRRGGBB per entry; <paramref name="persist"/> writes to flash slot 6 afterward.
    /// </summary>
    public bool SetSideLedColors(int[] wireColors, byte brightness = 0xFF, bool persist = true)
    {
        if (_nativePad is null) return false;
        try
        {
            // Base Camp names the target lighting slot before the Custom burst too:
            // 14 00 00 00 <profile> 06 precedes the whole 11 01/14 2C/14 2D/14 A0
            // sequence in ev_profile_load.pcapng (2026-08-22). Without it the burst and
            // its "13 55 00 00 06" persist address a slot the firmware was never told to
            // select — they only agreed by luck, because 6 IS Custom's menu index.
            _cachedMenuIndex = MenuIndexFor(Effect.Custom);
            SwitchProfile(_cachedProfile, _cachedMenuIndex);
            bool ok = _nativePad.EnableCustomLighting(brightness);
            ok &= _nativePad.SendSideLedColors(wireColors, brightness);
            if (persist) ok &= _nativePad.PersistCustomLighting();
            App.WriteLog($"[Everest.SetSideLedColors] persist={persist} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.SetSideLedColors] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Sends BOTH the 126 keycap colors and the 45 border colors in one raw-HID action,
    /// replicating Base Camp's own apply byte-for-byte (2026-07-22 captures): enable
    /// (<c>14 2C 0A</c>, brightness 0-100) → zone-02 switch + 7 keycap pages → zone-05
    /// switch + 3 ring pages → persist once. Replaces the SDKDLL.dll
    /// <see cref="SetCustomEffect"/> path, which produced NO wire traffic at all
    /// (evmax_fillall_k2.pcapng). <paramref name="keycapWireColors"/> indexed 0-132
    /// (only 0-125 meaningful) in the LedMatrixMapping index domain —
    /// capture-confirmed to be the wire position domain too;
    /// <paramref name="sideWireColors"/> indexed 0-44 — both 0xRRGGBB.
    /// <paramref name="brightness"/> is 0-100 (keycap pages; the ring pages keep 0xFF
    /// like Base Camp does).
    /// </summary>
    public bool ApplyEverestCustomLighting(int[] keycapWireColors, int[] sideWireColors,
                                            byte brightness = 100, bool persist = true,
                                            byte[]? ledEffectCode = null,
                                            IReadOnlyList<byte[]>? effectParamPackets = null)
    {
        if (_nativePad is null) return false;
        try
        {
            // Base Camp names the target lighting slot before the Custom burst too:
            // 14 00 00 00 <profile> 06 precedes the whole 11 01/14 2C/14 2D/14 A0
            // sequence in ev_profile_load.pcapng (2026-08-22). Without it the burst and
            // its "13 55 00 00 06" persist address a slot the firmware was never told to
            // select — they only agreed by luck, because 6 IS Custom's menu index.
            _cachedMenuIndex = MenuIndexFor(Effect.Custom);
            SwitchProfile(_cachedProfile, _cachedMenuIndex);
            bool ok = _nativePad.EnableCustomLighting(brightness);
            ok &= _nativePad.SendKeycapColors(keycapWireColors, brightness);
            ok &= _nativePad.SendSideLedColors(sideWireColors);
            // Dynamic per-region effects (Wave/Breathing/Reactive/... on a painted
            // subset of keys) — optional third channel alongside the static keycap/
            // side-ring colors above, see EverestSideLedProtocol's per-region-effects
            // section (2026-07-22 captures). Only sent when the caller actually has
            // dynamic-effect LEDs assigned.
            if (ledEffectCode is { Length: > 0 } && effectParamPackets is { Count: > 0 })
                ok &= _nativePad.SendCustomEffectRegions(ledEffectCode, effectParamPackets);
            if (persist) ok &= _nativePad.PersistCustomLighting();
            App.WriteLog($"[Everest.ApplyEverestCustomLighting] persist={persist} " +
                         $"effects={effectParamPackets?.Count ?? 0} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[Everest.ApplyEverestCustomLighting] threw: " + ex);
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

    private void OnKeyCallback(ushort wMatrix, bool bPressed)
    {
        try
        {
            // We emit the event without a lock: consumers might
            // call back into other EverestService methods (deadlock).
            // Key logging removed — too noisy in normal use.
            // DeviceId is always 0: the Everest is single-device and its callback
            // carries no id (see EverestSdkNative.KEY_CALLBACK's remarks).
            KeyEvent?.Invoke(this, new EverestKeyEventArgs(0, wMatrix, bPressed));
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
    public EverestKeyEventArgs(uint deviceId, ushort keyMatrix, bool pressed,
                               bool fromNativeKeyReport = false)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
        FromNativeKeyReport = fromNativeKeyReport;
    }

    /// <summary>True when <see cref="KeyMatrix"/> is a HID usage id read from the native
    /// engine's NKRO bitmap, NOT a firmware wMatrix from the vendor SDK callback — the
    /// two are different numbering spaces that overlap in the low integers, so the
    /// consumer must not translate one as if it were the other.</summary>
    public bool FromNativeKeyReport { get; }

    /// <summary>Device id reported by the SDK.</summary>
    public uint DeviceId { get; }

    /// <summary>Key matrix index (firmware's physical key index).</summary>
    public ushort KeyMatrix { get; }

    /// <summary>True = pressed, false = released.</summary>
    public bool Pressed { get; }
}
