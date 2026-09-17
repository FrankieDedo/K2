using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Keys" panel — modifier checkboxes (Ctrl/Shift/Alt/Win)
/// + an editable key combo, composing/parsing the same human-syntax string
/// (<c>"Ctrl + Shift + A"</c>) that <see cref="SendKeysTranslator.Translate"/> already
/// consumes — execution is unchanged, this only replaces the free-text entry with a picker.
/// The parse/build helpers are static and take explicit control references so the Hotkey
/// Switch panel (two shortcut rows) can reuse them instead of duplicating this logic.
/// </summary>
public partial class ButtonActionDialog
{
    private bool _keysPanelPopulated;

    private static void PopulateKeyItems(ComboBox cb) => KeyCombo.Populate(cb);

    private void EnsureKeysPanel()
    {
        if (_keysPanelPopulated) return;
        _keysPanelPopulated = true;
        PopulateKeyItems(CbKeyValue);
    }

    private void LoadKeysSpec(string value)
    {
        EnsureKeysPanel();
        ParseShortcut(value, ChkKeyCtrl, ChkKeyShift, ChkKeyAlt, ChkKeyWin, CbKeyValue);
    }

    private string SaveKeysSpec() => BuildShortcut(ChkKeyCtrl, ChkKeyShift, ChkKeyAlt, ChkKeyWin, CbKeyValue);

    /// <summary>Parses a human-syntax shortcut ("Ctrl + Shift + A") into the given modifier
    /// checkboxes + key combo. Kept as a name of its own because Hotkey Switch's two rows and the
    /// Elite/App panels all call it; the syntax itself lives in <see cref="KeyCombo"/>.</summary>
    private static void ParseShortcut(string value, CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey) =>
        KeyCombo.Load(value, chkCtrl, chkShift, chkAlt, chkWin, cbKey);

    /// <summary>Inverse of <see cref="ParseShortcut"/>.</summary>
    private static string BuildShortcut(CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey) =>
        KeyCombo.Save(chkCtrl, chkShift, chkAlt, chkWin, cbKey);
}
