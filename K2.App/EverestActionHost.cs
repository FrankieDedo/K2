using System;
using System.Collections.Generic;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// <see cref="IActionHost"/> adapter for the Everest module.
///
/// MainWindow already implements <see cref="IActionHost"/> for the MacroPad,
/// so the Everest needs a separate host: this class exposes the Everest's
/// device-specific operations to the shared action engine (K2.Core),
/// receiving them as delegates from the Everest module of MainWindow.
/// </summary>
internal sealed class EverestActionHost : IActionHost
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly Func<int> _currentProfile;
    private readonly Func<int> _sdkVersion;
    private readonly Func<IReadOnlyList<HostButton>> _getButtons;
    private readonly Action<int> _pressButton;
    private readonly Action<string> _switchProfile;
    private readonly Func<string?> _configuredPythonPath;

    public EverestActionHost(
        Dispatcher dispatcher,
        Action<string> log,
        Func<int> currentProfile,
        Func<int> sdkVersion,
        Func<IReadOnlyList<HostButton>> getButtons,
        Action<int> pressButton,
        Action<string> switchProfile,
        Func<string?> configuredPythonPath)
    {
        _dispatcher           = dispatcher;
        _log                  = log;
        _currentProfile       = currentProfile;
        _sdkVersion           = sdkVersion;
        _getButtons           = getButtons;
        _pressButton          = pressButton;
        _switchProfile        = switchProfile;
        _configuredPythonPath = configuredPythonPath;
    }

    Dispatcher IActionHost.Dispatcher => _dispatcher;

    void IActionHost.Log(string message) => _log(message);

    // Everest is single-device: conventional id = 1.
    int IActionHost.CurrentDevice => 1;

    int IActionHost.CurrentProfile => _currentProfile();

    int IActionHost.ProfileCount => EverestService.ProfileCount;

    // Everest keys are dynamic (mapped on-demand): "count" is the
    // number of keys currently in the list.
    int IActionHost.ButtonCount => _getButtons().Count;

    int IActionHost.SdkVersion => _sdkVersion();

    string? IActionHost.ConfiguredPythonPath => _configuredPythonPath();

    void IActionHost.SwitchProfile(string target) => _switchProfile(target);

    IReadOnlyList<HostButton> IActionHost.GetButtons() => _getButtons();

    void IActionHost.PressButton(int index) => _pressButton(index);
}
