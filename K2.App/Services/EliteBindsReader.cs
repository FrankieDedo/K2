using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace K2.App.Services;

/// <summary>
/// Reads Elite Dangerous' own KEY BINDINGS, so the game profile presses what the commander
/// actually bound rather than what the game ships with.
///
/// <para>
/// <b>Why.</b> The curated profile used to hard-code the defaults from
/// <c>ControlSchemes\KeyboardMouseOnly.binds</c> (gear = L, scoop = Home, ...). Those are right
/// until the pilot rebinds a control — after which every tile silently presses the wrong key, and
/// the only cure was typing an override per key. This reads the truth from the game instead.
/// </para>
///
/// <para>
/// <b>Where.</b> Customised bindings live in
/// <c>%LOCALAPPDATA%\Frontier Developments\Elite Dangerous\Options\Bindings\</c> as
/// <c>&lt;Preset&gt;.&lt;major&gt;.&lt;minor&gt;.binds</c>, with <c>StartPreset*.start</c> naming
/// the active preset. That folder does NOT exist until the pilot opens the controls options at
/// least once — an untouched install is running a shipped preset, which is exactly what the
/// built-in defaults already encode, so "no folder" is a normal answer and not an error.
/// </para>
///
/// <para>
/// <b>Format</b> (verified against the game's own shipped presets, not guessed):
/// <code>
/// &lt;LandingGearToggle&gt;
///   &lt;Primary Device="Keyboard" Key="Key_L" /&gt;
///   &lt;Secondary Device="{NoDevice}" Key="" /&gt;
/// &lt;/LandingGearToggle&gt;
/// </code>
/// A binding can carry <c>&lt;Modifier Device="Keyboard" Key="Key_LeftShift" /&gt;</c> children.
/// Bindings on a joystick/HOTAS are skipped: there is no keystroke to send for them.
/// </para>
/// </summary>
internal static class EliteBindsReader
{
    /// <summary>Where the game keeps a pilot's customised bindings.</summary>
    public static string BindingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Frontier Developments", "Elite Dangerous", "Options", "Bindings");

    private static readonly object _gate = new();
    private static Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private static string? _cachedFile;
    private static DateTime _cachedWriteUtc = DateTime.MinValue;
    private static bool _loggedMissing;

    /// <summary>Control name → shortcut in K2's human syntax ("L", "Ctrl + G"). EMPTY when the
    /// pilot has never customised anything, which the caller must read as "keep the built-in
    /// defaults" rather than "unbind everything". Re-parses only when the file changes on disk.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            try
            {
                string? file = ActiveBindsFile();
                if (file is null)
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        App.WriteLog($"[ED] no custom bindings in \"{BindingsDir}\" — " +
                                     "using the game's shipped defaults");
                    }
                    return _cache = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                _loggedMissing = false;

                var writeUtc = File.GetLastWriteTimeUtc(file);
                if (file == _cachedFile && writeUtc <= _cachedWriteUtc) return _cache;

                _cachedFile = file;
                _cachedWriteUtc = writeUtc;
                _cache = Parse(file);
                App.WriteLog($"[ED] bindings loaded from \"{Path.GetFileName(file)}\": " +
                             $"{_cache.Count} keyboard controls");
                return _cache;
            }
            catch (Exception ex)
            {
                App.WriteLog($"[ED] bindings read failed: {ex.Message}");
                return _cache;
            }
        }
    }

    /// <summary>The .binds the game is actually using: the preset named by <c>StartPreset*.start</c>
    /// when we can match it, else the most recently written one — a pilot with several presets on
    /// disk is running the one they last saved.</summary>
    private static string? ActiveBindsFile()
    {
        if (!Directory.Exists(BindingsDir)) return null;

        var files = Directory.GetFiles(BindingsDir, "*.binds");
        if (files.Length == 0) return null;
        if (files.Length == 1) return files[0];

        foreach (var start in Directory.GetFiles(BindingsDir, "StartPreset*.start"))
        {
            foreach (var line in File.ReadAllLines(start))
            {
                string name = line.Trim();
                if (name.Length == 0) continue;
                // "Custom" matches Custom.4.0.binds / Custom.binds — the version infix moves with
                // the game, so match on the leading segment rather than the whole file name.
                var hit = files.FirstOrDefault(f =>
                    Path.GetFileName(f).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
        }

        return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // Shared read: the game may hold the file open, same lesson as EliteStatusReader.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        var root = XDocument.Load(fs).Root;
        if (root is null) return map;

        foreach (var el in root.Elements())
        {
            string? shortcut = ShortcutOf(el.Element("Primary")) ?? ShortcutOf(el.Element("Secondary"));
            if (shortcut is not null) map[el.Name.LocalName] = shortcut;
        }
        return map;
    }

    /// <summary>One binding as a K2 shortcut, or null when it isn't a keyboard binding we can
    /// reproduce (a HOTAS button, or a key with no <see cref="TranslateKey"/> spelling).</summary>
    private static string? ShortcutOf(XElement? node)
    {
        if (node is null) return null;
        if ((string?)node.Attribute("Device") != "Keyboard") return null;

        string? main = TranslateKey((string?)node.Attribute("Key"));
        if (main is null) return null;

        var mods = node.Elements("Modifier")
            .Where(m => (string?)m.Attribute("Device") == "Keyboard")
            .Select(m => ModifierName((string?)m.Attribute("Key")))
            .Where(m => m is not null)
            .Distinct()
            .ToList();

        return mods.Count == 0 ? main : string.Join(" + ", mods) + " + " + main;
    }

    private static string? ModifierName(string? edKey) => edKey switch
    {
        "Key_LeftShift" or "Key_RightShift"     => "Shift",
        "Key_LeftControl" or "Key_RightControl" => "Ctrl",
        "Key_LeftAlt" or "Key_RightAlt"         => "Alt",
        "Key_LeftWin" or "Key_RightWin"         => "Win",
        _                                       => null,
    };

    /// <summary>Named keys whose K2 spelling isn't just the token with <c>Key_</c> stripped.
    /// The set of key names the game can actually emit was taken from its own shipped presets.</summary>
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Key_Enter"]      = "Enter",
        ["Key_Escape"]     = "Esc",
        ["Key_Tab"]        = "Tab",
        ["Key_Backspace"]  = "Backspace",
        ["Key_Delete"]     = "Delete",
        ["Key_Insert"]     = "Insert",
        ["Key_Home"]       = "Home",
        ["Key_End"]        = "End",
        ["Key_PageUp"]     = "PageUp",
        ["Key_PageDown"]   = "PageDown",
        ["Key_Space"]      = "Space",
        ["Key_UpArrow"]    = "Up",
        ["Key_DownArrow"]  = "Down",
        ["Key_LeftArrow"]  = "Left",
        ["Key_RightArrow"] = "Right",
        ["Key_CapsLock"]   = "CapsLock",
        ["Key_NumLock"]    = "NumLock",
        ["Key_ScrollLock"] = "ScrollLock",
    };

    /// <summary>The game's key token as K2 writes shortcuts, or null when we cannot press it
    /// reliably (numpad and punctuation keys have no spelling in the Keys panel's vocabulary).
    /// Returning null keeps the tile on its built-in default — a tile that presses nothing is
    /// better than one that presses the wrong key.</summary>
    private static string? TranslateKey(string? edKey)
    {
        if (string.IsNullOrWhiteSpace(edKey)) return null;
        if (NamedKeys.TryGetValue(edKey!, out var named)) return named;
        if (!edKey!.StartsWith("Key_", StringComparison.OrdinalIgnoreCase)) return null;

        string bare = edKey["Key_".Length..];
        if (bare.Length == 1 && char.IsLetterOrDigit(bare[0])) return bare.ToUpperInvariant();
        if (bare.Length is 2 or 3 && (bare[0] is 'F' or 'f') &&
            int.TryParse(bare[1..], out int fn) && fn is >= 1 and <= 24) return "F" + fn;

        return null;
    }
}
