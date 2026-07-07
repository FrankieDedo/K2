using System;
using System.Collections.Generic;
using System.Linq;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Keys" panel — modifier checkboxes (Ctrl/Shift/Alt/Win)
/// + an editable key combo, composing/parsing the same human-syntax string
/// (<c>"Ctrl + Shift + A"</c>) that <see cref="SendKeysTranslator.Translate"/> already
/// consumes — execution is unchanged, this only replaces the free-text entry with a picker.
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

    private void EnsureKeysPanel()
    {
        if (_keysPanelPopulated) return;
        _keysPanelPopulated = true;

        CbKeyValue.Items.Clear();
        foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ") CbKeyValue.Items.Add(c.ToString());
        foreach (var c in "0123456789") CbKeyValue.Items.Add(c.ToString());
        for (int i = 1; i <= 24; i++) CbKeyValue.Items.Add($"F{i}");
        foreach (var k in CommonSpecialKeys) CbKeyValue.Items.Add(k);
    }

    private void LoadKeysSpec(string value)
    {
        EnsureKeysPanel();

        ChkKeyCtrl.IsChecked = ChkKeyShift.IsChecked = ChkKeyAlt.IsChecked = ChkKeyWin.IsChecked = false;
        CbKeyValue.Text = "";
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(p => p.Trim())
                          .Where(p => p.Length > 0);

        string keyToken = "";
        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": ChkKeyCtrl.IsChecked  = true; break;
                case "SHIFT":                ChkKeyShift.IsChecked = true; break;
                case "ALT":                  ChkKeyAlt.IsChecked   = true; break;
                case "WIN": case "GUI": case "META": case "CMD":
                                              ChkKeyWin.IsChecked   = true; break;
                default: keyToken = p; break;
            }
        }
        CbKeyValue.Text = keyToken;
    }

    private string SaveKeysSpec()
    {
        var parts = new List<string>();
        if (ChkKeyCtrl.IsChecked  == true) parts.Add("Ctrl");
        if (ChkKeyShift.IsChecked == true) parts.Add("Shift");
        if (ChkKeyAlt.IsChecked   == true) parts.Add("Alt");
        if (ChkKeyWin.IsChecked   == true) parts.Add("Win");

        var key = (CbKeyValue.Text ?? "").Trim();
        if (key.Length > 0) parts.Add(key);

        return string.Join(" + ", parts);
    }
}
