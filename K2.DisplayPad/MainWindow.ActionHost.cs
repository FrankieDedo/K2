using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using K2.Core;
using K2.DisplayPad.Services;

namespace K2.DisplayPad;

/// <summary>
/// Partial della MainWindow: aggancio al motore azioni condiviso (K2.Core).
///
/// La MainWindow funge da <see cref="IActionHost"/> per il DisplayPad: espone
/// al motore le operazioni che dipendono dallo specifico dispositivo (cambio
/// profilo, stato, pressione software dei tasti). Tutta la logica delle azioni
/// device-agnostiche (url, exec, keys, pyscript, ...) vive invece in K2.Core.
/// </summary>
public partial class MainWindow : IActionHost
{
    private ButtonActionEngine? _engine;

    /// <summary>Crea e avvia il motore azioni condiviso. Chiamato dal costruttore.</summary>
    private void InitActionEngine()
    {
        _engine = new ButtonActionEngine(this);
        _engine.Start();
        Closed += (_, _) => _engine?.Dispose();
    }

    // ---- IActionHost: implementazione device-specific del DisplayPad ----

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
