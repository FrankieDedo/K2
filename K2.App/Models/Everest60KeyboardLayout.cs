using System.Collections.Generic;
using K2.App.Services;

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
/// <para><b>Numpad accessory:</b> hand-estimated geometry (same "eyeballed"
/// spirit as the Makalu hotspots) — no source ever modeled its layout.
/// <see cref="KeyDef.MatrixId"/> stays -1 (no lighting/remap protocol via
/// that identity), but each key now carries a real <see cref="KeyDef.NumpadIndex"/>
/// (0-16, same order as <c>Everest60Protocol.NumpadLedIndex</c>), confirmed
/// 2026-07-22 via USBPcap capture analysis (see CHANGELOG): the 17 keys speak
/// standard USB HID boot-keyboard reports when unassigned, and Base Camp's
/// own remap writes a real per-key binding over the existing lighting
/// feature-report channel. Key Binding UI wiring (identity/persistence) is
/// in place; the physical-press detection + device-side write are a
/// follow-up (Fase 2, pending one more targeted capture) — see the plan.</para>
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

    /// <summary>Numpad accessory keys — not paintable (MatrixId stays -1, no
    /// per-key lighting protocol), but each has a real <see cref="KeyDef.NumpadIndex"/>
    /// (0-16) for Key Binding identity.</summary>
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
    // PS/2 Set-1 scan code (MakeCode, +0x100 if E0-extended) -> LED index
    // (0-63), for translating physical main-board key presses reported by
    // Raw Input (see K2.App.Services.RawEv60KeyWatcher — the vendor SDK's
    // KEY_CALLBACK was found 2026-07-28 to never fire on real hardware even
    // when properly initialized, and a follow-up attempt to read the board's
    // own raw HID boot-keyboard reports directly hit a hard OS wall: Windows
    // reserves read/write access to a keyboard's HID collection for its own
    // class driver, so opening it returns ACCESS_DENIED — Raw Input is the
    // sanctioned way around exactly that restriction).
    //
    // Keyed by scan code, NOT by Raw Input's own VKey field: VKey is derived
    // from the scan code via the CURRENTLY ACTIVE keyboard layout, and for
    // the OEM/punctuation keys that translation genuinely varies by locale
    // (an Italian layout's physical ";" position reports a different VKey
    // than a US layout's) — user report 2026-07-28, "pressing an ITA-layout
    // key lights up a different key", same class of bug already solved for
    // Everest Max by keying off raw HID usage ids instead of anything
    // OS-translated (see EverestWMatrixMap.HidUsageToMatrixId's doc comment).
    // The scan code is the Raw-Input equivalent of that same fixed, physical,
    // locale-independent identity — standard PS/2 Set 1, unchanged since the
    // original IBM PC/AT, not vendor- or OS-version-specific, so this needed
    // no new capture to build. As a side benefit this also makes the L/R
    // Shift/Ctrl/Alt distinction (previously RawEv60KeyWatcher.NormalizeVKey,
    // now deleted) and the numpad-vs-main-board Enter/nav-cluster ambiguity
    // (previously RawEv60KeyWatcher.IsAmbiguousWithNumpad, now also deleted)
    // trivial: scan codes already distinguish both natively — the numpad's
    // own Enter/arrow-equivalent keys physically differ from the main
    // board's regardless of Num Lock state, unlike their OS-translated VKeys.
    // Fn (idx 60) has no entry: it's a pure firmware-side layer switch with
    // no host-visible signal at all, same as Everest Max's own
    // un-observable Fn key.
    //
    // The numpad accessory's 17 keys are included too (LED indices
    // Everest60Protocol.NumpadLedIndexBase + 0..16, same order as
    // Everest60Protocol.NumpadLedIndex / this class's own Numpad array) —
    // 2026-07-28, user question "why is the numpad on a slow 100ms poll when
    // typing on it is instant?" was the right challenge: the accessory
    // speaks the exact same standard boot-keyboard reports as the main
    // board (same USB device/interface, confirmed by the ev60_allkeys.pcapng
    // capture that grounded the whole main-board scan-code table above), so
    // it gets the same instant, event-driven Raw Input path instead of
    // Everest60NumpadKeyPoller's Feature-Report polling (retired from the
    // highlight/execution role — see MainWindow.Everest60.cs's history for
    // why that poller existed and why it's no longer wired up). Zero
    // collisions with the main-board codes above: every numpad-cluster key
    // that has a main-board homonym (Enter, arrows-when-NumLock-off) uses a
    // DIFFERENT combined code specifically because the main board's own
    // version of each is E0-extended and the numpad's own physical scan code
    // never is, regardless of Num Lock state.
    // ======================================================================
    internal static readonly IReadOnlyDictionary<int, int> ScanCodeToLedIndex = new Dictionary<int, int>
    {
        // Row 0: Esc 1-0 - = Backspace
        { 0x01, 0 }, { 0x02, 1 }, { 0x03, 2 }, { 0x04, 3 }, { 0x05, 4 },
        { 0x06, 5 }, { 0x07, 6 }, { 0x08, 7 }, { 0x09, 8 }, { 0x0A, 9 },
        { 0x0B, 10 }, { 0x0C, 11 }, { 0x0D, 12 }, { 0x0E, 13 },
        // Row 1: Tab Q-P [ ] \
        { 0x0F, 14 }, { 0x10, 15 }, { 0x11, 16 }, { 0x12, 17 }, { 0x13, 18 },
        { 0x14, 19 }, { 0x15, 20 }, { 0x16, 21 }, { 0x17, 22 }, { 0x18, 23 },
        { 0x19, 24 }, { 0x1A, 25 }, { 0x1B, 26 }, { 0x2B, 27 },
        // Row 2: Caps A-L ; ' Enter
        { 0x3A, 28 }, { 0x1E, 29 }, { 0x1F, 30 }, { 0x20, 31 }, { 0x21, 32 },
        { 0x22, 33 }, { 0x23, 34 }, { 0x24, 35 }, { 0x25, 36 }, { 0x26, 37 },
        { 0x27, 38 }, { 0x28, 39 }, { 0x1C, 40 },
        // Row 3: Shift Z-M , . / Shift Up Del
        { 0x2A, 41 }, { 0x2C, 42 }, { 0x2D, 43 }, { 0x2E, 44 }, { 0x2F, 45 },
        { 0x30, 46 }, { 0x31, 47 }, { 0x32, 48 }, { 0x33, 49 }, { 0x34, 50 },
        { 0x35, 51 }, { 0x36, 52 }, { 0x148, 53 }, { 0x153, 54 },
        // Row 4: Ctrl Win Alt Space Alt(Gr) Left Down Right — Fn has no scan code
        { 0x1D, 55 }, { 0x15B, 56 }, { 0x38, 57 }, { 0x39, 58 }, { 0x138, 59 },
        { 0x14B, 61 }, { 0x150, 62 }, { 0x14D, 63 },
        // Numpad accessory (idx 0-16, order matches BuildNumpad/NumpadIndex above):
        // Num Lock, /, *, -, 7, 8, 9, +, 4, 5, 6, 1, 2, 3, Enter, 0, .
        { 0x45, Everest60Protocol.NumpadLedIndexBase + 0 },
        { 0x135, Everest60Protocol.NumpadLedIndexBase + 1 },
        { 0x37, Everest60Protocol.NumpadLedIndexBase + 2 },
        { 0x4A, Everest60Protocol.NumpadLedIndexBase + 3 },
        { 0x47, Everest60Protocol.NumpadLedIndexBase + 4 },
        { 0x48, Everest60Protocol.NumpadLedIndexBase + 5 },
        { 0x49, Everest60Protocol.NumpadLedIndexBase + 6 },
        { 0x4E, Everest60Protocol.NumpadLedIndexBase + 7 },
        { 0x4B, Everest60Protocol.NumpadLedIndexBase + 8 },
        { 0x4C, Everest60Protocol.NumpadLedIndexBase + 9 },
        { 0x4D, Everest60Protocol.NumpadLedIndexBase + 10 },
        { 0x4F, Everest60Protocol.NumpadLedIndexBase + 11 },
        { 0x50, Everest60Protocol.NumpadLedIndexBase + 12 },
        { 0x51, Everest60Protocol.NumpadLedIndexBase + 13 },
        { 0x11C, Everest60Protocol.NumpadLedIndexBase + 14 },
        { 0x52, Everest60Protocol.NumpadLedIndexBase + 15 },
        { 0x53, Everest60Protocol.NumpadLedIndexBase + 16 },
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
        int idx = 0; // 0-16, same insertion order as Everest60Protocol.NumpadLedIndex

        void R(double yy, params (string label, double w)[] keys)
        {
            double x = npL;
            foreach (var (label, w) in keys)
            {
                k.Add(new KeyDef(-1, label, x, yy, w, U, idx++));
                x += w + G;
            }
        }

        R(y, ("Num", U), ("/", U), ("*", U), ("-", U));
        y += RH;
        R(y, ("7", U), ("8", U), ("9", U));
        k.Add(new KeyDef(-1, "+", npL + 3 * (U + G), y, U, RH + U, idx++));
        y += RH;
        R(y, ("4", U), ("5", U), ("6", U));
        y += RH;
        R(y, ("1", U), ("2", U), ("3", U));
        k.Add(new KeyDef(-1, "↵", npL + 3 * (U + G), y, U, RH + U, idx++));
        y += RH;
        R(y, ("0", 62), (".", U));

        return k.ToArray();
    }
}
