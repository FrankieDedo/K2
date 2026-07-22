using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;
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

    private bool _evSuppressProfile;

    /// <summary>Connection poll — mirrors Ev60/Makalu's own timers
    /// (Ev60RefreshStatus/MkRefreshStatus): only drives TabEverest's
    /// Visibility, deliberately quiet (no per-tick Log()) unlike the
    /// verbose EvRefresh() used by the toolbar buttons.</summary>
    private DispatcherTimer? _evPollTimer;

    /// <summary>Maps matrixId → Button in the keyboard Canvas for highlight.</summary>
    private readonly Dictionary<int, Button> _evKeyboardButtons = new();

    // ---- Drag & drop (swap two keys' action) ----
    private const string EverestKeyDragFormat = "K2.EverestKeyMatrixId";
    private Point _evDragStartPoint;
    private int? _evDragCandidateMatrix;

    // ---- Interactive key remapping (like MacroPad) ----
    /// <summary>Maps <c>SDK wMatrix → layout matrixId</c> to translate callback codes.</summary>
    private readonly Dictionary<int, int> _evWMatrixToLayout = new();

    /// <summary>
    /// Default wMatrix (DLLMatrixIndex) → MatrixId (VK code) translation.
    /// Derived from BaseCamp.db EverestKeyBidings. Used as fallback when no
    /// user-defined map exists, so "Mappa tasti" is not required on first run.
    /// Key insight: the SDK KEY_CALLBACK reports DLLMatrixIndex as wMatrix,
    /// NOT the VK code — so without this map Enter (DLLMatrixIndex=120) would
    /// be mistaken for F9 (VK=120), etc. Shared with BaseCampDbImporter (see
    /// <see cref="EverestWMatrixMap"/>) so imported keys land in the same
    /// VK-code space as live SDK presses translate to.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> s_defaultWMatrixMap = EverestWMatrixMap.Default;
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

    /// <summary>Backlight-off-when-idle timer (device setting, global across
    /// profiles — see BacklightIdleTimer). SetBacklight(false/true) is a real
    /// firmware on/off toggle, so it doesn't disturb the configured effect.</summary>
    private BacklightIdleTimer? _evAutoOffTimer;
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
        LstEvProfile.ContextMenu = EvBuildProfileContextMenu();
        BtnEvProfileMenu.ContextMenu = EvBuildProfileMenuNoEdit();
        EvRefreshProfiles();
        EvSelectProfileSlot(_evStore.GetCurrentProfile());

        _everest.KeyEvent += OnEverestKey;
        _everest.NumpadButtonEvent += OnEverestNumpadButton;

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
            try { StopEvAccessoryPoll();  } catch { /* ignore */ }
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
        // Edge case: if "Custom" was the persisted rgb.effect, earlier calls to
        // SetCustomPaintModeActive(true)/ReapplyCustomOverlays (InitEverestRgbPanel,
        // InitCustomLightingPanel) ran before this method built the actual keycap
        // Buttons — catch up now.
        if (_customPaintMode)
            ReapplyCustomOverlays();
        InitNumpadDisplayKeys();
        InitKeyboardLayoutSelector();
        UpdateKeyboardLayout();
        LoadEverestKeyMap();

        _evPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _evPollTimer.Tick += (_, _) => EvRefreshConnectionStatus();
        _evPollTimer.Start();
        EvRefreshConnectionStatus();
    }

    /// <summary>Quiet connection check driving TabEverest's Visibility — separate from
    /// the verbose EvRefresh() (device info/firmware log dump) used by the toolbar's
    /// Open/Refresh buttons, so this can run unattended every 3s without flooding the
    /// console.</summary>
    private void EvRefreshConnectionStatus() => SetDeviceTabVisible(TabEverest, EvIsPhysicallyConnected());

    /// <summary>Live, SDK-independent presence check: raw HID enumeration of the MI_03
    /// command interface (same approach Everest60Service/MakaluService already use for
    /// their own connection polls), NOT <see cref="EverestService.IsPlugged"/>. Confirmed
    /// on real hardware (2026-07-13) that SDKDLL.dll's IsDevicePlug() keeps reporting
    /// "plugged" after a full physical unplug — its internal state seems to only refresh
    /// on the next OpenUSBDriver() call, not on every query — so it cannot drive tab
    /// visibility reliably. EverestHidNative.FindCommandInterfacePath() opens each
    /// candidate with 0 access rights (metadata query only), so it never conflicts with
    /// whatever handle SDKDLL.dll itself holds.</summary>
    private static bool EvIsPhysicallyConnected() => EverestHidNative.FindCommandInterfacePath() is not null;

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

                var btn = new Button
                {
                    Width   = kd.W,
                    Height  = kd.H,
                    Style   = keyStyle,
                    Content = content,
                    Tag     = kd.MatrixId,
                };

                btn.Click += EvKeyboardButton_Click;
                btn.AllowDrop = true;
                btn.PreviewMouseLeftButtonDown += EvKeyboardButton_PreviewMouseLeftButtonDown;
                btn.PreviewMouseMove += EvKeyboardButton_PreviewMouseMove;
                btn.DragEnter += EvKeyboardButton_DragEnter;
                btn.DragLeave += EvKeyboardButton_DragLeave;
                btn.Drop += EvKeyboardButton_Drop;

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
        (black ? RbEvKbColorBlack : RbEvKbColorSilver).IsChecked = true;
        ApplyKeyboardColor(black);

        LoadKeycapAppearanceFromStore();
    }

    private void RbEvKbColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        bool black = ReferenceEquals(sender, RbEvKbColorBlack);
        _evStore.SetSetting("settings.keyboard_color", black ? "black" : "silver");
        ApplyKeyboardColor(black);
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
        if (sender is not Button { Tag: int matrixId } btn0) return;

        // Edit-individual-keycaps mode (Settings section): open the per-key color/image
        // customizer instead of anything else this click would normally do. _evKeyVisuals is
        // keyed by LED index (not matrixId/VK), so find this button's entry to get its KeyId.
        if (_evKeycapEditMode && IsEvSettingsSectionActive)
        {
            var match = _evKeyVisuals.FirstOrDefault(kv => ReferenceEquals(kv.Value.Button, btn0));
            if (match.Value.Button != null)
            {
                string label = (btn0.Content as TextBlock)?.Text ?? EvKeyLabelForMatrix(matrixId) ?? "";
                OpenEvKeycapCustomizeDialog(match.Key, label);
            }
            return;
        }

        // Custom lighting paint mode: color the key and consume the click
        if (TryCustomPaint(btn0, matrixId))
            return;

        // FN is reserved for the keyboard's own Fn-layer switching, not assignable
        // like other keys — Base Camp's own Razor markup marks the FN <span> with
        // pointer-events:none for the same reason, while still wrapping it in a
        // "keylighting" div so it keeps participating in RGB/custom-lighting.
        // TryCustomPaint above already ran, so lighting is unaffected by this guard.
        if (matrixId == 261) return;

        // Key editing is only enabled while the "Key Binding" section is active
        // (elsewhere the keyboard overlay is just a visual reference for other panels).
        if (!IsEvKeyBindingSectionActive) return;

        // Get or create the key entry. A newly created key is only added
        // in-memory (not persisted) until it's actually given an action below.
        bool isNewKey = !_evByMatrix.ContainsKey(matrixId);
        if (!_evByMatrix.TryGetValue(matrixId, out var key))
        {
            key = new EverestKey(matrixId) { Label = EvKeyLabelForMatrix(matrixId) ?? "" };
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

    // ============================================================
    // Drag & drop — swap two keys' action
    // ============================================================

    private void EvKeyboardButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _evDragStartPoint = e.GetPosition(null);
        _evDragCandidateMatrix = (sender as Button)?.Tag as int?;
    }

    private void EvKeyboardButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _evDragCandidateMatrix is not int matrixId) return;
        if (!IsEvKeyBindingSectionActive || matrixId == 261 ||
            !_evByMatrix.TryGetValue(matrixId, out var key) || !key.HasAction)
        {
            _evDragCandidateMatrix = null;
            return;
        }
        if (!DragDropHelper.ExceedsDragThreshold(_evDragStartPoint, e.GetPosition(null))) return;

        _evDragCandidateMatrix = null;
        DragDrop.DoDragDrop((Button)sender, new DataObject(EverestKeyDragFormat, matrixId), DragDropEffects.Move);
    }

    private void EvKeyboardButton_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(EverestKeyDragFormat) && sender is Button { Tag: int tgt } && tgt != 261;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok && sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, true);
    }

    private void EvKeyboardButton_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
    }

    private void EvKeyboardButton_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button btn) DragDropHelper.SetDropTargetHighlight(btn, false);
        if (!IsEvKeyBindingSectionActive) return;
        if (sender is not Button { Tag: int targetMatrix } || targetMatrix == 261) return;
        if (!e.Data.GetDataPresent(EverestKeyDragFormat)) return;

        int sourceMatrix = (int)e.Data.GetData(EverestKeyDragFormat);
        if (sourceMatrix == targetMatrix || sourceMatrix == 261) return;
        if (!_evByMatrix.TryGetValue(sourceMatrix, out var sourceKey)) return;

        if (!_evByMatrix.TryGetValue(targetMatrix, out var targetKey))
        {
            targetKey = new EverestKey(targetMatrix);
            _evKeys.Add(targetKey);
            _evByMatrix[targetMatrix] = targetKey;
        }

        (sourceKey.ActionType, targetKey.ActionType)   = (targetKey.ActionType, sourceKey.ActionType);
        (sourceKey.ActionValue, targetKey.ActionValue) = (targetKey.ActionValue, sourceKey.ActionValue);

        EvPersistOrDiscardKey(sourceKey);
        EvPersistOrDiscardKey(targetKey);

        LogEverest($"[KEY ] swapped 0x{sourceMatrix:X2} <-> 0x{targetMatrix:X2}");
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

    /// <summary>
    /// Looks up the printed legend for a layout matrixId (board + numpad),
    /// so the Key Binding list can show a real key name instead of a hex code.
    /// Returns null for matrixIds outside the current layout (e.g. dock/crown,
    /// handled separately via MainWindow.DockActions.cs).
    /// </summary>
    private string? EvKeyLabelForMatrix(int matrixId)
    {
        foreach (var kd in EverestKeyboardLayout.GetBoardLeft(_evLayoutType))
            if (kd.MatrixId == matrixId) return string.IsNullOrEmpty(kd.Label) ? null : kd.Label;
        foreach (var kd in EverestKeyboardLayout.BoardRight)
            if (kd.MatrixId == matrixId) return string.IsNullOrEmpty(kd.Label) ? null : kd.Label;
        return null;
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
        // Land the device on K2's current profile right away — every per-profile
        // operation (NDK uploads/resets target the ACTIVE firmware profile slot)
        // assumes the two agree, and at startup the keyboard may be on whatever
        // profile it was last left on.
        _everest.SwitchProfile(EvCurrentProfile());
        UpdateKeyboardLayout();
        ApplyCurrentEffect();
        ApplyEverestSettingsToDevice();
        StartLedPreview();
        // NDK image resync intentionally NOT done here (2026-07-16, user report):
        // sending the numpad display key pictures during the automatic startup
        // open hung the whole app on some setups. Still runs on a manual
        // "Open driver" click (BtnEvOpen_Click) and on profile switch/NDK
        // hot-plug, just not unconditionally at launch.

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
            EvUploadNdkImages(); // resync current profile's NDK pictures in case this is a different/reset device
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
        SetDeviceTabVisible(TabEverest, EvIsPhysicallyConnected()); // see EvIsPhysicallyConnected's doc: IsDevicePlug() alone is not reliable
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

            string profileName = root.Element("ProfileName")?.Value
                                 ?? System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            // Always land in a FRESH slot — the XML's own <Id> is just wherever the
            // profile happened to live on the machine it was exported from, and reusing
            // it here would silently overwrite whatever K2 profile already occupies that
            // slot number (see BaseCampDbImporter.FindFreeSlot's doc comment).
            int slot = BaseCampDbImporter.FindFreeSlot(_evStore.GetExistingProfiles());
            if (slot == 0)
            {
                MessageBox.Show(this, Loc.Get("import_no_free_slot", profileName),
                    Loc.Get("dp_open_bc_profile"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Real Base Camp XML exports the EverestKeyBindings navigation property as
            // a wrapper containing <KeyboardBinding> items (item element = the real
            // decompiled class name, confirmed 2026-07-15 against a genuine Base Camp
            // XML export — see EvProfileExporter's doc comment). Older K2 exports
            // (pre-fix) used flat, typo'd <EverestKeyBidings> elements — kept as a
            // fallback so previously-exported K2 files still import.
            var bindings = root.Descendants("KeyboardBinding").ToList();
            if (bindings.Count == 0)
                bindings = root.Descendants("EverestKeyBidings").ToList();
            if (bindings.Count == 0)
            {
                LogEverest("[IMP-XML] No KeyboardBinding/EverestKeyBidings found in XML.");
                return;
            }

            // Register the profile's name unconditionally, BEFORE translating any binding —
            // same fix as BaseCampDbImporter.ImportEverestProfile: without this, a profile
            // whose regular keys all translate to no action (or one that's entirely NDK/
            // touch-key content) writes no Keys row and never shows up in
            // EverestStore.GetExistingProfiles, so it silently disappears after import.
            _evStore.SetProfileName(slot, profileName);

            int regular = 0, touch = 0;

            // Existing K2 macro names, used by TranslateAction to auto-match a Base Camp
            // named-macro reference ("Default" FunctionType) against the user's own macro
            // library — see BaseCampDbImporter.TranslateDefaultAction's doc comment.
            var macroNames = _macroStore?.GetAll()
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            // FunctionType=="K2Action" is K2's own round-trip encoding (ActionType/Value
            // stashed verbatim in SubFunctionType/FunctionValue); anything else is real
            // Base Camp vocabulary translated through the shared table.
            (string? ActionType, string? ActionValue) TranslateBinding(System.Xml.Linq.XElement b)
            {
                string? funcType  = b.Element("FunctionType")?.Value;
                string? subType   = b.Element("SubFunctionType")?.Value;
                string? funcValue = b.Element("FunctionValue")?.Value;
                if (funcType == "K2Action")
                    return (subType, string.IsNullOrEmpty(funcValue) ? null : funcValue);
                return BaseCampDbImporter.TranslateAction(funcType, subType, funcValue, macroNames);
            }

            var touchBindings = new List<(int MatrixId, System.Xml.Linq.XElement El)>();
            foreach (var b in bindings)
            {
                if (!int.TryParse(b.Element("DLLMatrixIndex")?.Value, out int matrixId)) continue;
                bool isTouchKey = b.Element("IsTouchKey")?.Value
                                   ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                if (isTouchKey) { touchBindings.Add((matrixId, b)); continue; }

                var (actionType, actionValue) = TranslateBinding(b);
                if (actionType is null) continue;
                _evStore.SaveKey(new EverestKeyRecord(slot, matrixId, null, actionType, actionValue));
                regular++;
            }

            // NDK 0-3 (numpad LCD display keys, per-profile — see UploadNdkImage's doc
            // comment): assigned by ORDER among the touch-key bindings, not by
            // DLLMatrixIndex value. K2's own exports
            // use synthetic KeyIds 9001-9004 in ascending order (see EvProfileExporter),
            // but a genuine Base Camp XML export uses BC's own real, arbitrary KeyId for
            // these — matching by "matrixId - 9001" silently dropped every touch key
            // (icon included) from any real BC file. Ordering by DLLMatrixIndex then
            // taking the first NdkCount mirrors BaseCampDbImporter.ImportEverestProfile's
            // DB-based import, so both sources land on identical results.
            int ndkIndex = 0;
            foreach (var (_, b) in touchBindings.OrderBy(t => t.MatrixId))
            {
                if (ndkIndex >= NdkCount) break;
                var (actionType, actionValue) = TranslateBinding(b);

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
                                "K2.App", "imported_xml_ev", $"slot{slot}");
                            System.IO.Directory.CreateDirectory(dir);
                            string file = System.IO.Path.Combine(dir, $"ndk_{ndkIndex}.png");
                            System.IO.File.WriteAllBytes(file, bytes);
                            _evStore.SetSetting($"ndk.{slot}.{ndkIndex}.imagePath", file);
                        }
                    }
                    catch (Exception ex) { LogEverest($"[IMP-XML] ndk #{ndkIndex} image decode failed: {ex.Message}"); }
                }
                if (actionType is not null)
                {
                    _evStore.SetSetting($"ndk.{slot}.{ndkIndex}.actionType", actionType);
                    _evStore.SetSetting($"ndk.{slot}.{ndkIndex}.actionValue", actionValue ?? "");
                }
                touch++;
                ndkIndex++;
            }

            _evStore.SetCurrentProfile(slot);
            EvRefreshProfiles();
            EvSelectProfileSlot(slot);
            ReloadEverestProfile(); // refreshes NDK canvas thumbnails for the imported slot
            // Land the DEVICE on the imported profile BEFORE pushing its pictures —
            // EvSelectProfileSlot suppresses LstEvProfile_SelectionChanged, so the
            // firmware-profile alignment done there doesn't run on this path (see that
            // handler's comment for why the alignment matters at all).
            if (_everest.IsOpen) _everest.SwitchProfile(slot);
            // A freshly imported profile's pictures have never reached THIS physical device —
            // push them now (ReloadEverestProfile no longer does this on every plain switch),
            // then restore default artwork on any key the import left without an icon
            // (stale flash pictures from previous use would otherwise keep showing).
            if (touch > 0 && _everest.IsOpen) EvUploadNdkImages(busyMessage: Loc.Get("hw_busy_importing_profile"));
            EvResetEmptyNdkSlots(Loc.Get("hw_busy_importing_profile"));
            EvSyncNdkBindingsToFw();

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
        int? currentSlot = LstEvProfile.SelectedItem is EvProfileItem pi ? pi.Slot : null;

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

        string deviceLabel = TabEverest.Header as string ?? Loc.Get("tab_everest");

        List<BaseCampDbImporter.BcProfile> allProfiles;
        if (bcDevices.Count == 1)
        {
            allProfiles = bcDevices.Values.First().OrderBy(p => p.Slot).ToList();
        }
        else
        {
            // Base Camp has profiles for more than one physical Everest keyboard — let the
            // user pick which one, instead of silently flattening every BC device's
            // profiles together (the old behavior).
            var options = bcDevices.Select(kv => (
                BcDeviceId: kv.Key,
                Label: Loc.Get("bc_pick_device_label", kv.Key, kv.Value.Count,
                    string.Join(", ", kv.Value.Select(p => p.Name)))
            )).ToList();
            var picker = new BcDevicePickerDialog(deviceLabel, options) { Owner = this };
            if (picker.ShowDialog() != true) return;
            allProfiles = bcDevices[picker.SelectedBcDeviceId!.Value].OrderBy(p => p.Slot).ToList();
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Import {allProfiles.Count} profile(s) into \"{deviceLabel}\"?\n");
        foreach (var p in allProfiles)
            sb.AppendLine($"  {(p.IsSelected ? "[ACTIVE] " : "")}{p.Name}");
        sb.AppendLine();
        sb.AppendLine(Loc.Get("bc_import_will_wipe", deviceLabel));

        if (MessageBox.Show(this, sb.ToString(), "Import from Base Camp",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        // Pre-read every profile's bindings BEFORE wiping anything: this import is
        // destructive (replace, not append), so a corrupt/locked Base Camp DB must surface
        // while the existing K2 profiles are still intact — not after they're gone.
        try
        {
            foreach (var p in allProfiles)
                BaseCampDbImporter.ReadKeyBindings(dbPath, p.ProfileId);
        }
        catch (Exception ex)
        {
            LogEverest($"[IMP-BC] Pre-read failed, aborting before wipe: {ex.Message}");
            MessageBox.Show(this, Loc.Get("bc_import_read_failed", ex.Message),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Wipe: replace, don't append (unlike the old free-slot-seeking import).
        foreach (var slot in _evStore.GetExistingProfiles())
            _evStore.ClearProfile(slot);

        int totalRegular = 0, totalTouch = 0;

        var usedSlots = new HashSet<int>();

        // Existing K2 macro names, used by TranslateAction to auto-match a Base Camp
        // named-macro reference ("Default" FunctionType) against the user's own macro
        // library — same lookup the XML import path already uses (BaseCampDbImporter.
        // TranslateDefaultAction's doc comment), previously missing here so BC.db imports
        // never resolved named macros even when the library had a matching name.
        var macroNames = _macroStore?.GetAll()
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        foreach (var profile in allProfiles)
        {
            try
            {
                int targetSlot = BaseCampDbImporter.FindFreeSlot(usedSlots);
                if (targetSlot == 0) continue; // sanity ceiling only (5 real firmware slots)
                usedSlots.Add(targetSlot);

                var (reg, touch) = BaseCampDbImporter.ImportEverestProfile(dbPath, profile, _evStore, targetSlot, macroNames);
                totalRegular += reg;
                totalTouch   += touch;
                LogEverest($"[IMP-BC] slot {profile.Slot} '{profile.Name}' -> K2 slot {targetSlot}: {reg} keys, {touch} display keys");
                // Each profile's NDK pictures live in their own firmware slot (see
                // UploadNdkImage's doc comment) and have never reached THIS physical
                // device — push them now, per imported profile, while the "please wait"
                // overlay is up. Same behavior real Base Camp shows during a DB/profile
                // import (see K2/_reference/usb_dumps analysis, 2026-07-16).
                if (touch > 0 && _everest.IsOpen) EvUploadNdkImages(targetSlot, Loc.Get("hw_busy_importing_profiles"));
            }
            catch (Exception ex)
            {
                LogEverest($"[IMP-BC] Error slot {profile.Slot}: {ex.Message}");
            }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int activateSlot = usedSlots.DefaultIfEmpty(0).Min();
        EvRefreshProfiles();
        if (activateSlot > 0)
        {
            _evStore.SetCurrentProfile(activateSlot);
            EvSelectProfileSlot(activateSlot);
            // Same firmware-profile alignment as LstEvProfile_SelectionChanged (suppressed
            // by EvSelectProfileSlot on this path) — the keyboard lands on the imported
            // profile, so its per-profile NDK pictures actually become the visible ones.
            if (_everest.IsOpen) _everest.SwitchProfile(activateSlot);
        }
        ReloadEverestProfile();
        EvResetEmptyNdkSlots(Loc.Get("hw_busy_importing_profiles"));
        EvSyncNdkBindingsToFw();
        LoadNdkState();

        LogEverest($"[IMP-BC] Done: {totalRegular} regular + {totalTouch} display keys across {allProfiles.Count} profiles");
        LblStatus.Text = Loc.Get("ev_imported_bc", allProfiles.Count, totalRegular);
    }

    /// <summary>
    /// Re-pushes the CURRENT profile's NDK (numpad LCD display key) images to hardware and
    /// tells the firmware which pic slot maps to which physical key
    /// (<see cref="NdkRefreshDevicePicSlots"/>). Each firmware profile stores its own 4 NDK
    /// pictures separately (confirmed via USB capture, see <see cref="UploadNdkImage"/>'s
    /// doc comment), so switching between already-configured profiles needs no re-upload at
    /// all — this only runs right after a BC/XML import (the images may never have reached
    /// THIS physical device yet) and on a fresh device connect (EvAutoOpen/BtnEvOpen_Click),
    /// as a resync safety net in case a different/factory-reset unit is plugged in.
    /// Shows the blocking "please wait" overlay for the whole batch, same as a single-key
    /// edit — each image is its own ~2s synchronous SDK call.
    /// </summary>
    private void EvUploadNdkImages(int? forProfile = null, string? busyMessage = null)
    {
        int profile = forProfile ?? EvCurrentProfile();

        // Resolve which keys actually have a picture to push BEFORE handing off to the
        // background thread (RunHwBusy's work runs off the UI thread — _evStore's
        // SqliteConnection isn't safe for concurrent multi-thread use, so no store access
        // is allowed inside the lambda below).
        var toUpload = new System.Collections.Generic.List<(int Index, string Path)>(4);
        for (int i = 0; i < 4; i++)
        {
            var path = _evStore.GetSetting($"ndk.{profile}.{i}.imagePath");
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                toUpload.Add((i, path));
        }

        var uploadedKeys = RunHwBusy(busyMessage ?? Loc.Get("hw_busy_uploading_image"), () =>
        {
            var okKeys = new System.Collections.Generic.List<int>();
            foreach (var (i, path) in toUpload)
            {
                // StartPicUpdate returns instantly (async) but the firmware stays "busy"
                // writing the previous image to flash for SEVERAL seconds afterwards, and
                // instantly rejects any StartPicUpdate received in that window (confirmed
                // via user logs 2026-07-19: back-to-back calls fail within 4ms; even a
                // fixed 2.2s spacing still got keys 2-4 rejected — the window is longer
                // than one nominal ~2s transfer). A single manual upload (NdkApplyImage)
                // never hits this because human actions are naturally paced. So: retry
                // with a 2s backoff until accepted (max ~12s/key) instead of guessing a
                // fixed safe delay.
                bool ok = false;
                // 10 attempts × 2s ≈ 20s worst case: user log 2026-07-19 12:15 showed a
                // key exhausting the previous 6-attempt (12s) budget and being skipped.
                for (int attempt = 0; attempt < 10 && !ok; attempt++)
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(2000);
                    try { ok = UploadNdkImage(i, path, profile); }
                    catch (Exception ex)
                    {
                        LogEverestSafe($"[NDK] ndk.{profile}.{i} upload threw: {ex.Message}");
                        break;
                    }
                }
                if (!ok) LogEverestSafe($"[NDK] ndk.{profile}.{i} still rejected after retries — skipped");
                else okKeys.Add(i);
            }
            return okKeys;
        });
        // Flash now holds a CUSTOM picture for these keys: clear their "flashOk"
        // marker (see EvResetEmptyNdkSlots) — store writes must stay on the UI thread.
        foreach (int i in uploadedKeys)
            _evStore.SetSetting($"ndk.{profile}.{i}.flashOk", "");
        if (uploadedKeys.Count > 0) NdkRefreshDevicePicSlots();
    }

    /// <summary>
    /// Restores the factory-default artwork on every display key of the CURRENT profile
    /// that has NO custom icon configured in K2. Needed because the keyboard's flash keeps
    /// each profile slot's last-written pictures forever: a K2 profile "without icons" can
    /// still land on a firmware slot full of leftover pictures from Base Camp or earlier
    /// tests, which then show up on a plain profile switch (user report 2026-07-19).
    /// The per-key <c>ndk.{profile}.{i}.flashClean</c> marker records a successful reset so
    /// the (multi-second, 8-packet) sequence isn't re-sent on every switch — it's cleared
    /// whenever a custom picture is uploaded to that key (<see cref="EvUploadNdkImages"/>/
    /// <see cref="NdkApplyImage"/>). Must be called AFTER the device has landed on
    /// <see cref="EvCurrentProfile"/> — the native reset only acts on the active profile.
    /// </summary>
    private void EvResetEmptyNdkSlots(string? busyMessage = null)
    {
        if (!_everest.IsOpen) return;
        int profile = EvCurrentProfile();

        var toReset = new System.Collections.Generic.List<int>(4);
        for (int i = 0; i < NdkCount; i++)
        {
            var img = _evStore.GetSetting($"ndk.{profile}.{i}.imagePath");
            if (!string.IsNullOrEmpty(img) && System.IO.File.Exists(img)) continue;
            // "flashOk" (NOT the earlier "flashClean" key, retired on purpose: markers
            // written by the pre-2026-07-19f build could record resets the firmware had
            // silently dropped — see the calm-window logic below — and permanently stuck
            // profile 1 with its leftover icons on the user's machine).
            if (_evStore.GetSetting($"ndk.{profile}.{i}.flashOk") == "1") continue;
            toReset.Add(i);
        }
        if (toReset.Count == 0) return;

        // The command echo only proves the firmware RECEIVED a reset, not that it applied
        // it: for several seconds after a picture upload the firmware is still writing
        // flash and silently drops (while still acking) further commands — confirmed via
        // user log 2026-07-19: resets issued ~200ms after an import's upload batch were
        // acked but the old pictures stayed on screen. So WAIT OUT the busy window inside
        // the overlay before resetting (user request 2026-07-19: after an import, keys
        // whose icon is empty must actually be cleared, not left showing stale pictures).
        var done = RunHwBusy(busyMessage ?? Loc.Get("hw_busy_uploading_image"), () =>
        {
            EvSleepUntilNdkFlashCalm();
            var okKeys = new System.Collections.Generic.List<int>(toReset.Count);
            foreach (int i in toReset)
                if (_everest.ClearNumpadImage(i, (byte)profile)) okKeys.Add(i);
            return okKeys;
        });
        foreach (int i in done)
            _evStore.SetSetting($"ndk.{profile}.{i}.flashOk", "1");
        if (done.Count > 0)
            LogEverest($"[NDK] profile {profile}: {done.Count} empty display key(s) restored to default artwork");
    }

    /// <summary>Blocks until the firmware's post-picture-upload busy window (~15s from the
    /// last successful flash write, see <see cref="_evNdkFlashWriteTicks"/>) has elapsed —
    /// commands sent inside it get acked but silently dropped. Call from a RunHwBusy
    /// background lambda only, never on the UI thread.</summary>
    private void EvSleepUntilNdkFlashCalm()
    {
        long last = System.Threading.Interlocked.Read(ref _evNdkFlashWriteTicks);
        if (last == 0) return;
        var wait = TimeSpan.FromSeconds(15) - (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc));
        if (wait > TimeSpan.Zero) System.Threading.Thread.Sleep(wait);
    }

    /// <summary>
    /// Writes the CURRENT profile's display-key action bindings into the firmware (see
    /// EverestService.WriteNumpadBinding): the write that flips each key to "custom" mode
    /// so the keyboard's built-in default action stops firing alongside K2's own execution
    /// — the "double action" of user reports 2026-07-19 (per the evicon.pcapng capture,
    /// assigning an action in Base Camp = exactly this binding write). The per-key
    /// <c>ndk.{profile}.{i}.fwBind</c> marker records what was last written so unchanged
    /// bindings aren't re-sent on every profile switch; keys with NO action are handled by
    /// the reset flow instead (its 14 20 FF framing restores default mode — confirmed
    /// working by the user's remove-action test). Must run AFTER the device has landed on
    /// <see cref="EvCurrentProfile"/>.
    /// </summary>
    private void EvSyncNdkBindingsToFw()
    {
        if (!_everest.IsOpen) return;
        int profile = EvCurrentProfile();

        var toWrite = new System.Collections.Generic.List<(int Key, string Type, string Value, string Marker)>();
        for (int i = 0; i < NdkCount; i++)
        {
            var at = _evStore.GetSetting($"ndk.{profile}.{i}.actionType");
            if (string.IsNullOrEmpty(at)) continue;
            var av = _evStore.GetSetting($"ndk.{profile}.{i}.actionValue") ?? "";
            string marker = at + "|" + av;
            if (_evStore.GetSetting($"ndk.{profile}.{i}.fwBind") == marker) continue;
            toWrite.Add((i, at, av, marker));
        }
        if (toWrite.Count == 0) return;

        var done = RunHwBusy(Loc.Get("hw_busy_uploading_image"), () =>
        {
            EvSleepUntilNdkFlashCalm();   // small writes are dropped in the busy window too
            var ok = new System.Collections.Generic.List<(int Key, string Marker)>(toWrite.Count);
            foreach (var (k, at, av, marker) in toWrite)
                if (_everest.WriteNumpadBinding(k, at, av)) ok.Add((k, marker));
            return ok;
        });
        foreach (var (k, marker) in done)
            _evStore.SetSetting($"ndk.{profile}.{k}.fwBind", marker);
        if (done.Count > 0)
            LogEverest($"[NDK] profile {profile}: {done.Count} display-key binding(s) written to firmware");
    }

    private void LstEvProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_evSuppressProfile) return;
        if (LstEvProfile.SelectedItem is not EvProfileItem pi) return;
        int slot = pi.Slot;

        if (pi.IsNew)
        {
            // Create empty profile (see EverestStore.MarkProfileExists for why this
            // doesn't use a placeholder Keys row like MacroPad/DisplayPad do).
            _evStore.MarkProfileExists(slot);
            LogEverest($"[UI ] New empty Everest profile created: slot {slot}");
            EvRefreshProfiles();
            EvSelectProfileSlot(slot);
        }

        LogEverest($"[UI ] Everest profile selected: {slot}");
        EvActivateProfileSlot(slot);
    }

    /// <summary>
    /// Makes <paramref name="slot"/> the ACTIVE profile end-to-end: K2 store, device
    /// firmware profile, key list reload and empty-display-key reset sync. The firmware
    /// switch matters because NDK pictures and their uploads/clears are per-FIRMWARE-
    /// profile (byTargetPic — see UploadNdkImage's doc comment): without it, icon
    /// operations target flash slot N while the keyboard keeps displaying another
    /// profile — writes succeed but are invisible (user report 2026-07-19). Mirrors real
    /// Base Camp, which switches the active profile when one is selected in its UI.
    /// Shared by LstEvProfile_SelectionChanged and BtnEvDeleteProfile_Click (which must
    /// re-activate a SURVIVING slot — see its comment).
    /// </summary>
    private void EvActivateProfileSlot(int slot)
    {
        _evStore.SetCurrentProfile(slot);
        if (_everest.IsOpen) _everest.SwitchProfile(slot);
        ReloadEverestProfile();
        EvResetEmptyNdkSlots();
        EvSyncNdkBindingsToFw();
    }

    // ============================================================
    // Key list: configure / remove
    // ============================================================

    /// <summary>Configure/Remove only make sense with a row selected — mirrors
    /// LvMpKeys_SelectionChanged (MainWindow.Keys.cs).</summary>
    private void LvEvKeys_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool hasSelection = LvEvKeys.SelectedItem is not null;
        BtnEvConfig.IsEnabled = hasSelection;
        BtnEvRemove.IsEnabled = hasSelection;
    }

    private void BtnEvConfig_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key)
        {
            LogEverest("[WARN] select a key first");
            return;
        }
        // NDK display-key entry (see EvAddNdkEntriesToKeyList): configured via its own
        // image+action dialog, not the regular ButtonActionDialog/Keys-table path.
        if (key.NdkIndex is int ndkIdx)
        {
            ConfigureNdkKey(ndkIdx);
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
        if (key.NdkIndex is int ndkIdx)
        {
            ClearNdkKey(ndkIdx);
            return;
        }
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
        _evAutoOffTimer?.RegisterActivity();

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

        // Physical-press highlight disabled (2026-07-17, user request): the
        // wMatrix→matrixId translation has gaps, so the tint fired inconsistently
        // across keys and read as broken rather than useful.
        // EvHighlightKeyboardButton(matrix, e.Pressed);

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
                Label       = string.IsNullOrEmpty(r.Label) ? (EvKeyLabelForMatrix(r.KeyMatrix) ?? "") : r.Label,
                ActionType  = r.ActionType,
                ActionValue = r.ActionValue,
            };
            _evKeys.Add(k);
            _evByMatrix[r.KeyMatrix] = k;
        }
        // Refreshes the 4 NDK buttons' thumbnails/actions for this profile — no hardware
        // I/O, since each profile's pictures already live in their own firmware slot once
        // uploaded (see UploadNdkImage's doc comment). A switch only needs the on-screen
        // state to catch up with what's actually resident on the device.
        LoadNdkState();
        EvAddNdkEntriesToKeyList();
        LogEverest($"[DB  ] profile {profile}: loaded {_evKeys.Count} keys");

        ReloadEverestRgbForProfileSwitch();
    }

    /// <summary>
    /// Re-loads the RGB lighting panel for the profile that just became active
    /// (device firmware is already switched to it at this point — see callers of
    /// <see cref="ReloadEverestProfile"/>) and resends the effect, so each profile
    /// keeps its own remembered lighting when "sync across profiles" is off
    /// (no-op in practice when synced: same shared keys, same values). Mirrors
    /// Everest 60/Makalu's ReloadProfile. User request 2026-07-22.
    /// </summary>
    private void ReloadEverestRgbForProfileSwitch()
    {
        if (!_evRgbInitialized) return;
        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            LoadEverestRgbFromStore();
            UpdateEvCapabilities();
            LblEvBrightness.Text = $"{(int)SldEvBrightness.Value}%";
            ApplyColorButton(BtnEvColor1, _evColor1);
            ApplyColorButton(BtnEvColor2, _evColor2);
            ApplyColorButton(BtnEvColor3, _evColor3);
        }
        finally { _evRgbSuppress = prev; }
        ApplyCurrentEffect();
    }

    /// <summary>
    /// Surfaces the current profile's 4 numpad LCD "display keys" (NDK) in the same
    /// mapped-keys list as regular keys, but only when a key differs from its default
    /// (empty) state — i.e. carries a custom action and/or a custom icon. KeyMatrix is a
    /// negative placeholder (display keys have no real matrix code) and these entries are
    /// deliberately kept OUT of _evByMatrix — BtnEvConfig/BtnEvRemove branch on NdkIndex
    /// before touching any KeyMatrix-keyed persistence.
    /// </summary>
    private void EvAddNdkEntriesToKeyList()
    {
        int profile = EvCurrentProfile();
        for (int i = 0; i < NdkCount; i++)
        {
            string? at  = _evStore.GetSetting($"ndk.{profile}.{i}.actionType");
            string? av  = _evStore.GetSetting($"ndk.{profile}.{i}.actionValue");
            string? img = _evStore.GetSetting($"ndk.{profile}.{i}.imagePath");
            bool hasImg = !string.IsNullOrEmpty(img) && System.IO.File.Exists(img);
            bool hasAct = !string.IsNullOrEmpty(at);
            if (!hasImg && !hasAct) continue; // default/empty display key — omit

            _evKeys.Add(new EverestKey(-(i + 1))
            {
                NdkIndex    = i,
                Label       = Loc.Get("ev_display_key_label", i + 1),
                ActionType  = hasAct ? at : null,
                ActionValue = hasAct ? av : null,
                HasImage    = hasImg,
            });
        }
    }

    /// <summary>
    /// Re-derives just the NDK entries in the mapped-keys list (drops the old ones,
    /// re-adds whichever display keys still differ from default) without touching the
    /// regular per-profile keys already loaded in <see cref="_evKeys"/>/<see cref="_evByMatrix"/>.
    /// Called after any NDK edit — from the canvas display-key buttons
    /// (MainWindow.NumpadDisplayKeys.cs) as well as from this list's own Configure/Remove
    /// buttons — so both surfaces of the same state stay in sync.
    /// </summary>
    private void EvRefreshNdkInKeyList()
    {
        for (int i = _evKeys.Count - 1; i >= 0; i--)
            if (_evKeys[i].NdkIndex is not null) _evKeys.RemoveAt(i);
        EvAddNdkEntriesToKeyList();
    }

    // ============================================================
    // IActionHost adapter (delegates passed to EverestActionHost)
    // ============================================================

    private int EvCurrentProfile()
        => LstEvProfile.SelectedItem is EvProfileItem pi ? pi.Slot : 1;

    /// <summary>Populates the Everest profile combo with configured profiles + "New
    /// profile…" (device firmware always has 5 fixed slots — see EverestStore.
    /// GetExistingProfiles — but the UI only lists the ones actually in use, same
    /// as MacroPad/DisplayPad).</summary>
    private void EvRefreshProfiles()
    {
        _evSuppressProfile = true;
        try
        {
            var existing = _evStore.GetExistingProfiles();
            if (existing.Count == 0) existing.Add(1);
            var items = new List<EvProfileItem>();
            foreach (var slot in existing)
            {
                string name = _evStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
                items.Add(new EvProfileItem(slot, name));
            }
            int nextFree = Enumerable.Range(1, EverestService.ProfileCount)
                .FirstOrDefault(s => !existing.Contains(s));
            if (nextFree > 0)
                items.Add(new EvProfileItem(nextFree, Loc.Get("new_profile")));

            LstEvProfile.ItemsSource = items;

            EvRegisterProfileLaunchWatchers(existing);
        }
        finally { _evSuppressProfile = false; }
    }

    /// <summary>Registers this device's profiles with K2.Core.Services.ProfileLaunchWatcher
    /// — see DpRegisterProfileLaunchWatchers (MainWindow.DisplayPad.cs) for the shared
    /// pattern/rationale. Single-instance device, so the scope key has no device id.</summary>
    private void EvRegisterProfileLaunchWatchers(List<int> existing)
    {
        const string scope = "Ev:";
        var currentKeys = new HashSet<string>();
        foreach (var slot in existing)
        {
            string? exe = _evStore.GetSetting($"profile.{slot}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe,
                () => EvSwitchProfile(capturedSlot.ToString()));
        }
        foreach (var staleKey in ProfileLaunchWatcher.Instance.KeysWithPrefix(scope).Except(currentKeys))
            ProfileLaunchWatcher.Instance.RemoveRegistration(staleKey);
    }

    /// <summary>Selects a profile slot in the Everest combo (suppresses event).</summary>
    private void EvSelectProfileSlot(int slot)
    {
        _evSuppressProfile = true;
        try
        {
            if (LstEvProfile.ItemsSource is List<EvProfileItem> items)
                LstEvProfile.SelectedItem = items.Find(x => x.Slot == slot && !x.IsNew) ?? items[0];
        }
        finally { _evSuppressProfile = false; }
    }

    /// <summary>Right-click menu for LstEvProfile rows — see DpBuildProfileContextMenu
    /// (MainWindow.DisplayPad.cs) for the shared pattern/rationale.</summary>
    private ContextMenu EvBuildProfileContextMenu()
    {
        var menu = new ContextMenu();
        var miRename = new MenuItem { Header = Loc.Get("rename_profile") };
        miRename.Click += BtnEvRenameProfile_Click;
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnEvImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnEvImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnEvExportProfiles_Click;
        var miDelete = new MenuItem { Header = Loc.Get("delete_profile") };
        miDelete.Click += BtnEvDeleteProfile_Click;
        menu.Items.Add(miRename);
        menu.Items.Add(new Separator());
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDelete);
        return menu;
    }

    /// <summary>Same items as <see cref="EvBuildProfileContextMenu"/> minus Rename/Delete —
    /// opened from the small "…" button in the Profile header (BtnEvProfileMenu_Click),
    /// which is not tied to a specific row so renaming/deleting a specific profile
    /// wouldn't make sense there.</summary>
    private ContextMenu EvBuildProfileMenuNoEdit()
    {
        var menu = new ContextMenu();
        var miImportXml = new MenuItem { Header = Loc.Get("dp_import_xml") };
        miImportXml.Click += BtnEvImportXml_Click;
        var miImportBc = new MenuItem { Header = Loc.Get("import_bc") };
        miImportBc.Click += BtnEvImportBc_Click;
        var miExport = new MenuItem { Header = Loc.Get("export_profiles_btn") };
        miExport.Click += BtnEvExportProfiles_Click;
        menu.Items.Add(miImportXml);
        menu.Items.Add(miImportBc);
        menu.Items.Add(miExport);
        return menu;
    }

    private void BtnEvProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = btn;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
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
        // Cannot delete the last real profile
        if (_evStore.GetExistingProfiles().Count <= 1)
        {
            MessageBox.Show(Loc.Get("delete_profile_last"),
                Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
        // Land on a SURVIVING slot and activate it fully. The old code re-selected the
        // just-deleted slot: EvSelectProfileSlot's items[0] fallback then silently moved
        // the UI selection WITHOUT running the activation flow (store current profile,
        // hardware switch, reload), leaving half-updated state — the "phantom click" the
        // user saw when the selection later landed on the remaining profile (report
        // 2026-07-19: "simula un clic se ci clicco di nuovo sopra").
        int fallback = _evStore.GetExistingProfiles().DefaultIfEmpty(1).First();
        EvSelectProfileSlot(fallback);
        EvActivateProfileSlot(fallback);
    }

    /// <summary>Gear-icon popup for an Everest Max profile row (see ProfileGear_Click in
    /// MainWindow.xaml.cs): rename, delete (same guard as <see cref="BtnEvDeleteProfile_Click"/>),
    /// or link an executable whose launch auto-switches to this profile (see
    /// K2.Core.Services.ProfileLaunchWatcher, registered from <see cref="EvRefreshProfiles"/>).</summary>
    private void EvShowProfileGear(EvProfileItem pi)
    {
        string currentName = _evStore.GetProfileName(pi.Slot) ?? Loc.Get("profile_n", pi.Slot);
        string currentExe = _evStore.GetSetting($"profile.{pi.Slot}.launchExe") ?? "";
        var dlg = new ProfileSettingsDialog(currentName, currentExe) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.DeleteRequested)
        {
            if (_evStore.GetExistingProfiles().Count <= 1)
            {
                MessageBox.Show(Loc.Get("delete_profile_last"),
                    Loc.Get("delete_profile"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var res = MessageBox.Show(
                Loc.Get("delete_profile_confirm", currentName),
                Loc.Get("delete_profile"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
            _evStore.ClearProfile(pi.Slot);
            _evStore.SetSetting($"profile.{pi.Slot}.launchExe", "");
            LogEverest($"[UI ] Everest profile {pi.Slot} deleted (gear).");
            EvRefreshProfiles();
            int fallback = _evStore.GetExistingProfiles().DefaultIfEmpty(1).First();
            EvSelectProfileSlot(fallback);
            EvActivateProfileSlot(fallback);
            return;
        }

        _evStore.SetProfileName(pi.Slot, dlg.ProfileName);
        _evStore.SetSetting($"profile.{pi.Slot}.launchExe", dlg.ExePath);
        LogEverest($"[UI ] Everest profile {pi.Slot} settings updated (gear).");
        EvRefreshProfiles();
        EvSelectProfileSlot(pi.Slot);
    }

    /// <summary>Resets the currently selected profile's key bindings back to K2's
    /// defaults (empty) and re-applies. RGB lighting/keycap appearance are device-wide
    /// (not per-profile) for the Everest Max, so they are untouched — see the
    /// architectural note in _PROJECT_MAP.md.</summary>
    private void BtnEvRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        int slot = EvCurrentProfile();
        string profileName = _evStore.GetProfileName(slot) ?? Loc.Get("profile_n", slot);
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_profile_confirm", profileName),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;
        _evStore.ResetProfileToDefaults(slot);
        LogEverest($"[UI ] Everest profile {slot} restored to defaults.");
        EvRefreshProfiles();
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
        // EvSelectProfileSlot suppresses LstEvProfile_SelectionChanged (avoids re-entrant
        // handling while this method is already mid-switch) — which means it does NOT
        // call ReloadEverestProfile on its own. Call it explicitly so the key list AND
        // the NDK hardware re-upload (see ReloadEverestProfile's doc comment) actually
        // run on a profile switch triggered from the keyboard itself, not just from the
        // UI combo.
        EvSelectProfileSlot(next);
        ReloadEverestProfile();
        EvResetEmptyNdkSlots();
        EvSyncNdkBindingsToFw();
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
    // State (effect + params + colors) is persisted in Settings — shared
    // across profiles ("rgb.*") when "sync across profiles" is on, or
    // per-profile ("rgb.p{N}.*") when off, mirroring Everest 60/Makalu (see
    // EvRgbPrefix, user request 2026-07-22).
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
        new(EverestService.Effect.Custom,    "Custom"),
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

    /// <summary>Backs GridEvDirection's segmented buttons — mirrors what
    /// CbEvDirection.SelectedIndex used to provide before the direction
    /// ComboBox became a dynamically-rebuilt RadioButton row (2-4 options
    /// depending on the effect; see SegmentedButtonGroup).</summary>
    private int _evDirIndex;

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
            PnlEvSpeed.Visibility = caps.Speed ? Visibility.Visible : Visibility.Collapsed;

            // Direction: options depend on the effect
            if (caps.DirLabels.Length > 0)
            {
                int di = (_evSavedDirIndex >= 0 && _evSavedDirIndex < caps.DirLabels.Length) ? _evSavedDirIndex : 0;
                _evDirIndex = di;
                SegmentedButtonGroup.Rebuild(GridEvDirection, "EvDirection", caps.DirLabels, RbEvDirection_Checked, di);
                PnlEvDirection.Visibility = Visibility.Visible;
            }
            else
            {
                GridEvDirection.Children.Clear();
                PnlEvDirection.Visibility = Visibility.Collapsed;
            }

            // Color mode: Single/Double/Rainbow are one mutually-exclusive radio
            // group now (GroupName="EvColorMode") — WPF's RadioButton group
            // handles the mutual exclusion, no manual uncheck logic needed.
            // Rainbow/Double are only selectable when the effect supports them
            // (3rd color is always hidden); falls back to Single otherwise
            // (same pattern as the Direction/Speed Collapsed-when-unsupported
            // gating above).
            RbEvRainbow.IsEnabled = caps.Rainbow;
            RbEvRainbow.Visibility = caps.Rainbow ? Visibility.Visible : Visibility.Collapsed;
            if (!caps.Rainbow && RbEvRainbow.IsChecked == true)
                RbEvColorSingle.IsChecked = true;

            RbEvColorDouble.IsEnabled = caps.MaxColors >= 2;
            if (caps.MaxColors < 2 && RbEvColorDouble.IsChecked == true)
                RbEvColorSingle.IsChecked = true;

            UpdateEvColorRowVisibility();

            // "Custom" swaps the whole left column (direction/color-mode, all
            // irrelevant for per-key painting) for the Custom Lighting panel on the
            // right — see MainWindow.xaml's 2-column Grid comment.
            bool isCustom = pick.Eff == EverestService.Effect.Custom;
            PnlEvNormalControls.Visibility = isCustom ? Visibility.Collapsed : Visibility.Visible;
            PnlEvCustomLighting.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            // Only touch paint-mode/border-overlay state if RGB & Lighting is actually
            // the visible section right now — otherwise this can fire during startup
            // init (restoring a persisted "Custom" effect) while Key Binding/another
            // section is shown, incorrectly turning the border overlay on underneath
            // it. MainWindow.SectionNav.cs's ShowEvSection re-syncs correctly whenever
            // the user actually navigates to/from RGB & Lighting.
            if (_activeEvSection == PnlSecRgb)
                SetCustomPaintModeActive(isCustom);
        }
        finally
        {
            _evRgbSuppress = prev;
        }
    }

    private void InitEverestRgbPanel()
    {
        _evAutoOffTimer = new BacklightIdleTimer(Dispatcher, EvAutoOffTimeout, EvAutoOffWake);

        _evRgbSuppress = true;
        try
        {
            CbEvEffect.ItemsSource    = EvEffectList;
            CbEvEffect.DisplayMemberPath = "Label";

            // Direction is populated by UpdateEvCapabilities based on effect
            // (Wave 4-way, Tornado CW/CCW, others: none).

            // Defaults — overwritten if persisted settings exist.
            CbEvEffect.SelectedIndex     = 2; // Wave
            SldEvSpeed.Value             = 50;
            SldEvBrightness.Value        = 100;
            RbEvColorSingle.IsChecked    = true; // default, overridden by LoadEverestRgbFromStore if persisted

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
    /// Key namespace for the RGB effect settings: shared (<c>"rgb."</c>) when
    /// "sync across profiles" is on, or profile-scoped (<c>"rgb.p{N}."</c>)
    /// when off — synced means one shared effect for every profile by
    /// definition, so only the un-synced case needs per-profile storage
    /// (mirrors Everest 60/Makalu, user request 2026-07-22).
    /// </summary>
    private string EvRgbPrefix() =>
        CkEvSync.IsChecked == true ? "rgb." : $"rgb.p{EvCurrentProfile()}.";

    /// <summary>
    /// Loads RGB parameters saved from the previous session/profile (see
    /// <see cref="EvRgbPrefix"/>). The "sync" flag itself and the auto-off
    /// timer are always global device settings, not per-profile.
    /// </summary>
    private void LoadEverestRgbFromStore()
    {
        int? IntSetting(string key) =>
            int.TryParse(_evStore.GetSetting(key), out var v) ? v : null;

        if (IntSetting("rgb.sync") is int sy) CkEvSync.IsChecked = sy != 0;

        string prefix = EvRgbPrefix();
        if ((IntSetting(prefix + "effect") ?? IntSetting("rgb.effect")) is int eIdx)
        {
            for (int i = 0; i < EvEffectList.Length; i++)
                if ((byte)EvEffectList[i].Eff == eIdx) { CbEvEffect.SelectedIndex = i; break; }
        }
        if (CbEvEffect.SelectedItem is EvEffectChoice pick)
            LoadEffectParamsIntoControls(pick.Eff);

        CkEvAutoOffEnable.IsChecked = IntSetting("rgb.autoOffEnable") == 1;
        TxtEvAutoOffSeconds.Text    = (IntSetting("rgb.autoOffSeconds") ?? 60).ToString();
        EvApplyAutoOffConfig();
    }

    /// <summary>
    /// Loads one effect's remembered parameters (speed/direction/brightness/color
    /// mode/colors — keys <c>rgb.{effectByte}.*</c>) into the panel controls, so every
    /// effect keeps its own settings across switches (user request 2026-07-22: "se vado
    /// su custom e poi torno su wave ritrovo le stesse impostazioni"). Falls back to
    /// the pre-per-effect global <c>rgb.*</c> keys (one-time seeding for existing
    /// installs), then to the panel defaults. Caller is responsible for suppression
    /// (_evRgbSuppress) and for calling UpdateEvCapabilities afterwards — this only
    /// sets values. Custom's own state (per-LED colors) lives separately in
    /// <c>custom.keyColors</c>/<c>custom.sideColors</c> (MainWindow.CustomLighting.cs).
    /// </summary>
    private void LoadEffectParamsIntoControls(EverestService.Effect eff)
    {
        int? I(string key) =>
            int.TryParse(_evStore.GetSetting(key), out var v) ? v : null;
        // Profile-scoped (or shared, if synced — see EvRgbPrefix) namespace first,
        // falling back to the legacy always-global "rgb.{effectByte}." keys —
        // one-time seeding for existing installs/profiles that never had their
        // own per-profile value saved yet.
        string p  = $"{EvRgbPrefix()}{(byte)eff}.";
        string gp = $"rgb.{(byte)eff}.";

        int speed = I(p + "speed") ?? I(gp + "speed") ?? 50;
        if (speed is >= 0 and <= 100) SldEvSpeed.Value = speed;

        // Direction is applied by UpdateEvCapabilities (options depend on effect);
        // here we only restore the saved index, used there if valid.
        _evSavedDirIndex = I(p + "direction") ?? I(gp + "direction") ?? 0;
        _evDirIndex      = _evSavedDirIndex;

        int bright = I(p + "brightness") ?? I(gp + "brightness") ?? 100;
        if (bright is >= 0 and <= 100) SldEvBrightness.Value = bright;

        // Rainbow/Double/Single are one mutually-exclusive radio group — Rainbow
        // wins if both were somehow persisted true (shouldn't happen going forward).
        if ((I(p + "rainbow") ?? I(gp + "rainbow") ?? 0) != 0) RbEvRainbow.IsChecked = true;
        else if ((I(p + "colorDouble") ?? I(gp + "colorDouble") ?? 0) != 0) RbEvColorDouble.IsChecked = true;
        else RbEvColorSingle.IsChecked = true;

        _evColor1 = (I(p + "color1") ?? I(gp + "color1") ?? _evColor1) & 0xFFFFFF;
        _evColor2 = (I(p + "color2") ?? I(gp + "color2") ?? _evColor2) & 0xFFFFFF;
        _evColor3 = (I(p + "color3") ?? I(gp + "color3") ?? _evColor3) & 0xFFFFFF;
        ApplyColorButton(BtnEvColor1, _evColor1);
        ApplyColorButton(BtnEvColor2, _evColor2);
        ApplyColorButton(BtnEvColor3, _evColor3);
    }

    private void EvApplyAutoOffConfig()
    {
        bool enabled = CkEvAutoOffEnable.IsChecked == true;
        int  seconds = int.TryParse(TxtEvAutoOffSeconds.Text, out int s) ? s : 0;
        _evAutoOffTimer?.Configure(enabled, seconds);
    }

    /// <summary>Backlight-off-when-idle timer callbacks. Deliberately do NOT use
    /// SetMainBrightness/SetBacklight (SDKDLL.dll's on/off toggle): that call was
    /// suspected of crashing the SDK's internal callback thread on real hardware
    /// (2026-07-20 report — after auto-off engaged, no further physical key events
    /// were ever delivered again, meaning RegisterActivity/wake never re-fired;
    /// see App.xaml.cs's VEH crash-recovery mechanism, which exists precisely
    /// because SDKDLL.dll is known to crash). Instead, mirror Everest 60's
    /// approach (Everest60RgbPanel.SetBacklightForcedOff): resend the current
    /// effect via the same ChangeEffect/SetEffect path already exercised by
    /// every brightness-slider/effect change, just with brightness forced to 0,
    /// without touching the persisted brightness setting or the slider itself.</summary>
    private void EvAutoOffTimeout()
    {
        LogEverest("[RGB ] auto-off: resend effect at brightness=0");
        ApplyCurrentEffect(brightnessOverride: 0);
        CkEvBacklight.IsChecked = false;
    }

    private void EvAutoOffWake()
    {
        LogEverest("[RGB ] auto-off wake: resend current effect");
        ApplyCurrentEffect();
        CkEvBacklight.IsChecked = true;
    }

    private void CkEvAutoOffEnable_Click(object sender, RoutedEventArgs e)
    {
        _evStore.SetSetting("rgb.autoOffEnable", CkEvAutoOffEnable.IsChecked == true ? "1" : "0");
        EvApplyAutoOffConfig();
    }

    private void TxtEvAutoOffSeconds_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtEvAutoOffSeconds.Text, out int seconds) || seconds < 0)
        {
            seconds = 60;
            TxtEvAutoOffSeconds.Text = seconds.ToString();
        }
        _evStore.SetSetting("rgb.autoOffSeconds", seconds.ToString());
        EvApplyAutoOffConfig();
    }

    /// <summary>Saves the current panel payload to Settings — under the shared or
    /// profile-scoped namespace given by <see cref="EvRgbPrefix"/> (effect id under
    /// <c>{prefix}effect</c>, everything else under <c>{prefix}{effectByte}.*</c>,
    /// see <see cref="LoadEffectParamsIntoControls"/>). <c>rgb.sync</c> itself is
    /// always the global device flag.</summary>
    private void SaveEverestRgbToStore()
    {
        if (!_evRgbInitialized || _evRgbSuppress) return;
        if (CbEvEffect.SelectedItem is not EvEffectChoice pick) return;
        string prefix = EvRgbPrefix();
        string p = $"{prefix}{(byte)pick.Eff}.";
        _evStore.SetSetting(prefix + "effect", ((byte)pick.Eff).ToString());
        _evStore.SetSetting(p + "speed",       ((int)SldEvSpeed.Value).ToString());
        _evStore.SetSetting(p + "direction",   _evDirIndex.ToString());
        _evStore.SetSetting(p + "brightness",  ((int)SldEvBrightness.Value).ToString());
        _evStore.SetSetting(p + "color1",      _evColor1.ToString());
        _evStore.SetSetting(p + "color2",      _evColor2.ToString());
        _evStore.SetSetting(p + "color3",      _evColor3.ToString());
        _evStore.SetSetting("rgb.sync",        CkEvSync.IsChecked == true ? "1" : "0");
        _evStore.SetSetting(p + "rainbow",     RbEvRainbow.IsChecked == true ? "1" : "0");
        _evStore.SetSetting(p + "colorDouble", RbEvColorDouble.IsChecked == true ? "1" : "0");
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
        // Restore the newly selected effect's own remembered parameters before
        // realigning/applying (per-effect memory — the old values were already
        // saved under the previous effect's namespace on every change). Suppressed:
        // ApplyCurrentEffect below does the single apply+save.
        if (_evRgbInitialized && CbEvEffect.SelectedItem is EvEffectChoice pick)
        {
            bool prev = _evRgbSuppress;
            _evRgbSuppress = true;
            try { LoadEffectParamsIntoControls(pick.Eff); }
            finally { _evRgbSuppress = prev; }
        }
        UpdateEvCapabilities();   // realign the controls to the new effect
        ApplyCurrentEffect();
    }

    private void SldEvSpeed_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEvSpeed != null) LblEvSpeed.Text = $"{(int)SldEvSpeed.Value}%";
        ApplyCurrentEffect();
    }

    private void RbEvDirection_Checked(object sender, RoutedEventArgs e)
    {
        _evDirIndex = (int)((RadioButton)sender).Tag;
        ApplyCurrentEffect();
    }

    /// <summary>Single/Double/Rainbow color mode — one mutually-exclusive radio
    /// group (GroupName="EvColorMode"), so no manual uncheck logic is needed.</summary>
    private void RbEvColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_evRgbSuppress) return;
        UpdateEvColorRowVisibility();
        ApplyCurrentEffect();
    }

    /// <summary>Swatch rows follow the selected color mode: hidden entirely
    /// under Rainbow (colors are ignored), primary-only under Single, both
    /// under Double.</summary>
    private void UpdateEvColorRowVisibility()
    {
        bool rainbow = RbEvRainbow.IsChecked == true;
        PnlEvColor1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
        PnlEvColor2.Visibility = !rainbow && RbEvColorDouble.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

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

        int? picked = K2.Core.ColorPickerDialog.Pick(this, current);
        if (picked is not int rgb) return;

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

    private void CkEvBacklight_Click(object sender, RoutedEventArgs e)
    {
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }
        bool on = CkEvBacklight.IsChecked == true;
        LogEverest($"[RGB ] SetBacklight({on}) -> {_everest.SetBacklight(on)}");
        // Keep the idle timer's own forced-off/countdown state in sync with a
        // manual toggle — without this, turning the backlight back on here
        // after an auto-off never restarts the timer (it was Stop()'d in
        // Timer_Tick and only RegisterActivity/Configure ever call Start()
        // again), so the backlight would never auto-off a second time.
        _evAutoOffTimer?.RegisterActivity();
    }

    /// <summary>
    /// Reads all current panel parameters and sends them to the firmware.
    /// State is also persisted to Settings. No-op while the driver is not open
    /// or the first initialization is not yet complete.
    /// [RGB] log lines are diagnostic and go to the event panel so the user
    /// sees what happens without opening K2.App.log.
    /// </summary>
    private void ApplyCurrentEffect(int? brightnessOverride = null)
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
        if (effect == EverestService.Effect.Custom)
        {
            // Selecting Custom applies the remembered per-LED colors right away —
            // all-off if nothing was ever painted (user request 2026-07-22: entering
            // custom with no saved LEDs must turn everything dark, not keep the
            // previous effect running). The Custom panel's own Apply button covers
            // subsequent paint edits (BtnCustomApply_Click, MainWindow.CustomLighting.cs).
            byte cb = (byte)Math.Clamp(brightnessOverride ?? 100, 0, 100);
            LogEverest($"[RGB ] Custom selected: applying stored per-LED colors (bright={cb})");
            ApplyCustomColorsToDevice(cb);
            return;
        }
        var caps   = CapsFor(effect);

        // Speed: slider already snaps to 0/25/50/75/100 (scale 0..100, 0=slow, 100=fast).
        // The DLL transforms internally for both ChangeEffect and ChangeBlockEffect.
        int speedByte = caps.Speed ? (int)SldEvSpeed.Value : -1;

        // Direction: per-effect byte (Wave Right0/Down2/Left4/Up6,
        // Tornado CW9/CCW10). -1 = effect has no direction → use config.
        int dirByte = -1;
        if (caps.DirCodes.Length > 0)
            dirByte = caps.DirCodes[Math.Clamp(_evDirIndex, 0, caps.DirCodes.Length - 1)];

        bool rainbow    = caps.Rainbow && RbEvRainbow.IsChecked == true;
        bool useDouble  = !rainbow && caps.MaxColors >= 2 && RbEvColorDouble.IsChecked == true;
        int  colorCount = rainbow ? 1 : (useDouble ? caps.MaxColors : 1);
        int  bright     = brightnessOverride ?? (int)SldEvBrightness.Value;

        (byte r, byte g, byte b) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        (byte r, byte g, byte b)? secondary = null;
        if (useDouble) secondary = C(_evColor2);

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
