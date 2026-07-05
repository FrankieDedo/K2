using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// MainWindow partial: binds the MacroPad module to the shared action engine (K2.Core).
///
/// MainWindow acts as <see cref="IActionHost"/> for the MacroPad: exposes
/// device-specific operations to the engine. Device-agnostic actions (url,
/// exec, keys, pyscript, ...) are executed by the shared engine.
///
/// Note: MacroPad profile switching is not yet wired (native export missing in
/// the P/Invoke layer), so <see cref="IActionHost.SwitchProfile"/> only logs for now.
/// All other actions — including Python scripts — work.
/// </summary>
public partial class MainWindow : IActionHost
{
    private ButtonActionEngine? _engine;

    /// <summary>Creates and starts the shared action engine. Called from the constructor.</summary>
    private void InitActionEngine()
    {
        _engine = new ButtonActionEngine(this);
        _engine.Start();
        Closed += (_, _) => _engine?.Dispose();
    }

    // ---- IActionHost: MacroPad device-specific implementation ----

    Dispatcher IActionHost.Dispatcher => Dispatcher;

    void IActionHost.Log(string message) => Log(message);

    int IActionHost.CurrentDevice => CurrentDeviceId() ?? 0;

    int IActionHost.CurrentProfile => CurrentProfile();

    int IActionHost.ProfileCount => MacroPadService.ProfileCount;

    int IActionHost.ButtonCount => MacroPadService.ButtonCount;

    int IActionHost.SdkVersion => SafeSdkVersion();

    string? IActionHost.ConfiguredPythonPath => _store.GetSetting("python.exePath");

    void IActionHost.SwitchProfile(string target) => MpSwitchProfile(target);

    IReadOnlyList<HostButton> IActionHost.GetButtons()
        => _keys.Select(k => new HostButton(
               k.Index, k.KeyMatrix, false, null, k.ActionType, k.ActionValue))
           .ToList();

    void IActionHost.PressButton(int index)
    {
        if (index >= 0 && index < _keys.Length)
            TryExecuteAction(_keys[index]);
    }

    private int SafeSdkVersion()
    {
        try { return _macroPad.SdkVersion(); } catch { return 0; }
    }
}
