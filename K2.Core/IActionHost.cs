using System.Collections.Generic;
using System.Windows.Threading;

namespace K2.Core;

/// <summary>Button state, exposed to Python scripts via the <c>get_buttons</c> API.</summary>
public sealed record HostButton(
    int     Index,
    int?    KeyMatrix,
    bool    HasImage,
    string? ImagePath,
    string? ActionType,
    string? ActionValue);

/// <summary>
/// Abstraction of the "device host" for the shared action engine.
///
/// Each K2 device module (DisplayPad, MacroPad, Everest, ...) implements
/// this interface to expose device-specific operations (profile switching,
/// state, software button press). Device-agnostic actions (url, exec, keys,
/// oscmd, pyscript, ...) are handled directly by <see cref="ButtonActionEngine"/>.
///
/// All members are invoked on the UI thread.
/// </summary>
public interface IActionHost
{
    /// <summary>UI dispatcher of the host app (for marshalling RPC calls).</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>Writes a line to the app log.</summary>
    void Log(string message);

    /// <summary>ID of the currently selected device (0 = none).</summary>
    int CurrentDevice { get; }

    /// <summary>Currently selected profile (1..<see cref="ProfileCount"/>).</summary>
    int CurrentProfile { get; }

    /// <summary>Number of profiles stored on the device.</summary>
    int ProfileCount { get; }

    /// <summary>Number of buttons on the device.</summary>
    int ButtonCount { get; }

    /// <summary>Native SDK version for the device.</summary>
    int SdkVersion { get; }

    /// <summary>Manually configured Python interpreter path (null = autodetect).</summary>
    string? ConfiguredPythonPath { get; }

    /// <summary>Switch profile: <paramref name="target"/> = "Next" | "Previous" | "1".."N".</summary>
    void SwitchProfile(string target);

    /// <summary>Button states for the current profile (for the <c>get_buttons</c> API).</summary>
    IReadOnlyList<HostButton> GetButtons();

    /// <summary>Executes the action configured on button <paramref name="index"/>.</summary>
    void PressButton(int index);
}
