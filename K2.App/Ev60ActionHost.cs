using System;
using System.Collections.Generic;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// <see cref="IActionHost"/> adapter for the Everest 60 module — mirrors
/// <see cref="EverestActionHost"/> (Everest Max) exactly. MainWindow already
/// implements <see cref="IActionHost"/> for the MacroPad and has a separate
/// host for Everest Max, so Everest 60 needs its own too: this class exposes
/// the Everest 60's device-specific operations to the shared action engine
/// (K2.Core), receiving them as delegates from the Everest 60 module of
/// MainWindow.
/// </summary>
internal sealed class Ev60ActionHost : IActionHost
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Func<int> _currentProfile;
    private readonly Func<int> _profileCount;
    private readonly Func<int> _sdkVersion;
    private readonly Func<IReadOnlyList<HostButton>> _getButtons;
    private readonly Action<int> _pressButton;
    private readonly Action<string> _switchProfile;
    private readonly Func<string?> _configuredPythonPath;
    private readonly Func<IReadOnlyList<ProfileTargetOption>> _listAllProfileTargets;
    private readonly Action<string, string> _switchProfileByKey;
    private readonly Func<IReadOnlyList<string>> _listMacroNames;
    private readonly Action<string> _playMacro;

    public Ev60ActionHost(
        Dispatcher dispatcher,
        Action<string> log,
        Func<int> currentProfile,
        Func<int> profileCount,
        Func<int> sdkVersion,
        Func<IReadOnlyList<HostButton>> getButtons,
        Action<int> pressButton,
        Action<string> switchProfile,
        Func<string?> configuredPythonPath,
        Func<IReadOnlyList<ProfileTargetOption>> listAllProfileTargets,
        Action<string, string> switchProfileByKey,
        Func<IReadOnlyList<string>> listMacroNames,
        Action<string> playMacro)
    {
        _dispatcher             = dispatcher;
        _log                    = log;
        _currentProfile         = currentProfile;
        _profileCount           = profileCount;
        _sdkVersion             = sdkVersion;
        _getButtons             = getButtons;
        _pressButton            = pressButton;
        _switchProfile          = switchProfile;
        _configuredPythonPath   = configuredPythonPath;
        _listAllProfileTargets  = listAllProfileTargets;
        _switchProfileByKey     = switchProfileByKey;
        _listMacroNames         = listMacroNames;
        _playMacro              = playMacro;
    }

    Dispatcher IActionHost.Dispatcher => _dispatcher;

    void IActionHost.Log(string message) => _log(message);

    // Everest 60 is single-device: conventional id = 1.
    int IActionHost.CurrentDevice => 1;

    int IActionHost.CurrentProfile => _currentProfile();

    int IActionHost.ProfileCount => _profileCount();

    // Everest 60 keys are a fixed 64-key board, but only ones with an
    // assigned action live in the list — "count" is that list's size,
    // same convention as EverestActionHost.
    int IActionHost.ButtonCount => _getButtons().Count;

    int IActionHost.SdkVersion => _sdkVersion();

    string? IActionHost.ConfiguredPythonPath => _configuredPythonPath();

    void IActionHost.SwitchProfile(string? targetKey, string target)
    {
        if (string.IsNullOrEmpty(targetKey)) _switchProfile(target);
        else _switchProfileByKey(targetKey, target);
    }

    IReadOnlyList<ProfileTargetOption> IActionHost.ListProfileTargets() => _listAllProfileTargets();

    IReadOnlyList<HostButton> IActionHost.GetButtons() => _getButtons();

    void IActionHost.PressButton(int index) => _pressButton(index);

    IReadOnlyList<string> IActionHost.ListMacroNames() => _listMacroNames();

    void IActionHost.PlayMacro(string macroName) => _playMacro(macroName);

    // Everest 60 has no DisplayPad-page concept — see IActionHost.ListPages remarks.
    IReadOnlyList<(int PageId, string Name)> IActionHost.ListPages() => Array.Empty<(int, string)>();
    int? IActionHost.CreatePage(string name) => null;
    void IActionHost.RenamePage(int pageId, string name) { }
    bool IActionHost.SupportsPages => false;
}
