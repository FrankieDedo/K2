using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using K2.App.Models;
using K2.Core;

namespace K2.App;

/// <summary>
/// <see cref="IActionHost"/> adapter for the DisplayPad tab integrated in K2.App.
/// </summary>
internal sealed class DisplayPadActionHost : IActionHost
{
    private readonly MainWindow _win;

    public DisplayPadActionHost(MainWindow win) => _win = win;

    Dispatcher IActionHost.Dispatcher => _win.Dispatcher;
    void IActionHost.Log(string message) => _win.Dispatcher.Invoke(() => _win.DpLogPublic(message));
    int IActionHost.CurrentDevice => _win._activeDpDeviceId ?? 0;
    int IActionHost.CurrentProfile => _win.CbDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : 1;
    int IActionHost.ProfileCount => 5;
    int IActionHost.ButtonCount => 12;
    int IActionHost.SdkVersion => 0; // not relevant over IPC
    string? IActionHost.ConfiguredPythonPath => null;

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        _win.Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(targetKey)) _win.DpSwitchProfile(null, target);
            else _win.SwitchProfileByKey(targetKey, target);
        });
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets() => _win.ListAllProfileTargets();

    IReadOnlyList<HostButton> IActionHost.GetButtons() =>
        _win._dpKeys.Select(k => new HostButton(
            k.Index, k.KeyMatrix, k.HasImage, k.ImagePath, k.ActionType, k.ActionValue))
        .ToList();

    void IActionHost.PressButton(int index)
    {
        if (index >= 0 && index < _win._dpKeys.Length)
            _win._dpEngine?.Execute(_win._dpKeys[index].ActionType,
                                     _win._dpKeys[index].ActionValue, index);
    }

}
