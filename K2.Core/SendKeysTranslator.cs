using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace K2.Core;

/// <summary>
/// Translates a "human-readable" shortcut like <c>"Ctrl + Shift + A"</c> or
/// <c>"CTRL + ALT + F4"</c> into the
/// <see cref="System.Windows.Forms.SendKeys.SendWait(string)"/> syntax
/// (e.g. <c>"^+a"</c>, <c>"^%{F4}"</c>).
///
/// Accepted separators are <c>+</c>, <c>-</c> and multiple spaces.
/// Recognizes modifiers <c>Ctrl/Control</c>, <c>Shift</c>, <c>Alt</c>;
/// <c>Win/GUI</c> is ignored because SendKeys does not support it natively.
/// </summary>
public static class SendKeysTranslator
{
    private static readonly HashSet<string> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENTER","RETURN","ESC","ESCAPE","TAB","BACKSPACE","BS","BKSP",
        "DELETE","DEL","INSERT","INS","HOME","END","PGUP","PGDN","PAGEUP","PAGEDOWN",
        "UP","DOWN","LEFT","RIGHT","SPACE","BREAK","CAPSLOCK","NUMLOCK","SCROLLLOCK",
        "PRTSC","HELP"
    };

    // Some names need to be normalized to the SendKeys style
    private static readonly Dictionary<string, string> Normalized = new(StringComparer.OrdinalIgnoreCase)
    {
        { "RETURN",  "ENTER" },
        { "ESCAPE",  "ESC"   },
        { "BACKSPACE","BS"   },
        { "BKSP",    "BS"    },
        { "PAGEUP",  "PGUP"  },
        { "PAGEDOWN","PGDN"  },
        { "DELETE",  "DEL"   },
        { "INSERT",  "INS"   },
    };

    public static string Translate(string human)
    {
        if (string.IsNullOrWhiteSpace(human)) return "";

        var parts = human.Split(new[] {'+', '-'}, StringSplitOptions.RemoveEmptyEntries)
                         .Select(p => p.Trim())
                         .Where(p => p.Length > 0)
                         .ToList();

        var mods = new StringBuilder();
        var keyTokens = new List<string>();

        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    mods.Append('^');
                    break;
                case "SHIFT":
                    mods.Append('+');
                    break;
                case "ALT":
                    mods.Append('%');
                    break;
                case "WIN":
                case "GUI":
                case "META":
                case "CMD":
                    // SendKeys does not support the Windows key
                    break;
                default:
                    keyTokens.Add(p);
                    break;
            }
        }
        // A modifier-only combination ("Alt", "Ctrl + Shift") has no SendKeys spelling: "%" on its
        // own makes SendKeys.SendWait throw, and there is no way to say "press Alt and release it"
        // in that syntax at all. Return nothing so the caller can skip the call — the SendInput
        // path (HotkeySender) is the one that handles bare modifiers.
        var wrapped = keyTokens.Select(WrapKey).ToList();
        if (wrapped.Count == 0 || wrapped.Any(w => w.Length == 0)) return "";
        // Several keys ("Ctrl + A + B"): SendKeys cannot hold them together, the closest it
        // has is the group form, which keeps the modifiers down across all of them.
        if (wrapped.Count == 1) return mods.ToString() + wrapped[0];
        return mods.Length == 0 ? string.Concat(wrapped) : mods + "(" + string.Concat(wrapped) + ")";
    }

    private static string WrapKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        // F1..F16 — the ceiling System.Windows.Forms.SendKeys actually knows. "{F17}".."{F24}"
        // make SendKeys.SendWait throw (silently swallowed, keystroke lost — see issue #17), so
        // they are NOT emitted here: those high function keys only reach the OS through the
        // SendInput path (HotkeySender, VK_F1..VK_F24), which ButtonActionEngine.RunShortcut
        // already prefers. This translator is the fallback; for F17+ it has nothing valid to say.
        var fn = Regex.Match(key, @"^[Ff](\d{1,2})$");
        if (fn.Success)
            return int.Parse(fn.Groups[1].Value) is >= 1 and <= 16 ? "{" + key.ToUpperInvariant() + "}" : "";

        // Known special keys
        if (SpecialKeys.Contains(key))
        {
            var norm = Normalized.TryGetValue(key, out var n) ? n : key.ToUpperInvariant();
            return "{" + norm + "}";
        }

        // Single character: pass as lowercase, escape if it's a SendKeys meta-character
        if (key.Length == 1)
        {
            var c = key[0];
            if ("{}()+^%~[]".IndexOf(c) >= 0)
                return "{" + c + "}";
            return char.ToLower(c).ToString();
        }

        // Unrecognized word: nothing to send. Passing it through made SendKeys TYPE it — an
        // imported "Ctrl + NUMPAD8" went out as Ctrl+N followed by the letters "UMPAD8". (A raw
        // "{ENTER}"-style sequence never comes through here: RunShortcut sends it untranslated.)
        return "";
    }
}
