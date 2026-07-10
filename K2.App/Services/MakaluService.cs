using System;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Facade over <see cref="MakaluProtocol"/>/<see cref="MakaluHidNative"/>:
/// one find-open-send-close cycle per call (feature reports are stateless
/// control transfers — no persistent session needed, matching
/// controller.py's own open_device()/close() per command). Single mouse,
/// no profile switching — buttons are remapped directly in firmware, there
/// is no per-key action interception (<see cref="K2.Core.IActionHost"/> does
/// not apply here).
/// </summary>
internal sealed class MakaluService
{
    private readonly Action<string> _log;

    public MakaluService(Action<string>? log = null) => _log = log ?? (_ => { });

    public enum Model { Makalu67, MakaluMax }

    /// <summary>Detected model + the button count/DPI floor that differ between them
    /// (controller.py: <c>detect_model()</c>/<c>REMAP_DEFAULTS_67</c>/<c>_MAX</c>).</summary>
    public readonly record struct DeviceInfo(Model Model, string Label, int ButtonCount, int DpiMin);

    public bool IsConnected(out DeviceInfo info)
    {
        var found = MakaluHidNative.FindDevice();
        if (found is null) { info = default; return false; }
        bool isMax = found.Value.Pid == MakaluHidNative.PidMakaluMax;
        info = isMax
            ? new DeviceInfo(Model.MakaluMax, "Makalu Max", 8, MakaluProtocol.DpiMinMax)
            : new DeviceInfo(Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);
        return true;
    }

    private bool WithDevice(Func<SafeFileHandle, bool> action)
    {
        var found = MakaluHidNative.FindDevice(_log);
        if (found is null) { _log("[Makalu] not connected"); return false; }
        using var h = MakaluHidNative.Open(found.Value.Path, _log);
        if (h is null || h.IsInvalid) { _log("[Makalu] open failed"); return false; }
        return action(h);
    }

    // ---------------------------------------------------------------
    // Lighting
    // ---------------------------------------------------------------

    public bool SetLighting(MakaluProtocol.Effect effect, (byte r, byte g, byte b) color,
        int brightnessPct, byte param1 = 0, byte param2 = 0, (byte r, byte g, byte b)? secondary = null) =>
        WithDevice(h => MakaluProtocol.SetLighting(h, effect, color.r, color.g, color.b, brightnessPct, param1, param2, secondary));

    public bool SetLightingCustom((byte r, byte g, byte b)[] leds, int brightnessPct) =>
        WithDevice(h => MakaluProtocol.SetLightingCustom(h, leds, brightnessPct));

    // ---------------------------------------------------------------
    // Device settings
    // ---------------------------------------------------------------

    public bool SetPollingRate(int hz)   => WithDevice(h => MakaluProtocol.SetPollingRate(h, hz));
    public bool SetDebounce(int ms)      => WithDevice(h => MakaluProtocol.SetDebounce(h, ms));
    public bool SetAngleSnapping(bool on) => WithDevice(h => MakaluProtocol.SetAngleSnapping(h, on));
    public bool SetLiftOff(bool high)    => WithDevice(h => MakaluProtocol.SetLiftOff(h, high));

    // ---------------------------------------------------------------
    // DPI
    // ---------------------------------------------------------------

    /// <summary>Reads the 5 DPI levels + active index (0-based) from the device.
    /// Returns null if not connected/read failed.</summary>
    public (int[] Levels, int Active)? GetDpi(int dpiMin)
    {
        (int[], int)? result = null;
        WithDevice(h =>
        {
            var r = MakaluProtocol.GetDpi(h, dpiMin);
            if (r is null) return false;
            result = r;
            return true;
        });
        return result;
    }

    public bool SetAllDpi(int[] dpiList, int activeLevel1Based, int dpiMin) =>
        WithDevice(h => MakaluProtocol.SetAllDpi(h, dpiList, activeLevel1Based, dpiMin));

    // ---------------------------------------------------------------
    // Button remap + sniper
    // ---------------------------------------------------------------

    public bool SetButtonRemap(int buttonIndex1Based, string functionName) =>
        WithDevice(h => MakaluProtocol.SetButtonRemap(h, buttonIndex1Based, functionName));

    public bool SetButtonSniper(int buttonIndex1Based, int sniperDpi, int dpiMin) =>
        WithDevice(h => MakaluProtocol.SetButtonSniper(h, buttonIndex1Based, sniperDpi, dpiMin));
}
