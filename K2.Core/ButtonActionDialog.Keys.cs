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
    private static readonly string[] CommonSpecialKeys =
    {
        "Enter", "Esc", "Tab", "Backspace", "Delete", "Insert", "Home", "End",
        "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Space",
        "CapsLock", "NumLock", "ScrollLock", "PrtSc",
    };

    private bool _keysPanelPopulated;

    private static void PopulateKeyItems(ComboBox cb)
    {
        cb.Items.Clear();
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ") cb.Items.Add(c.ToString());
        foreach (var c in "0123456789") cb.Items.Add(c.ToString());
        for (int i = 1; i <= 24; i++) cb.Items.Add($"F{i}");
        foreach (var k in CommonSpecialKeys) cb.Items.Add(k);
    }

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
    /// checkboxes + key combo. Shared by the Keys panel and Hotkey Switch's two shortcut rows.</summary>
    private static void ParseShortcut(string value, CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey)
    {
        chkCtrl.IsChecked = chkShift.IsChecked = chkAlt.IsChecked = chkWin.IsChecked = false;
        cbKey.Text = "";
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Trim())
                          .Where(p => p.Length > 0);

        string keyToken = "";
        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": chkCtrl.IsChecked  = true; break;
                case "SHIFT":                chkShift.IsChecked = true; break;
                case "ALT":                  chkAlt.IsChecked   = true; break;
                case "WIN": case "GUI": case "META": case "CMD":
                                              chkWin.IsChecked   = true; break;
                default: keyToken = p; break;
            }
        }
        cbKey.Text = keyToken;
    }

    /// <summary>Inverse of <see cref="ParseShortcut"/>.</summary>
    private static string BuildShortcut(CheckBox chkCtrl, CheckBox chkShift, CheckBox chkAlt, CheckBox chkWin, ComboBox cbKey)
    {
        var parts = new List<string>();
        if (chkCtrl.IsChecked  == true) parts.Add("Ctrl");
        if (chkShift.IsChecked == true) parts.Add("Shift");
        if (chkAlt.IsChecked   == true) parts.Add("Alt");
        if (chkWin.IsChecked   == true) parts.Add("Win");

        var key = (cbKey.Text ?? "").Trim();
        if (key.Length > 0) parts.Add(key);

        return string.Join(" + ", parts);
    }
}
