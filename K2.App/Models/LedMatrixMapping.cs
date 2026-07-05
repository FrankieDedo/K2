using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>
/// VK code → GetColorData() array index mapping for the LED overlay.
/// Extracted from the EverestKeyBidings table in BaseCamp.db
/// (columns KeyId → DLLMatrixIndex).
///
/// The GetColorData array has 171 elements for Everest (KEYBOARD_COLOR)
/// and 126 for MacroPad (MACROPAD_COLOR).
/// </summary>
internal static class LedMatrixMapping
{
    // ==================================================================
    // EVEREST — board_left (main keyboard + navigation cluster)
    // ==================================================================

    /// <summary>
    /// Maps VK code (Button.Tag) → GetColorData index for the main
    /// keyboard (board_left). Includes nav keys that K2 maps to numpad VKs
    /// (because the SDK does not distinguish them in the callback).
    /// </summary>
    public static readonly Dictionary<int, int> EverestKeyboard = new()
    {
        // --- Column 0: Esc, Tab, Caps, LShift, LCtrl ---
        { 27, 0 },     // Esc
        { 9, 2 },      // Tab
        { 20, 3 },     // CapsLock
        { 160, 4 },    // LShift
        { 162, 5 },    // LCtrl

        // --- Column 1: F1, 1, Q, A, <(ISO), Win ---
        { 112, 9 },    // F1
        { 49, 10 },    // 1
        { 81, 11 },    // Q
        { 65, 12 },    // A
        { 226, 13 },   // < (ISO extra key)
        { 91, 14 },    // LWin

        // --- Column 2: F2, 2, W, S, Z, LAlt ---
        { 113, 18 },   // F2
        { 50, 19 },    // 2
        { 87, 20 },    // W
        { 83, 21 },    // S
        { 90, 22 },    // Z
        { 164, 23 },   // LAlt

        // --- Column 3: F3, 3, E, D, X ---
        { 114, 27 },   // F3
        { 51, 28 },    // 3
        { 69, 29 },    // E
        { 68, 30 },    // D
        { 88, 31 },    // X

        // --- Column 4: F4, 4, R, F, C, Space ---
        { 115, 36 },   // F4
        { 52, 37 },    // 4
        { 82, 38 },    // R
        { 70, 39 },    // F
        { 67, 40 },    // C
        { 32, 41 },    // Space

        // --- Column 5: F5, 5, T, G, V ---
        { 116, 45 },   // F5
        { 53, 46 },    // 5
        { 84, 47 },    // T
        { 71, 48 },    // G
        { 86, 49 },    // V

        // --- Column 6: F6, 6, Y, H, B ---
        { 117, 54 },   // F6
        { 54, 55 },    // 6
        { 89, 56 },    // Y
        { 72, 57 },    // H
        { 66, 58 },    // B

        // --- Column 7: F7, 7, U, J, N, RAlt ---
        { 118, 63 },   // F7
        { 55, 64 },    // 7
        { 85, 65 },    // U
        { 74, 66 },    // J
        { 78, 67 },    // N
        { 165, 68 },   // RAlt / AltGr

        // --- Column 8: F8, 8, I, K, M, RWin ---
        { 119, 72 },   // F8
        { 56, 73 },    // 8
        { 73, 74 },    // I
        { 75, 75 },    // K
        { 77, 76 },    // M
        { 92, 77 },    // RWin

        // --- Column 9: F9, 9, O, L, «,», FN, Backspace ---
        { 120, 81 },   // F9
        { 57, 82 },    // 9
        { 79, 83 },    // O
        { 76, 84 },    // L
        { 188, 85 },   // ,
        { 261, 86 },   // FN
        { 8, 87 },     // Backspace

        // --- Column 10: F10, 0, P, ò, «.», RCtrl ---
        { 121, 90 },   // F10
        { 48, 91 },    // 0
        { 80, 92 },    // P
        { 186, 93 },   // ò (VK_OEM_1) — home row after L
        { 190, 94 },   // .
        { 163, 95 },   // RCtrl

        // --- Column 11: F11, ', è, à, -, ← ---
        { 122, 99 },   // F11
        { 189, 100 },  // ' (VK_OEM_MINUS, number row after 0)
        { 219, 101 },  // è (VK_OEM_4, tab row after P)
        { 222, 102 },  // à (VK_OEM_7, home row after ò)
        { 191, 103 },  // - (VK_OEM_2, shift row after .)
        { 37, 104 },   // ←

        // --- Column 12: F12, ì, +, ù, ↓, ScrLk ---
        { 123, 108 },  // F12
        { 187, 109 },  // ì (VK_OEM_PLUS, number row after ')
        { 221, 110 },  // + (VK_OEM_6, tab row after è)
        { 220, 111 },  // ù (VK_OEM_5, home row after à — ISO)
        { 40, 113 },   // ↓
        { 145, 114 },  // Scroll Lock

        // --- Column 13+: PrtSc, Enter, RShift, →, Pause, ↑ ---
        { 44, 117 },   // PrtSc
        { 13, 120 },   // Enter
        { 161, 121 },  // RShift
        { 39, 122 },   // →
        { 19, 123 },   // Pause
        { 38, 124 },   // ↑

        // --- ISO IT: \ (VK_OEM_3=192, left-of-1 in the number row) ---
        { 192, 1 },    // \ — col 0, row 1 (matrixId 1)

        // --- Navigation cluster ---
        // K2 uses numpad VKs for nav keys (the SDK does not distinguish).
        // Mapped here to the physical LED positions of the NAV cluster.
        { 96, 96 },    // Ins  (K2 VK=96=Num0, physical nav LED Ins=96)
        { 103, 105 },  // Home (K2 VK=103=Num7, physical nav LED Home=105)
        { 105, 115 },  // PgUp (K2 VK=105=Num9, physical nav LED PgUp=115)
        { 110, 88 },   // Del  (K2 VK=110=Num., physical nav LED Del=88)
        { 97, 97 },    // End  (K2 VK=97=Num1, physical nav LED End=97)
        { 99, 106 },   // PgDn (K2 VK=99=Num3, physical nav LED PgDn=106)
    };

    // ==================================================================
    // EVEREST — board_right (numpad)
    // ==================================================================

    /// <summary>
    /// Maps VK code → GetColorData index for the numeric keypad.
    /// Separate because some VKs (96-110, 13) have different meanings
    /// compared to board_left (nav cluster).
    /// </summary>
    public static readonly Dictionary<int, int> EverestNumpad = new()
    {
        { 144, 6 },    // NumLock
        { 111, 24 },   // Num /
        { 106, 16 },   // Num *
        { 109, 15 },   // Num -
        { 103, 61 },   // Num 7
        { 104, 69 },   // Num 8
        { 105, 70 },   // Num 9
        { 107, 7 },    // Num +
        { 100, 51 },   // Num 4
        { 101, 52 },   // Num 5
        { 102, 60 },   // Num 6
        { 97, 34 },    // Num 1
        { 98, 42 },    // Num 2
        { 99, 43 },    // Num 3
        { 13, 33 },    // Num Enter
        { 96, 78 },    // Num 0
        { 110, 79 },   // Num .
    };

    // ==================================================================
    // MACROPAD — callback wMatrix → GetColorData index
    // ==================================================================

    /// <summary>
    /// Maps the wMatrix value reported by the MacroPad SDK KEY_CALLBACK
    /// → index in the GetColorData array (126 elements).
    /// wMatrix values are custom Mountain codes (170-179, 220, 221).
    /// </summary>
    public static readonly Dictionary<int, int> MacroPad = new()
    {
        { 170, 8 },    // M1
        { 171, 17 },   // M2
        { 172, 26 },   // M3
        { 173, 35 },   // M4
        { 174, 44 },   // M5
        { 175, 53 },   // M6
        { 176, 62 },   // M7
        { 177, 71 },   // M8
        { 178, 80 },   // M9
        { 179, 89 },   // M10
        { 220, 98 },   // M11
        { 221, 125 },  // M12
    };
}
