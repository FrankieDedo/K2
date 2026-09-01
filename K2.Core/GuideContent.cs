// GuideContent.cs — loads the in-app guides shown by GuideWindow.
//
// Content lives in Guides\guide.<lang>.md (EmbeddedResource in K2.Core, see
// K2.Core.csproj), one file per language. Each file is split into blocks by a
// marker line:
//
//     @@@ <key>
//
// where <key> is a dotted/colon path identifying the guide. Two families exist:
//   - device sections:  everest:keybinding, makalu:lighting, ...
//   - action picker:     picker:categories, picker:cat:input, picker:act:keys, ...
//
// The block body is a small markdown subset rendered by GuideWindow: "# " / "## "
// headings, "- " bullets (indented continuation lines allowed), blank-line
// paragraphs and **bold** inline spans.
//
// Resolution: the current UI language (Loc.CurrentLang) first, English as the
// fallback both for a missing language file and for a key a translated file
// happens not to contain.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace K2.Core;

public static class GuideContent
{
    // Parsed cache: lang -> (key -> body).
    private static readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Body markdown for a guide key, or null if neither the current
    /// language nor English defines it.</summary>
    public static string? Get(string key)
    {
        var lang = Load(Loc.CurrentLang);
        if (lang.TryGetValue(key, out var body)) return body;

        if (!string.Equals(Loc.CurrentLang, "en", StringComparison.OrdinalIgnoreCase))
        {
            var en = Load("en");
            if (en.TryGetValue(key, out var enBody)) return enBody;
        }
        return null;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        if (_cache.TryGetValue(lang, out var cached)) return cached;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? text = ReadResource($"guide.{lang}.md");
        if (text is not null) Parse(text, map);

        _cache[lang] = map;
        return map;
    }

    private static string? ReadResource(string fileSuffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        return null;
    }

    private static void Parse(string text, Dictionary<string, string> map)
    {
        string? currentKey = null;
        var buf = new List<string>();

        void Flush()
        {
            if (currentKey is not null)
                map[currentKey] = string.Join("\n", buf).Trim('\n');
            buf.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("@@@", StringComparison.Ordinal))
            {
                Flush();
                currentKey = raw[3..].Trim();
                continue;
            }
            if (currentKey is not null) buf.Add(raw);
        }
        Flush();
    }
}
