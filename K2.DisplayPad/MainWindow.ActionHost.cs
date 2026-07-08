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

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        // Standalone K2.DisplayPad only ever addresses its own DisplayPad devices, so the
        // "kind" prefix from ListProfileTargets is always "displaypad" here; just take the id.
        int? id = null;
        if (!string.IsNullOrEmpty(targetKey))
        {
            var parts = targetKey.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsed)) id = parsed;
        }
        ExecuteProfileSwitch(id, target);
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets()
        => _deviceIds.Select(id => new ProfileTargetOption(
               $"displaypad:{id}", $"DisplayPad {id}",
               Enumerable.Range(1, DisplayPadService.ProfileCount).ToList()))
           .ToList();

    IReadOnlyList<HostButton> IActionHost.GetButtons()
        => _cells.Select(c => new HostButton(
               c.Index, c.KeyMatrix, c.HasImage, c.ImagePath, c.ActionType, c.ActionValue))
           .ToList();

    void IActionHost.PressButton(int index)
    {
        if (index >= 0 && index < _cells.Length)
            TryExecuteAction(_cells[index]);
    }

    // The standalone K2.DisplayPad has no macro library (that concept lives in the
    // unified K2.App shell) — no macros to offer, and nothing to play back.
    IReadOnlyList<string> IActionHost.ListMacroNames() => System.Array.Empty<string>();

    void IActionHost.PlayMacro(string macroName) => Log($"[EXEC] macro: not supported in standalone K2.DisplayPad (\"{macroName}\")");
}
