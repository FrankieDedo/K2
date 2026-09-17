using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// The "Ctrl + Shift + A" editor, as a pair of functions: a row of modifier checkboxes plus an
/// editable key combo, read from and written to the SAME human-syntax string
/// <see cref="SendKeysTranslator.Translate"/> and <c>HotkeySender</c> already consume.
///
/// <para>It lives on its own because three different screens now edit a keystroke — the Keys
/// action, Hotkey Switch's two rows, and (since 2026-09-12) a game-studio reading tile, which
/// sends a combination on top of showing its reading. One definition, so a shortcut typed in one
/// of them cannot parse differently in another.</para>
/// </summary>
public static class KeyCombo
{
    /// <summary>Keys that are not a letter, a digit or an F-key, in the spelling the translator
    /// understands.</summary>
    private static readonly string[] SpecialKeys =
    {
        "Enter", "Esc", "Tab", "Backspace", "Delete", "Insert", "Home", "End",
        "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Space",
        "CapsLock", "NumLock", "ScrollLock", "PrtSc",
    };

    /// <summary>Every key the picker offers, in the order it offers them.</summary>
    public static IEnumerable<string> Keys
    {
        get
        {
            foreach (var c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ") yield return c.ToString();
            foreach (var c in "0123456789") yield return c.ToString();
            for (int i = 1; i <= 24; i++) yield return $"F{i}";
            foreach (var k in SpecialKeys) yield return k;
        }
    }

    public static void Populate(ComboBox cb)
    {
        cb.Items.Clear();
        foreach (var k in Keys) cb.Items.Add(k);
    }

    /// <summary>Parses a human-syntax shortcut into the modifier checkboxes + key combo.</summary>
    public static void Load(string? value, CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win,
                            ComboBox key)
    {
        ctrl.IsChecked = shift.IsChecked = alt.IsChecked = win.IsChecked = false;
        key.Text = "";
        if (string.IsNullOrWhiteSpace(value)) return;

        var parts = value.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0);

        string keyToken = "";
        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": ctrl.IsChecked  = true; break;
                case "SHIFT":                shift.IsChecked = true; break;
                case "ALT":                  alt.IsChecked   = true; break;
                case "WIN": case "GUI": case "META": case "CMD":
                                             win.IsChecked   = true; break;
                default: keyToken = p; break;
            }
        }
        key.Text = keyToken;
    }

    /// <summary>Inverse of <see cref="Load"/>. "" when nothing is picked — which is what "this tile
    /// presses no keys" looks like.</summary>
    public static string Save(CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win, ComboBox key)
    {
        var parts = new List<string>();
        if (ctrl.IsChecked  == true) parts.Add("Ctrl");
        if (shift.IsChecked == true) parts.Add("Shift");
        if (alt.IsChecked   == true) parts.Add("Alt");
        if (win.IsChecked   == true) parts.Add("Win");

        var k = (key.Text ?? "").Trim();
        if (k.Length > 0) parts.Add(k);

        return string.Join(" + ", parts);
    }
}
