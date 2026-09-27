using System;

namespace K2.Core;

/// <summary>
/// Splits a <see cref="KeyCombo"/> shortcut ("Win", "Ctrl + Shift + A") into the two things the
/// Everest 60's firmware remap commands take: a modifier mask and a single key name.
///
/// <para><b>Where this is used.</b> Normally a K2 binding is executed by K2: the key is silenced
/// in firmware and the action runs host-side when the press is reported. The Everest 60's
/// "save key bindings in the keyboard's memory" setting (Settings ▸ Everest 60) changes that for
/// the bindings that ARE just a keystroke — those are written into the keyboard's own remap table
/// with <c>ChangeKey</c>/<c>ChangeShortcutKey</c> and flashed, so they keep working with K2
/// closed. See <c>MainWindow.Everest60.cs</c>'s <c>PushEv60DisabledKeysToDevice</c>.</para>
///
/// <para>Only the split lives here, because it is device-agnostic; turning a key NAME into the
/// device's DLLKeyId needs the Everest 60 catalog in K2.App
/// (<c>Everest60RemapData.ResolveComboKeyName</c>).</para>
/// </summary>
public static class Ev60FirmwareRemap
{
    /// <summary>Modifier bits, matching the mask <c>ChangeShortcutKey</c> takes (and
    /// <c>Everest60RemapData.ModCtrl</c>/<c>ModShift</c>/<c>ModAlt</c>/<c>ModWin</c>).</summary>
    public const int ModCtrl = 1, ModShift = 2, ModAlt = 4, ModWin = 8;

    /// <summary>
    /// Splits a <see cref="KeyCombo"/> string into its modifier mask and its single non-modifier
    /// key name. <paramref name="keyName"/> comes back "" for a BARE modifier ("Alt"), which the
    /// caller turns into a plain key-to-key remap onto that modifier's own id — that is the
    /// "swap Alt and Win" case, and the firmware has no "shortcut with no key" form.
    /// </summary>
    public static (int Mask, string KeyName) Parse(string? value)
    {
        int mask = 0;
        string keyName = "";
        if (string.IsNullOrWhiteSpace(value)) return (0, "");

        foreach (var raw in value.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": mask |= ModCtrl; break;
                case "SHIFT": mask |= ModShift; break;
                case "ALT": mask |= ModAlt; break;
                case "WIN": case "GUI": case "META": case "CMD": mask |= ModWin; break;
                // Two non-modifier keys ("A+B") are not one key the firmware can emit: return a
                // name no key resolves to, so the caller leaves the binding to K2.
                default: keyName = keyName.Length == 0 ? part : "\0multi"; break;
            }
        }
        return (mask, keyName);
    }

    /// <summary>The single modifier a bare-modifier combination names, or "" when it names none
    /// or more than one. The firmware can remap a key ONTO one other key, so "Ctrl + Shift" with
    /// no key has no representation and the caller leaves that binding to K2.</summary>
    public static string SoleModifier(int mask) => mask switch
    {
        ModCtrl  => "Ctrl",
        ModShift => "Shift",
        ModAlt   => "Alt",
        ModWin   => "Win",
        _        => "",
    };
}
