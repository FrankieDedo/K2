using System;
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

    public Everest60Service(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>True if an Everest 60 is currently plugged in. Cheap enough to poll.</summary>
    public bool IsConnected(out string model)
    {
        var found = Everest60HidNative.FindDevice();
        model = found?.Pid == Everest60HidNative.PidIso ? "Everest 60 ISO" : "Everest 60";
        return found != null;
    }

    private bool WithDevice(Action<SafeFileHandle> action)
    {
        var found = Everest60HidNative.FindDevice(_log);
        if (found is null) { _log("[Ev60] not connected"); return false; }
        using var h = Everest60HidNative.Open(found.Value.Path, _log);
        if (h is null || h.IsInvalid) { _log("[Ev60] open failed"); return false; }
        action(h);
        return true;
    }

    /// <summary>Applies a preset effect. <paramref name="direction"/> is ignored
    /// unless the effect supports it (Wave/Tornado — see the UI's per-effect caps).</summary>
    public bool SetEffect(Everest60Protocol.Effect effect, int speedPct, int brightnessPct,
        (byte r, byte g, byte b) primary, (byte r, byte g, byte b)? secondary,
        bool rainbow, byte direction) =>
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
    /// Lights the 44-LED perimeter ring in a single solid color. NOTE: the
    /// device only addresses the side ring through Custom mode, so this also
    /// switches the main 64 keys to Custom (dark, unless a future per-key
    /// editor sets them) — whatever preset effect was running on the main
    /// keyboard is replaced while the ring is active.
    /// </summary>
    public bool SetSideRing((byte r, byte g, byte b) color, int brightnessPct) =>
        WithDevice(h =>
        {
            var side = new (byte, byte, byte)[Everest60Protocol.SideLedIndex.Length];
            for (int i = 0; i < side.Length; i++) side[i] = color;
            var keys = new (byte, byte, byte)[Everest60Protocol.NumKeys]; // dark
            Everest60Protocol.SendCustom(h, keys, brightnessPct, side, _log);
        });
}
