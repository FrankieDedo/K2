using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace K2.App.Services;

/// <summary>
/// Reads Deadside's own KEY BINDINGS, so the game profile presses what the player actually bound
/// rather than the keys the game ships with.
///
/// <para>
/// <b>Why it matters more here than elsewhere.</b> Deadside's survival controls are the ones
/// players rebind first (crouch, prone, use, reload all sit on letters that clash with the
/// building and vehicle sets), and there is no in-game way to see a pad's mapping — a tile that
/// pressed the shipped default would silently do the wrong thing, or nothing at all.
/// </para>
///
/// <para>
/// <b>Where.</b> Unreal writes the player's mappings to
/// <c>%LOCALAPPDATA%\Deadside\Saved\Config\WindowsNoEditor\Input.ini</c>, and writes the FULL set
/// there rather than only the changed rows, so one file is the whole truth. It does not exist
/// until the game has been run once — a normal answer, not an error: the catalogue's own values
/// are the shipped defaults (lifted from the game's packaged <c>DefaultInput.ini</c>, not from a
/// community list), so "no file" simply means the defaults still stand.
/// </para>
///
/// <para>
/// <b>Format</b> (verified against both the packaged defaults and a real player config):
/// <code>
/// ActionMappings=(ActionName="Crouch",bShift=False,bCtrl=False,bAlt=False,bCmd=False,Key=C)
/// </code>
/// An action may appear several times (a second binding, e.g. <c>ControlModuleLeft</c> on both A
/// and Left); the first one K2 can actually press wins. Mouse buttons, wheel directions and
/// bindings whose key is a bare modifier are skipped — there is no keystroke to send for them.
/// </para>
///
/// <para>
/// This is the only connection Deadside offers. The game exposes no status file, no local API and
/// no log of play state, and it runs under BattlEye — so there is nothing to light a tile up
/// from, and nothing here reads the game's memory or hooks it. Real binds are the whole
/// integration.
/// </para>
/// </summary>
internal static class DeadsideBindsReader
{
    /// <summary>Where the game keeps the player's mappings.</summary>
    public static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deadside", "Saved", "Config", "WindowsNoEditor", "Input.ini");

    private static readonly object _gate = new();
    private static Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private static DateTime _cachedWriteUtc = DateTime.MinValue;
    private static bool _loggedMissing;

    private static readonly Regex ActionLine = new(
        @"^ActionMappings=\(ActionName=""(?<name>[^""]*)""(?<flags>.*?),Key=(?<key>[A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    /// <summary>Action name → shortcut in K2's human syntax ("C", "Shift + Delete"). EMPTY when
    /// the player has never run the game, which the caller must read as "keep the shipped
    /// defaults" rather than "unbind everything". Re-parses only when the file changes.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            try
            {
                string file = ConfigFile;
                if (!File.Exists(file))
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        App.WriteLog($"[DS] no \"{file}\" — using the game's shipped defaults");
                    }
                    return _cache = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                _loggedMissing = false;

                var writeUtc = File.GetLastWriteTimeUtc(file);
                if (_cache.Count > 0 && writeUtc <= _cachedWriteUtc) return _cache;

                _cachedWriteUtc = writeUtc;
                _cache = Parse(file);
                App.WriteLog($"[DS] bindings loaded from Input.ini: {_cache.Count} keyboard actions");
                return _cache;
            }
            catch (Exception ex)
            {
                App.WriteLog($"[DS] bindings read failed: {ex.Message}");
                return _cache;
            }
        }
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // Shared read: the game may hold the file open, same lesson as the Elite readers.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);

        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            var m = ActionLine.Match(line.Trim());
            if (!m.Success) continue;

            string name = m.Groups["name"].Value;
            // First usable binding wins: a second row for the same action is the game's alternate
            // key, and overwriting would hand the tile whichever happened to come last in the file.
            if (map.ContainsKey(name)) continue;

            string? key = TranslateKey(m.Groups["key"].Value);
            if (key is null) continue;

            string flags = m.Groups["flags"].Value;
            var mods = new List<string>();
            if (flags.Contains("bCtrl=True", StringComparison.Ordinal)) mods.Add("Ctrl");
            if (flags.Contains("bShift=True", StringComparison.Ordinal)) mods.Add("Shift");
            if (flags.Contains("bAlt=True", StringComparison.Ordinal)) mods.Add("Alt");

            map[name] = mods.Count == 0 ? key : string.Join(" + ", mods) + " + " + key;
        }
        return map;
    }

    /// <summary>Unreal key names whose K2 spelling is not just the name itself. Punctuation keys
    /// are spelled the way <c>HotkeySender.Punct</c> expects them.</summary>
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.Ordinal)
    {
        ["SpaceBar"] = "Space",   ["Escape"] = "Esc",       ["BackSpace"] = "Backspace",
        ["Zero"] = "0",  ["One"] = "1",   ["Two"] = "2",    ["Three"] = "3", ["Four"] = "4",
        ["Five"] = "5",  ["Six"] = "6",   ["Seven"] = "7",  ["Eight"] = "8", ["Nine"] = "9",
        ["Equals"] = "=",          ["Apostrophe"] = "'",    ["Comma"] = ",",
        ["Period"] = ".",          ["Slash"] = "/",         ["Semicolon"] = ";",
        ["LeftBracket"] = "[",     ["RightBracket"] = "]",  ["Backslash"] = "\\",
        ["Tilde"] = "`",
        ["Tab"] = "Tab",           ["Enter"] = "Enter",     ["Delete"] = "Delete",
        ["Insert"] = "Insert",     ["Home"] = "Home",       ["End"] = "End",
        ["PageUp"] = "PageUp",     ["PageDown"] = "PageDown",
        ["Up"] = "Up",             ["Down"] = "Down",       ["Left"] = "Left",
        ["Right"] = "Right",       ["CapsLock"] = "CapsLock",
        ["NumLock"] = "NumLock",   ["ScrollLock"] = "ScrollLock",
    };

    /// <summary>The game's key token as K2 writes shortcuts, or null when K2 cannot press it:
    /// a mouse button or wheel direction, a gamepad button, a bare modifier (Deadside binds
    /// sprint to LeftShift and walk to LeftControl, and K2's shortcut syntax has no way to send a
    /// modifier on its own), or a numpad/unknown token. Null keeps the tile on the catalogue's
    /// shipped default — a tile that presses nothing beats one that presses the wrong key.</summary>
    private static string? TranslateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (NamedKeys.TryGetValue(key!, out var named)) return named;

        if (key!.Length == 1 && char.IsLetter(key[0])) return key.ToUpperInvariant();
        if (key.Length is 2 or 3 && key[0] == 'F' &&
            int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24) return "F" + fn;

        return null;
    }
}
