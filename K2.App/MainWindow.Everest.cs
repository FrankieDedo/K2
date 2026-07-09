using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// MainWindow partial: Everest Max module — Everest tab, SDK management,
/// on-demand mapped key list, action configuration and hook into the shared
/// action engine (K2.Core) via an <see cref="EverestActionHost"/>.
///
/// The Everest is single-device and has 100+ keys: no fixed grid. Keys are
/// "captured" by pressing the <c>Capture key</c> button and then the desired
/// physical key — the first press adds it to the current profile's list;
/// subsequent presses execute its assigned action.
/// </summary>
public partial class MainWindow
{
    private readonly EverestService _everest = new();
    private readonly EverestStore   _evStore = new();

    private ButtonActionEngine? _evEngine;
    internal EverestActionHost? _evActionHost;

    private readonly ObservableCollection<EverestKey> _evKeys = new();
    private readonly Dictionary<int, EverestKey> _evByMatrix = new();

    private bool _evCapturing;
    private bool _evSuppressProfile;

    /// <summary>Maps matrixId → Button in the keyboard Canvas for highlight.</summary>
    private readonly Dictionary<int, Button> _evKeyboardButtons = new();

    // ---- Interactive key remapping (like MacroPad) ----
    /// <summary>Maps <c>SDK wMatrix → layout matrixId</c> to translate callback codes.</summary>
    private readonly Dictionary<int, int> _evWMatrixToLayout = new();

    /// <summary>
    /// Default wMatrix (DLLMatrixIndex) → MatrixId (VK code) translation.
    /// Derived from BaseCamp.db EverestKeyBidings. Used as fallback when no
    /// user-defined map exists, so "Mappa tasti" is not required on first run.
    /// Key insight: the SDK KEY_CALLBACK reports DLLMatrixIndex as wMatrix,
    /// NOT the VK code — so without this map Enter (DLLMatrixIndex=120) would
    /// be mistaken for F9 (VK=120), etc.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> s_defaultWMatrixMap =
        new Dictionary<int, int>
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
    /// <summary>Index of the key currently awaited during guided remapping (-1 = inactive).</summary>
    private int _evMapAwaitingIndex = -1;
    /// <summary>Ordered list of KeyDefs to remap (board_left + board_right).</summary>
    private KeyDef[] _evMapKeyDefs = Array.Empty<KeyDef>();

    /// <summary>Current keyboard layout (detected at startup).</summary>
    private KeyboardLayoutType _evLayoutType = KeyboardLayoutType.AnsiUs;

    // ---- RGB effect panel state ------------------------------------------
    //
    // Values live in memory while the app is open; per-profile persistence
    // is a future step (see _PROJECT_MAP.md). Colors are 0xRRGGBB integers.
    private bool _evRgbInitialized;
    private bool _evRgbSuppress;
    private int  _evColor1 = 0x900000; // K2 teal
    private int  _evColor2 = 0x000000;
    private int  _evColor3 = 0x000000;

    // ============================================================
    // Initialization
    // ============================================================

    /// <summary>Starts the Everest module. Called from the MainWindow constructor.</summary>
    private void InitEverestModule()
    {
        LvEvKeys.ItemsSource    = _evKeys;
        EvRefreshProfiles();
        EvSelectProfileSlot(_evStore.GetCurrentProfile());

        _everest.KeyEvent += OnEverestKey;

        _evActionHost = new EverestActionHost(
            dispatcher:           Dispatcher,
            log:                  LogEverestSafe,
            currentProfile:       EvCurrentProfile,
            sdkVersion:           EvSdkVersion,
            getButtons:           EvGetButtons,
            pressButton:          EvPressButton,
            switchProfile:        EvSwitchProfile,
            configuredPythonPath: () => _evStore.GetSetting("python.exePath"),
            listAllProfileTargets: ListAllProfileTargets,
            switchProfileByKey:    SwitchProfileByKey,
            listMacroNames:        ListAllMacroNames,
            playMacro:             PlayMacroByName);

        _evEngine = new ButtonActionEngine(_evActionHost);
        _evEngine.Start();

        Closed += (_, _) =>
        {
            CleanupMediaDock();
            try { _evEngine?.Dispose(); } catch { /* ignore */ }
            try { _everest.Dispose();   } catch { /* ignore */ }
            try { _evStore.Dispose();   } catch { /* ignore */ }
        };

        ReloadEverestProfile();
        InitSectionNav();
        InitEverestRgbPanel();
        InitEverestSettingsPanel();
        InitMediaDockPanel();
        InitDisplayDialPanel();
        InitCustomLightingPanel();
        InitDockActionsPanel();
        _evLayoutType = EverestKeyboardLayout.DetectLayout();
        BuildEverestKeyboardOverlay();
        InitNumpadDisplayKeys();
        InitKeyboardLayoutSelector();
        UpdateKeyboardLayout();
        LoadEverestKeyMap();
    }

    // ============================================================
    // Interactive keyboard overlay
    // ============================================================

    /// <summary>
    /// Populates the <c>CvsEvKeyboard</c> and <c>CvsEvNumpad</c> Canvases with
    /// Buttons positioned according to <see cref="EverestKeyboardLayout"/>. Each
    /// key is styled like BC (3D borders, dark background, white text).
    /// </summary>
    // Font shared by all Everest key labels.
    // BC uses "system-ui, sans-serif" (= Segoe UI on Windows) at 0.5rem/8px.
    private static readonly FontFamily _evKeyFont =
        new("Segoe UI,system-ui,Arial,sans-serif");

    // Legend colours mirroring real Everest Max keycap printing: the base
    // (unshifted) character is bright white and the dominant glyph, while the
    // shift/AltGr corner symbols are smaller and colour-coded (grey for shift,
    // teal for AltGr/Shift+AltGr — the same teal used elsewhere in this file
    // for the layout-selector accent, 0x5BBEC3).
    private static readonly Brush _evBaseBrush  = Brushes.White;
    private static readonly Brush _evShiftBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly Brush _evAltGrBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3));

    /// <summary>
    /// Builds a 2×2 keycap legend matching physical keycap printing: shift
    /// (top-left, grey), Shift+AltGr (top-right, teal), base (bottom-left,
    /// white, larger), AltGr (bottom-right, teal).
    /// </summary>
    private FrameworkElement BuildCornerLegend(
        string baseLbl, string? shiftLbl, string? altGrLbl, string? sAltGrLbl,
        double fsCorner, double fsBase)
    {
        // 3×3 grid with a spacer row/column between the 4 corners. Font size
        // is right (per user feedback) — this is purely about the gap between
        // the corners, wider horizontally than vertically since the key is
        // wider than it is tall relative to the glyphs.
        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        grid.RowDefinitions.Add(new RowDefinition());

        void Corner(string? text, int row, int col, HorizontalAlignment h, Brush brush, double fs)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tb = new TextBlock
            {
                Text                = text,
                Foreground          = brush,
                FontSize            = fs,
                FontFamily          = _evKeyFont,
                HorizontalAlignment = h,
                VerticalAlignment   = row == 0 ? VerticalAlignment.Top
                                               : VerticalAlignment.Bottom,
                // Nudge the top row up: at this font size the two rows'
                // glyphs were tall enough to touch/overlap the bottom row
                // (letters disappearing behind the ones below). The bottom
                // row is already flush against the key's own bottom edge,
                // so only the top row has room to move.
                Margin = row == 0 ? new Thickness(0, -2, 0, 0) : new Thickness(0),
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        Corner(shiftLbl,  0, 0, HorizontalAlignment.Left,  _evShiftBrush, fsCorner);  // top-left
        Corner(sAltGrLbl, 0, 2, HorizontalAlignment.Right, _evAltGrBrush, fsCorner);  // top-right
        Corner(baseLbl,   2, 0, HorizontalAlignment.Left,  _evBaseBrush,  fsBase);    // bottom-left
        Corner(altGrLbl,  2, 2, HorizontalAlignment.Right, _evAltGrBrush, fsCorner);  // bottom-right
        return grid;
    }

    /// <summary>
    /// Small simplified Windows-flag icon (4 tiny squares), used in place of
    /// text on the Win keys — mirrors Base Camp, which renders a Font Awesome
    /// "windows" brand glyph there (<c>content:'\f17a'</c> in keyboard.css)
    /// instead of the literal "lwin"/"rwin" data-key value. K2 has no FA
    /// font bundled and Segoe MDL2 Assets has no Windows-logo glyph, so this
    /// draws the flag shape directly instead of relying on a font.
    /// </summary>
    private static FrameworkElement BuildWinIcon()
    {
        const double sq = 4.5, gap = 1;
        var grid = new Grid
        {
            Width = sq * 2 + gap, Height = sq * 2 + gap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sq) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sq) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(sq) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(sq) });

        for (int r = 0; r < 2; r++)
        for (int c = 0; c < 2; c++)
        {
            var rect = new System.Windows.Shapes.Rectangle { Fill = Brushes.White };
            Grid.SetRow(rect, r * 2);
            Grid.SetColumn(rect, c * 2);
            grid.Children.Add(rect);
        }
        return grid;
    }

    private void BuildEverestKeyboardOverlay()
    {
        _evKeyboardButtons.Clear();

        var keyStyle = (Style)FindResource("EverestKeyStyle");

        void AddKeys(Canvas canvas, KeyDef[] keys)
        {
            foreach (var kd in keys)
            {
                // BC keyboard.css: single label 0.5rem (8px); when a key shows
                // more than one legend, the whole pseudo-element is 7px. All white.
                double fs       = kd.W < 30 ? 6 : 8;   // single legend
                double fsMulti  = kd.W < 30 ? 6 : 7;   // multi-legend (BC 7px)
                double fsBig    = fs + 1;               // bumped size for multi-legend keycaps
                string? altLbl     = KeyLabelMap.AltLabel(_evLayoutType, kd.MatrixId);
                string? altGrLbl   = KeyLabelMap.AltGrLabel(_evLayoutType, kd.MatrixId);
                string? sAltGrLbl  = KeyLabelMap.ShiftAltGrLabel(_evLayoutType, kd.MatrixId);

                FrameworkElement content;
                if (kd.MatrixId is 91 or 92)
                {
                    // Windows key: real Base Camp markup is data-key="lwin"/"rwin" but
                    // CSS overrides it to a Font Awesome "windows" brand glyph (flag
                    // icon), not literal text — draw the same flag shape instead.
                    content = BuildWinIcon();
                }
                else if (kd.MatrixId == 9)
                {
                    // Tab: real Base Camp markup is data-alt="TAB" data-key="⇆" —
                    // word + arrow glyph stacked, not plain "Tab" text.
                    var sp = new StackPanel
                    {
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    sp.Children.Add(new TextBlock
                    {
                        Text                = "TAB",
                        Foreground          = Brushes.White,
                        FontSize            = fsMulti,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text                = "⇆",
                        Foreground          = Brushes.White,
                        FontSize            = fsMulti + 1,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    content = sp;
                }
                else if (altGrLbl is not null && (altLbl is not null || sAltGrLbl is not null))
                {
                    // 3/4-corner keycap for keys with AltGr AND shift (and maybe
                    // Shift+AltGr) legends: all corners a bit bigger than a normal
                    // letter key, flush into the true corners (BuildCornerLegend
                    // margin/spacer near zero) — this is the tightest case, so it
                    // gets everything the key physically has room for.
                    content = BuildCornerLegend(kd.Label, altLbl, altGrLbl, sAltGrLbl, fsBig, fsBig);
                }
                else if (altGrLbl is not null)
                {
                    // AltGr-only, no shift legend (e.g. E / €): the 4-corner grid
                    // would leave the whole top row empty and squeeze both legends
                    // into the bottom corners. A clean vertical stack reads better —
                    // base on top, AltGr below (opposite order from the shift-only
                    // stack below, matching where AltGr is usually printed on a
                    // real keycap: under the base character, not above it).
                    var sp = new StackPanel
                    {
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    sp.Children.Add(new TextBlock
                    {
                        Text                = kd.Label,
                        Foreground          = Brushes.White,
                        FontSize            = fsBig,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text                = altGrLbl,
                        Foreground          = _evAltGrBrush,
                        FontSize            = fsMulti + 1,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    content = sp;
                }
                else if (altLbl is not null)
                {
                    // Two-line label: shifted symbol above (grey, smaller),
                    // primary below (white, larger) — mirrors a real keycap
                    // where the base character dominates.
                    var sp = new StackPanel
                    {
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    };
                    sp.Children.Add(new TextBlock
                    {
                        Text                = altLbl,
                        Foreground          = _evShiftBrush,
                        FontSize            = fsMulti,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text                = kd.Label,
                        Foreground          = Brushes.White,
                        FontSize            = fs,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    });
                    content = sp;
                }
                else
                {
                    // BC's data-key text wraps at spaces (CSS white-space:normal) —
                    // narrow nav-cluster keys ("PRT SCN", "SCR LK", "PG UP"...) rely
                    // on this to fit in a 30px key without overflowing/being clipped
                    // by the Face border's rounded-corner clip. A single short word
                    // (e.g. "Esc", "F1") never wraps regardless, so this is safe for
                    // every key, not just the multi-word ones. Long single words with
                    // no space to wrap at ("HOME", "ENTER", "PAUSE") get an extra-small
                    // size instead, since Wrap can't help them (the worst offenders —
                    // "INSERT", "DELETE" — were shortened to "INS"/"DEL" instead, since
                    // even a tiny font can't fit 6 characters legibly in a 30px key).
                    // Only applies to actual 30px keys (<=32): the wider 38px modifier
                    // row (CTRL/ALT/FN) has enough room already — "CTRL" (4 chars) was
                    // wrongly caught here and shrunk well below "ALT" (3 chars, never
                    // matched), an obvious size mismatch between two keys in the same row.
                    bool   multiWord = kd.Label.Contains(' ');
                    bool   longWord  = !multiWord && kd.Label.Length >= 4 && kd.W <= 32;
                    double lblFs     = multiWord ? fsMulti
                                       : longWord ? (kd.W < 30 ? 4 : 5)
                                       : fs;
                    content = new TextBlock
                    {
                        Text                = kd.Label,
                        Foreground          = Brushes.White,
                        FontSize            = lblFs,
                        FontFamily          = _evKeyFont,
                        TextAlignment       = TextAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping        = multiWord ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    };
                }

                var tip = string.IsNullOrEmpty(kd.Label) ? "Space" : kd.Label;
                if (altLbl   is not null) tip += $"  ⇧ {altLbl}";
                if (altGrLbl is not null) tip += $"  AltGr {altGrLbl}";
                if (sAltGrLbl is not null) tip += $"  ⇧AltGr {sAltGrLbl}";
                tip += $"   (0x{kd.MatrixId:X2})";
                if (kd.MatrixId == 261) tip += "  — riservato, non configurabile";

                var btn = new Button
                {
                    Width   = kd.W,
                    Height  = kd.H,
                    Style   = keyStyle,
                    Content = content,
                    Tag     = kd.MatrixId,
                    ToolTip = tip,
                };

                btn.Click += EvKeyboardButton_Click;

                Canvas.SetLeft(btn, kd.X);
                Canvas.SetTop(btn, kd.Y);
                canvas.Children.Add(btn);

                if (kd.MatrixId != 0)
                    _evKeyboardButtons[kd.MatrixId] = btn;
            }
        }

        AddKeys(CvsEvKeyboard, EverestKeyboardLayout.GetBoardLeft(_evLayoutType));
        AddKeys(CvsEvNumpad,   EverestKeyboardLayout.BoardRight);
    }

    /// <summary>
    /// Clears and rebuilds the keyboard canvas with the current <see cref="_evLayoutType"/>.
    /// Also refreshes the LED tint map so overlays keep working.
    /// </summary>
    private void RebuildEverestKeyboardForLayout()
    {
        CvsEvKeyboard.Children.Clear();
        CvsEvNumpad.Children.Clear();
        BuildEverestKeyboardOverlay();

        _evKeyVisuals.Clear();
        BuildEverestKeyVisuals(CvsEvKeyboard, LedMatrixMapping.EverestKeyboard);
        BuildEverestKeyVisuals(CvsEvNumpad,   LedMatrixMapping.EverestNumpad);
        ApplyKeycapAppearanceToAllKeys();
    }

    // ---- Layout selector helpers ------------------------------------------

    private sealed record LayoutChoice(KeyboardLayoutType Layout, string Label)
    {
        // Fallback for the closed ComboBox: when the control's ancestor is still
        // Visibility="Collapsed" at the time ItemsSource/DisplayMemberPath are set
        // (the "Settings" section is not the default one shown), WPF may render
        // the closed box via ToString() instead of DisplayMemberPath. Matching
        // ToString() to the label keeps it correct either way (see RotationChoice
        // in MainWindow.Keys.cs for the same pattern).
        public override string ToString() => Label;
    }

    private void InitKeyboardLayoutSelector()
    {
        var choices = new[]
        {
            new LayoutChoice(KeyboardLayoutType.AnsiUs,    "English (US) — ANSI"),
            new LayoutChoice(KeyboardLayoutType.IsoUk,     "English (UK) — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoIt,     "Italian — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoDe,     "German (QWERTZ) — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoFr,     "French (AZERTY) — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoEs,     "Spanish — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoNordic, "Norwegian / Nordic — ISO"),
            new LayoutChoice(KeyboardLayoutType.IsoPt,     "Portuguese — ISO"),
        };
        CbEvKeyboardLayout.ItemsSource        = choices;
        CbEvKeyboardLayout.DisplayMemberPath  = nameof(LayoutChoice.Label);
        CbEvKeyboardLayout.SelectedItem       =
            System.Array.Find(choices, c => c.Layout == _evLayoutType) ?? choices[0];

        CbEvKeyboardLayout.SelectionChanged += OnKeyboardLayoutChanged;
    }

    private void OnKeyboardLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbEvKeyboardLayout.SelectedItem is not LayoutChoice c) return;
        if (c.Layout == _evLayoutType) return;
        _evLayoutType = c.Layout;
        RebuildEverestKeyboardForLayout();
    }

    // ---- Settings panel (Game Mode / Indicator LEDs / factory reset) -----

    /// <summary>True while <see cref="LoadEverestSettingsFromStore"/> is repopulating
    /// the checkboxes, to avoid re-saving/re-applying spuriously.</summary>
    private bool _evSettingsSuppress;

    private void InitEverestSettingsPanel()
    {
        InitKeyboardColorSelector();
        InitKeycapAppearanceControls();
        _evSettingsSuppress = true;
        try { LoadEverestSettingsFromStore(); }
        finally { _evSettingsSuppress = false; }
    }

    /// <summary>
    /// Loads Game Mode / Indicator LED state saved from the previous session
    /// (keys <c>settings.*</c>). "Sync across profiles" mirrors the RGB &amp;
    /// Lighting panel's checkbox: same physical device flag (SetSyncAcrossProfiles),
    /// so both controls stay aligned rather than tracking their own copy.
    /// </summary>
    private void LoadEverestSettingsFromStore()
    {
        CkSettingsSync.IsChecked = CkEvSync.IsChecked;

        int mode = int.TryParse(_evStore.GetSetting("settings.game_mode"), out var m) ? m : 0;
        CkGameModeShiftTab.IsChecked = (mode & 0x1) != 0;
        CkGameModeAltF4.IsChecked    = (mode & 0x2) != 0;
        CkGameModeWinKey.IsChecked   = (mode & 0x4) != 0;
        CkGameModeAltTab.IsChecked   = (mode & 0x8) != 0;

        CkCoreIndicatorLed.IsChecked =
            int.TryParse(_evStore.GetSetting("settings.indicator_led"), out var led) && led != 0;

        bool black = _evStore.GetSetting("settings.keyboard_color") == "black";
        CbSettingsKeyboardColor.SelectedItem =
            System.Array.Find(_keyboardColorChoices, c => c.IsBlack == black) ?? _keyboardColorChoices[0];
        ApplyKeyboardColor(black);

        LoadKeycapAppearanceFromStore();
    }

    private sealed record KeyboardColorChoice(bool IsBlack, string Label)
    {
        public override string ToString() => Label;
    }

    private KeyboardColorChoice[] _keyboardColorChoices = [];

    private void InitKeyboardColorSelector()
    {
        _keyboardColorChoices =
        [
            new KeyboardColorChoice(false, Loc.Get("color_silver")),
            new KeyboardColorChoice(true,  Loc.Get("color_black")),
        ];
        CbSettingsKeyboardColor.ItemsSource       = _keyboardColorChoices;
        CbSettingsKeyboardColor.DisplayMemberPath = nameof(KeyboardColorChoice.Label);
    }

    private void CbSettingsKeyboardColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        if (CbSettingsKeyboardColor.SelectedItem is not KeyboardColorChoice c) return;
        _evStore.SetSetting("settings.keyboard_color", c.IsBlack ? "black" : "silver");
        ApplyKeyboardColor(c.IsBlack);
    }

    /// <summary>Swaps the keyboard body art (cosmetic only — matches the app's
    /// rendering to the physical unit's actual color, no device command involved).</summary>
    private void ApplyKeyboardColor(bool black)
    {
        var keyBgFile      = black ? "keybg_black.png"      : "keybg.png";
        var boardRightFile = black ? "board_right_black.png" : "board_right.png";
        BrushEvKeyBg.ImageSource      = new BitmapImage(new Uri($"pack://application:,,,/Assets/{keyBgFile}"));
        BrushEvBoardRight.ImageSource = new BitmapImage(new Uri($"pack://application:,,,/Assets/{boardRightFile}"));
    }

    /// <summary>
    /// Bit layout confirmed by decompiling Base Camp's own
    /// <c>EverestOperations.SaveSettings</c> (BaseCamp.UI.dll): it builds a
    /// 4-char binary string "AltTab Win AltF4 Shift" and parses it base-2.
    /// </summary>
    private int EvGameModeBitmask() =>
        (CkGameModeShiftTab.IsChecked == true ? 0x1 : 0) |
        (CkGameModeAltF4.IsChecked    == true ? 0x2 : 0) |
        (CkGameModeWinKey.IsChecked   == true ? 0x4 : 0) |
        (CkGameModeAltTab.IsChecked   == true ? 0x8 : 0);

    /// <summary>Re-applies the persisted Game Mode / Indicator LED / Sync state
    /// to the device — called after the driver opens, mirroring RGB's ApplyCurrentEffect.</summary>
    private void ApplyEverestSettingsToDevice()
    {
        if (!_everest.IsOpen) return;
        LogEverest($"[SET ] SetGameMode({EvGameModeBitmask()}) -> {_everest.SetGameMode(EvGameModeBitmask())}");
        LogEverest($"[SET ] SetIndicatorLed({CkCoreIndicatorLed.IsChecked == true}) -> " +
                    $"{_everest.SetIndicatorLed(CkCoreIndicatorLed.IsChecked == true)}");
        _everest.SetSyncAcrossProfiles(CkEvSync.IsChecked == true);
    }

    private void CkGameMode_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        int mode = EvGameModeBitmask();
        _evStore.SetSetting("settings.game_mode", mode.ToString());
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open: state saved but not applied"); return; }
        LogEverest($"[SET ] SetGameMode({mode}) -> {_everest.SetGameMode(mode)}");
    }

    private void CkCoreIndicatorLed_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        bool enable = CkCoreIndicatorLed.IsChecked == true;
        _evStore.SetSetting("settings.indicator_led", enable ? "1" : "0");
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open: state saved but not applied"); return; }
        LogEverest($"[SET ] SetIndicatorLed({enable}) -> {_everest.SetIndicatorLed(enable)}");
    }

    private void CkSettingsSync_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        CkEvSync.IsChecked = CkSettingsSync.IsChecked; // same device flag as RGB & Lighting's checkbox
        CkEvSync_Click(sender, e);
    }

    private void BtnSettingsFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("settings_factory_reset_confirm"),
            Loc.Get("settings_factory_reset"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        LogEverest($"[SET ] ResetFlash(true) -> {_everest.ResetFlash(true)}");
    }

    /// <summary>
    /// Click on a key in the keyboard overlay — equivalent to "capture" if in
    /// capture mode; otherwise opens <see cref="ButtonActionDialog"/> to configure
    /// the key's action (adding it to the profile list first if not yet present).
    /// </summary>
    private void EvKeyboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int matrixId }) return;

        // Custom lighting paint mode: color the key and consume the click
        if (sender is Button btn && TryCustomPaint(btn, matrixId))
            return;

        // FN is reserved for the keyboard's own Fn-layer switching, not assignable
        // like other keys — Base Camp's own Razor markup marks the FN <span> with
        // pointer-events:none for the same reason, while still wrapping it in a
        // "keylighting" div so it keeps participating in RGB/custom-lighting.
        // TryCustomPaint above already ran, so lighting is unaffected by this guard.
        if (matrixId == 261) return;

        if (_evCapturing)
        {
            // Simulate capture as if the physical key was pressed
            HandleEverestKey(new EverestKeyEventArgs(0, (ushort)matrixId, true));
            return;
        }

        // Key editing is only enabled while the "Key Binding" section is active
        // (elsewhere the keyboard overlay is just a visual reference for other panels).
        if (!IsEvKeyBindingSectionActive) return;

        // Get or create the key entry. A newly created key is only added
        // in-memory (not persisted) until it's actually given an action below.
        bool isNewKey = !_evByMatrix.ContainsKey(matrixId);
        if (!_evByMatrix.TryGetValue(matrixId, out var key))
        {
            key = new EverestKey(matrixId);
            _evKeys.Add(key);
            _evByMatrix[matrixId] = key;
            LogEverest($"[CAP ] new key 0x{matrixId:X2} added via overlay click");
        }

        LvEvKeys.SelectedItem = key;

        // Open action dialog directly
        var dlg = new ButtonActionDialog(key.KeyMatrix, key.ActionType, key.ActionValue, _evActionHost) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            // Cancelled: discard a key that was only just created and never configured.
            if (isNewKey && key.ActionType is null)
            {
                _evKeys.Remove(key);
                _evByMatrix.Remove(matrixId);
            }
            return;
        }

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;

        EvPersistOrDiscardKey(key);
    }

    /// <summary>Highlights/un-highlights a key in the overlay when physically pressed. Uses the
    /// ControlTemplate's "Tint" overlay (see SetKeyTint in MainWindow.KeycapAppearance.cs) rather
    /// than touching Background directly: the keycap appearance system (custom color, live LED
    /// tint) owns Background/BorderBrush, and a plain assignment here would both silence it and,
    /// on release, fall back to the Style's default color instead of the user's configured one.</summary>
    private void EvHighlightKeyboardButton(int matrixId, bool pressed)
    {
        if (!_evKeyboardButtons.TryGetValue(matrixId, out var btn)) return;

        SetKeyTint(btn, pressed ? new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3)) : Brushes.Transparent); // K2 teal

        // Highlight text with contrasting color too
        SetLegendForeground(btn, pressed ? Brushes.Black : new SolidColorBrush(ResolveEverestKeycapTextColor()));
    }

    // ============================================================
    // Interactive key remapping (guided, like MacroPad)
    // ============================================================

    /// <summary>Loads the wMatrix→matrixId map from the DB (at startup).</summary>
    private void LoadEverestKeyMap()
    {
        _evWMatrixToLayout.Clear();

        // Always seed from the built-in default first (derived from BaseCamp.db
        // EverestKeyBidings.DLLMatrixIndex→VK). The SDK callback reports
        // DLLMatrixIndex as wMatrix, not VK codes, so the map is required for
        // correct highlighting (e.g. Enter: wMatrix=120 → VK=13, not F9).
        foreach (var (wMatrix, matrixId) in s_defaultWMatrixMap)
            _evWMatrixToLayout[wMatrix] = matrixId;

        // User-defined overrides (from "Mappa tasti") take precedence.
        var saved = _evStore.GetKeyMap();
        foreach (var (wMatrix, matrixId) in saved)
            _evWMatrixToLayout[wMatrix] = matrixId;

        LogEverest($"[MAP ] keyboard map: {_evWMatrixToLayout.Count} entries " +
                   $"(default + {saved.Count} user overrides)");
    }

    /// <summary>Starts or cancels the guided remapping of all keys.</summary>
    private void BtnEvMapKeys_Click(object sender, RoutedEventArgs e)
    {
        // Cancel if already in progress
        if (_evMapAwaitingIndex >= 0)
        {
            EvEndMapping(false);
            return;
        }

        // Build the ordered list of all keys to remap
        var left  = EverestKeyboardLayout.GetBoardLeft(_evLayoutType);
        var right = EverestKeyboardLayout.BoardRight;
        var all   = new List<KeyDef>(left.Length + right.Length);
        all.AddRange(left);
        all.AddRange(right);
        // Exclude keys without a MatrixId (placeholders/spacers)
        all.RemoveAll(kd => kd.MatrixId == 0);
        _evMapKeyDefs = all.ToArray();

        if (_evMapKeyDefs.Length == 0) return;

        // Clear the map and start
        _evWMatrixToLayout.Clear();
        _evMapAwaitingIndex = 0;
        BtnEvMapKeys.Content = Loc.Get("ev_cancel_mapping");
        EvHighlightMapTarget(0);
        LogEverest($"[MAP ] guided remapping started: {_evMapKeyDefs.Length} keys");
    }

    /// <summary>Highlights the current remap target key and updates the status bar.</summary>
    private void EvHighlightMapTarget(int index)
    {
        // Un-highlight the previous key (if any)
        if (index > 0)
        {
            var prev = _evMapKeyDefs[index - 1];
            if (_evKeyboardButtons.TryGetValue(prev.MatrixId, out var prevBtn))
            {
                SetKeyTint(prevBtn, Brushes.Transparent);
                SetLegendForeground(prevBtn, new SolidColorBrush(ResolveEverestKeycapTextColor()));
            }
        }

        if (index >= _evMapKeyDefs.Length) return;

        var target = _evMapKeyDefs[index];
        // Highlight the target key in gold
        if (_evKeyboardButtons.TryGetValue(target.MatrixId, out var btn))
        {
            SetKeyTint(btn, new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))); // gold
            SetLegendForeground(btn, Brushes.Black);
        }
        LblStatus.Text = Loc.Get("ev_mapping_step", index + 1, _evMapKeyDefs.Length, target.Label);
    }

    /// <summary>Ends the remapping (completed or cancelled).</summary>
    private void EvEndMapping(bool completed)
    {
        // Un-highlight the last highlighted key
        if (_evMapAwaitingIndex >= 0 && _evMapAwaitingIndex < _evMapKeyDefs.Length)
        {
            var last = _evMapKeyDefs[_evMapAwaitingIndex];
            if (_evKeyboardButtons.TryGetValue(last.MatrixId, out var btn))
            {
                SetKeyTint(btn, Brushes.Transparent);
                SetLegendForeground(btn, new SolidColorBrush(ResolveEverestKeycapTextColor()));
            }
        }

        _evMapAwaitingIndex = -1;
        BtnEvMapKeys.Content = Loc.Get("remap_keys");

        if (completed)
        {
            _evStore.SetKeyMap(_evWMatrixToLayout);
            LblStatus.Text = Loc.Get("ev_mapping_done", _evWMatrixToLayout.Count);
            LogEverest($"[MAP ] mapping complete and saved ({_evWMatrixToLayout.Count} keys)");
        }
        else
        {
            // Cancelled: reload the previous map
            LoadEverestKeyMap();
            LblStatus.Text = Loc.Get("mapping_cancelled");
            LogEverest("[MAP ] mapping cancelled");
        }
    }

    /// <summary>
    /// Translates an SDK wMatrix (DLLMatrixIndex) to the visual layout matrixId
    /// (VK code used as button Tag). Checks the merged user+default map first,
    /// then the built-in default, and finally falls back to wMatrix unchanged.
    /// </summary>
    private int EvTranslateMatrix(int wMatrix)
    {
        if (_evWMatrixToLayout.TryGetValue(wMatrix, out int layoutId)) return layoutId;
        if (s_defaultWMatrixMap.TryGetValue(wMatrix, out layoutId))    return layoutId;
        return wMatrix;
    }

    // ============================================================
    // Everest toolbar
    // ============================================================

    /// <summary>Auto-open Everest on startup (no UI feedback if SDK not found).</summary>
    internal void EvAutoOpen()
    {
        bool ok = _everest.Open();
        LogEverest($"[AutoOpen] Everest -> {ok}");
        if (!ok)
        {
            LogEverest("Hint: copy SDKDLL.dll from Mountain Base Camp\\ next to K2.App.exe. " +
                       "(Everest Max uses SDKDLL.dll; Everest360_USB.dll is for the Everest 60.)");
            return;
        }
        int ver = _everest.SdkVersion();
        LblEvSdk.Text = ver > 0 ? $"SDKDLL.dll v{ver}" : "SDKDLL.dll not available";
        EvRefresh();
        UpdateKeyboardLayout();
        ApplyCurrentEffect();
        ApplyEverestSettingsToDevice();
        StartLedPreview();

        // Restore custom device name if previously set
        var savedName = _evStore.GetSetting("device.name");
        if (!string.IsNullOrEmpty(savedName))
            TabEverest.Header = savedName;
    }

    private void BtnEvRename_Click(object sender, RoutedEventArgs e)
    {
        string current = TabEverest.Header as string ?? Loc.Get("tab_everest");
        string? name = ShowRenameDialog(current);
        if (name == null) return;
        TabEverest.Header = name;
        _evStore.SetSetting("device.name", name);
    }

    private void BtnEvOpen_Click(object sender, RoutedEventArgs e)
    {
        int ver = _everest.SdkVersion();
        LblEvSdk.Text = ver > 0 ? $"SDKDLL.dll v{ver}" : "SDKDLL.dll not available";
        LogEverest($"GetDLLVersion -> {ver}");

        bool ok = _everest.Open();
        LblStatus.Text = ok ? Loc.Get("ev_driver_opened") : Loc.Get("ev_driver_open_failed");
        LogEverest($"OpenUSBDriver -> {ok}");
        if (!ok)
            LogEverest("Hint: copy SDKDLL.dll from Mountain Base Camp\\ " +
                       "next to K2.App.exe, or keep Base Camp installed. " +
                       "(Everest Max uses SDKDLL.dll; Everest360_USB.dll is for the Everest 60.)");
        EvRefresh();

        if (ok)
        {
            UpdateKeyboardLayout();
            ApplyCurrentEffect();
            ApplyEverestSettingsToDevice();
            StartLedPreview();
        }
    }

    private void BtnEvClose_Click(object sender, RoutedEventArgs e)
    {
        _everest.Close();
        LblStatus.Text = Loc.Get("ev_driver_closed");
        LogEverest("CloseUSBDriver");
    }

    private void BtnEvRefresh_Click(object sender, RoutedEventArgs e) => EvRefresh();

    private void EvRefresh()
    {
        bool plugged = _everest.IsPlugged();
        LogEverest($"IsDevicePlug -> {plugged}");
        if (!plugged) return;

        ushort fw = _everest.FirmwareVersion();
        LogEverest($"GetDevAppVer -> {fw}");

        if (_everest.TryGetDeviceInfo(out var di))
            LogEverest($"VID=0x{di.vid:X4}  PID=0x{di.pid:X4}  FW=0x{di.fwVer:X4}  Boot=0x{di.bootloadVer:X4}");

        if (_everest.TryGetFirmwareInfo(out var fi))
            LogEverest($"Firmware current profile: {fi.currentlyProfileIndex}");

        UpdateKeyboardLayout();
    }

    private void BtnEvApOn_Click(object sender, RoutedEventArgs e)  =>
        LogEverest($"APEnable(true) -> {_everest.APEnable(true)}");
    private void BtnEvApOff_Click(object sender, RoutedEventArgs e) =>
        LogEverest($"APEnable(false) -> {_everest.APEnable(false)}");

    // ============================================================
    // Import XML (Base Camp-compatible or K2-only, same schema)
    // ============================================================

    private void BtnEvImportXml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = Loc.Get("dp_open_bc_profile"),
            Filter = Loc.Get("dp_filter_bc_xml"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var doc  = System.Xml.Linq.XDocument.Load(dlg.FileName);
            var root = doc.Root;
            if (root is null) return;

            int slot = 1;
            if (int.TryParse(root.Element("Id")?.Value, out var n) && n >= 1 && n <= 5)
                slot = n;
            string profileName = root.Element("ProfileName")?.Value
                                 ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            var bindings = root.Descendants("EverestKeyBidings").ToList();
            if (bindings.Count == 0)
            {
                LogEverest("[IMP-XML] No EverestKeyBidings found in XML.");
                return;
            }

            int regular = 0, touch = 0;

            foreach (var b in bindings)
            {
                if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out int matrixId)) continue;
                bool isTouchKey = b.Element("IsTouchKey")?.Value
                                   ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                string? funcType  = b.Element("FunctionType")?.Value;
                string? subType   = b.Element("SubFunctionType")?.Value;
                string? funcValue = b.Element("FunctionValue")?.Value;

                string? actionType, actionValue;
                if (funcType == "K2Action")
                {
                    actionType  = subType;
                    actionValue = string.IsNullOrEmpty(funcValue) ? null : funcValue;
                }
                else
                {
                    (actionType, actionValue) = BaseCampDbImporter.TranslateAction(funcType, subType, funcValue);
                }

                if (isTouchKey)
                {
                    // NDK 0-3: synthetic KeyId 9001-9004 (see EvProfileExporter) — device-global in K2.
                    int ndkIndex = matrixId - 9001;
                    if (ndkIndex < 0 || ndkIndex > 3) continue;

                    string? imageB64 = b.Element("base64Image")?.Value;
                    if (!string.IsNullOrEmpty(imageB64))
                    {
                        try
                        {
                            var bytes = BaseCampDbImporter.DecodeBase64Image(imageB64);
                            if (bytes is not null)
                            {
                                string dir = System.IO.Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "K2.App", "imported_xml_ev");
                                System.IO.Directory.CreateDirectory(dir);
                                string file = System.IO.Path.Combine(dir, $"ndk_{ndkIndex}.png");
                                System.IO.File.WriteAllBytes(file, bytes);
                                _evStore.SetSetting($"ndk.{ndkIndex}.imagePath", file);
                            }
                        }
                        catch (Exception ex) { LogEverest($"[IMP-XML] ndk #{ndkIndex} image decode failed: {ex.Message}"); }
                    }
                    if (actionType is not null)
                    {
                        _evStore.SetSetting($"ndk.{ndkIndex}.actionType", actionType);
                        _evStore.SetSetting($"ndk.{ndkIndex}.actionValue", actionValue ?? "");
                    }
                    touch++;
                }
                else
                {
                    if (actionType is null) continue;
                    _evStore.SaveKey(new EverestKeyRecord(slot, matrixId, null, actionType, actionValue));
                    regular++;
                }
            }

            _evStore.SetCurrentProfile(slot);
            EvRefreshProfiles();
            EvSelectProfileSlot(slot);
            ReloadEverestProfile();
            if (_everest.IsOpen && touch > 0) EvUploadNdkImages(slot);

            LogEverest($"[IMP-XML] '{profileName}' -> slot {slot}: {regular} keys, {touch} display keys");
            LblStatus.Text = Loc.Get("dp_imported_xml", profileName, slot);
        }
        catch (Exception ex)
        {
            LogEverest($"[ERR] import XML: {ex.Message}");
        }
    }

    // ============================================================
    // Export profiles — Base Camp-compatible XML / K2-only XML
    // ============================================================

    private void BtnEvExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var profiles = Enumerable.Range(1, EverestService.ProfileCount)
            .Select(slot => (Slot: slot, Name: _evStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot)))
            .ToList();
        int? currentSlot = CbEvProfile.SelectedItem is EvProfileItem pi ? pi.Slot : null;

        ExportProfileHelper.Run(
            owner: this,
            deviceLabel: "Everest",
            profiles: profiles,
            currentSlot: currentSlot,
            exportOne: (slot, name, bcCompatible, path) =>
            {
                var result = bcCompatible
                    ? EvProfileExporter.ExportBaseCamp(_evStore, slot, name, path)
                    : EvProfileExporter.ExportK2(_evStore, slot, name, path);
                return (result.Exported, result.SkippedActions, result.SkipReasons);
            },
            log: LogEverest,
            setStatus: s => LblStatus.Text = s);
    }

    // ============================================================
    // Import from Base Camp DB
    // ============================================================

    private void BtnEvImportBc_Click(object sender, RoutedEventArgs e)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            LogEverest("[IMP-BC] BaseCamp.db not found.");
            LblStatus.Text = Loc.Get("dp_bc_db_not_found");
            return;
        }
        LogEverest($"[IMP-BC] DB: {dbPath}");

        Dictionary<int, List<BaseCampDbImporter.BcProfile>> bcDevices;
        try { bcDevices = BaseCampDbImporter.ReadEverestProfiles(dbPath); }
        catch (Exception ex) { LogEverest($"[IMP-BC] Read error: {ex.Message}"); return; }

        if (bcDevices.Count == 0)
        {
            LogEverest("[IMP-BC] No Everest profiles in DB.");
            LblStatus.Text = Loc.Get("ev_no_profiles_in_bc");
            return;
        }

        // All Everest profiles share the same single device — collect them all
        var allProfiles = bcDevices.Values.SelectMany(x => x).OrderBy(p => p.Slot).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Base Camp → K2 Everest import:\n");
        foreach (var p in allProfiles)
            sb.AppendLine($"  Slot {p.Slot}: {p.Name}{(p.IsSelected ? " [ACTIVE]" : "")}");
        sb.AppendLine($"\nImport {allProfiles.Count} profile(s)?");

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int totalRegular = 0, totalTouch = 0;
        int activeSlot = -1;

        foreach (var profile in allProfiles)
        {
            try
            {
                var (reg, touch) = BaseCampDbImporter.ImportEverestProfile(dbPath, profile, _evStore);
                totalRegular += reg;
                totalTouch   += touch;
                LogEverest($"[IMP-BC] slot {profile.Slot} '{profile.Name}': {reg} keys, {touch} display keys");

                // Upload numpad display key images to hardware (if connected)
                if (_everest.IsOpen && touch > 0)
                    EvUploadNdkImages(profile.Slot);

                if (profile.IsSelected) activeSlot = profile.Slot;
            }
            catch (Exception ex)
            {
                LogEverest($"[IMP-BC] Error slot {profile.Slot}: {ex.Message}");
            }
        }

        // Switch to the active BC profile and reload UI
        if (activeSlot > 0)
        {
            _evStore.SetCurrentProfile(activeSlot);
            EvSelectProfileSlot(activeSlot);
            ReloadEverestProfile();
            LoadNdkState();
        }
        else
        {
            ReloadEverestProfile();
            LoadNdkState();
        }

        LogEverest($"[IMP-BC] Done: {totalRegular} regular + {totalTouch} display keys across {allProfiles.Count} profiles");
        LblStatus.Text = Loc.Get("ev_imported_bc", allProfiles.Count, totalRegular);
    }

    /// <summary>
    /// After importing BC profiles, uploads ndk images stored in EverestStore
    /// to the hardware (for the given profile slot).
    /// </summary>
    private void EvUploadNdkImages(int profileSlot)
    {
        for (int i = 0; i < 4; i++)
        {
            // ndk.{i}.imagePath is global (not per-profile) in EverestStore
            var path = _evStore.GetSetting($"ndk.{i}.imagePath");
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
            try
            {
                _everest.UploadNumpadImage(path, i + 1); // imagePathOrBase64, keyIndex (1-based)
                LogEverest($"[IMP-BC] ndk.{i} uploaded: {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                LogEverest($"[IMP-BC] ndk.{i} upload failed: {ex.Message}");
            }
        }
    }

    private void BtnEvCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_evCapturing)
        {
            _evCapturing = false;
            BtnEvCapture.Content = Loc.Get("ev_capture_key");
            LblStatus.Text = Loc.Get("ev_capture_cancelled");
            return;
        }
        _evCapturing = true;
        BtnEvCapture.Content = Loc.Get("ev_cancel_capture");
        LblStatus.Text = Loc.Get("ev_capture_prompt");
        LogEverest("[CAP ] waiting for a key…");
    }

    private void CbEvProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_evSuppressProfile) return;
        if (CbEvProfile.SelectedItem is not EvProfileItem pi) return;
        _evStore.SetCurrentProfile(pi.Slot);
        LogEverest($"[UI ] Everest profile selected: {pi.Slot}");
        ReloadEverestProfile();
    }

    // ============================================================
    // Key list: configure / remove
    // ============================================================

    private void BtnEvConfig_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key)
        {
            LogEverest("[WARN] select a key first");
            return;
        }
        if (key.KeyMatrix == 261)
        {
            LogEverest("[WARN] FN is reserved (Fn-layer key) — no action can be assigned to it");
            return;
        }
        var dlg = new ButtonActionDialog(key.KeyMatrix, key.ActionType, key.ActionValue, _evActionHost)
                  { Owner = this };
        if (dlg.ShowDialog() != true) return;

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;

        EvPersistOrDiscardKey(key);
    }

    private void BtnEvRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key) return;
        _evKeys.Remove(key);
        _evByMatrix.Remove(key.KeyMatrix);
        _evStore.RemoveKey(EvCurrentProfile(), key.KeyMatrix);
        LogEverest($"[KEY ] key 0x{key.KeyMatrix:X2} removed");
    }

    /// <summary>
    /// Persists a key's current action, or — if it has no action assigned —
    /// discards it entirely (list + DB) instead of keeping an empty entry.
    /// </summary>
    private void EvPersistOrDiscardKey(EverestKey key)
    {
        if (key.ActionType is null)
        {
            _evKeys.Remove(key);
            _evByMatrix.Remove(key.KeyMatrix);
            _evStore.RemoveKey(EvCurrentProfile(), key.KeyMatrix);
            LogEverest($"[KEY ] key 0x{key.KeyMatrix:X2} emptied, removed");
        }
        else
        {
            _evStore.SaveKey(new EverestKeyRecord(
                EvCurrentProfile(), key.KeyMatrix, key.Label, key.ActionType, key.ActionValue));
            LogEverest($"[ACT ] key 0x{key.KeyMatrix:X2} <- type={key.ActionType}");
        }
    }

    // ============================================================
    // SDK key events
    // ============================================================

    private void OnEverestKey(object? sender, EverestKeyEventArgs e) =>
        Dispatcher.BeginInvoke(() => HandleEverestKey(e));

    private void HandleEverestKey(EverestKeyEventArgs e)
    {
        int rawMatrix = e.KeyMatrix;

        // Per-key-press log: noisy in normal use, so it only fires at LogLevel.Verbose
        // (see General Settings tab / AppSettings.LogLevel).
        if (AppSettings.LogLevel == K2LogLevel.Verbose)
            LogEverest($"[KEY ] wMatrix=0x{rawMatrix:X2} {(e.Pressed ? "down" : "up")}");

        // ---- Guided remapping in progress: capture wMatrix → matrixId ----
        if (e.Pressed && _evMapAwaitingIndex >= 0 && _evMapAwaitingIndex < _evMapKeyDefs.Length)
        {
            var target = _evMapKeyDefs[_evMapAwaitingIndex];
            _evWMatrixToLayout[rawMatrix] = target.MatrixId;
            LogEverest($"[MAP ] «{target.Label}» <- wMatrix=0x{rawMatrix:X2} → matrixId=0x{target.MatrixId:X2}");

            _evMapAwaitingIndex++;
            if (_evMapAwaitingIndex >= _evMapKeyDefs.Length)
                EvEndMapping(true);
            else
                EvHighlightMapTarget(_evMapAwaitingIndex);
            return;
        }

        // ---- HW capture for dock/display/dial slots ----
        if (e.Pressed && TryHwCapture(rawMatrix))
            return;

        // ---- Assigned dock/display/dial actions ----
        if (e.Pressed && TryExecuteHwAction(rawMatrix))
            return;

        // Translate SDK wMatrix to visual layout matrixId
        int matrix = EvTranslateMatrix(rawMatrix);

        // Capture mode: next pressed key is added to the list.
        if (_evCapturing && e.Pressed)
        {
            _evCapturing = false;
            BtnEvCapture.Content = Loc.Get("ev_capture_key");
            if (_evByMatrix.TryGetValue(matrix, out var existing))
            {
                LblStatus.Text = $"Key 0x{matrix:X2} already in list.";
                LvEvKeys.SelectedItem = existing;
            }
            else
            {
                // Only added in-memory (not persisted) until it's actually
                // given an action via BtnEvConfig_Click.
                var k = new EverestKey(matrix);
                _evKeys.Add(k);
                _evByMatrix[matrix] = k;
                LvEvKeys.SelectedItem = k;
                LogEverest($"[CAP ] new key 0x{matrix:X2} added");
                LblStatus.Text = $"Key 0x{matrix:X2} captured. Configure its action.";
            }
            return;
        }

        // Highlight in the visual keyboard overlay
        EvHighlightKeyboardButton(matrix, e.Pressed);

        if (_evByMatrix.TryGetValue(matrix, out var key))
        {
            key.IsHighlighted = e.Pressed;
            if (e.Pressed) ExecuteEverestKey(key);
        }
    }

    private void ExecuteEverestKey(EverestKey k) =>
        _evEngine?.Execute(k.ActionType, k.ActionValue, _evKeys.IndexOf(k));

    private void ReloadEverestProfile()
    {
        _evKeys.Clear();
        _evByMatrix.Clear();
        int profile = EvCurrentProfile();
        foreach (var r in _evStore.LoadProfile(profile))
        {
            var k = new EverestKey(r.KeyMatrix)
            {
                Label       = r.Label ?? "",
                ActionType  = r.ActionType,
                ActionValue = r.ActionValue,
            };
            _evKeys.Add(k);
            _evByMatrix[r.KeyMatrix] = k;
        }
        LogEverest($"[DB  ] profile {profile}: loaded {_evKeys.Count} keys");
    }

    // ============================================================
    // IActionHost adapter (delegates passed to EverestActionHost)
    // ============================================================

    private int EvCurrentProfile()
        => CbEvProfile.SelectedItem is EvProfileItem pi ? pi.Slot : 1;

    /// <summary>Rebuilds the Everest profile combo with names (slots 1..5).</summary>
    private void EvRefreshProfiles()
    {
        _evSuppressProfile = true;
        try
        {
            var items = Enumerable.Range(1, EverestService.ProfileCount)
                .Select(s =>
                {
                    string name = _evStore.GetProfileName(s) ?? Loc.Get("profile_n", s);
                    return new EvProfileItem(s, name);
                })
                .ToList();
            CbEvProfile.DisplayMemberPath = nameof(EvProfileItem.Label);
            CbEvProfile.ItemsSource = items;
        }
        finally { _evSuppressProfile = false; }
    }

    /// <summary>Selects a profile slot in the Everest combo (suppresses event).</summary>
    private void EvSelectProfileSlot(int slot)
    {
        _evSuppressProfile = true;
        try
        {
            if (CbEvProfile.ItemsSource is List<EvProfileItem> items)
                CbEvProfile.SelectedItem = items.Find(x => x.Slot == slot) ?? items[0];
        }
        finally { _evSuppressProfile = false; }
    }

    private void BtnEvRenameProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = EvCurrentProfile();
        string current = _evStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        string? name = ShowRenameDialog(current,
            Loc.Get("rename_profile_title"),
            Loc.Get("rename_profile_prompt"));
        if (name is null) return;
        _evStore.SetProfileName(slot, name);
        EvRefreshProfiles();
        EvSelectProfileSlot(slot);
        LogEverest($"[UI ] Everest profile {slot} renamed to \"{name}\"");
    }

    private void BtnEvDeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        int slot = EvCurrentProfile();
        string profileName = _evStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("delete_profile_confirm", profileName),
            Loc.Get("delete_profile"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _evStore.ClearProfile(slot);
        LogEverest($"[UI ] Everest profile {slot} deleted.");
        EvRefreshProfiles();
        EvSelectProfileSlot(slot);
        ReloadEverestProfile();
    }

    private int EvSdkVersion()
    {
        try { return _everest.SdkVersion(); } catch { return 0; }
    }

    private IReadOnlyList<HostButton> EvGetButtons()
    {
        var list = new List<HostButton>(_evKeys.Count);
        for (int i = 0; i < _evKeys.Count; i++)
        {
            var k = _evKeys[i];
            list.Add(new HostButton(
                Index: i, KeyMatrix: k.KeyMatrix, HasImage: false, ImagePath: null,
                ActionType: k.ActionType, ActionValue: k.ActionValue));
        }
        return list;
    }

    private void EvPressButton(int index)
    {
        if (index >= 0 && index < _evKeys.Count)
            ExecuteEverestKey(_evKeys[index]);
    }

    /// <summary>
    /// Resolves "Next"/"Previous"/"1..N" and switches the Everest profile by
    /// calling the native SwitchProfile. Also updates the profile combo in UI.
    /// </summary>
    private void EvSwitchProfile(string target)
    {
        int cur = EvCurrentProfile();
        int next = cur;
        var t = (target ?? "").Trim();
        if (t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Next Profile", StringComparison.OrdinalIgnoreCase))
            next = cur == EverestService.ProfileCount ? 1 : cur + 1;
        else if (t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("Previous Profile", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("prev", StringComparison.OrdinalIgnoreCase))
            next = cur == 1 ? EverestService.ProfileCount : cur - 1;
        else if (int.TryParse(t, out var n) && n >= 1 && n <= EverestService.ProfileCount)
            next = n;
        else
        {
            LogEverest($"[EXEC] profile: target \"{t}\" not resolved");
            return;
        }
        if (next == cur) { LogEverest($"[EXEC] profile: already on {cur}"); return; }

        _everest.SwitchProfile(next);
        _evStore.SetCurrentProfile(next);
        EvSelectProfileSlot(next);
        LogEverest($"[EXEC] profile -> {next}");
    }

    // ============================================================
    // RGB lighting panel
    // ============================================================
    //
    // The panel populates Effect / Speed / Direction and hooks into sliders
    // and color pickers. Each change sends a ChangeEffect(EffData) to the
    // firmware (firmware presets are "fire & forget"). Colors are chosen with
    // System.Windows.Forms.ColorDialog (WPF has no built-in color dialog).
    //
    // State (effect + params + colors) lives only in memory — per-profile
    // persistence is a future step.
    // ------------------------------------------------------------

    /// <summary>
    /// "Effect" combo item. Record (not ValueTuple) because WPF resolves
    /// <c>DisplayMemberPath</c> via reflection on properties — elements of a
    /// <c>(Effect, string)</c> tuple become <c>Item1</c>/<c>Item2</c> at
    /// runtime and WPF falls back to <c>ToString()</c> producing "(Static, Static)".
    /// </summary>
    private sealed record EvEffectChoice(EverestService.Effect Eff, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly EvEffectChoice[] EvEffectList =
    {
        new(EverestService.Effect.Static,    "Static"),
        new(EverestService.Effect.Breath,    "Breath"),
        new(EverestService.Effect.Wave,      "Wave"),
        new(EverestService.Effect.ReactiveA, "Reactive A"),
        new(EverestService.Effect.ReactiveB, "Reactive B"),
        new(EverestService.Effect.ReactiveC, "Reactive C"),
        new(EverestService.Effect.Yeti,      "Yeti"),
        new(EverestService.Effect.Tornado,   "Tornado"),
        new(EverestService.Effect.Matrix,    "Matrix"),
        new(EverestService.Effect.Matrix2,   "Matrix 2"),
        new(EverestService.Effect.Off,       "Off"),
    };

    // ------------------------------------------------------------
    // Per-effect capabilities (from user effect list + USB captures).
    // Drive both UI controls (enable/disable, direction options) and
    // the bytes sent. Direction codes and speed scale from BC dumps.
    // ------------------------------------------------------------
    private sealed record EvCaps(
        int MaxColors,        // 1 or 2 color pickers used
        bool Rainbow,         // supports rainbow colors
        bool Speed,           // supports speed
        string[] DirLabels,   // direction options (empty = none)
        int[] DirCodes);      // byDirection for each option

    private static EvCaps CapsFor(EverestService.Effect e) => e switch
    {
        EverestService.Effect.Static    => new(1, false, false, System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.Breath    => new(2, true,  true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.Wave      => new(2, true,  true,  new[] { "Right", "Down", "Left", "Up" }, new[] { 0, 2, 4, 6 }),
        EverestService.Effect.Tornado   => new(1, true,  true,  new[] { "Clockwise", "Counter-CW" }, new[] { 9, 10 }),
        EverestService.Effect.ReactiveA => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.ReactiveB => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.ReactiveC => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.Yeti      => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.Matrix    => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        EverestService.Effect.Matrix2   => new(2, false, true,  System.Array.Empty<string>(), System.Array.Empty<int>()),
        _                               => new(1, false, false, System.Array.Empty<string>(), System.Array.Empty<int>()), // Off
    };

    /// <summary>Direction index restored from settings (applied if valid for the effect).</summary>
    private int _evSavedDirIndex;

    /// <summary>
    /// Aligns RGB controls to the selected effect's capabilities:
    /// enables/disables speed, direction (with the right options),
    /// rainbow and the 2nd color picker. Suppresses events to avoid
    /// spurious applies while repopulating controls.
    /// </summary>
    private void UpdateEvCapabilities()
    {
        if (CbEvEffect.SelectedItem is not EvEffectChoice pick) return;
        var caps = CapsFor(pick.Eff);

        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            // Speed
            CbEvSpeed.IsEnabled = caps.Speed;

            // Direction: options depend on the effect
            if (caps.DirLabels.Length > 0)
            {
                CbEvDirection.ItemsSource = caps.DirLabels;
                int di = (_evSavedDirIndex >= 0 && _evSavedDirIndex < caps.DirLabels.Length) ? _evSavedDirIndex : 0;
                CbEvDirection.SelectedIndex = di;
                CbEvDirection.IsEnabled = true;
            }
            else
            {
                CbEvDirection.ItemsSource = null;
                CbEvDirection.IsEnabled = false;
            }

            // Rainbow
            CkEvRainbow.IsEnabled = caps.Rainbow;
            if (!caps.Rainbow) CkEvRainbow.IsChecked = false;

            // 2nd color (3rd is always hidden)
            PnlEvColor2.Visibility = caps.MaxColors >= 2
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }
        finally
        {
            _evRgbSuppress = prev;
        }
    }

    private void InitEverestRgbPanel()
    {
        _evRgbSuppress = true;
        try
        {
            CbEvEffect.ItemsSource    = EvEffectList;
            CbEvEffect.DisplayMemberPath = "Label";

            // Speed = 5 positions (wire scale 10..6 is mapped in ApplyCurrentEffect).
            // Direction is populated by UpdateEvCapabilities based on effect
            // (Wave 4-way, Tornado CW/CCW, others: none).
            CbEvSpeed.ItemsSource     = new[] { "1 — slow", "2", "3", "4", "5 — fast" };

            // Defaults — overwritten if persisted settings exist.
            CbEvEffect.SelectedIndex     = 2; // Wave
            CbEvSpeed.SelectedIndex      = 2; // middle
            SldEvBrightness.Value        = 100;

            LoadEverestRgbFromStore();
            UpdateEvCapabilities();

            LblEvBrightness.Text = $"{(int)SldEvBrightness.Value}%";
            ApplyColorButton(BtnEvColor1, _evColor1);
            ApplyColorButton(BtnEvColor2, _evColor2);
            ApplyColorButton(BtnEvColor3, _evColor3);
        }
        finally
        {
            _evRgbSuppress = false;
        }
        _evRgbInitialized = true;
    }

    /// <summary>
    /// Loads RGB parameters saved from the previous session (keys
    /// <c>rgb.*</c> in the Settings table). Global state, not per-profile:
    /// per-profile extension is a future step.
    /// </summary>
    private void LoadEverestRgbFromStore()
    {
        int? IntSetting(string key) =>
            int.TryParse(_evStore.GetSetting(key), out var v) ? v : null;

        if (IntSetting("rgb.effect") is int eIdx)
        {
            for (int i = 0; i < EvEffectList.Length; i++)
                if ((byte)EvEffectList[i].Eff == eIdx) { CbEvEffect.SelectedIndex = i; break; }
        }
        if (IntSetting("rgb.speed")      is int sp && sp is >= 0 and <= 4) CbEvSpeed.SelectedIndex = sp;
        // Direction is set by UpdateEvCapabilities (depends on effect);
        // here we only restore the saved index, applied later if valid.
        if (IntSetting("rgb.direction")  is int dr && dr >= 0) _evSavedDirIndex = dr;
        if (IntSetting("rgb.brightness") is int br && br is >= 0 and <= 100) SldEvBrightness.Value = br;
        if (IntSetting("rgb.rainbow")    is int rb) CkEvRainbow.IsChecked = rb != 0;
        if (IntSetting("rgb.color1")     is int c1) _evColor1 = c1 & 0xFFFFFF;
        if (IntSetting("rgb.color2")     is int c2) _evColor2 = c2 & 0xFFFFFF;
        if (IntSetting("rgb.color3")     is int c3) _evColor3 = c3 & 0xFFFFFF;
        if (IntSetting("rgb.sync")       is int sy) CkEvSync.IsChecked = sy != 0;
    }

    /// <summary>Saves the current panel payload to Settings.</summary>
    private void SaveEverestRgbToStore()
    {
        if (!_evRgbInitialized || _evRgbSuppress) return;
        if (CbEvEffect.SelectedItem is not EvEffectChoice pick) return;
        _evStore.SetSetting("rgb.effect",     ((byte)pick.Eff).ToString());
        _evStore.SetSetting("rgb.speed",      CbEvSpeed.SelectedIndex.ToString());
        _evStore.SetSetting("rgb.direction",  CbEvDirection.SelectedIndex.ToString());
        _evStore.SetSetting("rgb.brightness", ((int)SldEvBrightness.Value).ToString());
        _evStore.SetSetting("rgb.color1",     _evColor1.ToString());
        _evStore.SetSetting("rgb.color2",     _evColor2.ToString());
        _evStore.SetSetting("rgb.color3",     _evColor3.ToString());
        _evStore.SetSetting("rgb.sync",       CkEvSync.IsChecked == true ? "1" : "0");
        _evStore.SetSetting("rgb.rainbow",    CkEvRainbow.IsChecked == true ? "1" : "0");
    }

    // WPF does NOT raise SelectionChanged when re-clicking the already selected item,
    // so the effect would not be re-sent. To allow re-sending on the same item
    // we use DropDownClosed. The flag prevents double-sending when the item
    // actually changes (SelectionChanged already handles it); reset on menu open.
    private bool _evEffectChangedWhileOpen;

    private void CbEvEffect_DropDownOpened(object sender, EventArgs e) =>
        _evEffectChangedWhileOpen = false;

    private void CbEvEffect_DropDownClosed(object sender, EventArgs e)
    {
        if (_evEffectChangedWhileOpen) { _evEffectChangedWhileOpen = false; return; }
        ApplyCurrentEffect(); // same item re-clicked -> resend anyway
    }

    private void CbEvEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _evEffectChangedWhileOpen = true;
        UpdateEvCapabilities();   // realign the controls to the new effect
        ApplyCurrentEffect();
    }

    private void CbEvEffectParam_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyCurrentEffect();

    private void CkEvRainbow_Click(object sender, RoutedEventArgs e) =>
        ApplyCurrentEffect();

    private void SldEvBrightness_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEvBrightness != null) LblEvBrightness.Text = $"{(int)e.NewValue}%";
        ApplyCurrentEffect();
    }

    private void BtnEvColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        int current = tag switch { "1" => _evColor1, "2" => _evColor2, _ => _evColor3 };

        // WinForms ColorDialog: the one system dialog WPF doesn't have.
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen     = true,
            AnyColor     = true,
            SolidColorOnly = true,
            Color        = System.Drawing.Color.FromArgb(
                (current >> 16) & 0xFF, (current >> 8) & 0xFF, current & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        switch (tag)
        {
            case "1": _evColor1 = rgb; break;
            case "2": _evColor2 = rgb; break;
            default:  _evColor3 = rgb; break;
        }
        ApplyColorButton(btn, rgb);
        ApplyCurrentEffect();
    }

    private void CkEvSync_Click(object sender, RoutedEventArgs e)
    {
        CkSettingsSync.IsChecked = CkEvSync.IsChecked; // same device flag as the Settings panel's checkbox
        SaveEverestRgbToStore();
        if (!_everest.IsOpen)
        {
            LogEverest("[WARN] Everest driver not open: state saved but not applied");
            return;
        }
        _everest.SetSyncAcrossProfiles(CkEvSync.IsChecked == true);
    }

    private void BtnEvLightOn_Click(object sender, RoutedEventArgs e)
    {
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        LogEverest($"[RGB ] SetBacklight(true) -> {_everest.SetBacklight(true)}");
    }

    private void BtnEvLightOff_Click(object sender, RoutedEventArgs e)
    {
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        LogEverest($"[RGB ] SetBacklight(false) -> {_everest.SetBacklight(false)}");
    }

    private void BtnEvLightReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        LogEverest($"[RGB ] ResetEffects -> {_everest.ResetEffects()}");
    }

    private void BtnEvLightSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        // 6 = ALL_PROFILE (see enum PROFILE_ID_T).
        LogEverest($"[RGB ] SaveFlash(ALL_PROFILE) -> {_everest.SaveFlash(6)}");
    }

    /// <summary>
    /// Reads all current panel parameters and sends them to the firmware.
    /// State is also persisted to Settings. No-op while the driver is not open
    /// or the first initialization is not yet complete.
    /// [RGB] log lines are diagnostic and go to the event panel so the user
    /// sees what happens without opening K2.App.log.
    /// </summary>
    private void ApplyCurrentEffect()
    {
        // Exit WITHOUT logging if the UI has not finished loading: during
        // InitializeComponent() the Slider raises ValueChanged setting Value=100
        // and arrives here before the MainWindow constructor has called
        // InitEverestModule/InitEverestRgbPanel.
        if (!_evRgbInitialized) return;
        if (_evRgbSuppress)     { LogEverest("[RGB ] skip: suppress active"); return; }
        SaveEverestRgbToStore();
        if (!_everest.IsOpen)   { LogEverest("[RGB ] skip: Everest driver not open");          return; }

        if (CbEvEffect.SelectedItem is not EvEffectChoice pick)
        {
            LogEverest($"[RGB ] skip: CbEvEffect.SelectedItem={CbEvEffect.SelectedItem?.GetType().Name ?? "null"}");
            return;
        }
        var effect = pick.Eff;
        var caps   = CapsFor(effect);

        // Speed: 5 UI positions -> scale 0..100 (pos0=0 slow … pos4=100 fast).
        // The DLL transforms internally for both ChangeEffect and ChangeBlockEffect.
        int speedByte = caps.Speed ? Math.Clamp(CbEvSpeed.SelectedIndex, 0, 4) * 25 : -1;

        // Direction: per-effect byte (Wave Right0/Down2/Left4/Up6,
        // Tornado CW9/CCW10). -1 = effect has no direction → use config.
        int dirByte = -1;
        if (caps.DirCodes.Length > 0 && CbEvDirection.SelectedIndex >= 0)
            dirByte = caps.DirCodes[Math.Clamp(CbEvDirection.SelectedIndex, 0, caps.DirCodes.Length - 1)];

        bool rainbow    = caps.Rainbow && CkEvRainbow.IsChecked == true;
        int  colorCount = rainbow ? 1 : caps.MaxColors;   // rainbow ignores color pickers
        int  bright     = (int)SldEvBrightness.Value;

        (byte r, byte g, byte b) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        (byte r, byte g, byte b)? secondary = null;
        if (caps.MaxColors >= 2) secondary = C(_evColor2);

        LogEverest($"[RGB ] apply eff={effect} speedByte={speedByte} dir={dirByte} rainbow={rainbow} " +
                   $"colors={colorCount} bright={bright}% c1=#{_evColor1:X6} c2=#{_evColor2:X6}");
        // NB: NO EnsureApMode here. ChangeEffect requires the device in normal mode;
        // AP mode is only for ChangeSWEffect (per-key streaming).
        bool ok = _everest.SetEffect(
            effect:             effect,
            primary:            C(_evColor1),
            secondary:          secondary,
            brightness:         bright,
            randomColor:        rainbow,
            speedByte:          speedByte,
            directionByte:      dirByte,
            colorCountOverride: colorCount);
        LogEverest($"[RGB ] ChangeEffect -> {ok}");
    }

    private static void ApplyColorButton(Button btn, int rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8)  & 0xFF);
        byte b = (byte) (rgb        & 0xFF);
        btn.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        btn.ToolTip    = $"#{rgb:X6}";
    }

    // ============================================================
    // Everest log
    // ============================================================

    private void LogEverest(string text)
    {
        // Suppressed entirely when LogLevel is Off (General Settings tab).
        if (AppSettings.LogLevel == K2LogLevel.Off) return;

        // Safety: XAML controls can raise events (e.g. Slider.ValueChanged when
        // the loader sets Value="100" in InitializeComponent) BEFORE generated
        // fields are assigned. Without this null-check, any early event would
        // throw NullReferenceException in MainWindow.
        App.WriteLog("[Everest] " + text);
        if (TxtEvLog == null) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        TxtEvLog.AppendText(line + Environment.NewLine);
        TxtEvLog.ScrollToEnd();
    }

    /// <summary>Thread-safe log for engine callbacks.</summary>
    private void LogEverestSafe(string text)
    {
        if (Dispatcher.CheckAccess()) LogEverest(text);
        else Dispatcher.BeginInvoke(new Action(() => LogEverest(text)));
    }
}
