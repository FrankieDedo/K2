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
        string keyToken = "";

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
                    keyToken = p;
                    break;
            }
        }
        return mods.ToString() + WrapKey(keyToken);
    }

    private static string WrapKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        // F1..F24
        if (Regex.IsMatch(key, @"^[Ff]\d{1,2}$"))
            return "{" + key.ToUpperInvariant() + "}";

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

        // Unrecognized word: pass it as-is (the user can use placeholders
        // like {ENTER} manually).
        return key;
    }
}
