using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>
/// Everest 60 keyboard layout — positions of the 64 main-board keys plus a
/// decorative-only numpad accessory layout.
///
/// <para><b>Main board (64 keys):</b> ported 1:1 from BaseCampLinux's
/// <c>shared/ui_helpers.py</c> <c>_build_kb60_layout()</c> (label, row,
/// order), rescaled from that project's 0.82 Tk-canvas factor to K2's native
/// 30px key / 2px gap grid (matching <see cref="EverestKeyboardLayout"/>).
/// <see cref="KeyDef.MatrixId"/> is repurposed here to hold the **LED index**
/// (0-63), not a VK code: the Everest 60 has no known key-remap protocol
/// (raw HID, firmware protocol never reverse-engineered by any source), so
/// there is nothing to capture/remap — the only thing these 64 keys drive is
/// per-key custom lighting via <c>Everest60Protocol.SendCustom</c>. The LED
/// index order matches <c>Everest60Protocol.LedIndex</c> exactly (both
/// ported from the same controller.py source, cross-checked independently).
/// 64 keys total, **no backtick key** — confirmed hardware quirk of this
/// board, not an omission.</para>
///
/// <para><b>Numpad accessory:</b> no source (Base Camp Windows, BaseCampLinux,
/// or the decompiled DB schema) has ever modeled a LED/remap protocol for
/// it — <c>has_numpad=False</c> everywhere it's referenced in BaseCampLinux.
/// This layout is a hand-estimated approximation (same "eyeballed" spirit as
/// the Makalu hotspots) for **visual/decorative purposes only**: every key
/// has <see cref="KeyDef.MatrixId"/> = -1 and is not wired to any click
/// handler or lighting command.</para>
/// </summary>
public static class Everest60KeyboardLayout
{
    private const double U  = 30;  // 1U = standard key width (native K2 scale)
    private const double G  = 2;   // gap between keys
    private const double RH = U + G; // row height (vertical pitch)

    private const double PL = 14;  // padding left
    private const double PT = 14;  // padding top

    /// <summary>The 64 main-board keys (US ANSI legends), row-major, LED index
    /// 0-63 (matches <c>Everest60Protocol.LedIndex</c> order).</summary>
    public static readonly KeyDef[] MainBoard = BuildMainBoard();

    // ---- Locale legend cache (lazy) ----
    private static readonly Dictionary<KeyboardLayoutType, KeyDef[]> _mainBoardCache = new();

    /// <summary>
    /// Returns the 64 main-board keys with locale-specific legends. Geometry
    /// and <see cref="KeyDef.MatrixId"/> (LED index) never change — this is a
    /// single fixed physical board (no ISO variant: confirmed no backtick key,
    /// no split-shift/ISO-102 key), so a "layout" here only swaps the printed
    /// character on each key, same physical position/LED index as ANSI US.
    /// </summary>
    public static KeyDef[] GetMainBoard(KeyboardLayoutType layout)
    {
        if (layout == KeyboardLayoutType.AnsiUs) return MainBoard;
        if (_mainBoardCache.TryGetValue(layout, out var cached)) return cached;

        var overrides = LocaleLegends.For(layout);
        var built = new KeyDef[MainBoard.Length];
        for (int i = 0; i < MainBoard.Length; i++)
        {
            var kd = MainBoard[i];
            built[i] = overrides.TryGetValue(kd.MatrixId, out var label)
                ? kd with { Label = label }
                : kd;
        }
        _mainBoardCache[layout] = built;
        return built;
    }

    /// <summary>Decorative-only numpad accessory keys (MatrixId = -1: not
    /// paintable, not clickable — no known protocol for this block).</summary>
    public static readonly KeyDef[] Numpad = BuildNumpad();

    // ======================================================================
    // Main board — 5 rows, 64 keys total (idx 0-63)
    // ======================================================================

    private static KeyDef[] BuildMainBoard()
    {
        var k = new List<KeyDef>();
        int idx = 0;
        double y = PT;

        // Row 0: Esc 1-0 - = Backspace (14 keys, idx 0-13) — no backtick
        Row(k, ref idx, PL, y,
            (0, "Esc", U), (0, "1", U), (0, "2", U), (0, "3", U), (0, "4", U),
            (0, "5", U), (0, "6", U), (0, "7", U), (0, "8", U), (0, "9", U),
            (0, "0", U), (0, "-", U), (0, "=", U), (0, "⭠", 60));

        // Row 1: Tab Q-P [ ] \ (14 keys, idx 14-27)
        y += RH;
        Row(k, ref idx, PL, y,
            (0, "Tab", 45), (0, "Q", U), (0, "W", U), (0, "E", U), (0, "R", U),
            (0, "T", U), (0, "Y", U), (0, "U", U), (0, "I", U), (0, "O", U),
            (0, "P", U), (0, "[", U), (0, "]", U), (0, "\\", 45));

        // Row 2: Caps A-L ; ' Enter (13 keys, idx 28-40)
        y += RH;
        Row(k, ref idx, PL, y,
            (0, "Caps", 53), (0, "A", U), (0, "S", U), (0, "D", U), (0, "F", U),
            (0, "G", U), (0, "H", U), (0, "J", U), (0, "K", U), (0, "L", U),
            (0, ";", U), (0, "'", U), (0, "↵", 67));

        // Row 3: Shift Z-/ small-Shift ↑ Del (14 keys, idx 41-54)
        y += RH;
        Row(k, ref idx, PL, y,
            (0, "⇧", 60), (0, "Z", U), (0, "X", U), (0, "C", U), (0, "V", U),
            (0, "B", U), (0, "N", U), (0, "M", U), (0, ",", U), (0, ".", U),
            (0, "/", U), (0, "⇧", U), (0, "↑", U), (0, "Del", U));

        // Row 4: Ctrl Win Alt Space Alt Fn ← ↓ → (9 keys, idx 55-63).
        // ← ↓ → align under row 3's small-Shift/↑/Del columns respectively
        // (row 3's last 3 keys, in order: small Shift, ↑, Del).
        double arrowUpX  = k[^2].X; // ↑ from row 3
        double delX      = k[^1].X; // Del from row 3
        double leftArrowX = arrowUpX - U - G;
        y += RH;
        Row(k, ref idx, PL, y,
            (0, "Ctrl", 38), (0, "⊞", U), (0, "Alt", 38), (0, "", 194),
            (0, "Alt", U), (0, "Fn", U));
        k.Add(new KeyDef(idx++, "←", leftArrowX, y, U, U));
        k.Add(new KeyDef(idx++, "↓", arrowUpX,   y, U, U));
        k.Add(new KeyDef(idx++, "→", delX,       y, U, U));

        return k.ToArray();
    }

    /// <summary>Adds a row of keys, auto-incrementing the shared LED index.</summary>
    private static void Row(List<KeyDef> list, ref int idx, double x0, double y,
                            params (int _, string label, double w)[] keys)
    {
        double x = x0;
        foreach (var (_, label, w) in keys)
        {
            list.Add(new KeyDef(idx++, label, x, y, w, U));
            x += w + G;
        }
    }

    // ======================================================================
    // LED index -> Windows Virtual Key code, for the number/symbol/letter keys
    // only (idx <-> physical key mapping documented in BuildMainBoard above).
    // Lets MainWindow.Everest60.cs query the SAME KeyLabelMap (AltGr/Shift
    // corner legends) that Everest Max's board uses via its own MatrixId,
    // which IS a VK code there (see KeyboardLayout.cs) — Everest 60's own
    // MatrixId is the LED index instead (see BuildMainBoard's doc comment),
    // so it needs this separate bridge to reach the same VK-keyed data.
    // Modifier/whitespace/nav keys (Esc, Tab, Caps, Enter, Shift, Ctrl, Win,
    // Alt, Space, Fn, arrows, Del, Backspace) have no entry — KeyLabelMap has
    // no AltGr/Shift legends for them either, so a lookup miss is correct.
    // ======================================================================
    internal static readonly IReadOnlyDictionary<int, int> LedIndexToVk = new Dictionary<int, int>
    {
        // Row 0: 1-0 - =
        { 1,49 },{ 2,50 },{ 3,51 },{ 4,52 },{ 5,53 },{ 6,54 },{ 7,55 },{ 8,56 },{ 9,57 },{ 10,48 },
        { 11,189 },{ 12,187 },
        // Row 1: Q-P [ ] \
        { 15,81 },{ 16,87 },{ 17,69 },{ 18,82 },{ 19,84 },{ 20,89 },{ 21,85 },{ 22,73 },{ 23,79 },{ 24,80 },
        { 25,219 },{ 26,221 },{ 27,220 },
        // Row 2: A-L ; '
        { 29,65 },{ 30,83 },{ 31,68 },{ 32,70 },{ 33,71 },{ 34,72 },{ 35,74 },{ 36,75 },{ 37,76 },
        { 38,186 },{ 39,222 },
        // Row 3: Z-M , . /
        { 42,90 },{ 43,88 },{ 44,67 },{ 45,86 },{ 46,66 },{ 47,78 },{ 48,77 },{ 49,188 },{ 50,190 },{ 51,191 },
    };

    // ======================================================================
    // Locale legend overrides, keyed by LED index (see BuildMainBoard above
    // for the idx <-> physical key mapping). Values ported from Base Camp's
    // locale legend set, same content as EverestKeyboardLayout.IsoLegends
    // (VK-keyed there) but re-keyed to this board's LED indices and with the
    // two entries this board has no physical key for dropped: VK 192 (` —
    // this board has no backtick key) and VK 226 (ISO-102 <> — this board
    // has no ISO variant).
    // ======================================================================
    private static class LocaleLegends
    {
        public static IReadOnlyDictionary<int, string> For(KeyboardLayoutType layout) => layout switch
        {
            KeyboardLayoutType.IsoIt     => It,
            KeyboardLayoutType.IsoUk     => Uk,
            KeyboardLayoutType.IsoDe     => De,
            KeyboardLayoutType.IsoFr     => Fr,
            KeyboardLayoutType.IsoEs     => Es,
            KeyboardLayoutType.IsoNordic => Nordic,
            KeyboardLayoutType.IsoPt     => Pt,
            _ => Empty,
        };

        private static readonly Dictionary<int, string> Empty = new();

        // idx11="-" idx12="=" idx25="[" idx26="]" idx27="\" idx38=";" idx39="'" idx51="/"
        private static readonly Dictionary<int, string> It = new()
        {
            {11,"'"},{12,"ì"},{25,"è"},{26,"+"},{38,"ò"},{39,"à"},{27,"ù"},{51,"-"},
        };

        private static readonly Dictionary<int, string> Uk = new()
        {
            {27,"#"},
        };

        // German QWERTZ: idx20 (Y-position) -> "Z", idx42 (Z-position) -> "Y"
        private static readonly Dictionary<int, string> De = new()
        {
            {11,"ß"},{12,"´"},{20,"Z"},{42,"Y"},{25,"ü"},{26,"+"},{38,"ö"},{39,"ä"},{27,"#"},{51,"-"},
        };

        // French AZERTY: number row + Q/W<->A/Z swap + M relocation.
        private static readonly Dictionary<int, string> Fr = new()
        {
            {1,"&"},{2,"é"},{3,"\""},{4,"'"},{5,"("},{6,"-"},{7,"è"},{8,"_"},{9,"ç"},{10,"à"},{11,")"},
            {15,"A"},{16,"Z"},{25,"^"},{26,"$"},
            {29,"Q"},{38,"M"},{39,"ù"},{27,"*"},
            {42,"W"},{48,","},{49,";"},{50,":"},{51,"!"},
        };

        private static readonly Dictionary<int, string> Es = new()
        {
            {11,"'"},{12,"¡"},{25,"`"},{26,"+"},{38,"ñ"},{39,"´"},{27,"ç"},{51,"-"},
        };

        private static readonly Dictionary<int, string> Nordic = new()
        {
            {11,"+"},{12,"\\"},{25,"å"},{26,"¨"},{38,"ø"},{39,"æ"},{27,"'"},{51,"-"},
        };

        private static readonly Dictionary<int, string> Pt = new()
        {
            {11,"'"},{12,"«"},{25,"+"},{26,"´"},{38,"ç"},{39,"º"},{27,"~"},{51,"-"},
        };
    }

    // ======================================================================
    // Numpad accessory — decorative only, hand-estimated geometry.
    // Modeled on the Everest Max numpad block (NumLock/-/=/-, 7-8-9-+, 4-5-6,
    // 1-2-3-Enter, 0-.) — no verified source for this device's numpad.
    // ======================================================================

    private static KeyDef[] BuildNumpad()
    {
        var k = new List<KeyDef>();
        const double npL = 14;
        const double npT = 14;
        double y = npT;

        void R(double yy, params (string label, double w)[] keys)
        {
            double x = npL;
            foreach (var (label, w) in keys)
            {
                k.Add(new KeyDef(-1, label, x, yy, w, U));
                x += w + G;
            }
        }

        R(y, ("Num", U), ("/", U), ("*", U), ("-", U));
        y += RH;
        R(y, ("7", U), ("8", U), ("9", U));
        k.Add(new KeyDef(-1, "+", npL + 3 * (U + G), y, U, RH + U));
        y += RH;
        R(y, ("4", U), ("5", U), ("6", U));
        y += RH;
        R(y, ("1", U), ("2", U), ("3", U));
        k.Add(new KeyDef(-1, "↵", npL + 3 * (U + G), y, U, RH + U));
        y += RH;
        R(y, ("0", 62), (".", U));

        return k.ToArray();
    }
}
