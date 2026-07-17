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
    int IActionHost.CurrentProfile => _win.LstDpProfile.SelectedItem is DpProfileItem pi ? pi.Slot : 1;
    // See DisplayPadBackgroundActionHost.ProfileCount below for the rationale.
    int IActionHost.ProfileCount => _win._activeDpDeviceId is int id
        ? System.Math.Max(5, _win._dpStore.GetExistingProfiles(id).DefaultIfEmpty(0).Max())
        : 5;
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

    IReadOnlyList<string> IActionHost.ListMacroNames() => _win.ListAllMacroNames();

    void IActionHost.PlayMacro(string macroName) => _win.PlayMacroByName(macroName);

    IReadOnlyList<(int PageId, string Name)> IActionHost.ListPages() =>
        _win._activeDpDeviceId is int id
            ? _win.DpListPages(id, ((IActionHost)this).CurrentProfile)
            : System.Array.Empty<(int, string)>();

    int? IActionHost.CreatePage(string name) =>
        _win._activeDpDeviceId is int id
            ? _win.DpCreatePage(id, ((IActionHost)this).CurrentProfile, name)
            : null;

    void IActionHost.RenamePage(int pageId, string name) => _win.DpRenamePage(pageId, name);

    bool IActionHost.SupportsPages => true;
}

/// <summary>
/// <see cref="IActionHost"/> for a connected DisplayPad that is NOT the current foreground
/// tab (see <c>MainWindow.DisplayPad.cs</c>'s <c>DpHandleBackgroundKey</c>/
/// <c>DpActivateBackgroundDevice</c>). Unlike <see cref="DisplayPadActionHost"/> — which
/// reflects whichever single device tab happens to be visible via <c>_activeDpDeviceId</c>
/// and UI-bound controls — every member here is pinned to one fixed <see cref="_deviceId"/>
/// and reads straight from the SQLite store, so an action fired by THIS pad's own physical
/// key press never bleeds into whatever device the user is currently looking at.
/// </summary>
internal sealed class DisplayPadBackgroundActionHost : IActionHost
{
    private readonly MainWindow _win;
    private readonly int _deviceId;

    public DisplayPadBackgroundActionHost(MainWindow win, int deviceId)
    {
        _win = win;
        _deviceId = deviceId;
    }

    Dispatcher IActionHost.Dispatcher => _win.Dispatcher;
    void IActionHost.Log(string message) => _win.Dispatcher.Invoke(() => _win.DpLogPublic($"[bg {_deviceId}] {message}"));
    int IActionHost.CurrentDevice => _deviceId;
    int IActionHost.CurrentProfile => _win._dpStore.GetCurrentProfile(_deviceId);
    // No firmware slot cap for DisplayPad (see DpSwitchProfile's doc comment) — reflect
    // however many profiles actually exist, floored at 5 for parity with the other
    // devices' real firmware limit, so the "switch to profile N" action picker
    // (ButtonActionDialog.Profile.cs) always offers at least the classic 5.
    int IActionHost.ProfileCount => System.Math.Max(5, _win._dpStore.GetExistingProfiles(_deviceId).DefaultIfEmpty(0).Max());
    int IActionHost.ButtonCount => 12;
    int IActionHost.SdkVersion => 0;
    string? IActionHost.ConfiguredPythonPath => null;

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        _win.Dispatcher.Invoke(() =>
        {
            // Self-target must explicitly name THIS device: passing null (like the foreground
            // host does) would fall back to whichever tab the user has open (see DpSwitchProfile).
            if (string.IsNullOrEmpty(targetKey)) _win.DpSwitchProfile(_deviceId, target);
            else _win.SwitchProfileByKey(targetKey, target);
        });
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets() => _win.ListAllProfileTargets();

    IReadOnlyList<HostButton> IActionHost.GetButtons()
    {
        int profile = _win._dpStore.GetCurrentProfile(_deviceId);
        return _win._dpStore.LoadPage(_deviceId, profile, 0)
            .Select(r => new HostButton(r.ButtonIndex, null,
                !string.IsNullOrEmpty(r.ImagePath), r.ImagePath, r.ActionType, r.ActionValue))
            .ToList();
    }

    void IActionHost.PressButton(int index)
    {
        int profile = _win._dpStore.GetCurrentProfile(_deviceId);
        var row = _win._dpStore.LoadPage(_deviceId, profile, 0).FirstOrDefault(r => r.ButtonIndex == index);
        if (row is not null) _win.DpEngineFor(_deviceId).Execute(row.ActionType, row.ActionValue, index);
    }

    IReadOnlyList<string> IActionHost.ListMacroNames() => _win.ListAllMacroNames();

    void IActionHost.PlayMacro(string macroName) => _win.PlayMacroByName(macroName);

    IReadOnlyList<(int PageId, string Name)> IActionHost.ListPages() =>
        _win.DpListPages(_deviceId, ((IActionHost)this).CurrentProfile);

    int? IActionHost.CreatePage(string name) =>
        _win.DpCreatePage(_deviceId, ((IActionHost)this).CurrentProfile, name);

    void IActionHost.RenamePage(int pageId, string name) => _win.DpRenamePage(pageId, name);

    bool IActionHost.SupportsPages => true;
}
