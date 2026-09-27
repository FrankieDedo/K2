using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Reads the KEY BINDINGS of the games in <see cref="ModLinkGames"/>, so their tiles press what the
/// player actually bound rather than the keys the game ships with. No mod is needed for this:
/// both games keep their bindings in a plain config file.
///
/// <para><b>Minecraft</b> writes <c>options.txt</c> in the game directory
/// (<c>%APPDATA%\.minecraft</c> for the official launcher), one control per line:
/// <c>key_key.inventory:key.keyboard.e</c>. A launcher that gives each instance its own directory
/// (CurseForge, Prism) is followed through the directory the running mod reports
/// (<see cref="MinecraftGameDir"/>).</para>
///
/// <para><b>Space Engineers</b> writes <c>%APPDATA%\SpaceEngineers\SpaceEngineers.cfg</c>, whose
/// <c>ControlsButtons</c> dictionary holds the FULL set (<c>Keyboard</c>, <c>KeyboardModifier</c>,
/// <c>Keyboard2</c>, <c>Mouse</c> per control) under the game's <c>MyControlsSpace</c> names.</para>
///
/// <para>Returns null — keep the shipped default — whenever reading would be guessing: file
/// missing, control not listed, bound to the mouse, or to a key K2 cannot press.</para>
/// </summary>
internal static class ModLinkBinds
{
    /// <summary>The game directory the Minecraft mod last reported, or null before it has
    /// answered — set by <see cref="ModLinkClient"/>.</summary>
    internal static volatile string? MinecraftGameDir;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, string> Map)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string? For(ModLinkGame game, string? bind)
    {
        if (bind is not { Length: > 0 }) return null;
        var map = game.ProfileId switch
        {
            ModLinkGames.MinecraftId => Load(MinecraftOptionsFile(), ParseMinecraft),
            ModLinkGames.SpaceEngineersId => Load(SpaceEngineersConfigFile, ParseSpaceEngineers),
            _ => null,
        };
        return map is not null && map.TryGetValue(bind, out var keys) ? keys : null;
    }

    private static string MinecraftOptionsFile()
    {
        string? dir = MinecraftGameDir;
        if (dir is { Length: > 0 } && File.Exists(Path.Combine(dir, "options.txt")))
            return Path.Combine(dir, "options.txt");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            ".minecraft", "options.txt");
    }

    private static string SpaceEngineersConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpaceEngineers", "SpaceEngineers.cfg");

    /// <summary>Re-parses only when the file changed. Shared read: the game may hold it open.</summary>
    private static Dictionary<string, string>? Load(string file, Func<string, Dictionary<string, string>> parse)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var stamp = File.GetLastWriteTimeUtc(file);
                if (_cache.TryGetValue(file, out var hit) && hit.Stamp == stamp) return hit.Map;

                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();

                var map = parse(text);
                _cache[file] = (stamp, map);
                App.WriteLog($"[MODLINK] bindings loaded from \"{file}\": {map.Count} keyboard controls");
                return map;
            }
            catch (Exception ex)
            {
                App.WriteLog($"[MODLINK] bindings read failed for \"{file}\": {ex.Message}");
                return _cache.TryGetValue(file, out var stale) ? stale.Map : null;
            }
        }
    }

    // ------------------------------------------------------------------ Minecraft

    internal static Dictionary<string, string> ParseMinecraft(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("key_", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string control = line[4..colon];
            if (GlfwKey(line[(colon + 1)..]) is { } keys) map[control] = keys;
        }
        return map;
    }

    /// <summary>A GLFW key name as Minecraft stores it (<c>key.keyboard.left.shift</c>) in K2's
    /// shortcut syntax, or null for the mouse, an unbound control or a key K2 cannot press.</summary>
    internal static string? GlfwKey(string name)
    {
        const string kb = "key.keyboard.";
        if (!name.StartsWith(kb, StringComparison.Ordinal)) return null;
        string k = name[kb.Length..];

        if (k.Length == 1 && char.IsLetterOrDigit(k[0])) return k.ToUpperInvariant();
        if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k[1..], out int fn) && fn is >= 1 and <= 24)
            return "F" + fn;
        if (k.StartsWith("keypad.", StringComparison.Ordinal))
        {
            string pad = k["keypad.".Length..];
            if (pad.Length == 1 && char.IsDigit(pad[0])) return "Num" + pad;
            return pad switch
            {
                "multiply" => "NumMultiply", "add" => "NumAdd", "subtract" => "NumSubtract",
                "decimal" => "NumDecimal", "divide" => "NumDivide", "enter" => "Enter",
                _ => null,
            };
        }
        return k switch
        {
            "left.shift" or "right.shift" => "Shift",
            "left.control" or "right.control" => "Ctrl",
            "left.alt" or "right.alt" => "Alt",
            "space" => "Space", "tab" => "Tab", "enter" => "Enter", "escape" => "Esc",
            "backspace" => "Backspace", "insert" => "Insert", "delete" => "Delete",
            "home" => "Home", "end" => "End", "page.up" => "PageUp", "page.down" => "PageDown",
            "up" => "Up", "down" => "Down", "left" => "Left", "right" => "Right",
            "caps.lock" => "CapsLock", "pause" => "Pause",
            "slash" => "/", "period" => ".", "comma" => ",", "semicolon" => ";",
            "apostrophe" => "'", "left.bracket" => "[", "right.bracket" => "]",
            "backslash" => "\\", "grave.accent" => "`", "equal" => "=", "minus" => "Minus",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ Space Engineers

    internal static Dictionary<string, string> ParseSpaceEngineers(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var doc = XDocument.Parse(text);

        XElement? buttons = null;
        foreach (var item in doc.Descendants("item"))
            if ((string?)item.Element("Key") == "ControlsButtons") { buttons = item; break; }
        if (buttons?.Element("Value")?.Element("Value")?.Element("dictionary") is not { } dict) return map;

        foreach (var control in dict.Elements("item"))
        {
            string? name = (string?)control.Element("Key");
            if (name is null) continue;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in control.Element("Value")?.Element("dictionary")?.Elements("item") ?? Array.Empty<XElement>())
                if ((string?)f.Element("Key") is { } fk) fields[fk] = (string?)f.Element("Value") ?? "";

            string? keys = SeCombo(fields.GetValueOrDefault("Keyboard"), fields.GetValueOrDefault("KeyboardModifier"))
                        ?? SeCombo(fields.GetValueOrDefault("Keyboard2"), fields.GetValueOrDefault("KeyboardModifier2"));
            if (keys is not null) map[name] = keys;
        }
        return map;
    }

    /// <summary>One SE binding (<c>MyKeys</c> name + <c>MyKeyboardModifiers</c> flags such as
    /// <c>"Control, Shift"</c>) as a K2 shortcut, or null when there is no key to press.</summary>
    private static string? SeCombo(string? key, string? modifiers)
    {
        if (SeKey(key) is not { } k) return null;
        var mods = new List<string>();
        foreach (string m in (modifiers ?? "").Split(','))
            switch (m.Trim())
            {
                case "Control": mods.Add("Ctrl"); break;
                case "Shift": mods.Add("Shift"); break;
                case "Alt": mods.Add("Alt"); break;
            }
        return mods.Count == 0 ? k : string.Join(" + ", mods) + " + " + k;
    }

    internal static string? SeKey(string? key)
    {
        if (key is not { Length: > 0 } || key == "None") return null;
        if (key.Length == 1 && char.IsLetter(key[0])) return key;
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return key[1..];
        if (key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 && char.IsDigit(key[6]))
            return "Num" + key[6];
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24)
            return key;
        return key switch
        {
            "Shift" or "LeftShift" or "RightShift" => "Shift",
            "Control" or "LeftControl" or "RightControl" => "Ctrl",
            "Alt" or "LeftAlt" or "RightAlt" => "Alt",
            "Space" => "Space", "Tab" => "Tab", "Enter" => "Enter", "Escape" => "Esc",
            "Back" => "Backspace", "Insert" => "Insert", "Delete" => "Delete",
            "Home" => "Home", "End" => "End", "PageUp" => "PageUp", "PageDown" => "PageDown",
            "Up" => "Up", "Down" => "Down", "Left" => "Left", "Right" => "Right",
            "CapsLock" => "CapsLock", "Pause" => "Pause",
            "Multiply" => "NumMultiply", "Add" => "NumAdd", "Subtract" => "NumSubtract",
            "Decimal" => "NumDecimal", "Divide" => "NumDivide",
            "OemQuestion" => "/", "OemPeriod" => ".", "OemComma" => ",", "OemSemicolon" => ";",
            "OemQuotes" => "'", "OemOpenBrackets" => "[", "OemCloseBrackets" => "]",
            "OemPipe" or "OemBackslash" => "\\", "OemTilde" => "`", "OemPlus" => "=",
            "OemMinus" => "Minus",
            _ => null,
        };
    }
}
