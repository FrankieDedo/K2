using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace K2.Core;

/// <summary>
/// Lightweight localization loader — works like Android's strings.xml.
///
/// At startup call <see cref="Init"/> (or let the static constructor handle it).
/// Retrieve a string with <see cref="Get"/> or the indexer <c>Loc.S["key"]</c>.
///
/// Resolution order:
///   1. <c>Strings.{lang}.xml</c> next to the running executable
///   2. Embedded default <c>Strings.xml</c> shipped inside K2.Core
///
/// The language code comes from (in order):
///   - <c>K2.lang</c> file next to the running executable  (written by <see cref="SetLanguage"/>)
///   - <c>K2_LANG</c> environment variable
///   - Current UI culture's two-letter ISO code (e.g. "it", "de")
/// Set <c>K2_LANG=en</c> (or write "en" in K2.lang) to force English (built-in default).
///
/// To switch language at runtime call <see cref="SetLanguage"/> — it saves the choice
/// and raises <see cref="RestartRequested"/> so the host app can restart.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    /// <summary>The language code currently in use (e.g. "en", "it").</summary>
    public static string CurrentLang { get; private set; } = "en";

    /// <summary>
    /// Raised when <see cref="SetLanguage"/> is called with a different language.
    /// The host app should restart (or fully reload) to apply the new strings.
    /// The argument is the newly selected language code.
    /// </summary>
    public static event Action<string>? RestartRequested;

    /// <summary>Indexer shorthand: <c>Loc.S["key"]</c>.</summary>
    public static IReadOnlyDictionary<string, string> S => _strings;

    /// <summary>
    /// Get a localized string by key.  Supports <c>{0}</c>, <c>{1}</c>… placeholders
    /// via optional <paramref name="args"/>.
    /// Returns the key itself (wrapped in <c>[key]</c>) if not found — makes
    /// missing translations visible without crashing.
    /// </summary>
    public static string Get(string key, params object[] args)
    {
        EnsureInit();
        if (!_strings.TryGetValue(key, out var value))
            return $"[{key}]";
        return args.Length > 0 ? string.Format(value, args) : value;
    }

    /// <summary>
    /// Saves <paramref name="lang"/> to the <c>K2.lang</c> file next to the
    /// executable, then raises <see cref="RestartRequested"/> so the host app
    /// can restart and pick up the new language.
    /// Always writes and restarts — never skips silently.
    /// </summary>
    public static void SetLanguage(string lang)
    {
        lang = lang.Trim().ToLowerInvariant();
        try
        {
            var langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "K2.lang");
            File.WriteAllText(langFile, lang);
        }
        catch { /* non-fatal */ }

        RestartRequested?.Invoke(lang);
    }

    /// <summary>
    /// Explicitly initialize the localization system.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        // 1. Load embedded defaults (English)
        LoadEmbeddedDefaults();

        // 2. Determine language (K2.lang file > env var > UI culture)
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var langFile = Path.Combine(exeDir, "K2.lang");

        string? lang = null;
        if (File.Exists(langFile))
        {
            try { lang = File.ReadAllText(langFile).Trim(); } catch { }
        }
        lang ??= Environment.GetEnvironmentVariable("K2_LANG");
        lang ??= "en"; // default: English (user can switch via the language menu; choice is saved to K2.lang)

        lang = lang.ToLowerInvariant();
        CurrentLang = lang;

        if (string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
            return; // English defaults already loaded

        // 3. Try to load override file next to the executable
        var overridePath = Path.Combine(exeDir, $"Strings.{lang}.xml");
        if (File.Exists(overridePath))
            LoadFromFile(overridePath);
    }

    // ── private ──────────────────────────────────────────────────────

    private static void EnsureInit()
    {
        if (!_initialized) Init();
    }

    private static void LoadEmbeddedDefaults()
    {
        // The Strings.xml is embedded as EmbeddedResource in K2.Core
        var asm = Assembly.GetExecutingAssembly();
        var resName = "K2.Core.Strings.xml";

        using var stream = asm.GetManifestResourceStream(resName);
        if (stream == null)
        {
            // Fallback: try loading from file next to the assembly
            var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Strings.xml");
            if (File.Exists(fallback))
                LoadFromFile(fallback);
            return;
        }

        var doc = XDocument.Load(stream);
        ParseXml(doc);
    }

    private static void LoadFromFile(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            ParseXml(doc);
        }
        catch
        {
            // Silently ignore malformed override files — keep defaults
        }
    }

    private static void ParseXml(XDocument doc)
    {
        if (doc.Root == null) return;
        foreach (var el in doc.Root.Elements("string"))
        {
            var name = el.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name))
                _strings[name] = el.Value;
        }
    }
}
