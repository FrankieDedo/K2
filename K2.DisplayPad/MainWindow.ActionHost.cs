using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using K2.Core;
using K2.DisplayPad.Services;

namespace K2.DisplayPad;

/// <summary>
/// MainWindow partial: hook into the shared action engine (K2.Core).
///
/// MainWindow acts as the <see cref="IActionHost"/> for the DisplayPad: it exposes
/// to the engine the operations that depend on the specific device (profile
/// switch, state, software button press). All device-agnostic action logic
/// (url, exec, keys, pyscript, ...) instead lives in K2.Core.
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

    // ---- IActionHost: DisplayPad device-specific implementation ----

    Dispatcher IActionHost.Dispatcher => Dispatcher;

    void IActionHost.Log(string message) => Log(message);

    int IActionHost.CurrentDevice => CbDevice.SelectedItem is int d ? d : 0;

    int IActionHost.CurrentProfile => CurrentProfile();

    int IActionHost.ProfileCount => DisplayPadService.ProfileCount;

    int IActionHost.ButtonCount => DisplayPadService.ButtonCount;

    int IActionHost.SdkVersion => SafeCall(() => _service.SdkVersion(), 0);

    string? IActionHost.ConfiguredPythonPath => _store.GetSetting("python.exePath");

    void IActionHost.SwitchProfile(string target) => ExecuteProfileSwitch(target);

    IReadOnlyList<HostButton> IActionHost.GetButtons()
        => _cells.Select(c => new HostButton(
               c.Index, c.KeyMatrix, c.HasImage, c.ImagePath, c.ActionType, c.ActionValue))
           .ToList();

    void IActionHost.PressButton(int index)
    {
        if (index >= 0 && index < _cells.Length)
            TryExecuteAction(_cells[index]);
    }
}
