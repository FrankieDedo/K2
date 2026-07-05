using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>
/// Maps Windows Virtual Key codes to the shifted (alt) label shown on the top half
/// of each key cap, replicating BC's data-alt attribute.
///
/// Only symbol/number keys are listed — letter keys always shift to uppercase
/// which is already implied by the primary label.
///
/// Usage:
///   if (KeyLabelMap.AltLabel(_layout, vk) is string alt) { /* use alt */ }
/// </summary>
public static class KeyLabelMap
{
    // ── US ANSI ──────────────────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _ansiUs = new()
    {
        // Number row (VK 48–57) + flanking OEM
        { 192, "~"  },   // ` → ~
        {  49, "!"  },   // 1 → !
        {  50, "@"  },   // 2 → @
        {  51, "#"  },   // 3 → #
        {  52, "$"  },   // 4 → $
        {  53, "%"  },   // 5 → %
        {  54, "^"  },   // 6 → ^
        {  55, "&"  },   // 7 → &
        {  56, "*"  },   // 8 → *
        {  57, "("  },   // 9 → (
        {  48, ")"  },   // 0 → )
        { 189, "_"  },   // - → _
        { 187, "+"  },   // = → +
        // Brackets / backslash
        { 219, "{"  },   // [ → {
        { 221, "}"  },   // ] → }
        { 220, "|"  },   // \ → |
        // Middle row OEM
        { 186, ":"  },   // ; → :
        { 222, "\"" },   // ' → "
        // Bottom row OEM
        { 188, "<"  },   // , → <
        { 190, ">"  },   // . → >
        { 191, "?"  },   // / → ?
    };

    // ── Italian ISO ──────────────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoIt = new()
    {
        // Extra key before 1 (VK_OEM_3 / 192 in K2's IsoIt layout)
        { 192, "|"  },   // \ → |
        // Number row
        {  49, "!"  },   // 1 → !
        {  50, "\"" },   // 2 → "
        {  51, "£"  },   // 3 → £
        {  52, "$"  },   // 4 → $
        {  53, "%"  },   // 5 → %
        {  54, "&"  },   // 6 → &
        {  55, "/"  },   // 7 → /
        {  56, "("  },   // 8 → (
        {  57, ")"  },   // 9 → )
        {  48, "="  },   // 0 → =
        { 189, "?"  },   // ' → ?
        { 187, "^"  },   // ì → ^
        // Row 2 right side
        { 219, "é"  },   // è → é
        { 221, "*"  },   // + → *
        // Row 3 right side
        { 186, "ç"  },   // ò → ç
        { 222, "°"  },   // à → °
        { 220, "§"  },   // ù → §
        // Bottom row
        { 226, ">"  },   // < → >
        { 188, ";"  },   // , → ;
        { 190, ":"  },   // . → :
        { 191, "_"  },   // - → _
    };

    // ── English UK (ISO) ──────────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoUk = new()
    {
        { 192, "¬" },  // ` → ¬
        {  49, "!"  }, {  50, "\"" }, {  51, "£"  }, {  52, "$"  }, {  53, "%"  },
        {  54, "^"  }, {  55, "&"  }, {  56, "*"  }, {  57, "("  }, {  48, ")"  },
        { 189, "_"  }, { 187, "+"  },
        { 219, "{"  }, { 221, "}"  },
        { 186, ":"  }, { 222, "@"  }, { 220, "~"  },   // # → ~
        { 226, "|"  }, { 188, "<"  }, { 190, ">"  }, { 191, "?"  },
    };

    // ── German (QWERTZ, ISO) ──────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoDe = new()
    {
        { 192, "°"  },  // ^ → °
        {  49, "!"  }, {  50, "\"" }, {  51, "§"  }, {  52, "$"  }, {  53, "%"  },
        {  54, "&"  }, {  55, "/"  }, {  56, "("  }, {  57, ")"  }, {  48, "="  },
        { 189, "?"  },  // ß → ?
        { 187, "`"  },  // ´ → `
        { 219, "Ü"  }, { 221, "*"  },   // + → *
        { 186, "Ö"  }, { 222, "Ä"  }, { 220, "'"  },   // # → '
        { 226, ">"  }, { 188, ";"  }, { 190, ":"  }, { 191, "_"  },
    };

    // ── French (AZERTY, ISO) ──────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoFr = new()
    {
        // Number row: base = accent/symbol, shift = the digit
        {  49, "1"  }, {  50, "2"  }, {  51, "3"  }, {  52, "4"  }, {  53, "5"  },
        {  54, "6"  }, {  55, "7"  }, {  56, "8"  }, {  57, "9"  }, {  48, "0"  },
        { 189, "°"  },  // ) → °
        { 187, "+"  },  // = → +
        { 219, "¨"  },  // ^ → ¨
        { 221, "£"  },  // $ → £
        { 222, "%"  },  // ù → %
        { 220, "µ"  },  // * → µ
        { 226, ">"  },  // < → >
        {  77, "?"  },  // , → ?  (US-M position)
        { 188, "."  },  // ; → .
        { 190, "/"  },  // : → /
        { 191, "§"  },  // ! → §
    };

    // ── Spanish (ISO) ─────────────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoEs = new()
    {
        { 192, "ª"  },  // º → ª
        {  49, "!"  }, {  50, "\"" }, {  51, "·"  }, {  52, "$"  }, {  53, "%"  },
        {  54, "&"  }, {  55, "/"  }, {  56, "("  }, {  57, ")"  }, {  48, "="  },
        { 189, "?"  },  // ' → ?
        { 187, "¿"  },  // ¡ → ¿
        { 219, "^"  },  // ` → ^
        { 221, "*"  },  // + → *
        { 186, "Ñ"  }, { 222, "¨"  },  // ´ → ¨
        { 220, "Ç"  }, { 226, ">"  }, { 188, ";"  }, { 190, ":"  }, { 191, "_"  },
    };

    // ── Norwegian / Nordic (ISO) ──────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoNordic = new()
    {
        { 192, "§"  },  // | → §
        {  49, "!"  }, {  50, "\"" }, {  51, "#"  }, {  52, "¤"  }, {  53, "%"  },
        {  54, "&"  }, {  55, "/"  }, {  56, "("  }, {  57, ")"  }, {  48, "="  },
        { 189, "?"  },  // + → ?
        { 187, "`"  },  // \ → `
        { 219, "Å"  }, { 221, "^"  },  // ¨ → ^
        { 186, "Ø"  }, { 222, "Æ"  }, { 220, "*"  },  // ' → *
        { 226, ">"  }, { 188, ";"  }, { 190, ":"  }, { 191, "_"  },
    };

    // ── Portuguese (Portugal, ISO) ────────────────────────────────────────────
    private static readonly Dictionary<int, string> _isoPt = new()
    {
        { 192, "|"  },  // \ → |
        {  49, "!"  }, {  50, "\"" }, {  51, "#"  }, {  52, "$"  }, {  53, "%"  },
        {  54, "&"  }, {  55, "/"  }, {  56, "("  }, {  57, ")"  }, {  48, "="  },
        { 189, "?"  },  // ' → ?
        { 187, "»"  },  // « → »
        { 219, "*"  },  // + → *
        { 221, "`"  },  // ´ → `
        { 186, "Ç"  }, { 222, "ª"  },  // º → ª
        { 220, "^"  },  // ~ → ^
        { 226, ">"  }, { 188, ";"  }, { 190, ":"  }, { 191, "_"  },
    };

    private static readonly IReadOnlyDictionary<KeyboardLayoutType, Dictionary<int, string>> _map =
        new Dictionary<KeyboardLayoutType, Dictionary<int, string>>
        {
            { KeyboardLayoutType.AnsiUs,    _ansiUs    },
            { KeyboardLayoutType.IsoIt,     _isoIt     },
            { KeyboardLayoutType.IsoUk,     _isoUk     },
            { KeyboardLayoutType.IsoDe,     _isoDe     },
            { KeyboardLayoutType.IsoFr,     _isoFr     },
            { KeyboardLayoutType.IsoEs,     _isoEs     },
            { KeyboardLayoutType.IsoNordic, _isoNordic },
            { KeyboardLayoutType.IsoPt,     _isoPt     },
        };

    /// <summary>
    /// Returns the shifted (alt) label for a key, or <c>null</c> if none is defined.
    /// </summary>
    public static string? AltLabel(KeyboardLayoutType layout, int vk)
    {
        if (!_map.TryGetValue(layout, out var dict)) return null;
        return dict.TryGetValue(vk, out var alt) ? alt : null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  AltGr (level 3) and Shift+AltGr (level 4) legends.
    //  These are the symbols engraved on the bottom-right of physical keycaps
    //  (often in a different colour). Only keys that produce something on the
    //  AltGr layer are listed.
    // ══════════════════════════════════════════════════════════════════════════

    // ── AltGr (level 3) ───────────────────────────────────────────────────────
    private static readonly Dictionary<int, string> _altGrIt = new()
    {
        {  69, "€" },                       // e → €
        { 219, "[" }, { 221, "]" },         // è → [ , + → ]
        { 186, "@" }, { 222, "#" },         // ò → @ , à → #
    };

    private static readonly Dictionary<int, string> _altGrUk = new()
    {
        {  52, "€" },                       // 4 → €
    };

    private static readonly Dictionary<int, string> _altGrDe = new()
    {
        {  81, "@" }, {  69, "€" },
        {  50, "²" }, {  51, "³" },
        {  55, "{" }, {  56, "[" }, {  57, "]" }, {  48, "}" },
        { 189, "\\" },                      // ß → backslash
        { 221, "~" },                       // + → ~
        { 226, "|" },                       // <> → |
        {  77, "µ" },                       // m → µ
    };

    private static readonly Dictionary<int, string> _altGrFr = new()
    {
        {  50, "~" }, {  51, "#" }, {  52, "{" }, {  53, "[" }, {  54, "|" },
        {  55, "`" }, {  56, "\\" }, {  57, "^" }, {  48, "@" },
        { 189, "]" }, { 187, "}" },         // ) → ] , = → }
        {  69, "€" },
    };

    private static readonly Dictionary<int, string> _altGrEs = new()
    {
        {  49, "|" }, {  50, "@" }, {  51, "#" }, {  52, "~" }, {  54, "¬" },
        {  69, "€" },
        { 219, "[" }, { 221, "]" },         // ` → [ , + → ]
        { 222, "{" }, { 220, "}" },         // ´ → { , ç → }
    };

    private static readonly Dictionary<int, string> _altGrNordic = new()
    {
        {  50, "@" }, {  51, "£" },
        {  55, "{" }, {  56, "[" }, {  57, "]" }, {  48, "}" },
        {  69, "€" }, { 226, "|" },
    };

    private static readonly Dictionary<int, string> _altGrPt = new()
    {
        {  50, "@" }, {  51, "£" }, {  52, "§" },
        {  55, "{" }, {  56, "[" }, {  57, "]" }, {  48, "}" },
        {  69, "€" },
    };

    private static readonly IReadOnlyDictionary<KeyboardLayoutType, Dictionary<int, string>> _altGr =
        new Dictionary<KeyboardLayoutType, Dictionary<int, string>>
        {
            { KeyboardLayoutType.IsoIt,     _altGrIt     },
            { KeyboardLayoutType.IsoUk,     _altGrUk     },
            { KeyboardLayoutType.IsoDe,     _altGrDe     },
            { KeyboardLayoutType.IsoFr,     _altGrFr     },
            { KeyboardLayoutType.IsoEs,     _altGrEs     },
            { KeyboardLayoutType.IsoNordic, _altGrNordic },
            { KeyboardLayoutType.IsoPt,     _altGrPt     },
        };

    // ── Shift+AltGr (level 4) ─────────────────────────────────────────────────
    // Rare; on most ISO layouts the brackets sit directly on AltGr. Italian is
    // the notable case: [ ] are AltGr, { } are Shift+AltGr on the same keys.
    private static readonly Dictionary<int, string> _shiftAltGrIt = new()
    {
        { 219, "{" }, { 221, "}" },         // è → { , + → }
    };

    private static readonly IReadOnlyDictionary<KeyboardLayoutType, Dictionary<int, string>> _shiftAltGr =
        new Dictionary<KeyboardLayoutType, Dictionary<int, string>>
        {
            { KeyboardLayoutType.IsoIt, _shiftAltGrIt },
        };

    /// <summary>Returns the AltGr (level-3) legend for a key, or <c>null</c>.</summary>
    public static string? AltGrLabel(KeyboardLayoutType layout, int vk)
    {
        if (!_altGr.TryGetValue(layout, out var dict)) return null;
        return dict.TryGetValue(vk, out var s) ? s : null;
    }

    /// <summary>Returns the Shift+AltGr (level-4) legend for a key, or <c>null</c>.</summary>
    public static string? ShiftAltGrLabel(KeyboardLayoutType layout, int vk)
    {
        if (!_shiftAltGr.TryGetValue(layout, out var dict)) return null;
        return dict.TryGetValue(vk, out var s) ? s : null;
    }
}
