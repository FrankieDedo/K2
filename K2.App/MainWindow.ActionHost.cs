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
/// Also hosts <see cref="ListAllProfileTargets"/>/<see cref="SwitchProfileByKey"/>, the
/// shared cross-device "switch profile" dispatcher used by all three K2.App action-host
/// adapters (this class for MacroPad, <see cref="EverestActionHost"/>, <see cref="DisplayPadActionHost"/>) —
/// they all live in the same process/MainWindow instance, so no IPC is needed.
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

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        if (string.IsNullOrEmpty(targetKey)) MpSwitchProfile(null, target);
        else SwitchProfileByKey(targetKey, target);
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets() => ListAllProfileTargets();

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

    // ---- Cross-device "switch profile" dispatch (shared by all 3 IActionHost adapters
    // in this process: MainWindow itself for MacroPad, EverestActionHost, DisplayPadActionHost) ----

    /// <summary>
    /// All devices this K2.App instance can address for a "switch profile" action:
    /// the active MacroPad, every currently-known DisplayPad (per <see cref="_dpDeviceLabels"/>),
    /// and the Everest (single device). Only devices visible/known right now are listed —
    /// matches the rest of the UI (tabs/labels also only exist for known devices).
    /// </summary>
    internal IReadOnlyList<ProfileTargetOption> ListAllProfileTargets()
    {
        var list = new List<ProfileTargetOption>();

        if (_activeMpDeviceId is int mpId)
        {
            string label = TabMacroPad.Header as string ?? Loc.Get("tab_macropad");
            list.Add(new ProfileTargetOption($"macropad:{mpId}", label, _store.GetExistingProfiles(mpId)));
        }

        foreach (var (id, label) in _dpDeviceLabels)
            list.Add(new ProfileTargetOption($"displaypad:{id}", label, _dpStore.GetExistingProfiles(id)));

        string evLabel = TabEverest.Header as string ?? Loc.Get("tab_everest");
        list.Add(new ProfileTargetOption("everest:1", evLabel,
            Enumerable.Range(1, EverestService.ProfileCount).ToList()));

        return list;
    }

    /// <summary>Dispatches a "{kind}:{id}" target key (see <see cref="ListAllProfileTargets"/>) to the right device's switch-profile logic.</summary>
    internal void SwitchProfileByKey(string targetKey, string target)
    {
        var parts = targetKey.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int id))
        {
            Log($"[EXEC] profile: bad target key \"{targetKey}\"");
            return;
        }
        switch (parts[0])
        {
            case "macropad":   MpSwitchProfile(id, target); break;
            case "displaypad": DpSwitchProfile(id, target); break;
            case "everest":    EvSwitchProfile(target); break; // single device: id is always 1
            default:
                Log($"[EXEC] profile: unknown device kind \"{parts[0]}\"");
                break;
        }
    }
}
