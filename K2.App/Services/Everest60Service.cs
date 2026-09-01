using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Facade over <see cref="Everest60Protocol"/>/<see cref="Everest60HidNative"/>:
/// one find-open-send-close cycle per call (feature reports are stateless
/// control transfers — no persistent session needed, matching
/// BaseCampLinux's own open_device()/close() per command). Single-device,
/// no profile switching (not implemented — key/profile remapping needs the
/// firmware protocol, see <see cref="Everest60HidNative"/> remarks).
/// </summary>
internal sealed class Everest60Service
{
    private readonly Action<string> _log;

    /// <summary>Serializes every find-open-send-close cycle below. Added
    /// 2026-07-22 alongside <see cref="Everest60NumpadKeyPoller"/>: with
    /// three independent background pollers now hitting this same raw-HID
    /// channel (LED colors every 300ms, numpad key events every 100ms,
    /// numpad-presence status every 3s), a request from one thread could
    /// land between another thread's SET and GET — <see cref="Everest60HidNative.SendFeature"/>'s
    /// retry-until-echo-matches logic tolerates a one-off mismatch, but a
    /// real-hardware report showed the numpad-presence check occasionally
    /// losing that race and reading back the WRONG command's response,
    /// which <see cref="MainWindow"/>'s <c>Ev60RefreshStatus</c> then
    /// (correctly, given what it got back) interpreted as "no numpad
    /// present" for one 3s tick — the accessory visibly disappearing from
    /// the UI for a few seconds before the next tick re-detected it. A
    /// plain <c>lock</c> is enough: every call here is a short, synchronous,
    /// already-blocking HID round trip, never awaited.</summary>
    private readonly object _hidLock = new();

    /// <summary>Last device path/PID resolved by <see cref="Everest60HidNative.FindDevice"/>,
    /// reused directly (one CreateFile call) instead of re-enumerating the entire system HID
    /// tree on every single call. Added 2026-07-22: with the LED poller (300ms), numpad poller
    /// (100ms) and this class's own 3s status poll all going through FindDevice, its
    /// SetupDiGetClassDevsW walk-and-open-every-candidate was running roughly 14x/second —
    /// expensive enough on a machine with several HID devices to make the on-screen LED
    /// preview visibly stutter/stall (user report 2026-07-22). Cleared (so the next call falls
    /// back to a fresh FindDevice) whenever the cached path fails to open, e.g. an unplug/replug.
    /// Written under <see cref="_hidLock"/> everywhere for a consistent snapshot, but
    /// <see cref="IsConnected"/> only holds the lock for that snapshot (not the HID I/O itself)
    /// — see its own comment for why.</summary>
    private Everest60HidNative.FoundDevice? _cachedDevice;

    public Everest60Service(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>True if an Everest 60 is currently plugged in. Cheap enough to poll.
    /// Deliberately does NOT hold <see cref="_hidLock"/> for the actual HID I/O (only for the
    /// brief <see cref="_cachedDevice"/> snapshot/update) — this runs synchronously on the UI
    /// thread every 3s (MainWindow.Everest60.cs's Ev60RefreshStatus), so blocking it behind a
    /// LED/numpad poller's in-progress read would trade one stutter for another.</summary>
    public bool IsConnected(out string model)
    {
        Everest60HidNative.FoundDevice? cached;
        lock (_hidLock) { cached = _cachedDevice; }

        if (cached is { } c)
        {
            using var hc = Everest60HidNative.Open(c.Path, _log);
            if (hc != null && !hc.IsInvalid)
            {
                model = c.Pid == Everest60HidNative.PidIso ? "Everest 60 ISO" : "Everest 60";
                return true;
            }
            // stale (unplugged/path changed) — fall through to a fresh enumeration below
            // rather than touch _cachedDevice here, avoiding a race with a poller that may
            // have already refreshed it.
        }

        var found = Everest60HidNative.FindDevice(_log);
        model = found?.Pid == Everest60HidNative.PidIso ? "Everest 60 ISO" : "Everest 60";
        if (found is null) return false;
        lock (_hidLock) { _cachedDevice = found; }
        return true;
    }

    /// <summary>Diagnostic addition 2026-07-22: logs how long each call spent
    /// WAITING for <see cref="_hidLock"/> vs actually doing HID I/O, tagged
    /// by <paramref name="op"/> (e.g. "NumpadPosition") — added to
    /// narrow down a real-hardware report that the numpad still disappears
    /// from the UI (and, once, took several seconds to appear at startup)
    /// even after serializing HID access. If <c>lockWaitMs</c> is
    /// consistently near zero, the pollers aren't actually contending for
    /// the lock and the cause is elsewhere (e.g. the firmware itself, or
    /// <c>Everest60SdkService</c>'s separate vendor-DLL session touching the
    /// same physical device outside this lock entirely — see its remarks).
    /// Logs only when <paramref name="op"/> is supplied, so this stays a
    /// no-op for callers that don't care.</summary>
    private bool WithDevice(Action<SafeFileHandle> action, string? op = null)
    {
        var waitSw = op != null ? Stopwatch.StartNew() : null;
        bool entered = false;
        try
        {
            Monitor.Enter(_hidLock, ref entered);
            long lockWaitMs = waitSw?.ElapsedMilliseconds ?? 0;
            var ioSw = op != null ? Stopwatch.StartNew() : null;

            // Fast path: reuse the last resolved device instead of a fresh
            // FindDevice (full HID-tree enumeration) — see _cachedDevice's doc
            // comment. Falls through to the slow path below if the cached
            // handle no longer opens (unplug/replug).
            if (_cachedDevice is { } cached)
            {
                using var hc = Everest60HidNative.Open(cached.Path, _log);
                if (hc != null && !hc.IsInvalid)
                {
                    action(hc);
                    if (op != null)
                        _log($"[Ev60-HID] {op}: ok cached (lockWait={lockWaitMs}ms, io={ioSw!.ElapsedMilliseconds}ms)");
                    return true;
                }
                _cachedDevice = null;
            }

            var found = Everest60HidNative.FindDevice(_log);
            if (found is null)
            {
                _log("[Ev60] not connected");
                if (op != null) _log($"[Ev60-HID] {op}: not connected (lockWait={lockWaitMs}ms)");
                return false;
            }
            using var h = Everest60HidNative.Open(found.Value.Path, _log);
            if (h is null || h.IsInvalid)
            {
                _log("[Ev60] open failed");
                if (op != null) _log($"[Ev60-HID] {op}: open failed (lockWait={lockWaitMs}ms)");
                return false;
            }
            _cachedDevice = found;
            action(h);
            if (op != null)
                _log($"[Ev60-HID] {op}: ok (lockWait={lockWaitMs}ms, io={ioSw!.ElapsedMilliseconds}ms)");
            return true;
        }
        finally
        {
            if (entered) Monitor.Exit(_hidLock);
        }
    }

    /// <summary>Applies a preset effect. <paramref name="direction"/> is ignored
    /// unless the effect supports it (Wave/Tornado — see the UI's per-effect caps).</summary>
    public bool SetEffect(Everest60Protocol.Effect effect, int speedPct, int brightnessPct,
        (byte r, byte g, byte b) primary, (byte r, byte g, byte b)? secondary,
        bool rainbow, byte direction) =>
        SignalRgbGuard.BlockLighting("Ev60.SetEffect") ||
        WithDevice(h =>
        {
            var mode = rainbow ? Everest60Protocol.ColorMode.Rainbow
                     : secondary.HasValue ? Everest60Protocol.ColorMode.Dual
                     : Everest60Protocol.ColorMode.Single;
            var (r2, g2, b2) = secondary ?? ((byte)0, (byte)0, (byte)0);
            Everest60Protocol.SendMode(h, effect, speedPct, brightnessPct,
                primary.r, primary.g, primary.b, r2, g2, b2, mode, direction, _log);
        });

    /// <summary>
    /// "Game Mode" key-lock — see <see cref="Everest60Protocol.SetGameMode"/> for
    /// the wire format and the (Everest-Max-different) bit layout:
    /// bit0=Shift(+Tab), bit1=AltTab, bit2=AltF4, bit3=Win. Raw HID on interface 2,
    /// same channel as lighting — NOT the vendor SDK.
    /// </summary>
    public bool SetGameMode(int mask) =>
        WithDevice(h => Everest60Protocol.SetGameMode(h, mask, _log), op: "SetGameMode");

    /// <summary>Core / indicator LEDs on-off — see <see cref="Everest60Protocol.SetCoreLed"/>.</summary>
    public bool SetCoreLed(bool on) =>
        WithDevice(h => Everest60Protocol.SetCoreLed(h, on, _log), op: "SetCoreLed");

    /// <summary>
    /// Full Custom-mode apply: 64 main-board keys + 44 border-ring LEDs + 17
    /// numpad accessory keys + 22 numpad-ring LEDs, all in one combined wire
    /// command — the "Custom Lighting" system ported from Everest Max
    /// (2026-07-24), which paints keycaps and the side ring together. Any
    /// array may be all-black (unpainted positions), same convention as
    /// Everest Max's ApplyEverestCustomLighting.
    /// </summary>
    public bool SetCustomLighting(IReadOnlyList<(byte r, byte g, byte b)> keys,
        IReadOnlyList<(byte r, byte g, byte b)> sideColors,
        IReadOnlyList<(byte r, byte g, byte b)> numpadColors,
        IReadOnlyList<(byte r, byte g, byte b)> numpadRingColors,
        int brightnessPct) =>
        SignalRgbGuard.BlockLighting("Ev60.SetCustomLighting") ||
        WithDevice(h => Everest60Protocol.SendCustom(h, keys, brightnessPct, sideColors, numpadColors, numpadRingColors, _log));

    /// <summary>
    /// Live LED-color readback, over the SAME raw-HID channel as the lighting
    /// writes above (not the vendor SDK — see <see cref="Everest60Protocol.ReadColorData"/>
    /// for why: <c>Everest60SdkNative.GetColorData2</c> was found to reliably
    /// fail whenever a Makalu mouse is also connected, while this raw-HID
    /// path kept working in every test). Powers <see cref="Everest60LedColorPoller"/>.
    /// </summary>
    public bool TryGetColorData(EverestSdkNative.FWColor[] colors)
    {
        bool ok = false;
        WithDevice(h => ok = Everest60Protocol.ReadColorData(h, colors, _log), op: "LedColorRead");
        return ok;
    }

    /// <summary>Numpad accessory position, over raw HID (see
    /// Everest60Protocol.ReadNumpadPosition's doc comment — the SDK's
    /// GetSubDeviceInfo was found to reliably fail with a Makalu also
    /// connected). Returns null if the main keyboard itself isn't reachable
    /// or the read failed.</summary>
    public Ev60NumpadPosition? TryGetNumpadPosition()
    {
        Ev60NumpadPosition? pos = null;
        bool reached = WithDevice(h => pos = Everest60Protocol.ReadNumpadPosition(h, _log), op: "NumpadPosition");
        _log($"[Ev60-NumpadPosition] reached={reached} pos={pos?.ToString() ?? "null"}");
        return pos;
    }

    /// <summary>Binds a numpad accessory key on the device (see
    /// <see cref="Everest60Protocol.NumpadKeyBinding"/> for the write
    /// sequence/protocol notes, incl. why <paramref name="label"/> doesn't
    /// need to mean anything to Base Camp). Call once per key when the user
    /// assigns a K2Action to it, and again for every already-bound numpad
    /// key on profile load/device reconnect (the firmware doesn't persist
    /// this across a replug, same reason lighting needs re-applying).</summary>
    public bool WriteNumpadKeyBinding(int dllKeyId, string label) =>
        WithDevice(h =>
        {
            Everest60Protocol.NumpadKeyBinding.WriteKeyActionType(h, dllKeyId, Everest60Protocol.NumpadBoundMarker, _log);
            Everest60Protocol.NumpadKeyBinding.WriteKeyActionParam(h, label, _log);
            Everest60Protocol.NumpadKeyBinding.CommitKeyBinding(h, _log);
        }, op: "WriteNumpadKeyBinding");

    /// <summary>Switches a MAIN-BOARD key off in firmware, or puts it back to its
    /// factory function — see <see cref="Everest60Protocol.MainKeyBinding"/>. Unlike
    /// every other Key Binding operation on this board (which execute purely in K2
    /// software off the SDK key callback), this one has to reach the firmware: nothing
    /// host-side can stop the keyboard from typing.</summary>
    public bool SetMainKeyDisabled(int dllKeyId, bool disabled) =>
        WithDevice(h =>
        {
            if (disabled) Everest60Protocol.MainKeyBinding.DisableKey(h, dllKeyId, _log);
            else          Everest60Protocol.MainKeyBinding.RestoreKey(h, dllKeyId, _log);
        }, op: disabled ? "DisableMainKey" : "RestoreMainKey");

    /// <summary>Restores a numpad accessory key to its factory (unassigned,
    /// literal-keystroke) state — see
    /// <see cref="Everest60Protocol.NumpadKeyBinding"/>'s doc comment.</summary>
    public bool UnassignNumpadKey(int dllKeyId) =>
        WithDevice(h => Everest60Protocol.NumpadKeyBinding.UnassignKey(h, dllKeyId, _log), op: "UnassignNumpadKey");

    /// <summary>Polls for a numpad accessory key press/release event (see
    /// <see cref="Everest60Protocol.NumpadKeyBinding.QueryNumpadKeyEvent"/>).
    /// Powers <see cref="Everest60NumpadKeyPoller"/>.</summary>
    public (int Counter, int DllKeyId, bool Pressed)? TryQueryNumpadKeyEvent()
    {
        (int Counter, int DllKeyId, bool Pressed)? result = null;
        WithDevice(h => result = Everest60Protocol.NumpadKeyBinding.QueryNumpadKeyEvent(h, _log), op: "NumpadKeyEventPoll");
        return result;
    }
}
