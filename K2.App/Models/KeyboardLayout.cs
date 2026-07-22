using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace K2.App.Models;

/// <summary>
/// Definition of a single key in the keyboard view.
/// Coordinates are relative to the Canvas (board_left or board_right).
/// </summary>
public readonly record struct KeyDef(
    int    MatrixId,  // VK code reported by the SDK KEY_CALLBACK callback
    string Label,     // label displayed on the key
    double X,         // left position in the Canvas
    double Y,         // top position in the Canvas
    double W,         // width
    double H,         // height
    int    NumpadIndex = -1  // Everest 60 numpad accessory key index (0-16,
                              // same order as Everest60Protocol.NumpadLedIndex);
                              // -1 for every other key on every other board
);

/// <summary>Physical keyboard layout type. Mirrors Base Camp's selectable set
/// (English/UK/French/German/Italian/Nordic/Spanish/Portuguese; Hebrew/Korean TBD).</summary>
public enum KeyboardLayoutType
{
    AnsiUs,      // English (US) — ANSI
    IsoIt,       // Italian — ISO
    IsoUk,       // English (UK) — ISO
    IsoDe,       // German (QWERTZ) — ISO
    IsoFr,       // French (AZERTY) — ISO
    IsoEs,       // Spanish — ISO
    IsoNordic,   // Norwegian/Nordic — ISO
    IsoPt,       // Portuguese — ISO
    // Future: Hebrew, Korean (dual-alphabet legends)
}

/// <summary>
/// Everest Max keyboard layout — positions of all keys in the Canvas.
///
/// Canvas dimensions: board_left 642×260, board_right 166×260.
/// Values derived from Base Camp CSS (keyboard.css):
///   standard key: 30×30, gap ~2px, row height 32px (30+2 margin).
///   Special keys have variable widths (see keyboard.css).
///
/// MatrixIds are **Windows Virtual Key** (VK) codes, extracted from
/// the <c>data-id</c> field of Base Camp's Razor views. They match
/// the <c>wMatrix</c> parameter of the <c>KEY_CALLBACK</c> callback in
/// <c>SDKDLL.dll</c> and <c>DLLMatrixIndex</c> in the Base Camp DB.
///
/// ⚠ These are NOT PS/2 scan codes (those are in the
///   <c>data-matrixId</c> HTML column, used only for lighting).
///
/// Supports multiple layouts (ANSI US, ISO IT, ...). To add a new one,
/// create a <c>BuildBoardLeft_XxxYy()</c> method and register it
/// in <see cref="GetBoardLeft"/>.
/// </summary>
public static class EverestKeyboardLayout
{
    // ---- Geometry constants (from BC CSS) ----
    private const double U  = 30;   // 1U = standard key width
    private const double G  = 2;    // gap between keys
    private const double RH = U + G; // row height (vertical pitch)

    // Inner margins of the Canvas board_left (key area inside the image)
    private const double PL = 27;   // padding left
    private const double PT = 47;   // padding top

    // Bottom-row widths — sized to fit within navStart
    // (7 modifier × 38px) + Space(196) + 7 gap(2) = 266+196+14 = 476
    private const double ModW   = 38;  // bottom row modifier key
    private const double SpaceW = 196; // spacebar

    /// <summary>Keys of board_right (numpad, 166×260).
    /// Identical for all layouts.</summary>
    public static readonly KeyDef[] BoardRight = BuildBoardRight();

    // ---- Layout cache (lazy) ----
    private static readonly Dictionary<KeyboardLayoutType, KeyDef[]> _cache = new();

    /// <summary>Returns the board_left keys for the requested layout.</summary>
    public static KeyDef[] GetBoardLeft(KeyboardLayoutType layout)
    {
        if (_cache.TryGetValue(layout, out var cached)) return cached;

        KeyDef[] built = layout switch
        {
            KeyboardLayoutType.AnsiUs    => BuildBoardLeft_AnsiUs(),
            KeyboardLayoutType.IsoIt     => BuildBoardLeft_Iso(IsoLegends.It),
            KeyboardLayoutType.IsoUk     => BuildBoardLeft_Iso(IsoLegends.Uk),
            KeyboardLayoutType.IsoDe     => BuildBoardLeft_Iso(IsoLegends.De),
            KeyboardLayoutType.IsoFr     => BuildBoardLeft_Iso(IsoLegends.Fr),
            KeyboardLayoutType.IsoEs     => BuildBoardLeft_Iso(IsoLegends.Es),
            KeyboardLayoutType.IsoNordic => BuildBoardLeft_Iso(IsoLegends.Nordic),
            KeyboardLayoutType.IsoPt     => BuildBoardLeft_Iso(IsoLegends.Pt),
            _ => BuildBoardLeft_AnsiUs(),
        };
        _cache[layout] = built;
        return built;
    }

    /// <summary>Detects the keyboard layout from the current Windows locale.</summary>
    public static KeyboardLayoutType DetectLayout()
    {
        try
        {
            // GetKeyboardLayout(0) returns the foreground thread's HKL.
            // The low 16 bits are the Language ID (LANGID).
            nint hkl = GetKeyboardLayout(0);
            int langId = (int)(hkl.ToInt64() & 0xFFFF);

            // LANGID: primary language (bits 0-9) + sublanguage (bits 10-15)
            // Italian = primary 0x10 → LANGID 0x0410 (it-IT)
            int primary = langId & 0x3FF;
            int sub     = (langId >> 10) & 0x3F;
            return primary switch
            {
                0x10 => KeyboardLayoutType.IsoIt,                 // Italian
                0x07 => KeyboardLayoutType.IsoDe,                 // German (QWERTZ)
                0x0C => KeyboardLayoutType.IsoFr,                 // French (AZERTY)
                0x0A => KeyboardLayoutType.IsoEs,                 // Spanish
                0x14 => KeyboardLayoutType.IsoNordic,            // Norwegian
                0x16 => KeyboardLayoutType.IsoPt,                 // Portuguese
                0x09 => sub == 0x02 ? KeyboardLayoutType.IsoUk   // English UK
                                    : KeyboardLayoutType.AnsiUs, // English (other) → US
                _ => KeyboardLayoutType.AnsiUs,
            };
        }
        catch
        {
            return KeyboardLayoutType.AnsiUs;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    // ======================================================================
    // Helper to build rows
    // ======================================================================

    private static void Row(List<KeyDef> list, double x0, double y,
                            params (int id, string label, double w)[] keys)
    {
        double x = x0;
        foreach (var (id, label, w) in keys)
        {
            list.Add(new KeyDef(id, label, x, y, w, U));
            x += w + G;
        }
    }

    /// <summary>Adds a key with a custom height (e.g. tall ISO Enter).</summary>
    private static void Key(List<KeyDef> list, int id, string label,
                            double x, double y, double w, double h)
    {
        list.Add(new KeyDef(id, label, x, y, w, h));
    }

    // ======================================================================
    // VK code constants — Windows Virtual Key codes used by the SDK
    // ======================================================================
    //
    // Letters: VK_A=65 .. VK_Z=90 (match uppercase ASCII)
    // Numbers: VK_0=48 .. VK_9=57 (match ASCII '0'..'9')
    // F-keys:  VK_F1=112 .. VK_F12=123
    // Modifier (left/right specific): LShift=160, RShift=161,
    //   LCtrl=162, RCtrl=163, LAlt=164, RAlt=165
    // Nav:     Ins=96*, Home=103*, PgUp=105*, Del=110*, End=97*, PgDn=99*
    //   (*match VK_NUMPAD*; the SDK doesn't distinguish nav from numpad)
    // Numpad:  VK_NUMPAD0=96 .. VK_NUMPAD9=105, +=107, -=109, *=106,
    //   /=111, .=110, NumLock=144
    // OEM:     `=192, -=189, ==187, [=219, ]=221, \=220, ;=186,
    //   '=222, ,=188, .=190, /=191, ISO <>=226
    // Misc:    Esc=27, Tab=9, Backspace=8, CapsLock=20, Enter=13,
    //   Space=32, PrtSc=44, ScrLk=145, Pause=19, LWin=91, RWin=92
    //   FN=261 (custom Mountain code, not a standard Windows one)

    // ======================================================================
    // Rows common to all layouts
    // ======================================================================

    /// <summary>Row 0 (F-row) + PrtSc/ScrLk/Pause — same for all layouts.</summary>
    private static double AddFRow(List<KeyDef> list, double x0, double y,
                                  out double navStart)
    {
        Row(list, x0, y, (27, "Esc", U));
        double fxStart = x0 + U + G + 15;
        Row(list, fxStart, y,
            (112, "F1", U), (113, "F2", U), (114, "F3", U), (115, "F4", U));
        double f5Start = fxStart + 4 * (U + G) + 10;
        Row(list, f5Start, y,
            (116, "F5", U), (117, "F6", U), (118, "F7", U), (119, "F8", U));
        double f9Start = f5Start + 4 * (U + G) + 10;
        Row(list, f9Start, y,
            (120, "F9", U), (121, "F10", U), (122, "F11", U), (123, "F12", U));
        navStart = f9Start + 4 * (U + G) + 45;
        Row(list, navStart, y,
            (44, "PRT SCN", U), (145, "SCR LK", U), (19, "PAUSE", U));
        return y;
    }

    /// <summary>Bottom row (modifiers + arrows) — same for all layouts.</summary>
    private static void AddBottomRow(List<KeyDef> list, double x0, double y,
                                     double navStart)
    {
        Row(list, x0, y,
            (162, "CTRL", ModW), (91, "Win", ModW), (164, "ALT", ModW),
            (32, "", SpaceW), // BC keyboard.css: space bar has no legend (content: none)
            (165, "ALT", ModW), (92, "Win", ModW), (261, "FN", ModW),
            (163, "CTRL", ModW));
        Row(list, navStart, y,
            (37, "←", U), (40, "↓", U), (39, "→", U));
    }

    // ======================================================================
    // ANSI US
    // ======================================================================

    private static KeyDef[] BuildBoardLeft_AnsiUs()
    {
        var k = new List<KeyDef>();
        double x0 = PL;

        // ---- Row 0: F-row ----
        double y = PT;
        AddFRow(k, x0, y, out double navStart);

        // ---- Row 1: ` 1-0 -= Backspace | Ins Home PgUp ----
        y += RH + 6;
        Row(k, x0, y,
            (192, "`", U), (49, "1", U), (50, "2", U), (51, "3", U),
            (52, "4", U), (53, "5", U), (54, "6", U), (55, "7", U),
            (56, "8", U), (57, "9", U), (48, "0", U), (189, "-", U),
            (187, "=", U), (8, "⭠", 68));
        Row(k, navStart, y,
            (96, "INS", U), (103, "HOME", U), (105, "PG UP", U));

        // ---- Row 2: Tab Q-P [] \ | Del End PgDn ----
        y += RH;
        Row(k, x0, y,
            (9, "Tab", 50), (81, "Q", U), (87, "W", U), (69, "E", U),
            (82, "R", U), (84, "T", U), (89, "Y", U), (85, "U", U),
            (73, "I", U), (79, "O", U), (80, "P", U), (219, "[", U),
            (221, "]", U), (220, "\\", 50));
        Row(k, navStart, y,
            (110, "DEL", U), (97, "END", U), (99, "PG DN", U));

        // ---- Row 3: CapsLock A-L ;' Enter ----
        y += RH;
        Row(k, x0, y,
            (20, "CAPS LOCK", 60), (65, "A", U), (83, "S", U), (68, "D", U),
            (70, "F", U), (71, "G", U), (72, "H", U), (74, "J", U),
            (75, "K", U), (76, "L", U), (186, ";", U), (222, "'", U),
            (13, "ENTER", 73));

        // ---- Row 4: LShift Z-M ,./ RShift | ↑ ----
        y += RH;
        Row(k, x0, y,
            (160, "SHIFT", 80), (90, "Z", U), (88, "X", U), (67, "C", U),
            (86, "V", U), (66, "B", U), (78, "N", U), (77, "M", U),
            (188, ",", U), (190, ".", U), (191, "/", U), (161, "SHIFT", 88));
        Row(k, navStart + (U + G), y, (38, "↑", U));

        // ---- Row 5: bottom row ----
        y += RH;
        AddBottomRow(k, x0, y, navStart);

        return k.ToArray();
    }

    // ======================================================================
    // Generic ISO builder (L-shaped Enter, extra <> key)
    // ======================================================================
    //
    // Geometry is identical for every ISO locale (IT/UK/DE/FR/ES/Nordic/PT);
    // only the printed legends change. The MatrixId (VK) stays bound to the
    // PHYSICAL position so SDK highlighting keeps working regardless of locale
    // — e.g. German QWERTZ shows "Z" on VK_Y's key, but the key still reports
    // VK 89. Per-locale overrides come from <see cref="IsoLegends"/>; the
    // shifted (alt) legends live in <see cref="KeyLabelMap"/>.

    private static KeyDef[] BuildBoardLeft_Iso(IReadOnlyDictionary<int, string> over)
    {
        // Label resolver: locale override → ISO default.
        string L(int vk, string def) => over.TryGetValue(vk, out var s) ? s : def;

        var k = new List<KeyDef>();
        double x0 = PL;

        // ---- Row 0: F-row (same) ----
        double y = PT;
        AddFRow(k, x0, y, out double navStart);

        // ---- Row 1: <oem> 1-0 <oem><oem> Backspace | Ins Home PgUp ----
        y += RH + 6;
        Row(k, x0, y,
            (192, L(192, "`"), U), (49, L(49, "1"), U), (50, L(50, "2"), U), (51, L(51, "3"), U),
            (52, L(52, "4"), U), (53, L(53, "5"), U), (54, L(54, "6"), U), (55, L(55, "7"), U),
            (56, L(56, "8"), U), (57, L(57, "9"), U), (48, L(48, "0"), U), (189, L(189, "-"), U),
            (187, L(187, "="), U), (8, "⭠", 68));
        Row(k, navStart, y,
            (96, "INS", U), (103, "HOME", U), (105, "PG UP", U));

        // ---- Row 2: Tab Q-P <oem><oem> [Enter tall 2 rows] | Del End PgDn ----
        y += RH;
        double row2Y = y;
        Row(k, x0, y,
            (9, "Tab", 50), (81, L(81, "Q"), U), (87, L(87, "W"), U), (69, L(69, "E"), U),
            (82, L(82, "R"), U), (84, L(84, "T"), U), (89, L(89, "Y"), U), (85, L(85, "U"), U),
            (73, L(73, "I"), U), (79, L(79, "O"), U), (80, L(80, "P"), U), (219, L(219, "["), U),
            (221, L(221, "]"), U));
        // ISO Enter: tall rectangle spanning rows 2–3.
        // Positioned after the 13th key: x = x0 + Tab(50)+G + 12×(U+G) = 27+52+384 = 463
        double enterX = x0 + 50 + G + 12 * (U + G);
        Key(k, 13, "ENTER", enterX, row2Y, 42, RH + U); // h = 62 (2 rows)

        Row(k, navStart, y,
            (110, "DEL", U), (97, "END", U), (99, "PG DN", U));

        // ---- Row 3: CapsLock A-L <oem><oem><oem> (Enter covers the right side) ----
        y += RH;
        Row(k, x0, y,
            (20, "CAPS LOCK", 60), (65, L(65, "A"), U), (83, L(83, "S"), U), (68, L(68, "D"), U),
            (70, L(70, "F"), U), (71, L(71, "G"), U), (72, L(72, "H"), U), (74, L(74, "J"), U),
            (75, L(75, "K"), U), (76, L(76, "L"), U), (186, L(186, ";"), U), (222, L(222, "'"), U),
            (220, L(220, "#"), U));
        // No Enter here: the tall key from row 2 covers this space.

        // ---- Row 4: LShift(short) <oem> Z-M <oem><oem><oem> RShift | ↑ ----
        y += RH;
        // ISO: LShift shorter (1.25U ≈ 50px), extra <> key (VK_OEM_102=226)
        Row(k, x0, y,
            (160, "SHIFT", 50), (226, L(226, "<"), U),
            (90, L(90, "Z"), U), (88, L(88, "X"), U), (67, L(67, "C"), U),
            (86, L(86, "V"), U), (66, L(66, "B"), U), (78, L(78, "N"), U), (77, L(77, "M"), U),
            (188, L(188, ","), U), (190, L(190, "."), U), (191, L(191, "/"), U), (161, "SHIFT", 58));
        Row(k, navStart + (U + G), y, (38, "↑", U));

        // ---- Row 5: bottom row (same) ----
        y += RH;
        AddBottomRow(k, x0, y, navStart);

        return k.ToArray();
    }

    // ======================================================================
    // Per-locale label overrides (base/unshifted legends).
    // Only keys that differ from the ISO default are listed.
    // Shifted (alt) legends are in KeyLabelMap.
    // ======================================================================
    private static class IsoLegends
    {
        // Italian — \ ' ì / è + / ò à ù / < … - (letters unchanged)
        public static readonly Dictionary<int, string> It = new()
        {
            {192,"\\"},{189,"'"},{187,"ì"},{219,"è"},{221,"+"},
            {186,"ò"},{222,"à"},{220,"ù"},{226,"<"},{191,"-"},
        };

        // English UK — base legends match the ISO default (only alts differ),
        // so just the # key is explicit for clarity.
        public static readonly Dictionary<int, string> Uk = new()
        {
            {220,"#"},
        };

        // German QWERTZ — Y/Z swapped, accents on number row tail.
        public static readonly Dictionary<int, string> De = new()
        {
            {192,"^"},{189,"ß"},{187,"´"},
            {89,"Z"},{90,"Y"},
            {219,"ü"},{221,"+"},{186,"ö"},{222,"ä"},{220,"#"},
            {226,"<"},{191,"-"},
        };

        // French AZERTY — number row carries accents (digits on Shift),
        // A/Q, Z/W swapped, M shifted to row 3.
        public static readonly Dictionary<int, string> Fr = new()
        {
            {192,"²"},
            {49,"&"},{50,"é"},{51,"\""},{52,"'"},{53,"("},{54,"-"},
            {55,"è"},{56,"_"},{57,"ç"},{48,"à"},{189,")"},{187,"="},
            {81,"A"},{87,"Z"},{219,"^"},{221,"$"},
            {65,"Q"},{186,"M"},{222,"ù"},{220,"*"},
            {226,"<"},{90,"W"},{77,","},{188,";"},{190,":"},{191,"!"},
        };

        // Spanish — ñ, accents, inverted punctuation.
        public static readonly Dictionary<int, string> Es = new()
        {
            {192,"º"},{189,"'"},{187,"¡"},{219,"`"},{221,"+"},
            {186,"ñ"},{222,"´"},{220,"ç"},{226,"<"},{191,"-"},
        };

        // Norwegian / Nordic — å ø æ.
        public static readonly Dictionary<int, string> Nordic = new()
        {
            {192,"|"},{189,"+"},{187,"\\"},{219,"å"},{221,"¨"},
            {186,"ø"},{222,"æ"},{220,"'"},{226,"<"},{191,"-"},
        };

        // Portuguese (Portugal) — ç, ordinals, « », tilde.
        public static readonly Dictionary<int, string> Pt = new()
        {
            {192,"\\"},{189,"'"},{187,"«"},{219,"+"},{221,"´"},
            {186,"ç"},{222,"º"},{220,"~"},{226,"<"},{191,"-"},
        };
    }

    // ======================================================================
    // Numpad (common to all layouts)
    // ======================================================================

    private static KeyDef[] BuildBoardRight()
    {
        var k = new List<KeyDef>();

        const double npL = 20;
        const double npT = 47;
        double y = npT;

        y += RH + 6; // align to the number row

        // NumLock / * -
        Row(k, npL, y,
            (144, "NUM LOCK", U), (111, "/", U), (106, "*", U), (109, "-", U));

        // 7 8 9 + (+ is double-height)
        y += RH;
        Row(k, npL, y,
            (103, "7", U), (104, "8", U), (105, "9", U));
        k.Add(new KeyDef(107, "+", npL + 3 * (U + G), y, U, 62));

        // 4 5 6
        y += RH;
        Row(k, npL, y,
            (100, "4", U), (101, "5", U), (102, "6", U));

        // 1 2 3 Enter (Enter double-height)
        y += RH;
        Row(k, npL, y,
            (97, "1", U), (98, "2", U), (99, "3", U));
        k.Add(new KeyDef(13, "ENTER", npL + 3 * (U + G), y, U, 62));

        // 0 . (0 is double-width)
        y += RH;
        Row(k, npL, y,
            (96, "0", 62), (110, ".", U));

        return k.ToArray();
    }
}
