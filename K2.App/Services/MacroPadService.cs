using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Application facade over <see cref="MacroPadSdkNative"/>.
///
/// Exposes the native MacroPad SDK as a clean .NET API: driver open/close,
/// device enumeration, firmware info reads, and typed events
/// (.NET <see cref="EventHandler{TEventArgs}"/>) for keys, plug/unplug and
/// firmware update progress.
///
/// Mirrors the role that <c>DisplayPadService</c> plays in the DisplayPad
/// module, so the unified K2.App shell treats every device the same way.
///
/// <para>
/// <b>Events.</b> Key events arrive via a callback on an internal SDK
/// thread; plug and progress arrive as Windows messages on the HWND passed
/// to <see cref="Open"/>. The consumer must forward the window messages
/// to <see cref="HandleWindowMessage"/> (typically from a WndProc hook).
/// All events may therefore be raised on a thread other than the UI
/// thread: the handler is responsible for marshalling.
/// </para>
/// </summary>
public sealed class MacroPadService : IDisposable
{
    // The delegate must be kept in a field: if the GC collected it, the SDK
    // would call a no-longer-valid function pointer -> native crash.
    private MacroPadSdkNative.KEY_CALLBACK? _keyCallback;
    private bool _opened;

    /// <summary>MacroPad key pressed or released.</summary>
    public event EventHandler<MacroPadKeyEventArgs>? KeyEvent;

    /// <summary>Device plugged / unplugged (<c>WM_DEVICE_PLUG</c> message).</summary>
    public event EventHandler<MacroPadPlugEventArgs>? DevicePlug;

    /// <summary>Firmware update progress (<c>WM_FW_PROGRESS</c> message).</summary>
    public event EventHandler<MacroPadProgressEventArgs>? FirmwareProgress;

    /// <summary>Max slots addressable by the SDK.</summary>
    public const int MaxDeviceCount = MacroPadSdkNative.MAX_DEV_COUNT;

    /// <summary>Physical keys on the MacroPad.</summary>
    public const int ButtonCount = MacroPadSdkNative.FW_NUM_KEY;

    /// <summary>Profiles stored on each device.</summary>
    public const int ProfileCount = MacroPadSdkNative.FW_NUM_PROFILE;

    /// <summary>True if the USB driver was opened successfully.</summary>
    public bool IsOpen => _opened;

    /// <summary>
    /// Opens the MacroPad USB driver. <paramref name="hWnd"/> is the HWND of
    /// the window that will receive plug/progress messages: it must be the
    /// same HWND whose WndProc forwards to <see cref="HandleWindowMessage"/>.
    /// Also registers the key callback.
    /// </summary>
    public bool Open(IntPtr hWnd)
    {
        if (_opened) return true;

        // The key callback is global (one registration per process):
        // we hook it before opening the driver, as the original worker does.
        _keyCallback = OnKeyCallback;
        try
        {
            MacroPadSdkNative.SetKeyCallBack(_keyCallback);
            App.WriteLog("[MacroPad.Open] SetKeyCallBack registered");
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.Open] SetKeyCallBack threw: " + ex);
        }

        App.WriteLog($"[MacroPad.Open] OpenUSBDriver(0x{hWnd.ToInt64():X})");
        bool ok;
        try
        {
            ok = MacroPadSdkNative.OpenUSBDriver(hWnd);
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.Open] OpenUSBDriver threw: " + ex);
            return false;
        }
        App.WriteLog($"[MacroPad.Open] -> {ok}");
        _opened = ok;
        return ok;
    }

    /// <summary>Closes the USB driver.</summary>
    public void Close()
    {
        if (!_opened) return;
        try { MacroPadSdkNative.CloseUSBDriver(); }
        catch (Exception ex) { App.WriteLog("[MacroPad.Close] threw: " + ex); }
        _opened = false;
        // _keyCallback stays referenced as long as the service is alive: the SDK
        // might still have the pointer registered.
        _initializedSlots.Clear();
        App.WriteLog("[MacroPad.Close] driver closed");
    }

    // ---- per-slot DLL state init ----------------------------------------

    private readonly HashSet<uint> _initializedSlots = new();

    /// <summary>
    /// Replicates, per device slot, the initialization calls Base Camp makes
    /// after a MacroPad connects — mirrors <c>EverestService.InitDllState</c>
    /// (same doc comment there: "Even though we don't use the returned data,
    /// the DLL's internal side effects prepare the state for ChangeEffect/
    /// ChangeBlockEffect"). MacroPad never had an equivalent: <c>Open()</c>
    /// only called <c>OpenUSBDriver</c>. Idempotent per slot (cheap to call
    /// before every effect apply). Added 2026-07-09 after two rounds of
    /// decompile-based fixes (ChangeBlockEffect routing, blittable EffData)
    /// left Wave/Tornado working but every other preset — "Off" included —
    /// still doing nothing: the remaining common factor with Everest's own
    /// history is exactly this missing one-time init.
    /// </summary>
    internal void EnsureSlotInitialized(uint id)
    {
        if (!_initializedSlots.Add(id)) return;

        try
        {
            var fwInfo = new MacroPadSdkNative.FWInfo();
            bool fi = MacroPadSdkNative.GetFWInfo(ref fwInfo, id);
            App.WriteLog($"[MacroPad.Init] id={id} GetFWInfo -> {fi} profile={fwInfo.currentlyProfileIndex} " +
                         $"effectMode={fwInfo.byEffectModeIndex} effectMenu={fwInfo.byEffectMenuIndex}");
        }
        catch (Exception ex) { App.WriteLog("[MacroPad.Init] GetFWInfo threw: " + ex); }

        try
        {
            int layout = 0;
            bool fl = MacroPadSdkNative.GetFWLayout(ref layout, id);
            App.WriteLog($"[MacroPad.Init] id={id} GetFWLayout -> {fl} layout={layout}");
        }
        catch (Exception ex) { App.WriteLog("[MacroPad.Init] GetFWLayout threw: " + ex); }

        try
        {
            bool ek = MacroPadSdkNative.EnableKeyFunc(true, id);
            App.WriteLog($"[MacroPad.Init] id={id} EnableKeyFunc(true) -> {ek}");
        }
        catch (Exception ex) { App.WriteLog("[MacroPad.Init] EnableKeyFunc threw: " + ex); }

        // Forces the firmware out of AP mode (may have been left in AP from a
        // previous K2/BC session) — same rationale as Everest's InitDllState.
        try
        {
            bool ap = MacroPadSdkNative.APEnable(false, id);
            App.WriteLog($"[MacroPad.Init] id={id} APEnable(false) -> {ap}");
        }
        catch (Exception ex) { App.WriteLog("[MacroPad.Init] APEnable(false) threw: " + ex); }
    }

    /// <summary>Switches the MacroPad's active profile. Calls native SwitchProfile(profile, 0, id).</summary>
    public bool SwitchProfile(uint deviceId, int profile)
    {
        try
        {
            bool ok = MacroPadSdkNative.SwitchProfile(profile, 0, deviceId);
            App.WriteLog($"[MacroPad.SwitchProfile] device={deviceId} profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SwitchProfile] threw: " + ex);
            return false;
        }
    }

    /// <summary>Version of the SDK's native DLL.</summary>
    public int SdkVersion()
    {
        try { return MacroPadSdkNative.GetDLLVersion(); }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SdkVersion] threw: " + ex);
            return 0;
        }
    }

    /// <summary>Device count reported by <c>GetDevCount</c> (-1 on error).</summary>
    public int DeviceCount()
    {
        int n = 0;
        try
        {
            bool ok = MacroPadSdkNative.GetDevCount(ref n);
            if (!ok) App.WriteLog("[MacroPad.DeviceCount] GetDevCount -> false");
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.DeviceCount] threw: " + ex);
            return -1;
        }
        return n;
    }

    /// <summary>
    /// Slots with devices actually plugged in. Probes slots 1..<see cref="MaxDeviceCount"/>
    /// with <c>IsDevicePlug</c>. In this first step there's no "phantom" filtering:
    /// the log reports everything so we can observe the real behavior.
    /// </summary>
    public IReadOnlyList<uint> DeviceIds()
    {
        var found = new List<uint>();
        for (uint id = 1; id <= MaxDeviceCount; id++)
        {
            try
            {
                if (MacroPadSdkNative.IsDevicePlug(id))
                    found.Add(id);
            }
            catch (Exception ex)
            {
                App.WriteLog($"[MacroPad.DeviceIds] IsDevicePlug({id}) threw: {ex.Message}");
            }
        }
        App.WriteLog($"[MacroPad.DeviceIds] devices plugged in -> [{string.Join(", ", found)}]");
        return found;
    }

    /// <summary>True if there's a device on the given slot.</summary>
    public bool IsPlugged(uint id)
    {
        try { return MacroPadSdkNative.IsDevicePlug(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.IsPlugged] id={id} threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Application firmware version of the device.</summary>
    public ushort FirmwareVersion(uint id)
    {
        try { return MacroPadSdkNative.GetDevAppVer(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.FirmwareVersion] id={id} threw: {ex.Message}");
            return 0;
        }
    }

    /// <summary>True if a firmware update is in progress on the device.</summary>
    public bool IsUpdating(uint id)
    {
        try { return MacroPadSdkNative.IsUpdating(id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.IsUpdating] id={id} threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads <see cref="MacroPadSdkNative.DevInfo"/> (VID/PID/versions).
    /// <c>internal</c>: exposes a P/Invoke layer type (also internal).</summary>
    internal bool TryGetDeviceInfo(uint id, out MacroPadSdkNative.DevInfo info)
    {
        info = default;
        try { return MacroPadSdkNative.GetDeviceInfo(ref info, id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.TryGetDeviceInfo] id={id} threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads <see cref="MacroPadSdkNative.FWInfo"/> (current profile/effect).
    /// <c>internal</c>: exposes a P/Invoke layer type (also internal).</summary>
    internal bool TryGetFirmwareInfo(uint id, out MacroPadSdkNative.FWInfo info)
    {
        info = default;
        try { return MacroPadSdkNative.GetFWInfo(ref info, id); }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.TryGetFirmwareInfo] id={id} threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>Enables/disables software control (AP mode) on the device.</summary>
    public bool APEnable(uint id, bool enable)
    {
        try
        {
            bool ok = MacroPadSdkNative.APEnable(enable, id);
            App.WriteLog($"[MacroPad.APEnable] id={id} enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog($"[MacroPad.APEnable] id={id} threw: {ex.Message}");
            return false;
        }
    }

    // =======================================================================
    // LED lighting (firmware presets)
    //
    // Mirrors the proven logic from the Everest module, but every native
    // MacroPad call takes a trailing `uint ID` parameter = the device slot.
    // =======================================================================

    /// <summary>Lighting preset: aliases for the native firmware indices.</summary>
    public enum Effect : byte
    {
        Static    = (byte)MacroPadSdkNative.EffectIndex.Static,
        Breath    = (byte)MacroPadSdkNative.EffectIndex.Breath,
        Wave      = (byte)MacroPadSdkNative.EffectIndex.Wave,
        ReactiveA = (byte)MacroPadSdkNative.EffectIndex.ReactiveA,
        ReactiveB = (byte)MacroPadSdkNative.EffectIndex.ReactiveB,
        ReactiveC = (byte)MacroPadSdkNative.EffectIndex.ReactiveC,
        Yeti      = (byte)MacroPadSdkNative.EffectIndex.Yeti,
        Tornado   = (byte)MacroPadSdkNative.EffectIndex.Tornado,
        Matrix    = (byte)MacroPadSdkNative.EffectIndex.Matrix,
        Off       = (byte)MacroPadSdkNative.EffectIndex.Off,
    }

    /// <summary>Rotation/scroll direction (kept for source compatibility; the actual
    /// wire codes are effect-specific — see <see cref="MainWindow.MacroLed"/>'s CapsFor).</summary>
    public enum Direction : byte { ClockWise = 0, CounterClockWise = 1 }

    /// <summary>
    /// Applies a lighting preset to the given device slot.
    /// <para>
    /// Reverse-engineered 2026-07-09 from the extracted <c>BaseCamp.UI.dll</c>
    /// (<c>MacroPadOperations.SetMacroPadLighting</c> / <c>MacroPadDLLHelper.
    /// getChangeEffect</c> / <c>getChangeBlockEffect</c>) — see
    /// <c>K2/_reference/BaseCamp_decompiled_UI/</c>. Two findings fixed here:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Wave and Tornado are NOT firmware presets applied via
    ///   <c>ChangeEffect</c></b> — Base Camp routes them through
    ///   <c>ChangeBlockEffect</c> (<see cref="MacroPadSdkNative.BlockData"/>)
    ///   instead, exactly like the Everest keyboard. Wave happens to be the
    ///   UI's default selection, so this was very likely why "nothing seemed
    ///   to happen".</item>
    ///   <item>Base Camp never calls <c>APEnable</c> around this — that call
    ///   (previously here, copied from the Everest module) doesn't appear
    ///   anywhere in the MacroPad lighting code path of <c>BaseCamp.UI.dll</c>
    ///   and has been removed.</item>
    /// </list>
    /// <c>SaveFlash</c> is still called after every apply to make the preset
    /// persistent on the slot (Base Camp does the same in <c>SetMacroPadLighting</c>).
    /// </summary>
    /// <param name="speedByte">Raw firmware speed 0..100 (DB default 60). Ignored
    /// (forced to 255, "N/A") for Static/Off — see <see cref="MacroPadSdkNative.EffData.New"/>.</param>
    /// <param name="directionByte">Raw firmware direction byte, effect-specific
    /// (Wave: 0/2/4/6, Tornado: 9/10). Only meaningful for Wave/Tornado.</param>
    public bool SetEffect(uint id, Effect effect,
                          (byte r, byte g, byte b) primary,
                          (byte r, byte g, byte b)? secondary = null,
                          (byte r, byte g, byte b)? tertiary = null,
                          (byte r, byte g, byte b)? background = null,
                          int brightness = 100,
                          bool randomColor = false,
                          byte speedByte = 60,
                          int directionByte = -1)
    {
        EnsureSlotInitialized(id);

        MacroPadSdkNative.FWColor C((byte, byte, byte) c) => new(c.Item1, c.Item2, c.Item3);
        var bright = QuantizeBrightness(brightness);

        if (effect == Effect.Wave || effect == Effect.Tornado)
        {
            byte dirB = (byte)(directionByte >= 0 ? directionByte : 0);
            MacroPadSdkNative.FWColor? c2b = secondary is { } s ? C(s) : null;
            var block = MacroPadSdkNative.BlockData.New(
                eff:       (MacroPadSdkNative.EffectIndex)effect,
                direction: dirB,
                speed:     speedByte,
                lightness: (byte)bright,
                c1:        C(primary),
                c2:        c2b,
                rainbow:   randomColor);
            try
            {
                bool okB = MacroPadSdkNative.ChangeBlockEffect(block, id);
                App.WriteLog($"[MacroPad.SetEffect] BLOCK id={id} eff={effect} dir={dirB} speed={speedByte} " +
                             $"bright={bright} rainbow={randomColor} -> {okB}");
                App.WriteLog("[MacroPad.SetEffect] DUMP BlockData(62B): " + DumpBlockData(block));

                try
                {
                    bool flashOk = MacroPadSdkNative.SaveFlash(6, id); // 6 = ALL_PROFILE
                    App.WriteLog($"[MacroPad.SetEffect] SaveFlash(ALL,id={id}) (commit) -> {flashOk}");
                }
                catch (Exception ex2) { App.WriteLog("[MacroPad.SetEffect] SaveFlash threw: " + ex2); }

                return okB;
            }
            catch (Exception ex)
            {
                App.WriteLog("[MacroPad.SetEffect] ChangeBlockEffect threw: " + ex);
                return false;
            }
        }

        var data = MacroPadSdkNative.EffData.New(
            eff:        (MacroPadSdkNative.EffectIndex)effect,
            c1:         C(primary),
            c2:         secondary is { } s2 ? C(s2) : null,
            c3:         tertiary  is { } t ? C(t) : null,
            background: background is { } bg ? C(bg) : null,
            speed:      speedByte,
            bright:     bright,
            randomColor: randomColor);
        try
        {
            bool ok = MacroPadSdkNative.ChangeEffect(data, id);
            App.WriteLog($"[MacroPad.SetEffect] id={id} eff={effect} speed={speedByte} bright={bright} -> {ok}");
            App.WriteLog("[MacroPad.SetEffect] DUMP EffData(62B): " + DumpEffData(data));

            try
            {
                bool flashOk = MacroPadSdkNative.SaveFlash(6, id); // 6 = ALL_PROFILE
                App.WriteLog($"[MacroPad.SetEffect] SaveFlash(ALL,id={id}) (commit) -> {flashOk}");
            }
            catch (Exception ex2) { App.WriteLog("[MacroPad.SetEffect] SaveFlash threw: " + ex2); }

            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetEffect] threw: " + ex);
            return false;
        }
    }

    /// <summary>Hex-dump of the EffData struct's bytes (diagnostics).</summary>
    private static string DumpEffData(MacroPadSdkNative.EffData d)
    {
        int sz = Marshal.SizeOf<MacroPadSdkNative.EffData>();
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

    /// <summary>Hex-dump of the BlockData struct's bytes (diagnostics). Uses
    /// <c>sizeof()</c>/pointer walk, not <c>Marshal.SizeOf</c>, because the
    /// struct has a <c>fixed byte</c> buffer (mirrors EverestService.DumpBlockData).</summary>
    private static unsafe string DumpBlockData(MacroPadSdkNative.BlockData d)
    {
        int sz = sizeof(MacroPadSdkNative.BlockData);
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

    /// <summary>Resets the slot's effects to the firmware default.</summary>
    public bool ResetEffects(uint id)
    {
        try
        {
            bool ok = MacroPadSdkNative.ResetEffects(id);
            App.WriteLog($"[MacroPad.ResetEffects] id={id} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.ResetEffects] threw: " + ex);
            return false;
        }
    }

    /// <summary>Enables/disables syncing the effect across all profiles.</summary>
    public bool SetSyncAcrossProfiles(uint id, bool enable)
    {
        try
        {
            bool ok = MacroPadSdkNative.SetSyncAcrossProfiles(enable, id);
            App.WriteLog($"[MacroPad.SetSyncAcrossProfiles] id={id} enable={enable} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetSyncAcrossProfiles] threw: " + ex);
            return false;
        }
    }

    /// <summary>Reads the slot's current cross-profile sync state.</summary>
    public bool GetSyncAcrossProfiles(uint id)
    {
        try
        {
            bool enabled = false;
            return MacroPadSdkNative.GetSyncAcrossProfiles(ref enabled, id) && enabled;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.GetSyncAcrossProfiles] threw: " + ex);
            return false;
        }
    }

    /// <summary>Saves the current state to flash. Profile 1..5 or 6 = ALL_PROFILE.</summary>
    public bool SaveFlash(uint id, int profile = 6)
    {
        try
        {
            bool ok = MacroPadSdkNative.SaveFlash(profile, id);
            App.WriteLog($"[MacroPad.SaveFlash] id={id} profile={profile} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SaveFlash] threw: " + ex);
            return false;
        }
    }

    /// <summary>Turns the slot backlight on/off ("main" brightness).</summary>
    public bool SetBacklight(uint id, bool on)
    {
        try
        {
            bool ok = MacroPadSdkNative.SetMainBrightness(on, id);
            App.WriteLog($"[MacroPad.SetBacklight] id={id} on={on} -> {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            App.WriteLog("[MacroPad.SetBacklight] threw: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Quantizes 0..100 to the 5 firmware brightness steps (0/25/50/75/100):
    /// the firmware only accepts these values.
    /// </summary>
    private static MacroPadSdkNative.BrightT QuantizeBrightness(int pct)
    {
        if (pct <= 12) return MacroPadSdkNative.BrightT.B0;
        if (pct <= 37) return MacroPadSdkNative.BrightT.B25;
        if (pct <= 62) return MacroPadSdkNative.BrightT.B50;
        if (pct <= 87) return MacroPadSdkNative.BrightT.B75;
        return MacroPadSdkNative.BrightT.B100;
    }

    /// <summary>
    /// Must be invoked from the WndProc of the window that passed its own HWND to
    /// <see cref="Open"/>. Translates the <c>WM_DEVICE_PLUG</c> and
    /// <c>WM_FW_PROGRESS</c> messages into the corresponding .NET events.
    /// </summary>
    public void HandleWindowMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case MacroPadSdkNative.WM_DEVICE_PLUG:
            {
                int w = wParam.ToInt32();
                int l = lParam.ToInt32();
                App.WriteLog($"[MacroPad.WM_DEVICE_PLUG] wParam={w} lParam={l}");
                DevicePlug?.Invoke(this, new MacroPadPlugEventArgs(w, l));
                break;
            }
            case MacroPadSdkNative.WM_FW_PROGRESS:
            {
                int percent = wParam.ToInt32();
                App.WriteLog($"[MacroPad.WM_FW_PROGRESS] percent={percent}");
                FirmwareProgress?.Invoke(this, new MacroPadProgressEventArgs(percent));
                break;
            }
        }
    }

    public void Dispose() => Close();

    // ---- native callback (SDK thread) ----------------------------------

    private void OnKeyCallback(ushort wMatrix, bool bPressed, uint id)
    {
        try
        {
            App.WriteLog($"[MacroPad.Key] id={id} matrix={wMatrix} pressed={bPressed}");
            KeyEvent?.Invoke(this, new MacroPadKeyEventArgs(id, wMatrix, bPressed));
        }
        catch (Exception ex)
        {
            // Never let a managed exception propagate into native code.
            App.WriteLog("[MacroPad.OnKeyCallback] threw: " + ex);
        }
    }
}

/// <summary>Arguments for the <see cref="MacroPadService.KeyEvent"/> event.</summary>
public sealed class MacroPadKeyEventArgs : EventArgs
{
    public MacroPadKeyEventArgs(uint deviceId, ushort keyMatrix, bool pressed)
    {
        DeviceId = deviceId;
        KeyMatrix = keyMatrix;
        Pressed = pressed;
    }

    /// <summary>Slot of the device that raised the event.</summary>
    public uint DeviceId { get; }

    /// <summary>Key matrix index (firmware's physical index).</summary>
    public ushort KeyMatrix { get; }

    /// <summary>True = pressed, false = released.</summary>
    public bool Pressed { get; }
}

/// <summary>Arguments for the <see cref="MacroPadService.DevicePlug"/> event.</summary>
public sealed class MacroPadPlugEventArgs : EventArgs
{
    public MacroPadPlugEventArgs(int wParam, int lParam)
    {
        WParam = wParam;
        LParam = lParam;
    }

    /// <summary>Raw wParam of the <c>WM_DEVICE_PLUG</c> message.</summary>
    public int WParam { get; }

    /// <summary>Raw lParam of the <c>WM_DEVICE_PLUG</c> message.</summary>
    public int LParam { get; }
}

/// <summary>Arguments for the <see cref="MacroPadService.FirmwareProgress"/> event.</summary>
public sealed class MacroPadProgressEventArgs : EventArgs
{
    public MacroPadProgressEventArgs(int percent) => Percent = percent;

    /// <summary>Firmware update progress percentage (-1 = failed).</summary>
    public int Percent { get; }

    /// <summary>True if the update failed.</summary>
    public bool Failed => Percent == -1;
}
