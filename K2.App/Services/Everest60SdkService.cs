using System;
using System.Runtime.InteropServices;

namespace K2.App.Services;

/// <summary>
/// Application facade over <see cref="Everest60SdkNative"/> — the vendor SDK
/// path used for key remap (ChangeKey/ChangeFnKey/ChangeShortcutKey/
/// SetSingleMacroContent), key-press capture, AND live LED-color readback
/// (<see cref="TryGetColorData"/>, powering <see cref="Everest60LedColorPoller"/>).
/// Lighting WRITES stay on the raw-HID path (<see cref="Everest60Service"/>/
/// <see cref="Everest60Protocol"/>) — see <see cref="Everest60SdkNative"/>'s
/// remarks for why the two paths split; color READBACK has no raw-HID
/// equivalent (never reverse-engineered), so it necessarily goes through
/// this SDK session instead.
///
/// <para>
/// Mirrors <see cref="EverestService"/>'s open-once-keep-open shape (not
/// <see cref="Everest60Service"/>'s per-call find-open-send-close shape):
/// the SDK DLL manages its own persistent USB session internally once
/// <c>OpenUSBDriver</c> succeeds, same as it does for the Everest Max.
/// </para>
///
/// <para>
/// <b>Unverified on real hardware</b> (2026-07-11, no Everest 60 connected in
/// the session that wrote this): whether holding this SDK session open at the
/// same time <see cref="Everest60Service"/> makes its brief per-call raw-HID
/// bursts (for lighting) causes any contention on the underlying USB
/// interface. Both target the same physical device but different logical
/// paths (vendor DLL vs raw Feature Reports on interface 2) — needs a real
/// device to confirm they don't collide.
/// </para>
/// </summary>
/// <summary>Numpad accessory position, from Everest60SdkNative.GetSubDeviceInfo.</summary>
internal enum Ev60NumpadPosition
{
    None = 0,
    Left = 1,
    Right = 2,
}

internal sealed class Everest60SdkService : IDisposable
{
    private Everest60SdkNative.KEY_CALLBACK? _keyCallback;
    private bool _opened;

    /// <summary>Real top-level window handle, passed to <c>OpenUSBDriver</c>.
    /// Set by <see cref="Open"/> — see its remarks for why this must be a
    /// real HWND, not IntPtr.Zero.</summary>
    private IntPtr _hWnd;

    /// <summary>Physical key pressed or released, reported via the SDK's
    /// callback (same mechanism as EverestService.KeyEvent) — used to let the
    /// user "capture" a key by pressing it instead of hunting through a list.</summary>
    public event EventHandler<(ushort WMatrix, bool Pressed, uint Id)>? KeyEvent;

    public bool IsOpen => _opened;

    /// <summary>Opens the SDK driver and registers the key callback.
    /// <paramref name="hWnd"/> MUST be a real top-level window handle, not
    /// IntPtr.Zero — 2026-07-12, real-hardware log showed OpenUSBDriver
    /// intermittently returning true with IntPtr.Zero, but APEnable/
    /// EnableKeyFunc/GetSubDeviceInfo returning false on every single call
    /// afterwards, even right after a successful Open. Unlike
    /// EverestSdkNative.OpenUSBDriver() (Everest Max, verified parameterless
    /// via ECMA metadata dump), Everest60SdkNative.OpenUSBDriver DOES take an
    /// IntPtr (also confirmed via metadata) — consistent with this SDK using
    /// an internal hidden-window message pump for device-plug notifications
    /// (see CHANGELOG 2026-07-11's EV60MessageHandler/message 0x5401 finding),
    /// which likely needs a real HWND to finish initializing internally even
    /// though K2 itself doesn't consume those messages (connectivity is
    /// polled instead, same as Everest60Service.IsConnected). Same reasoning
    /// as why MacroPad/DisplayPad pass their real _hWnd to their own
    /// OpenUSBDriver.</summary>
    public bool Open(IntPtr hWnd, Action<string>? log = null)
    {
        _hWnd = hWnd;
        if (_opened) return true;

        _keyCallback = OnKeyCallback;
        try
        {
            Everest60SdkNative.SetKeyCallBack(_keyCallback);
            log?.Invoke("[Ev60SDK] SetKeyCallBack registered");
        }
        catch (Exception ex) { log?.Invoke("[Ev60SDK] SetKeyCallBack threw: " + ex); }

        _opened = DoOpenAndInit(log);
        return _opened;
    }

    /// <summary>
    /// OpenUSBDriver + the same post-open "warm-up" sequence Base Camp itself
    /// runs for the Everest Max right after opening (APEnable/EnableKeyFunc —
    /// see EverestService.Open's comment on why these aren't optional: some
    /// SDK calls return true but are silent no-ops on the wire without them).
    /// 2026-07-11: added after a real-hardware report that
    /// GetSubDeviceInfo/QueryNumpadPosition never detected an attached numpad
    /// when opened bare (no APEnable/EnableKeyFunc) — same class of bug as
    /// the Everest Max's ChangeEffect/GetColorData issues this mirrors.
    /// </summary>
    private bool DoOpenAndInit(Action<string>? log)
    {
        bool opened;
        try
        {
            opened = Everest60SdkNative.OpenUSBDriver(_hWnd);
        }
        catch (Exception ex)
        {
            log?.Invoke("[Ev60SDK] OpenUSBDriver threw: " + ex);
            return false;
        }
        log?.Invoke($"[Ev60SDK] OpenUSBDriver(0x{_hWnd.ToInt64():X}) -> {opened}");
        if (!opened) return false;

        try
        {
            bool ap = Everest60SdkNative.APEnable(true);
            bool ek = Everest60SdkNative.EnableKeyFunc(true);
            log?.Invoke($"[Ev60SDK] APEnable={ap} EnableKeyFunc={ek}");
        }
        catch (Exception ex) { log?.Invoke("[Ev60SDK] APEnable/EnableKeyFunc threw: " + ex); }

        return true;
    }

    public void Close()
    {
        if (!_opened) return;
        try { Everest60SdkNative.CloseUSBDriver(); } catch { /* best-effort */ }
        _opened = false;
    }

    public bool IsPlugged()
    {
        try { return Everest60SdkNative.IsDevicePlug(); }
        catch { return false; }
    }

    /// <summary>
    /// Polls whether the numpad accessory is attached and on which side.
    /// If a persistent session is already open (Key Binding section
    /// visited), reuses it; otherwise opens/closes its own brief session —
    /// deliberately NOT holding the SDK open continuously just for a status
    /// poll (same reasoning as the class doc comment's per-call vs open-once
    /// tradeoff, applied the other way here since this is called every few
    /// seconds from Ev60RefreshStatus, unlike Key Binding's occasional writes).
    /// </summary>
    public Ev60NumpadPosition QueryNumpadPosition(Action<string>? log = null)
    {
        bool weOpened = false;
        try
        {
            if (!_opened)
            {
                weOpened = DoOpenAndInit(log);
                if (!weOpened)
                {
                    log?.Invoke("[Ev60SDK] QueryNumpadPosition: open+init failed, skipping GetSubDeviceInfo");
                    return Ev60NumpadPosition.None;
                }
            }

            int fwVer = 0, position = 0;
            bool ok = Everest60SdkNative.GetSubDeviceInfo(1, ref fwVer, ref position);
            log?.Invoke($"[Ev60SDK] GetSubDeviceInfo(1) -> ok={ok} fwVer={fwVer} position={position}");
            if (!ok)
                log?.Invoke("[Ev60SDK] GetSubDeviceInfo returned false — device may not support/expose this sub-device index, or the SDK isn't fully warmed up yet");
            return ok && position is 1 or 2 ? (Ev60NumpadPosition)position : Ev60NumpadPosition.None;
        }
        catch (Exception ex)
        {
            log?.Invoke("[Ev60SDK] QueryNumpadPosition threw: " + ex);
            return Ev60NumpadPosition.None;
        }
        finally
        {
            if (weOpened) { try { Everest60SdkNative.CloseUSBDriver(); } catch { /* best-effort */ } }
        }
    }

    /// <summary>Remaps a physical key (main layer) to another key's identity.
    /// targetDllKeyId = 255 resets/disables the key.</summary>
    public bool ChangeKey(int srcDllKeyId, int targetDllKeyId)
    {
        try { return Everest60SdkNative.ChangeKey(srcDllKeyId, targetDllKeyId); }
        catch { return false; }
    }

    /// <summary>Remaps the Fn-layer function of a physical key.</summary>
    public bool ChangeFnKey(int srcDllKeyId, int targetDllKeyId)
    {
        try { return Everest60SdkNative.ChangeFnKey(srcDllKeyId, targetDllKeyId); }
        catch { return false; }
    }

    /// <summary>Binds a physical key to a modifier+key shortcut.
    /// modifierMask: ctrl=1, shift=2, alt=4, win=8 (combinable).</summary>
    public bool ChangeShortcutKey(int srcDllKeyId, int targetDllKeyId, int modifierMask)
    {
        try { return Everest60SdkNative.ChangeShortcutKey(srcDllKeyId, targetDllKeyId, modifierMask); }
        catch { return false; }
    }

    /// <summary>Binds a physical key to a media/OS action (type=3, code 1-7).</summary>
    public bool SetMediaKey(int srcDllKeyId, int code)
    {
        try { return Everest60SdkNative.SetSingleMacroContent(srcDllKeyId, 3, code, 0); }
        catch { return false; }
    }

    /// <summary>Number of RGB entries in a <see cref="TryGetColorData"/> buffer
    /// (576-byte wire buffer / 3 bytes per FWColor).</summary>
    public const int ColorEntryCount = Everest60SdkNative.ColorBufferSize / 3;

    /// <summary>
    /// Reads back the keyboard's current LED colors. <paramref name="colors"/>
    /// must have exactly <see cref="ColorEntryCount"/> (192) elements, indexed
    /// by firmware LED hardware address — see <see cref="Everest60SdkNative.GetColorData2"/>
    /// for the decompile trace this mirrors 1:1 (AllocHGlobal(576) →
    /// GetColorData2(ptr,576) → copy out → FreeHGlobal). Returns false (buffer
    /// left unchanged) if the SDK isn't open or the call fails.
    /// </summary>
    private DateTime _lastColorDataFailLog = DateTime.MinValue;

    public bool TryGetColorData(EverestSdkNative.FWColor[] colors)
    {
        if (!_opened || colors.Length != ColorEntryCount) return false;

        IntPtr buf = Marshal.AllocHGlobal(Everest60SdkNative.ColorBufferSize);
        try
        {
            bool ok = Everest60SdkNative.GetColorData2(buf, Everest60SdkNative.ColorBufferSize);
            if (!ok)
            {
                // Throttled (poller ticks every 60ms — an unthrottled log here
                // would flood K2.App.log): logs at most once/sec, same pattern
                // as OnEv60ColorsUpdated's unknown-LED diagnostic.
                if (DateTime.UtcNow - _lastColorDataFailLog > TimeSpan.FromSeconds(1))
                {
                    _lastColorDataFailLog = DateTime.UtcNow;
                    App.WriteLog("[Ev60SDK] GetColorData2 returned false");
                }
                return false;
            }

            var raw = new byte[Everest60SdkNative.ColorBufferSize];
            Marshal.Copy(buf, raw, 0, raw.Length);
            for (int i = 0; i < ColorEntryCount; i++)
                colors[i] = new EverestSdkNative.FWColor(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
            return true;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - _lastColorDataFailLog > TimeSpan.FromSeconds(1))
            {
                _lastColorDataFailLog = DateTime.UtcNow;
                App.WriteLog("[Ev60SDK] GetColorData2 threw: " + ex.Message);
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Resets all key bindings to factory defaults.</summary>
    public bool ResetKeys()
    {
        try { return Everest60SdkNative.ResetKeys(); }
        catch { return false; }
    }

    /// <summary>Persists the current key bindings to the keyboard's flash.</summary>
    public bool SaveFlash()
    {
        try { return Everest60SdkNative.SaveFlash(0); }
        catch { return false; }
    }

    private void OnKeyCallback(ushort wMatrix, bool bPressed, uint id)
    {
        try { KeyEvent?.Invoke(this, (wMatrix, bPressed, id)); }
        catch (Exception ex) { App.WriteLog("[Ev60SDK.OnKeyCallback] threw: " + ex); }
    }

    public void Dispose() => Close();
}
