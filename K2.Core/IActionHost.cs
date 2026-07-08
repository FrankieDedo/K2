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
/// One device a "switch profile" action can target, offered by <see cref="IActionHost.ListProfileTargets"/>.
/// <see cref="Key"/> format is <c>"{kind}:{id}"</c> (e.g. <c>"macropad:1"</c>, <c>"displaypad:2"</c>,
/// <c>"everest:1"</c>); <see cref="Profiles"/> lists that specific device's existing profile numbers.
/// </summary>
public sealed record ProfileTargetOption(string Key, string Label, IReadOnlyList<int> Profiles);

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

    /// <summary>
    /// Switch profile. <paramref name="targetKey"/> is null/"" for "the device this button
    /// lives on" (self-target, the only mode that existed before cross-device targeting was
    /// added), or one of the keys returned by <see cref="ListProfileTargets"/> to target a
    /// different device. <paramref name="target"/> = "Next" | "Previous" | "1".."N".
    /// </summary>
    void SwitchProfile(string? targetKey, string target);

    /// <summary>
    /// Devices this host can target with <see cref="SwitchProfile"/> besides "self" (used to
    /// populate the "switch profile" action picker). K2.App (unified shell) returns MacroPad +
    /// DisplayPad + Everest entries; the standalone K2.DisplayPad returns only its own DisplayPad
    /// devices.
    /// </summary>
    IReadOnlyList<ProfileTargetOption> ListProfileTargets();

    /// <summary>Button states for the current profile (for the <c>get_buttons</c> API).</summary>
    IReadOnlyList<HostButton> GetButtons();

    /// <summary>Executes the action configured on button <paramref name="index"/>.</summary>
    void PressButton(int index);

    /// <summary>
    /// Names of the macros available to assign as a "macro" action (used to populate
    /// <see cref="ButtonActionDialog"/>'s macro picker). K2.App (unified shell) returns the
    /// shared macro library; hosts with no macro concept (the standalone K2.DisplayPad)
    /// return an empty list.
    /// </summary>
    IReadOnlyList<string> ListMacroNames();

    /// <summary>Plays back the macro named <paramref name="macroName"/> (see <see cref="ListMacroNames"/>).</summary>
    void PlayMacro(string macroName);

    /// <summary>
    /// DisplayPad sub-pages ("dp_folder" targets) existing for the currently selected
    /// device+profile, used to populate the "Page" action type's picker in
    /// <see cref="ButtonActionDialog"/> (both to assign an existing page and to show/rename
    /// the current one). Hosts with no DisplayPad-page concept (MacroPad, Everest, the
    /// standalone K2.DisplayPad app) return an empty list.
    /// </summary>
    IReadOnlyList<(int PageId, string Name)> ListPages();

    /// <summary>
    /// Creates a new DisplayPad sub-page named <paramref name="name"/> for the currently
    /// selected device+profile and returns its page ID, or null on a host with no
    /// DisplayPad-page concept (see <see cref="ListPages"/>).
    /// </summary>
    int? CreatePage(string name);

    /// <summary>Renames an existing page (no-op on hosts with no DisplayPad-page concept).</summary>
    void RenamePage(int pageId, string name);

    /// <summary>True only for a host with a real DisplayPad-page concept (see <see cref="ListPages"/>) —
    /// <see cref="ButtonActionDialog"/> hides the "Page" action type entirely for hosts that
    /// return false, rather than showing it non-functional/empty like "macro" does.</summary>
    bool SupportsPages { get; }
}
