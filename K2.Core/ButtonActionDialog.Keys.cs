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

    /// <summary>How many keys one combination can hold — the combos share the width the single
    /// one used to take, a fifth each.</summary>
    private const int MaxKeys = 5;

    /// <summary>The key combos currently shown, left to right. <c>CbKeyValue</c> is always the first.</summary>
    private readonly List<ComboBox> _keyCombos = new();

    private void EnsureKeysPanel()
    {
        if (_keysPanelPopulated) return;
        _keysPanelPopulated = true;
        PopulateKeyItems(CbKeyValue);
        _keyCombos.Add(CbKeyValue);
    }

    /// <summary>Grows or shrinks the row to <paramref name="count"/> combos, keeping the "+"
    /// button right after the last one (gone once the row is full).</summary>
    private void SetKeyComboCount(int count)
    {
        count = Math.Clamp(count, 1, MaxKeys);
        while (_keyCombos.Count > count)
        {
            KeysRow.Children.Remove(_keyCombos[^1]);
            _keyCombos.RemoveAt(_keyCombos.Count - 1);
        }
        while (_keyCombos.Count < count)
        {
            var cb = new ComboBox { IsEditable = true, Margin = CbKeyValue.Margin };
            PopulateKeyItems(cb);
            KeysRow.Children.Insert(_keyCombos.Count, cb);
            _keyCombos.Add(cb);
        }
        BtnKeyAdd.Visibility = count < MaxKeys ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void BtnKeyAdd_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        EnsureKeysPanel();
        SetKeyComboCount(_keyCombos.Count + 1);
        _keyCombos[^1].Focus();
    }

    private void LoadKeysSpec(string value)
    {
        EnsureKeysPanel();
        SetKeyComboCount(KeyCombo.KeyTokens(value).Count);
        KeyCombo.Load(value, ChkKeyCtrl, ChkKeyShift, ChkKeyAlt, ChkKeyWin, _keyCombos);
    }

    private string SaveKeysSpec()
    {
        EnsureKeysPanel();
        return KeyCombo.Save(ChkKeyCtrl, ChkKeyShift, ChkKeyAlt, ChkKeyWin, _keyCombos);
    }

    /// <summary>Parses a human-syntax shortcut ("Ctrl + Shift + A") into the given modifier
    /// checkboxes + key combo. Kept as a name of its own because Hotkey Switch's two rows and the
    /// Elite/App panels all call it; the syntax itself lives in <see cref="KeyCombo"/>.</summary>
    private static void ParseShortcut(string value, CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey) =>
        KeyCombo.Load(value, chkCtrl, chkShift, chkAlt, chkWin, cbKey);

    /// <summary>Inverse of <see cref="ParseShortcut"/>.</summary>
    private static string BuildShortcut(CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey) =>
        KeyCombo.Save(chkCtrl, chkShift, chkAlt, chkWin, cbKey);
}
