using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace K2.Core;

/// <summary>
/// The "Ctrl + Shift + A" editor, as a pair of functions: a row of modifier checkboxes plus a
/// searchable key combo, read from and written to the SAME human-syntax string
/// <see cref="SendKeysTranslator.Translate"/> and <c>HotkeySender</c> already consume.
///
/// <para>It lives on its own because three different screens now edit a keystroke — the Keys
/// action, Hotkey Switch's two rows, and (since 2026-09-12) a game-studio reading tile, which
/// sends a combination on top of showing its reading. One definition, so a shortcut typed in one
/// of them cannot parse differently in another.</para>
/// </summary>
public static class KeyCombo
{
    /// <summary>Keys that are not a letter, a digit or an F-key, in the spelling
    /// <c>HotkeySender</c> understands.</summary>
    private static readonly string[] SpecialKeys =
    {
        "Enter", "Esc", "Tab", "Backspace", "Delete", "Insert", "Home", "End",
        "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Space",
        "CapsLock", "NumLock", "ScrollLock", "PrtSc", "Pause",
        "Numpad0", "Numpad1", "Numpad2", "Numpad3", "Numpad4",
        "Numpad5", "Numpad6", "Numpad7", "Numpad8", "Numpad9",
        "NumpadDecimal", "NumpadAdd", "NumpadSubtract", "NumpadMultiply", "NumpadDivide",
        // "-" is a separator in the shortcut syntax, hence the name.
        "Minus", ".", ",", ";", "/", "'", "[", "]", "\\", "=", "`",
    };

    /// <summary>Other spellings of a key in <see cref="Keys"/> — the short forms the sender
    /// already accepted, and the <c>Num3</c>/<c>NumDecimal</c> names the keypad went by until
    /// 2026-10-06 (profiles saved before then, and the binds read from a game, still carry
    /// them). Base Camp's own <c>NUMPAD3</c>/<c>NUMPADDECIMAL</c> need no entry: they are the
    /// picker's spelling in another case.</summary>
    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    private static readonly Dictionary<string, string> ByName =
        Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildAliases()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NumDecimal"] = "NumpadDecimal", ["NumAdd"] = "NumpadAdd",
            ["NumSubtract"] = "NumpadSubtract", ["NumMultiply"] = "NumpadMultiply",
            ["NumDivide"] = "NumpadDivide",
            ["Escape"] = "Esc", ["Return"] = "Enter", ["Del"] = "Delete", ["Ins"] = "Insert",
            ["PgUp"] = "PageUp", ["PgDn"] = "PageDown", ["BS"] = "Backspace",
            // Base Camp's arrow names (real export: "ALT  + ARROWRIGHT").
            ["ArrowUp"] = "Up", ["ArrowDown"] = "Down", ["ArrowLeft"] = "Left", ["ArrowRight"] = "Right",
        };
        for (int i = 0; i <= 9; i++) d[$"Num{i}"] = $"Numpad{i}";
        return d;
    }

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

    /// <summary>The picker's own spelling of <paramref name="token"/> ("num3" -> "Numpad3",
    /// "escape" -> "Esc"), or null when it names no key the picker offers.</summary>
    public static string? Canonical(string? token)
    {
        var t = (token ?? "").Trim();
        if (t.Length == 0) return null;
        if (ByName.TryGetValue(t, out var k)) return k;
        return Aliases.TryGetValue(t, out var a) ? a : null;
    }

    /// <summary>Rewrites a shortcut whose KEY is the minus sign ("CTRL + SHIFT  + -", as Base
    /// Camp writes it) to the "Minus" name. "-" doubles as a separator, so the key would
    /// otherwise vanish in the split and the shortcut go out as its bare modifiers.</summary>
    public static string NormalizeMinus(string shortcut)
    {
        var t = shortcut.TrimEnd();
        if (!t.EndsWith('-')) return shortcut;
        var head = t[..^1].TrimEnd();
        return head.Length == 0 ? "Minus" : head.EndsWith('+') ? head + " Minus" : shortcut;
    }

    /// <summary>The name a shortcut goes by in the key lists: modifiers first, then the keys in
    /// the picker's own spelling, no spaces ("CTRL  + ALT + x + NUMPAD3" -> "Ctrl+Alt+X+Numpad3").
    /// An imported shortcut is stored as Base Camp wrote it, so without this the list showed
    /// "NUMPAD3" for a key the dialog opens as "Numpad3".</summary>
    public static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        bool ctrl = false, shift = false, alt = false, win = false;
        var keys = new List<string>();
        foreach (var raw in NormalizeMinus(value).Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Trim();
            switch (p.ToUpperInvariant())
            {
                case "": break;
                case "CTRL": case "CONTROL": ctrl  = true; break;
                case "SHIFT":                shift = true; break;
                case "ALT":                  alt   = true; break;
                case "WIN": case "GUI": case "META": case "CMD":
                                             win   = true; break;
                default:
                    string k = Canonical(p) ?? p;
                    if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
                    break;
            }
        }

        var parts = new List<string>();
        if (ctrl)  parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt)   parts.Add("Alt");
        if (win)   parts.Add("Win");
        parts.AddRange(keys);
        return string.Join("+", parts);
    }

    /// <summary>Per-combo state of the search behaviour <see cref="Populate"/> installs.</summary>
    private sealed class SearchState
    {
        public bool Typing, Busy;
        public string Last = "";
    }

    private static readonly ConditionalWeakTable<ComboBox, SearchState> States = new();

    /// <summary>Fills <paramref name="cb"/> with the fixed key list and makes its text box a
    /// SEARCH over that list rather than a free-text value: typing filters the dropdown, and
    /// what stays in the box when focus leaves is always one of the listed keys (the best
    /// match of what was typed, else the previous key). A key name is therefore never invented
    /// by hand — an unknown word used to reach SendKeys and get typed out letter by letter.</summary>
    public static void Populate(ComboBox cb)
    {
        cb.Items.Filter = null;
        cb.Items.Clear();
        foreach (var k in Keys) cb.Items.Add(k);
        if (States.TryGetValue(cb, out _)) return;

        var st = new SearchState();
        States.Add(cb, st);
        cb.IsEditable = true;
        cb.IsTextSearchEnabled = false;   // no autocomplete: it would fight the filter
        cb.StaysOpenOnEdit = true;

        cb.PreviewTextInput += (_, _) => st.Typing = true;
        cb.PreviewKeyDown += (_, e) => { if (e.Key is Key.Back or Key.Delete) st.Typing = true; };
        cb.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) =>
        {
            // Only a text change the user typed is a search; one caused by picking an item
            // or by Load is the value itself.
            if (st.Busy || !st.Typing) return;
            st.Typing = false;
            if (e.OriginalSource is not TextBox tb) return;

            string typed = tb.Text;
            string needle = typed.Trim();
            int caret = tb.CaretIndex;
            st.Busy = true;
            try
            {
                cb.Items.Filter = needle.Length == 0
                    ? null
                    : o => ((string)o).Contains(needle, StringComparison.OrdinalIgnoreCase);
                cb.IsDropDownOpen = true;
                // Filtering out the selected item clears the text, and opening the dropdown
                // selects it all (the next keystroke would replace it) — put both back.
                if (tb.Text != typed) tb.Text = typed;
                tb.CaretIndex = Math.Min(caret, typed.Length);
            }
            finally { st.Busy = false; }
        }));
        cb.DropDownClosed += (_, _) => { st.Busy = true; cb.Items.Filter = null; st.Busy = false; };
        cb.IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is false) Commit(cb, st); };
    }

    /// <summary>The listed key <paramref name="text"/> stands for: its exact (or alias) name,
    /// an entry <see cref="Load"/> kept from an older profile, else the first key starting
    /// with it, else the first containing it. Null when nothing matches.</summary>
    private static string? Resolve(ComboBox cb, string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (Canonical(t) is { } k) return k;
        var all = cb.Items.SourceCollection.Cast<string>().ToList();
        return all.FirstOrDefault(i => i.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(i => i.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(i => i.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static void Commit(ComboBox cb, SearchState st)
    {
        st.Busy = true;
        try
        {
            cb.Items.Filter = null;
            var t = (cb.Text ?? "").Trim();
            st.Last = t.Length == 0 ? "" : Resolve(cb, t) ?? st.Last;
            cb.Text = st.Last;
        }
        finally { st.Busy = false; }
    }

    /// <summary>The non-modifier keys of a shortcut, in the order written ("Ctrl + A + B" ->
    /// A, B). The editors that show one combo per key size themselves from this.</summary>
    public static List<string> KeyTokens(string? value)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(value)) return keys;
        foreach (var raw in NormalizeMinus(value).Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.Trim();
            if (p.Length > 0 && !IsModifier(p)) keys.Add(p);
        }
        return keys;
    }

    private static bool IsModifier(string token) => token.ToUpperInvariant()
        is "CTRL" or "CONTROL" or "SHIFT" or "ALT" or "WIN" or "GUI" or "META" or "CMD";

    /// <summary>Parses a human-syntax shortcut into the modifier checkboxes + key combo.</summary>
    public static void Load(string? value, CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win,
                            ComboBox key) =>
        Load(value, ctrl, shift, alt, win, new[] { key });

    /// <summary>Parses a human-syntax shortcut into the modifier checkboxes + one combo per key.
    /// With more keys than combos the LAST combo takes the last key (what the single-combo
    /// editors always showed).</summary>
    public static void Load(string? value, CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win,
                            IReadOnlyList<ComboBox> keys)
    {
        ctrl.IsChecked = shift.IsChecked = alt.IsChecked = win.IsChecked = false;
        foreach (var raw in (value ?? "").Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": ctrl.IsChecked  = true; break;
                case "SHIFT":                shift.IsChecked = true; break;
                case "ALT":                  alt.IsChecked   = true; break;
                case "WIN": case "GUI": case "META": case "CMD":
                                             win.IsChecked   = true; break;
            }
        }

        var tokens = KeyTokens(value);
        for (int i = 0; i < keys.Count; i++)
        {
            bool last = i == keys.Count - 1;
            SetKey(keys[i], i < tokens.Count ? tokens[last ? tokens.Count - 1 : i] : "");
        }
    }

    private static void SetKey(ComboBox key, string token)
    {
        // A token from an older profile that names no listed key is kept as an entry of its
        // own, so opening and saving the dialog doesn't silently drop it.
        string shown = Canonical(token) ?? token;
        if (shown.Length > 0 && !key.Items.SourceCollection.Cast<string>().Contains(shown))
            key.Items.Add(shown);
        key.Text = shown;
        if (States.TryGetValue(key, out var st)) st.Last = shown;
    }

    /// <summary>Inverse of <see cref="Load"/>. "" when nothing is picked — which is what "this tile
    /// presses no keys" looks like.</summary>
    public static string Save(CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win, ComboBox key) =>
        Save(ctrl, shift, alt, win, new[] { key });

    /// <summary>Several-keys form of <see cref="Save(CheckBox,CheckBox,CheckBox,CheckBox,ComboBox)"/>:
    /// an empty combo is skipped, a key picked twice is written once.</summary>
    public static string Save(CheckBox ctrl, CheckBox shift, CheckBox alt, CheckBox win,
                              IReadOnlyList<ComboBox> keys)
    {
        var parts = new List<string>();
        if (ctrl.IsChecked  == true) parts.Add("Ctrl");
        if (shift.IsChecked == true) parts.Add("Shift");
        if (alt.IsChecked   == true) parts.Add("Alt");
        if (win.IsChecked   == true) parts.Add("Win");

        // Resolved, not read raw: Enter on the default button saves without the box ever
        // losing focus, so the text may still be a half-typed search.
        foreach (var key in keys)
            if (Resolve(key, key.Text) is { } k && !parts.Contains(k, StringComparer.OrdinalIgnoreCase))
                parts.Add(k);

        return string.Join(" + ", parts);
    }
}
