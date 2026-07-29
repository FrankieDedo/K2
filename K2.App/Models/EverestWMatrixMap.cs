using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>
/// Default Everest Max wMatrix (SDK KEY_CALLBACK code / Base Camp DB's DLLMatrixIndex) →
/// matrixId (Windows VK code, see <see cref="KeyDef.MatrixId"/>) translation table for
/// regular keyboard keys. Derived from a real BaseCamp.db EverestKeyBidings dump.
///
/// Shared by MainWindow.Everest.cs (translating live SDK key-press callbacks via
/// EvTranslateMatrix) and <see cref="K2.App.Services.BaseCampDbImporter"/> (translating
/// imported DLLMatrixIndex bindings into that same VK-code space before they're saved as
/// EverestKeyRecord.KeyMatrix) — the two MUST agree, since a manually-created key (via the
/// keyboard overlay click, whose Tag is already a VK-code MatrixId) and an imported key are
/// looked up from the very same <c>_evByMatrix</c> dictionary on a physical key press.
/// Before this shared table existed, the importer wrote the raw wMatrix straight into
/// KeyMatrix — a different numbering space from the VK codes translated live presses land
/// on, so an imported key could never be found by a physical press (confirmed user report
/// 2026-07-19: "l'azione non funziona" after import).
/// </summary>
public static class EverestWMatrixMap
{
    public static readonly IReadOnlyDictionary<int, int> Default = new Dictionary<int, int>
    {
        {   0,  27 },  // Esc
        {   2,   9 },  // Tab
        {   3,  20 },  // Caps Lk
        {   4, 160 },  // LShift
        {   5, 162 },  // LCtrl
        {   6, 144 },  // Num Lk
        {   7, 107 },  // Num +
        {   9, 112 },  // F1
        {  10,  49 },  // 1
        {  11,  81 },  // Q
        {  12,  65 },  // A
        {  13, 226 },  // < (ISO extra key)
        {  14,  91 },  // Win
        {  15, 109 },  // Num -
        {  16, 106 },  // Num *
        {  18, 113 },  // F2
        {  19,  50 },  // 2
        {  20,  87 },  // W
        {  21,  83 },  // S
        {  22,  90 },  // Z
        {  23,  18 },  // Alt
        {  24, 111 },  // Num /
        {  27, 114 },  // F3
        {  28,  51 },  // 3
        {  29,  69 },  // E
        {  30,  68 },  // D
        {  31,  88 },  // X
        {  33,  13 },  // Num Enter
        {  34,  97 },  // Num 1
        {  35, 173 },  // Mute
        {  36, 115 },  // F4
        {  37,  52 },  // 4
        {  38,  82 },  // R
        {  39,  70 },  // F
        {  40,  67 },  // C
        {  41,  32 },  // Space
        {  42,  98 },  // Num 2
        {  43,  99 },  // Num 3
        {  45, 116 },  // F5
        {  46,  53 },  // 5
        {  47,  84 },  // T
        {  48,  71 },  // G
        {  49,  86 },  // V
        {  51, 100 },  // Num 4
        {  52, 101 },  // Num 5
        {  53, 177 },  // Prev Track
        {  54, 117 },  // F6
        {  55,  54 },  // 6
        {  56,  89 },  // Y
        {  57,  72 },  // H
        {  58,  66 },  // B
        {  60, 102 },  // Num 6
        {  61, 103 },  // Num 7
        {  62, 176 },  // Next Track
        {  63, 118 },  // F7
        {  64,  55 },  // 7
        {  65,  85 },  // U
        {  66,  74 },  // J
        {  67,  78 },  // N
        {  68, 165 },  // Alt Gr (VK_RMENU)
        {  69, 104 },  // Num 8
        {  70, 105 },  // Num 9
        {  72, 119 },  // F8
        {  73,  56 },  // 8
        {  74,  73 },  // I
        {  76,  77 },  // M
        {  77,  91 },  // Win (right)
        {  78,  96 },  // Num 0
        {  79, 110 },  // Num .
        {  81, 120 },  // F9
        {  82,  57 },  // 9
        {  83,  79 },  // O
        {  84,  76 },  // L
        {  85, 188 },  // ,
        {  87,   8 },  // Backspace
        {  88,  46 },  // Del
        {  90, 121 },  // F10
        {  91,  48 },  // 0
        {  92,  80 },  // P
        {  93, 222 },  // ò
        {  94, 190 },  // .
        {  95, 163 },  // RCtrl
        {  96,  45 },  // Insert
        {  97,  35 },  // End
        {  99, 122 },  // F11
        { 101, 186 },  // è
        { 102, 192 },  // à
        { 103, 189 },  // -
        { 104,  37 },  // ←
        { 105,  36 },  // Home
        { 106,  34 },  // PgDn
        { 108, 123 },  // F12
        { 110, 187 },  // +
        { 111, 219 },  // ù
        { 113,  40 },  // ↓
        { 114, 145 },  // Scroll Lk
        { 115,  33 },  // PgUp
        { 117,  44 },  // Prt Sc
        { 120,  13 },  // Enter  ← wMatrix=120 = Enter's DLLMatrixIndex
        { 121, 161 },  // RShift
        { 122,  39 },  // →
        { 123,  19 },  // Pause
        { 124,  38 },  // ↑
        { 183, 179 },  // Play/Pause
    };

    /// <summary>
    /// matrixId (VK code, K2's own key identity — see <see cref="KeyDef.MatrixId"/>) →
    /// <b>DLLKeyId</b>, a THIRD numbering space alongside the VK codes above and the
    /// wMatrix/DLLMatrixIndex keys of <see cref="Default"/>. Needed only by
    /// <see cref="K2.App.Services.EverestHidNative.Pad.WriteKeyOutputMode"/>: the
    /// firmware's per-key write addresses keys by DLLKeyId, while everything else in K2
    /// (overlay clicks, stored EverestKeyRecord.KeyMatrix, live press translation)
    /// works in VK codes.
    ///
    /// <para>Extracted 2026-07-27 from the real <c>BaseCamp.db</c>
    /// (<c>SELECT DISTINCT KeyId, KeyName, DLLKeyId FROM EverestKeyBidings WHERE
    /// ProfileId = &lt;the selected Everest profile&gt;</c>) — Base Camp's <c>KeyId</c>
    /// column IS the VK code, so the pairing is read straight off the rows, none
    /// inferred. All 127 catalog rows, KeyId unique throughout (verified), including
    /// the display keys D1-D4 (257-260), Fn (261) and the two scroll keys (262/263)
    /// whose VK "codes" are Base Camp's own out-of-VK-range identifiers.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> MatrixIdToDllKeyId = new Dictionary<int, int>
    {
        {    8,  15 },  // Backspace
        {    9,  16 },  // Tab
        {   13,  43 },  // Enter
        {   19, 126 },  // Pause
        {   20,  30 },  // Caps Lk
        {   27, 110 },  // Esc
        {   32,  61 },  // Space
        {   33,  85 },  // PgUp
        {   34,  86 },  // PgDn
        {   35,  81 },  // End
        {   36,  80 },  // Home
        {   37,  79 },  // Left
        {   38,  83 },  // Up
        {   39,  89 },  // Right
        {   40,  84 },  // Down
        {   44, 124 },  // Prt Sc
        {   45,  75 },  // Insert
        {   46,  76 },  // Del
        {   48,  11 },  // 0
        {   49,   2 },  // 1
        {   50,   3 },  // 2
        {   51,   4 },  // 3
        {   52,   5 },  // 4
        {   53,   6 },  // 5
        {   54,   7 },  // 6
        {   55,   8 },  // 7
        {   56,   9 },  // 8
        {   57,  10 },  // 9
        {   65,  31 },  // A
        {   66,  50 },  // B
        {   67,  48 },  // C
        {   68,  33 },  // D
        {   69,  19 },  // E
        {   70,  34 },  // F
        {   71,  35 },  // G
        {   72,  36 },  // H
        {   73,  24 },  // I
        {   74,  37 },  // J
        {   75,  38 },  // K
        {   76,  39 },  // L
        {   77,  52 },  // M
        {   78,  51 },  // N
        {   79,  25 },  // O
        {   80,  26 },  // P
        {   81,  17 },  // Q
        {   82,  20 },  // R
        {   83,  32 },  // S
        {   84,  21 },  // T
        {   85,  23 },  // U
        {   86,  49 },  // V
        {   87,  18 },  // W
        {   88,  47 },  // X
        {   89,  22 },  // Y
        {   90,  46 },  // Z
        {   91,  59 },  // Win (Left)
        {   92,  63 },  // Win (Right)
        {   96,  99 },  // Num 0
        {   97,  93 },  // Num 1
        {   98,  98 },  // Num 2
        {   99, 103 },  // Num 3
        {  100,  92 },  // Num 4
        {  101,  97 },  // Num 5
        {  102, 102 },  // Num 6
        {  103,  91 },  // Num 7
        {  104,  96 },  // Num 8
        {  105, 101 },  // Num 9
        {  106, 100 },  // Num *
        {  107, 106 },  // Num +
        {  109, 105 },  // Num -
        {  110, 104 },  // Num .
        {  111,  95 },  // Num /
        {  112, 112 },  // F1
        {  113, 113 },  // F2
        {  114, 114 },  // F3
        {  115, 115 },  // F4
        {  116, 116 },  // F5
        {  117, 117 },  // F6
        {  118, 118 },  // F7
        {  119, 119 },  // F8
        {  120, 120 },  // F9
        {  121, 121 },  // F10
        {  122, 122 },  // F11
        {  123, 123 },  // F12
        {  144,  90 },  // Num Lk
        {  145, 125 },  // Scroll Lk
        {  160,  44 },  // LShift
        {  161,  57 },  // RShift
        {  162,  58 },  // LCtrl
        {  163,  64 },  // RCtrl
        {  164,  60 },  // Alt
        {  165,  62 },  // Alt Gr
        {  173, 184 },  // Mute
        {  176, 180 },  // Next Track
        {  177, 181 },  // Prev Track
        {  179, 183 },  // Play/Pause
        {  186,  27 },  // OEM 1
        {  187,  28 },  // +
        {  188,  53 },  // ,
        {  189,  55 },  // -
        {  190,  54 },  // .
        {  191,  42 },  // OEM 2
        {  192,  40 },  // OEM 3
        {  219,  12 },  // OEM 4
        {  220,   1 },  // Backslash
        {  221,  13 },  // OEM 6
        {  222,  41 },  // OEM 7
        {  226,  45 },  // < (ISO extra key)
        {  230, 230 },  // F13
        {  231, 231 },  // F14
        {  232, 232 },  // F15
        {  233, 233 },  // F16
        {  234, 234 },  // F17
        {  235, 235 },  // F18
        {  236, 236 },  // F19
        {  237, 237 },  // F20
        {  238, 238 },  // F21
        {  239, 239 },  // F22
        {  240, 240 },  // F23
        {  241, 241 },  // F24
        {  257, 170 },  // D1
        {  258, 171 },  // D2
        {  259, 172 },  // D3
        {  260, 173 },  // D4
        {  261, 154 },  // Fn
        {  262, 188 },  // Scroll L
        {  263, 187 },  // Scroll R
        { 3612, 108 },  // Num Enter
    };

    /// <summary>
    /// HID Keyboard/Keypad usage id → matrixId (VK code). Used only in native-engine
    /// mode, where key presses arrive as an NKRO usage bitmap rather than through the
    /// vendor SDK's wMatrix callback — see
    /// <see cref="K2.App.Services.EverestHidNative.Pad.DecodeKeyBitmap"/>.
    ///
    /// <para>Covers the layout-INDEPENDENT part of usage page 0x07 (letters, digits,
    /// F-keys, navigation, modifiers, keypad, punctuation/OEM and the named keys) — the
    /// standard's own fixed assignment, one usage per PHYSICAL position regardless of
    /// locale, same "VK = physical position" identity <see cref="K2.App.Models.KeyboardLayout"/>
    /// already relies on for its ISO builder (a German board reports VK_Y for the key
    /// printed "Z", etc.). The punctuation/OEM usages (0x2D-0x38, 0x64) were previously
    /// left out here on the mistaken belief their VK depended on locale — it doesn't, only
    /// the PRINTED LEGEND does (<see cref="K2.App.Models.KeyboardLayout.IsoLegends"/> is
    /// exactly that per-locale legend table, layered on the same fixed VK identity below).
    /// Leaving them out meant an Italian board's ò/à/è/ù/ì/+/- keys (usages 0x33/0x34/0x2F/
    /// 0x32/0x2E/0x30/0x2D) fell through untranslated to their raw usage byte, which then
    /// collided with an unrelated existing VK (e.g. usage 0x33 = 51 = VK '3') and lit up
    /// THAT key instead (user report 2026-07-27). 0x31 (ANSI \) and 0x32 (ISO non-US #)
    /// intentionally share VK 220, same as the two board builders in KeyboardLayout.cs —
    /// a given physical board only ever sends one of the two usages, never both.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> HidUsageToMatrixId = new Dictionary<int, int>
    {
        // 0x04-0x1D: A-Z → VK 65-90
        { 0x04, 65 }, { 0x05, 66 }, { 0x06, 67 }, { 0x07, 68 }, { 0x08, 69 }, { 0x09, 70 },
        { 0x0A, 71 }, { 0x0B, 72 }, { 0x0C, 73 }, { 0x0D, 74 }, { 0x0E, 75 }, { 0x0F, 76 },
        { 0x10, 77 }, { 0x11, 78 }, { 0x12, 79 }, { 0x13, 80 }, { 0x14, 81 }, { 0x15, 82 },
        { 0x16, 83 }, { 0x17, 84 }, { 0x18, 85 }, { 0x19, 86 }, { 0x1A, 87 }, { 0x1B, 88 },
        { 0x1C, 89 }, { 0x1D, 90 },
        // 0x1E-0x26: 1-9 → VK 49-57, 0x27: 0 → VK 48
        { 0x1E, 49 }, { 0x1F, 50 }, { 0x20, 51 }, { 0x21, 52 }, { 0x22, 53 },
        { 0x23, 54 }, { 0x24, 55 }, { 0x25, 56 }, { 0x26, 57 }, { 0x27, 48 },
        { 0x28, 13 },   // Enter
        { 0x29, 27 },   // Esc
        { 0x2A,  8 },   // Backspace
        { 0x2B,  9 },   // Tab
        { 0x2C, 32 },   // Space
        // 0x2D-0x38, 0x64: punctuation/OEM — physical position, not locale (see class doc)
        { 0x2D, 189 },  // - / _ position
        { 0x2E, 187 },  // = / + position
        { 0x2F, 219 },  // [ / { position
        { 0x30, 221 },  // ] / } position
        { 0x31, 220 },  // \ / | position (ANSI)
        { 0x32, 220 },  // Non-US # / ~ position (ISO, next to Enter — same VK as ANSI \ above)
        { 0x33, 186 },  // ; / : position
        { 0x34, 222 },  // ' / " position
        { 0x35, 192 },  // ` / ~ position (top-left corner)
        { 0x36, 188 },  // , / < position
        { 0x37, 190 },  // . / > position
        { 0x38, 191 },  // / / ? position
        { 0x64, 226 },  // Non-US \ / | position (ISO, left of Z)
        { 0x39, 20 },   // Caps Lock
        // 0x3A-0x45: F1-F12 → VK 112-123
        { 0x3A, 112 }, { 0x3B, 113 }, { 0x3C, 114 }, { 0x3D, 115 }, { 0x3E, 116 }, { 0x3F, 117 },
        { 0x40, 118 }, { 0x41, 119 }, { 0x42, 120 }, { 0x43, 121 }, { 0x44, 122 }, { 0x45, 123 },
        { 0x46,  44 },  // Print Screen
        { 0x47, 145 },  // Scroll Lock
        { 0x48,  19 },  // Pause
        { 0x49,  45 },  // Insert
        { 0x4A,  36 },  // Home
        { 0x4B,  33 },  // PgUp
        { 0x4C,  46 },  // Delete
        { 0x4D,  35 },  // End
        { 0x4E,  34 },  // PgDn
        { 0x4F,  39 },  // Right
        { 0x50,  37 },  // Left
        { 0x51,  40 },  // Down
        { 0x52,  38 },  // Up
        { 0x53, 144 },  // Num Lock
        { 0x54, 111 },  // Num /
        { 0x55, 106 },  // Num *
        { 0x56, 109 },  // Num -
        { 0x57, 107 },  // Num +
        { 0x58,  13 },  // Num Enter (same VK as Enter)
        // 0x59-0x61: Num 1-9 → VK 97-105, 0x62: Num 0 → VK 96
        { 0x59,  97 }, { 0x5A,  98 }, { 0x5B,  99 }, { 0x5C, 100 }, { 0x5D, 101 },
        { 0x5E, 102 }, { 0x5F, 103 }, { 0x60, 104 }, { 0x61, 105 }, { 0x62,  96 },
        { 0x63, 110 },  // Num .
        // 0x68-0x73: F13-F24 → VK 124-135 (this board's own F13+ rows use 230-241 as
        // KeyId, handled by the guided remap if ever needed — VK is the standard one here)
        { 0x68, 124 }, { 0x69, 125 }, { 0x6A, 126 }, { 0x6B, 127 }, { 0x6C, 128 }, { 0x6D, 129 },
        { 0x6E, 130 }, { 0x6F, 131 }, { 0x70, 132 }, { 0x71, 133 }, { 0x72, 134 }, { 0x73, 135 },
        // 0xE0-0xE7: modifiers
        { 0xE0, 162 },  // LCtrl
        { 0xE1, 160 },  // LShift
        { 0xE2, 164 },  // LAlt
        { 0xE3,  91 },  // LWin
        { 0xE4, 163 },  // RCtrl
        { 0xE5, 161 },  // RShift
        { 0xE6, 165 },  // AltGr
        { 0xE7,  92 },  // RWin
    };

    /// <summary>Translates a wMatrix/DLLMatrixIndex to its VK matrixId via <see cref="Default"/>,
    /// falling back to the input unchanged when there's no known translation (e.g.
    /// programmable/dock/media-dock keys, which go through the separate HW-capture mechanism
    /// instead — see MainWindow.DockActions.cs).</summary>
    public static int Translate(int wMatrix) =>
        Default.TryGetValue(wMatrix, out var vk) ? vk : wMatrix;
}
