using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace K2.Core;

/// <summary>
/// The emoji offered by <see cref="EmojiPickerDialog"/>: name + category for every emoji
/// K2 can both type and draw.
///
/// Source data is <c>Assets/emoji_catalog.tsv</c> (embedded), generated from Unicode's own
/// <c>emoji-test.txt</c> (Emoji 15.1) — one row per emoji: codepoint, group, CLDR name.
/// It was pre-filtered to SINGLE-codepoint emoji: multi-codepoint sequences (ZWJ families,
/// skin-tone variants, country flags) need the font's GSUB table to resolve to one composed
/// glyph, which <see cref="EmojiGlyphRenderer"/> deliberately doesn't parse — offering them
/// would mean a picker entry that types fine but draws as two half-glyphs on a display key.
/// Entries the installed Segoe UI Emoji build has no color art for are dropped at load time
/// on top of that, so what the picker shows is exactly what a key can display.
/// </summary>
public static class EmojiCatalog
{
    public sealed record EmojiEntry(string Emoji, string Name, string Group)
    {
        /// <summary>Lower-cased name, kept ready for the picker's per-keystroke search.</summary>
        public string SearchName { get; } = Name.ToLowerInvariant();
    }

    private static readonly object Sync = new();
    private static IReadOnlyList<EmojiEntry>? _all;
    private static IReadOnlyList<string>? _groups;

    /// <summary>Every renderable emoji, in Unicode's own order (which is grouped by category
    /// and roughly by theme within it — the order users expect from any emoji picker).</summary>
    public static IReadOnlyList<EmojiEntry> All
    {
        get { EnsureLoaded(); return _all!; }
    }

    /// <summary>Category names as they appear in the catalog ("Smileys &amp; Emotion",
    /// "People &amp; Body", …), in catalog order — see <see cref="LocalizedGroup"/> for what
    /// the UI actually shows.</summary>
    public static IReadOnlyList<string> Groups
    {
        get { EnsureLoaded(); return _groups!; }
    }

    /// <summary>Localized label for one of the Unicode category names in
    /// <see cref="Groups"/> — falls back to the English name itself for a category the
    /// string table doesn't cover (a newer Unicode revision adding a group).</summary>
    public static string LocalizedGroup(string group) => group switch
    {
        "Smileys & Emotion" => Loc.Get("emoji_grp_smileys"),
        "People & Body"     => Loc.Get("emoji_grp_people"),
        "Animals & Nature"  => Loc.Get("emoji_grp_animals"),
        "Food & Drink"      => Loc.Get("emoji_grp_food"),
        "Travel & Places"   => Loc.Get("emoji_grp_travel"),
        "Activities"        => Loc.Get("emoji_grp_activities"),
        "Objects"           => Loc.Get("emoji_grp_objects"),
        "Symbols"           => Loc.Get("emoji_grp_symbols"),
        "Flags"             => Loc.Get("emoji_grp_flags"),
        _                   => group,
    };

    /// <summary>The catalog entry for an emoji string (as stored in an action value), or null
    /// when it isn't one of ours — used to label a key/action with the emoji's name.</summary>
    public static EmojiEntry? Find(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        int? cp = EmojiGlyphRenderer.SingleCodepoint(emoji);
        if (cp is null) return null;
        string normalized = char.ConvertFromUtf32(cp.Value);
        return All.FirstOrDefault(e => e.Emoji == normalized);
    }

    /// <summary>Entries matching a category (null/empty = all) and a free-text query matched
    /// against the emoji's English name — every whitespace-separated term must appear, so
    /// "red heart" narrows instead of widening.</summary>
    public static IEnumerable<EmojiEntry> Search(string? group, string? query)
    {
        IEnumerable<EmojiEntry> source = All;
        if (!string.IsNullOrEmpty(group))
            source = source.Where(e => e.Group == group);

        string[] terms = (query ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return source;

        return source.Where(e => terms.All(t => e.SearchName.Contains(t, StringComparison.Ordinal)));
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_all is not null) return;

            var entries = new List<EmojiEntry>();
            try { entries = Parse(ReadCatalogText()); }
            catch (Exception) { /* missing/corrupt resource: empty picker, no crash */ }

            _all    = entries;
            _groups = entries.Select(e => e.Group).Distinct().ToList();
        }
    }

    private static List<EmojiEntry> Parse(string text)
    {
        var entries = new List<EmojiEntry>();
        foreach (var line in text.Split('\n'))
        {
            var row = line.Trim('\r', ' ');
            if (row.Length == 0) continue;

            var parts = row.Split('\t');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out int cp))
                continue;

            // Drop what this machine's font can't actually draw in color — better a slightly
            // shorter picker than an entry that turns into an empty key image.
            if (!EmojiGlyphRenderer.HasColorGlyph(cp)) continue;

            entries.Add(new EmojiEntry(char.ConvertFromUtf32(cp), parts[2], parts[1]));
        }
        return entries;
    }

    private static string ReadCatalogText()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("emoji_catalog.tsv", StringComparison.Ordinal));
        if (name is null) return "";

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
