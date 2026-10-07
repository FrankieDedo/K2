using System;
using System.Collections.Generic;
using System.Windows.Threading;
using K2.Core;

namespace K2.App;

/// <summary>
/// <see cref="IActionHost"/> adapter for the Makalu module — mirrors
/// <see cref="Ev60ActionHost"/>. The mouse runs most button functions itself
/// (firmware remap); this host exists for the buttons that carry a K2 action
/// instead, picked from the shared <see cref="ButtonActionDialog"/> and run
/// by <see cref="ButtonActionEngine"/> when the mouse reports the press
/// (MainWindow.Makalu.cs, OnMakaluButtonEvent).
/// </summary>
internal sealed class MkActionHost : IActionHost
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Func<int> _currentProfile;
    private readonly Func<int> _profileCount;
    private readonly Func<IReadOnlyList<HostButton>> _getButtons;
    private readonly Action<int> _pressButton;
    private readonly Action<string> _switchProfile;
    private readonly Func<string?> _configuredPythonPath;
    private readonly Func<IReadOnlyList<ProfileTargetOption>> _listAllProfileTargets;
    private readonly Action<string, string> _switchProfileByKey;
    private readonly Func<IReadOnlyList<string>> _listMacroNames;
    private readonly Action<string> _playMacro;
    private readonly ILightingController? _lighting;

    public MkActionHost(
        Dispatcher dispatcher,
        Action<string> log,
        Func<int> currentProfile,
        Func<int> profileCount,
        Func<IReadOnlyList<HostButton>> getButtons,
        Action<int> pressButton,
        Action<string> switchProfile,
        Func<string?> configuredPythonPath,
        Func<IReadOnlyList<ProfileTargetOption>> listAllProfileTargets,
        Action<string, string> switchProfileByKey,
        Func<IReadOnlyList<string>> listMacroNames,
        Action<string> playMacro,
        ILightingController? lighting = null)
    {
        _dispatcher            = dispatcher;
        _log                   = log;
        _currentProfile        = currentProfile;
        _profileCount          = profileCount;
        _getButtons            = getButtons;
        _pressButton           = pressButton;
        _switchProfile         = switchProfile;
        _configuredPythonPath  = configuredPythonPath;
        _listAllProfileTargets = listAllProfileTargets;
        _switchProfileByKey    = switchProfileByKey;
        _listMacroNames        = listMacroNames;
        _playMacro             = playMacro;
        _lighting              = lighting;
    }

    Dispatcher IActionHost.Dispatcher => _dispatcher;

    ILightingController? IActionHost.Lighting => _lighting;

    void IActionHost.Log(string message) => _log(message);

    // Makalu is single-device: conventional id = 1.
    int IActionHost.CurrentDevice => 1;

    int IActionHost.CurrentProfile => _currentProfile();

    int IActionHost.ProfileCount => _profileCount();

    int IActionHost.ButtonCount => _getButtons().Count;

    // No vendor SDK on K2's side — the Makalu is driven over raw HID.
    int IActionHost.SdkVersion => 0;

    string? IActionHost.ConfiguredPythonPath => _configuredPythonPath();

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        if (string.IsNullOrEmpty(targetKey)) _switchProfile(target);
        else _switchProfileByKey(targetKey, target);
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets() => _listAllProfileTargets();

    // Makalu is single-device: conventional key "makalu:1". It is not a "switch profile"
    // target (see MainWindow.ListAllProfileTargets) — the key names the mouse for the
    // lighting actions' "this device" (MainWindow.LightingActions.cs).
    string IActionHost.SelfTargetKey => "makalu:1";

    IReadOnlyList<HostButton> IActionHost.GetButtons() => _getButtons();

    void IActionHost.PressButton(int index) => _pressButton(index);

    IReadOnlyList<string> IActionHost.ListMacroNames() => _listMacroNames();

    void IActionHost.PlayMacro(string macroName) => _playMacro(macroName);

    // Makalu has no DisplayPad-page concept — see IActionHost.ListPages remarks.
    IReadOnlyList<(int PageId, string Name)> IActionHost.ListPages() => Array.Empty<(int, string)>();
    int? IActionHost.CreatePage(string name) => null;
    void IActionHost.RenamePage(int pageId, string name) { }
    bool IActionHost.SupportsPages => false;

    // The one host that offers the picker's "Makalu" category (firmware functions).
    bool IActionHost.SupportsMouseFirmwareActions => true;
}
