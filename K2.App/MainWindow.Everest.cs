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

    /// <summary>Current keyboard layout: the user's persisted choice if there is one,
    /// otherwise auto-detected from the Windows locale at startup
    /// (see <see cref="LoadPersistedKeyboardLayout"/>).</summary>
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
        LstEvProfile.ContextMenu = WithProfileGuide(EvBuildProfileContextMenu(), "everest");
        BtnEvProfileMenu.ContextMenu = WithProfileGuide(EvBuildProfileMenuNoEdit(), "everest");
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
            try { CleanupDisplayDial(); } catch { /* ignore */ }
            try { RestoreEvDisabledKeysOnExit(); } catch { /* ignore */ }
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
        _evLayoutType = LoadPersistedKeyboardLayout();
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
        _evPollTimer.Tick += (_, _) =>
        {
            EvRefreshConnectionStatus();
            SyncEverestGameModeStatusFromDevice();   // reflect Fn+Pause toggles
        };
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

        // The 4 display-key buttons live on CvsEvNumpad too (InitNumpadDisplayKeys adds
        // them there), and the Clear() above took them with it. They used to be built
        // exactly ONCE at startup, so any layout rebuild made them vanish from the UI for
        // the rest of the session — including the one an XML import triggers when the
        // imported profile carries IsLayoutConfigured (see BtnEvImportXml_Click), which is
        // what the user saw as "spariscono i tasti display dall'interfaccia" right after
        // importing a profile (2026-08-21). Rebuilding them here is safe: the method ends
        // with LoadNdkState(), which repopulates thumbnails and actions from the store.
        InitNumpadDisplayKeys();
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

    /// <summary>
    /// The layout the keycap legends are drawn with: the user's persisted choice when
    /// there is one, otherwise <see cref="EverestKeyboardLayout.DetectLayout"/>'s guess
    /// from the Windows locale.
    ///
    /// <para>Base Camp keeps the same distinction in its own DB — <c>KeyboardLayout</c>
    /// plus an <c>IsLayoutConfigured</c> flag in <c>KeyboardSettings</c> — because the
    /// locale guess is only ever a guess: an Italian-locale PC with a UK board got the
    /// wrong legends on every launch, and before this the user's correction lasted only
    /// until the app closed.</para>
    ///
    /// <para>Deliberate deviation from BC: BC stores the layout PER PROFILE (one row per
    /// ProfileId, plus a SysncAcrossProfile flag), so its printed legends can change when
    /// you switch profile. The physical keycaps obviously do not, so K2 stores ONE value
    /// per device.</para>
    ///
    /// <para>Nothing here goes to the keyboard, and that is not an omission: the layout
    /// is host-side on the wire too. Two captures of real BC switching layout
    /// (everest_layout.pcapng UK→IT and ev_deutsch.pcapng IT→DE, both 2026-08-21) are
    /// BYTE-IDENTICAL — <c>12 08 00 01</c> → <c>13 42 00 00 0f</c> →
    /// <c>13 60 00 00 00</c> — so not one bit of the chosen layout reaches the firmware;
    /// it only ever lands in BC's SQLite (<c>KeyboardSettings.KeyboardLayout</c>, values
    /// US/UK/Italian/German/French/Spanish/Nordic/Portugues). K2 must NOT replay that
    /// sequence: <c>13 42</c> with a key mask is our known display-key artwork reset
    /// (see <c>EverestHidNative.ResetDisplayKeyPic</c>), and the mask 0x0F sits at
    /// byte 4 = profile 1, i.e. it wipes all four display-key pictures of profile 1.</para>
    /// </summary>
    private KeyboardLayoutType LoadPersistedKeyboardLayout()
    {
        try
        {
            if (EverestKeyboardLayout.ParseStorageString(
                    _evStore.GetSetting(EverestKeyboardLayout.LayoutSettingKey)) is { } stored)
                return stored;
        }
        catch (Exception ex) { App.WriteLog("[Everest] LoadPersistedKeyboardLayout failed: " + ex); }
        return EverestKeyboardLayout.DetectLayout();
    }

    private void OnKeyboardLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbEvKeyboardLayout.SelectedItem is not LayoutChoice c) return;
        if (c.Layout == _evLayoutType) return;
        _evLayoutType = c.Layout;
        try
        {
            _evStore.SetSetting(EverestKeyboardLayout.LayoutSettingKey,
                                EverestKeyboardLayout.ToStorageString(c.Layout));
        }
        catch (Exception ex) { App.WriteLog("[Everest] Saving keyboard layout failed: " + ex); }
        RebuildEverestKeyboardForLayout();
    }

    // ---- Settings panel (Game Mode / Indicator LEDs / factory reset) -----

    /// <summary>True while <see cref="LoadEverestSettingsFromStore"/> is repopulating
    /// the checkboxes, to avoid re-saving/re-applying spuriously.</summary>
    private bool _evSettingsSuppress;

    /// <summary>False until <see cref="InitEverestSettingsPanel"/> has run once — guards
    /// <see cref="ReloadEverestSettingsForProfileSwitch"/> against the very first
    /// <c>ReloadEverestProfile</c> call, which happens before this panel's own Init
    /// (see the call order comment where ReloadEverestProfile is first invoked). Mirrors
    /// <c>_evRgbInitialized</c>'s exact role for the RGB panel.</summary>
    private bool _evSettingsInitialized;

    private void InitEverestSettingsPanel()
    {
        InitKeycapAppearanceControls();
        _evSettingsSuppress = true;
        try { LoadEverestSettingsFromStore(); }
        finally { _evSettingsSuppress = false; }
        _evSettingsInitialized = true;
    }

    /// <summary>
    /// Loads Game Mode / Indicator LED state saved from the previous session
    /// (keys <c>settings.*</c>). "Sync across profiles" mirrors the RGB &amp;
    /// Lighting panel's checkbox: same physical device flag (SetSyncAcrossProfiles),
    /// so both controls stay aligned rather than tracking their own copy.
    /// </summary>
    private void LoadEverestSettingsFromStore()
    {
        // Own flag since 2026-08-28; one-time migration: fall back to the old shared
        // rgb.sync so users who had "sync on" keep the Settings section synced too.
        CkSettingsSync.IsChecked =
            (_evStore.GetSetting("settings.sync") ?? _evStore.GetSetting("rgb.sync")) == "1";

        string prefix = EvSettingsPrefix();
        int mode = int.TryParse(_evStore.GetSetting(prefix + "game_mode")
            ?? _evStore.GetSetting("settings.game_mode"), out var m) ? m : 0;
        CkGameModeShiftTab.IsChecked = (mode & 0x1) != 0;
        CkGameModeAltF4.IsChecked    = (mode & 0x2) != 0;
        CkGameModeWinKey.IsChecked   = (mode & 0x4) != 0;
        CkGameModeAltTab.IsChecked   = (mode & 0x8) != 0;

        // Master engage/disengage (also toggled with Fn+Pause on the keyboard).
        CkGameModeMaster.IsChecked =
            int.TryParse(_evStore.GetSetting(prefix + "game_mode_master")
                ?? _evStore.GetSetting("settings.game_mode_master"), out var gmm) && gmm != 0;

        CkCoreIndicatorLed.IsChecked =
            int.TryParse(_evStore.GetSetting(prefix + "indicator_led")
                ?? _evStore.GetSetting("settings.indicator_led"), out var led) && led != 0;

        // Keyboard body color is a physical/cosmetic fact about the unit, not a
        // per-profile preference — always the global key, never prefixed.
        bool black = _evStore.GetSetting("settings.keyboard_color") == "black";
        (black ? RbEvKbColorBlack : RbEvKbColorSilver).IsChecked = true;
        ApplyKeyboardColor(black);

        LoadKeycapAppearanceFromStore();
    }

    /// <summary>
    /// Re-loads Game Mode/Indicator LED/Keycap Appearance for the profile that just
    /// became active and re-applies them to the device — mirrors
    /// <see cref="ReloadEverestRgbForProfileSwitch"/>. Called from
    /// <see cref="ReloadEverestProfile"/>. User request 2026-07-25.
    /// </summary>
    private void ReloadEverestSettingsForProfileSwitch()
    {
        if (!_evSettingsInitialized) return;
        bool prev = _evSettingsSuppress;
        _evSettingsSuppress = true;
        try { LoadEverestSettingsFromStore(); }
        finally { _evSettingsSuppress = prev; }
        ApplyEverestSettingsToDevice();
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
        LogEverest($"[SET ] SetGameMode(0x{EvGameModeBitmask():X2}) -> {_everest.SetGameMode(EvGameModeBitmask())}");
        LogEverest($"[SET ] SetGameModeStatus({CkGameModeMaster.IsChecked == true}) -> " +
                    $"{_everest.SetGameModeStatus(CkGameModeMaster.IsChecked == true)}");
        LogEverest($"[SET ] SetIndicatorLed({CkCoreIndicatorLed.IsChecked == true}) -> " +
                    $"{_everest.SetIndicatorLed(CkCoreIndicatorLed.IsChecked == true)}");
        _everest.SetSyncAcrossProfiles(CkSettingsSync.IsChecked == true);
    }

    private void CkGameMode_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        int mode = EvGameModeBitmask();
        _evStore.SetSetting(EvSettingsPrefix() + "game_mode", mode.ToString());
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open: state saved but not applied"); return; }
        LogEverest($"[SET ] SetGameMode(0x{mode:X2}) -> {_everest.SetGameMode(mode)}");
    }

    private void CkGameModeMaster_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        bool on = CkGameModeMaster.IsChecked == true;
        _evStore.SetSetting(EvSettingsPrefix() + "game_mode_master", on ? "1" : "0");
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open: state saved but not applied"); return; }
        LogEverest($"[SET ] SetGameModeStatus({on}) -> {_everest.SetGameModeStatus(on)}");
    }

    /// <summary>Polls the keyboard's Game Mode master state (changes when the user
    /// presses Fn+Pause) and reflects it on <see cref="CkGameModeMaster"/>. Called
    /// from the Everest 1 Hz tick. No-op while the Settings panel is repopulating.</summary>
    private void SyncEverestGameModeStatusFromDevice()
    {
        if (_evSettingsSuppress || !_everest.IsOpen) return;
        if (_everest.GetGameModeStatus() is not bool onDevice) return;
        if ((CkGameModeMaster.IsChecked == true) == onDevice) return;
        bool prev = _evSettingsSuppress;
        _evSettingsSuppress = true;
        try
        {
            CkGameModeMaster.IsChecked = onDevice;
            _evStore.SetSetting(EvSettingsPrefix() + "game_mode_master", onDevice ? "1" : "0");
            LogEverest($"[GET ] Game Mode master changed on device -> {onDevice}");
        }
        finally { _evSettingsSuppress = prev; }
    }

    private void CkCoreIndicatorLed_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        bool enable = CkCoreIndicatorLed.IsChecked == true;
        _evStore.SetSetting(EvSettingsPrefix() + "indicator_led", enable ? "1" : "0");
        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open: state saved but not applied"); return; }
        LogEverest($"[SET ] SetIndicatorLed({enable}) -> {_everest.SetIndicatorLed(enable)}");
    }

    /// <summary>
    /// The SETTINGS section's own "sync across profiles" flag (<c>settings.sync</c>),
    /// independent of the Lighting (<c>CkEvSync</c>) and Display Dial (<c>CkDialSync</c>)
    /// flags since 2026-08-28. Re-saves Game Mode + Indicator LED under the namespace it
    /// just switched to (<see cref="EvSettingsPrefix"/>) and, on the rising edge, replays
    /// them into every profile slot (mirrors <see cref="CkEvSync_Click"/>).
    /// </summary>
    private void CkSettingsSync_Click(object sender, RoutedEventArgs e)
    {
        if (_evSettingsSuppress) return;
        _evStore.SetSetting("settings.sync", CkSettingsSync.IsChecked == true ? "1" : "0");
        // "What's on screen becomes the new state" — re-save under the switched namespace
        // so flipping sync doesn't reveal a stale/default value from the other one.
        CkGameMode_Click(sender, e);
        CkGameModeMaster_Click(sender, e);
        CkCoreIndicatorLed_Click(sender, e);
        if (!_everest.IsOpen)
        {
            LogEverest("[WARN] Everest driver not open: state saved but not applied");
            return;
        }
        _everest.SetSyncAcrossProfiles(CkSettingsSync.IsChecked == true);
        if (CkSettingsSync.IsChecked == true) ReplayEverestSectionToAllProfiles(EvSyncSection.Settings);
    }

    /// <summary>
    /// TRUE hardware factory reset: wipes the keyboard's flash (<see cref="EverestService.ResetFlash"/>,
    /// wire <c>13 40 00 00 01</c>), then deletes K2's own saved configuration for this
    /// keyboard and RELEASES the device.
    ///
    /// Deliberately NOT what Base Camp does. In everest_reset.pcapng (2026-08-21) BC wipes
    /// the flash and immediately re-pushes its active profile — settings blob
    /// <c>11 14 00 01 …</c> carrying the user's own display-key pic slots, then
    /// <c>14 00 00 00 01 01</c> (SwitchProfile 1) — so the device is factory-clean for all
    /// of two seconds. Here "factory" means factory: nothing gets pushed back.
    ///
    /// Order matters:
    /// (1) stop the LED preview poller — it reads the device continuously and the firmware
    ///     is MUTE for ~3.5s while erasing (see EverestHidNative.Pad.ResetFlash);
    /// (2) wipe the store — otherwise every host-side cache describing the device's
    ///     contents survives the reset and lies: <c>ndk.{p}.{i}.fwBind</c> would keep K2
    ///     from rewriting the display-key bindings (bringing back the double action),
    ///     <c>flashOk</c>/imagePath would claim pictures that no longer exist in flash,
    ///     and profiles/RGB/Game Mode would describe firmware slots that are now empty;
    /// (3) restart the lighting. The wipe leaves the keyboard DARK (user report
    ///     2026-08-21: "le luci si interrompono") — the factory flash it falls back to has
    ///     no effect running, and K2 is still the app in charge, so it seeds the fresh
    ///     store with the FIRST effect of the list (Static, K2's default color) and pushes
    ///     it. This is the ONLY thing pushed back: no profiles, no bindings, no display-key
    ///     artwork — the device stays factory-clean everywhere else, unlike Base Camp,
    ///     which re-uploads the user's whole active profile.
    /// </summary>
    private void BtnSettingsFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("settings_factory_reset_confirm"),
            Loc.Get("settings_factory_reset"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        if (!_everest.IsOpen) { LogEverest("[WARN] Everest driver not open"); return; }

        StopLedPreview();

        // ~3.5s blocking round-trip — must not run on the UI thread (RunHwBusy pumps the
        // overlay and runs the work on the pool).
        bool ok = RunHwBusy(Loc.Get("settings_factory_reset_busy"), () => _everest.ResetFlash(true));
        LogEverest($"[SET ] ResetFlash(true) -> {ok}");

        if (!ok)
        {
            // Nothing was erased (or we cannot prove it was): leave K2's store alone and
            // put the preview back the way it was.
            StartLedPreview();
            MessageBox.Show(Loc.Get("settings_factory_reset_failed"),
                Loc.Get("settings_factory_reset"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wiped = _evStore.ResetAllData();
        LogEverest($"[SET ] factory reset: K2's Everest store wiped " +
                   $"(keys={wiped.Keys} settings={wiped.Settings} keycaps={wiped.KeycapOverrides})");

        // Rebuild the panels from the now-empty store, with the lighting seeded to the
        // first effect of the list — ReloadEverestProfile ends in ApplyCurrentEffect,
        // which is what actually turns the keyboard back on.
        EvRefreshProfiles();
        EvSelectProfileSlot(1);
        EvSetRgbToFirstEffect();
        ReloadEverestProfile();

        // Same firmware quirk as a fresh open: the very first effect sent right after the
        // device's config state changes underneath us can be silently dropped, and a
        // re-apply a couple of seconds later always takes (see EvAutoOpen's call site).
        // A dropped apply here would leave the keyboard dark — exactly the symptom this
        // whole branch exists to fix — so pay the same 2s insurance.
        EvScheduleStartupEffectResend();
        StartLedPreview();

        MessageBox.Show(Loc.Get("settings_factory_reset_done"),
            Loc.Get("settings_factory_reset"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Seeds the RGB panel + store with the FIRST effect of <see cref="EvEffectList"/>
    /// (Static) on K2's default color, used by the hardware factory reset to bring the
    /// keyboard's lighting back up after the wipe leaves it dark (user request 2026-08-21:
    /// the first effect of the list is fine). Unlike the sidebar's "Restore defaults"
    /// (<see cref="BtnEvRestoreDefaults_Click"/>), the hardware reset genuinely needs
    /// SOME light pushed back — the wipe leaves the keyboard dark, not just unconfigured.</summary>
    private void EvSetRgbToFirstEffect()
    {
        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            CbEvEffect.SelectedIndex  = 0;   // Static
            SldEvSpeed.Value          = 50;
            SldEvBrightness.Value     = 100;
            _evDirIndex               = 0;
            RbEvColorSingle.IsChecked = true;
            _evColor1 = 0x900000; _evColor2 = 0; _evColor3 = 0;
        }
        finally { _evRgbSuppress = prev; }
        SaveEverestRgbToStore();
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
        if (_evKeycapEditMode && IsEvAppearanceSectionActive)
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

        // Same red as MacroPad's press flash (user request 2026-07-27), via the Tint
        // overlay rather than MacroPad's Background/BorderBrush trigger — see the class
        // doc comment above for why that keeps this immune to MacroPad's "stuck gray" bug.
        SetKeyTint(btn, pressed ? new SolidColorBrush(Color.FromRgb(0x90, 0x00, 0x00)) : Brushes.Transparent);

        // Highlight text with contrasting color too (white reads on the dark red tint).
        SetLegendForeground(btn, pressed ? Brushes.White : new SolidColorBrush(ResolveEverestKeycapTextColor()));
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
    /// <param name="fromHidUsage">True when the value is a HID usage from the native
    /// engine's NKRO bitmap instead of an SDK wMatrix. The standard usage table is then
    /// consulted FIRST and the wMatrix table not at all: the two spaces overlap in the
    /// low integers, so a leftover guided-remap entry (or the default wMatrix table)
    /// would happily translate a usage into an unrelated key. The learned map still acts
    /// as the fallback, which is what covers the layout-dependent punctuation keys
    /// EverestWMatrixMap.HidUsageToMatrixId deliberately leaves out.</param>
    private int EvTranslateMatrix(int wMatrix, bool fromHidUsage = false)
    {
        if (fromHidUsage)
        {
            if (EverestWMatrixMap.HidUsageToMatrixId.TryGetValue(wMatrix, out int vk)) return vk;
            if (_evWMatrixToLayout.TryGetValue(wMatrix, out vk))                        return vk;
            return wMatrix;
        }
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

        // Resend the effect once more ~2s after the initial apply above — same
        // "click the effect again" fix a user doing it manually already relies on
        // (2026-08-18 hardware reports: the very first apply right after Open()/
        // SwitchProfile() can silently be dropped by the firmware — wrong bytes were
        // ruled out, correct bytes were confirmed on the wire via the runtime log,
        // still not applied — while a manual re-apply moments later always works).
        // The 150ms AP-off settle delay in EverestService.SetEffect narrows the
        // window but doesn't fully close it; this brute-force resend is the
        // pragmatic fix in the meantime, mirroring exactly what already reliably
        // recovers it: doing the same apply again a bit later.
        EvScheduleStartupEffectResend();

        // Restore custom device name if previously set
        var savedName = _evStore.GetSetting("device.name");
        if (!string.IsNullOrEmpty(savedName))
            TabEverest.Header = savedName;
    }

    /// <summary>One-shot timer backing <see cref="EvScheduleStartupEffectResend"/> —
    /// field so a second EvAutoOpen (shouldn't normally happen, but mirrors the
    /// defensive pattern of _evAutoOffTimer) doesn't stack multiple pending resends.</summary>
    private DispatcherTimer? _evStartupResendTimer;

    /// <summary>
    /// Fires <see cref="ApplyCurrentEffect"/> once more ~2s after <see cref="EvAutoOpen"/>'s
    /// initial apply — see the call site's comment for why. Also re-applies the
    /// Settings panel (Game Mode/Indicator LED/Sync), same rationale, same firmware
    /// AP-mode-transition window.
    /// </summary>
    private void EvScheduleStartupEffectResend()
    {
        _evStartupResendTimer?.Stop();
        _evStartupResendTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _evStartupResendTimer.Tick += (_, _) =>
        {
            _evStartupResendTimer!.Stop();
            if (!_everest.IsOpen) return;
            LogEverest("[RGB ] startup resend: re-applying effect (see EvAutoOpen comment)");
            ApplyCurrentEffect();
            ApplyEverestSettingsToDevice();
        };
        _evStartupResendTimer.Start();
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

        // Both open paths (EvAutoOpen at startup, BtnEvOpen_Click) end here, and the
        // profile is loaded before either runs — so a "disabled key" in the stored
        // profile only reaches the firmware now. Also re-applies after a reconnect,
        // which the device forgets (nothing here is flash-persisted).
        PushEvDisabledKeysToDevice();
    }

    private void BtnEvApOn_Click(object sender, RoutedEventArgs e)  =>
        LogEverest($"APEnable(true) -> {_everest.APEnable(true)}");
    private void BtnEvApOff_Click(object sender, RoutedEventArgs e) =>
        LogEverest($"APEnable(false) -> {_everest.APEnable(false)}");

    // ============================================================
    // Import XML (Base Camp-compatible or K2-only, same schema)
    // ============================================================

    /// <summary>
    /// With "sync across profiles" ON, every Everest panel reads and writes the SHARED
    /// namespace (<c>rgb.</c>/<c>custom.</c>/<c>settings.</c>/<c>dial.</c>, no slot
    /// segment — see <see cref="EvRgbPrefix"/> and its twins), while both import paths
    /// always write the profile-scoped one. Without this mirror an import done with sync
    /// on changed nothing on screen or on the keyboard: the values landed in
    /// <c>rgb.p{slot}.*</c> and the panel kept reading <c>rgb.*</c> — one of the two
    /// halves of "carico un profilo e non mi mostra l'effetto giusto" (2026-08-22).
    ///
    /// <para>Copies <paramref name="slot"/>'s per-profile rows over the shared ones, which
    /// is what synced actually means: one look shared by all profiles, and the profile just
    /// imported is the one that defines it. No-op when sync is off.</para>
    /// </summary>
    private void EvMirrorImportedProfileToSharedIfSynced(int slot)
    {
        if (CkEvSync.IsChecked != true) return;
        int copied = 0;
        foreach (var family in new[] { "rgb.", "custom.", "settings.", "dial." })
        {
            foreach (var kv in _evStore.GetSettingsWithPrefix($"{family}p{slot}."))
            {
                _evStore.SetSetting(family + kv.Key, kv.Value);
                copied++;
            }
        }
        LogEverest($"[IMP ] sync across profiles is on: mirrored {copied} setting(s) " +
                   $"of slot {slot} into the shared namespace");
    }

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
            // Fresh slot = fresh display-key namespace, including the firmware markers —
            // see EvClaimNdkSlot (and the new-profile branch of LstEvProfile_SelectionChanged).
            EvClaimNdkSlot(slot);

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
                string? customUrl = b.Element("CustomURL")?.Value;
                if (funcType == "K2Action")
                    return (subType, string.IsNullOrEmpty(funcValue) ? null : funcValue);
                return BaseCampDbImporter.TranslateAction(funcType, subType, funcValue, macroNames, customUrl);
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
                // DLLMatrixIndex is the raw SDK wMatrix code, a different numbering space
                // from the VK-code matrixId a physical key press resolves to — same
                // translation BaseCampDbImporter.ImportEverestProfile already applies for
                // the DB import path. Without it an imported key's KeyMatrix never matches
                // what a live press looks up, so the action silently never fires (this was
                // missing here — confirmed real bug, 2026-07-26).
                int keyMatrix = Models.EverestWMatrixMap.Translate(matrixId);
                _evStore.SaveKey(new EverestKeyRecord(slot, keyMatrix, null, actionType, actionValue));
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
                                K2Paths.For("K2.App"), "imported_xml_ev", $"slot{slot}");
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

            // Lighting (EverestLightings/Lighting) — always written under the
            // profile-scoped rgb.p{slot}. namespace, see BaseCampDbImporter.
            // ImportEverestProfile's matching DB-import comment for why.
            var lightingRows = new List<BaseCampDbImporter.BcLightingRow>();
            BaseCampDbImporter.BcCustomLighting? importedCustom = null;
            foreach (var lt in root.Descendants("Lighting"))
            {
                string? effName = lt.Element("EffIndex")?.Value;
                int speed = int.TryParse(lt.Element("Speed")?.Value, out var sp) ? sp : 50;
                int brightness = int.TryParse(lt.Element("Brightness")?.Value, out var br) ? br : 100;
                int direction = int.TryParse(lt.Element("Direction")?.Value, out var di) ? di : 0;
                bool active = string.Equals(lt.Element("IsActive")?.Value, "true", StringComparison.OrdinalIgnoreCase);
                byte effByte = BaseCampDbImporter.ResolveLightingEffectByte(effName, null);
                int c1 = BaseCampDbImporter.ParseBcColor(lt.Element("Color1")?.Value, 0x900000);
                int c2 = BaseCampDbImporter.ParseBcColor(lt.Element("Color2")?.Value, 0);
                int c3 = BaseCampDbImporter.ParseBcColor(lt.Element("Color3")?.Value, 0);
                // <Type> is Base Camp's color-type pill (0 single / 1 dual / 2 rainbow) —
                // see BaseCampDbImporter.ApplyLightingToStore.
                int colorType = int.TryParse(lt.Element("Type")?.Value, out var ct) ? ct : 0;
                lightingRows.Add(new BaseCampDbImporter.BcLightingRow(effByte, speed, brightness, direction, c1, c2, c3, active, colorType));

                // Per-key paint state of the Custom effect (126 keycap LEDs + which keys
                // carry a dynamic effect), from the Custom row's own <CustomLightings>.
                //
                // ONLY when that row is the ACTIVE effect: Base Camp's XML exporter
                // SYNTHESIZES this payload when the profile has no saved paint at all —
                // proven 2026-07-26 by exporting a profile whose DB CustomLightings has
                // every nested entry null, which came out as a full 126 x #FFFFFF board
                // (12 x #000000 on the MacroPad). Importing that unconditionally turned
                // "this profile never used Custom" into "every key painted white". Real
                // paint, when it exists, does live in the DB (verified: a painted profile
                // reads back 61 red / 64 white / 1 green), so the DB import path is the
                // trustworthy source for Custom — see BaseCampDbImporter.
                // ReadKeyboardCustomLighting.
                if (effByte == (byte)EverestSdkNative.EffectIndex.Custom && active)
                {
                    importedCustom = BaseCampDbImporter.ParseKeyboardCustomLighting(
                        lt.Element("CustomLightings")?.Value, BaseCampDbImporter.EverestKeycapLedCount);
                    if (importedCustom is not null)
                    {
                        BaseCampDbImporter.ApplyCustomLightingToStore(_evStore.SetSetting,
                            $"custom.p{slot}.keyLedColors", $"custom.p{slot}.keyEffects", importedCustom);
                        LogEverest($"[IMP-XML] custom lighting: {importedCustom.Colors.Count} painted LED(s), " +
                                   $"{importedCustom.Effects.Count} dynamic-effect LED(s) — note: Base Camp's XML " +
                                   "export may carry a synthesized default board here; import from " +
                                   "BaseCamp.db for the real paint");
                    }
                }
            }
            if (lightingRows.Count > 0)
                BaseCampDbImporter.ApplyLightingToStore(_evStore.SetSetting, $"rgb.p{slot}.", lightingRows);

            // After the effect rows (they set rgb.p{slot}.effect from IsActive): a really
            // painted board wins — see BaseCampDbImporter.LooksPainted.
            if (importedCustom is not null
                && BaseCampDbImporter.LooksPainted(importedCustom, BaseCampDbImporter.EverestKeycapLedCount))
            {
                _evStore.SetSetting($"rgb.p{slot}.effect",
                    ((int)EverestSdkNative.EffectIndex.Custom).ToString());
            }

            // Settings (EverestKeyboardSettings/KeyboardSetting) — Game Mode/Core LED/
            // Dial turn-off+clock, same fields BaseCampDbImporter.ReadKeyboardSettings
            // reads from the DB (see that method's doc comment for the bit layout and
            // for why the fuller Display Dial page config isn't here at all).
            var settingsEl = root.Descendants("KeyboardSetting").FirstOrDefault();
            if (settingsEl is not null)
            {
                bool B(string name) => string.Equals(settingsEl.Element(name)?.Value, "true", StringComparison.OrdinalIgnoreCase);
                int mode = (B("DisableShift") ? 0x1 : 0) | (B("DisableAltF4") ? 0x2 : 0)
                         | (B("DisableWin") ? 0x4 : 0) | (B("DisableAltTab") ? 0x8 : 0);
                string sp2 = $"settings.p{slot}.";
                _evStore.SetSetting(sp2 + "game_mode", mode.ToString());
                _evStore.SetSetting(sp2 + "indicator_led", B("EnableCoreLED") ? "1" : "0");

                string dp2 = $"dial.p{slot}.";
                _evStore.SetSetting(dp2 + "turnOffEnable", B("IsTurnOffAfter") ? "1" : "0");
                _evStore.SetSetting(dp2 + "turnOff",
                    BaseCampDbImporter.TurnOffSecondsFromTimeSpanText(settingsEl.Element("TurnOffAfter")?.Value).ToString());
                _evStore.SetSetting(dp2 + "clockType",
                    (int.TryParse(settingsEl.Element("ClockType")?.Value, out var ct) ? ct : 0).ToString());

                // Keycap legends: only when the exporting side flagged the layout as
                // user-chosen (IsLayoutConfigured), same gate BaseCampDbImporter applies
                // to the DB column — an unconfirmed BC locale guess must not override
                // this machine's own guess. Device-level, so it deliberately escapes the
                // per-slot prefix above.
                if (string.Equals(settingsEl.Element("IsLayoutConfigured")?.Value, "true",
                                  StringComparison.OrdinalIgnoreCase)
                    && EverestKeyboardLayout.ParseStorageString(
                           settingsEl.Element("KeyboardLayout")?.Value) is { } impLayout)
                {
                    _evStore.SetSetting(EverestKeyboardLayout.LayoutSettingKey,
                                        EverestKeyboardLayout.ToStorageString(impLayout));
                    _evLayoutType = impLayout;
                    CbEvKeyboardLayout.SelectedItem = (CbEvKeyboardLayout.ItemsSource as LayoutChoice[])
                        ?.FirstOrDefault(x => x.Layout == impLayout) ?? CbEvKeyboardLayout.SelectedItem;
                    RebuildEverestKeyboardForLayout();
                }
            }

            // K2-format extra: the full per-profile Settings + Display Dial namespace.
            // AFTER the <KeyboardSetting> block above on purpose — the four values Base
            // Camp also has a column for appear in both, and K2's own copy is the one that
            // round-trips exactly. Absent from Base Camp files and from K2 exports made
            // before 2026-08-22, in which case this is a no-op and the BC block above is
            // still the only source.
            int extraSettings = K2ProfileSettingsXml.Apply(
                root, _evStore.SetSetting, slot, K2ProfileSettingsXml.EverestFamilies);
            if (extraSettings > 0)
                LogEverest($"[IMP-XML] {extraSettings} K2 profile setting(s) (Settings + Display Dial) restored");

            // K2-only, device-global: the physical unit's keyboard body colour (see
            // EvProfileExporter's matching write). Absent from Base Camp files, in
            // which case the machine's own existing setting is left untouched.
            // User request 2026-08-22.
            string? kbColor = root.Element("KeyboardColor")?.Value;
            if (kbColor is "black" or "silver")
            {
                _evStore.SetSetting("settings.keyboard_color", kbColor);
                if (_evSettingsInitialized)
                {
                    bool black = kbColor == "black";
                    (black ? RbEvKbColorBlack : RbEvKbColorSilver).IsChecked = true;
                    ApplyKeyboardColor(black);
                }
            }

            EvMirrorImportedProfileToSharedIfSynced(slot);

            _evStore.SetCurrentProfile(slot);
            EvRefreshProfiles();
            EvSelectProfileSlot(slot);
            // ReloadEverestProfile refreshes the NDK canvas thumbnails for the imported
            // slot AND re-runs the three per-profile panel reloads — RGB, Settings and
            // Display Dial — each of which ends by pushing its state to the device
            // (ReloadEverestDialForProfileSwitch -> ApplyDialToDevice). That is what makes
            // an imported profile's dial configuration actually reach the Media Dock
            // without the user opening the panel.
            ReloadEverestProfile();
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

            // Replay the effect-dropdown pick so the imported lighting (Custom board
            // included) reaches the keyboard and the preview without the user having to
            // touch the dropdown — see EvReapplySelectedEffect.
            EvReapplySelectedEffect();

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
        // The Everest import also wipes each targeted firmware slot before loading into
        // it (see the ResetProfileContent call below) — destructive beyond K2's own
        // database, so it has to be in the confirmation, not only in the log.
        if (_everest.IsOpen) sb.AppendLine(Loc.Get("ev_bc_import_will_wipe_firmware"));

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
        //
        // ALL FIVE slots, not just the ones GetExistingProfiles reports (changed
        // 2026-08-22, user request "l'import da Base Camp deve pulire tutti i profili e
        // tutti gli slot nel firmware, poi caricare"): a slot that never held a K2
        // profile can still carry stale rgb./custom./settings./dial./ndk. rows — left by
        // a profile deleted before ClearProfile started deleting whole namespaces
        // (2026-08-21), or by an earlier import — and those silently become the starting
        // point of whatever lands there next.
        for (int wipeSlot = 1; wipeSlot <= EverestService.ProfileCount; wipeSlot++)
            _evStore.ClearProfile(wipeSlot);

        // Per-profile firmware wipe, not a whole-keyboard factory reset (changed
        // 2026-08-22, user request "l'import non deve resettare tutta la tastiera ma
        // solo i profili"): only the K2 slots this import actually targets get their
        // firmware content cleared (13 40 00 00 00, see EverestHidNative.Pad.
        // ResetProfileContent), one SwitchProfile+reset per slot inside the loop below,
        // instead of the old ResetFlash(true) factory wipe (13 40 00 00 01) that also
        // reset every other slot and dropped the device back on profile 1. The LED
        // preview poller still stands down for the duration since each reset is its own
        // ~1.17s of firmware silence; it comes back up at the end of the import.
        bool ledPreviewPaused = false;
        if (_everest.IsOpen)
        {
            StopLedPreview();
            ledPreviewPaused = true;
        }

        int totalRegular = 0, totalTouch = 0, skipped = 0;

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
                if (targetSlot == 0) { skipped++; continue; } // more BC profiles than the 5 firmware slots allow
                usedSlots.Add(targetSlot);
                // Redundant with the all-slots ClearProfile sweep above (which deletes
                // ndk.{slot}.* too) but kept as the explicit statement of intent: this
                // profile's display-key namespace starts empty, so EvResetEmptyNdkSlots
                // below really re-blanks all four keys instead of trusting a stale
                // "flashOk"/"fwBind" marker. See EvClaimNdkSlot.
                EvClaimNdkSlot(targetSlot);

                if (_everest.IsOpen)
                {
                    _everest.SwitchProfile(targetSlot);
                    bool slotWiped = RunHwBusy(Loc.Get("hw_busy_importing_profiles"), () => _everest.ResetProfileContent());
                    LogEverest($"[IMP-BC] slot {targetSlot} firmware wipe: ResetProfileContent() -> {slotWiped}");
                }

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

        // Blank the leftover firmware pictures of EVERY imported slot, not just the one
        // we're about to activate: each profile owns its own 4 pictures in flash, so a
        // profile imported into slot 2/3 with fewer than 4 icons would otherwise keep
        // showing the previous occupant's artwork on the keys the import left empty —
        // the "overlapping icons" of the user report. The binding sync has to follow the
        // reset per slot (the reset restores DEFAULT key mode) and, unlike the reset, is
        // NOT profile-addressed, hence the SwitchProfile before each pass.
        if (_everest.IsOpen)
        {
            foreach (int slot in usedSlots.OrderBy(x => x))
            {
                _everest.SwitchProfile(slot);
                EvResetEmptyNdkSlots(slot, Loc.Get("hw_busy_importing_profiles"));
                EvSyncNdkBindingsToFw(slot);
            }
        }

        // Always land on the FIRST imported profile and force a reload — simpler and
        // safer than trying to restore whatever was active in Base Camp (user request:
        // a plain, predictable refresh after import beats guessing at BC's own state).
        int activateSlot = usedSlots.DefaultIfEmpty(0).Min();
        if (activateSlot > 0) EvMirrorImportedProfileToSharedIfSynced(activateSlot);
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

        // Same post-import apply as the XML path — see EvReapplySelectedEffect.
        EvReapplySelectedEffect();
        if (ledPreviewPaused) StartLedPreview();

        LogEverest($"[IMP-BC] Done: {totalRegular} regular + {totalTouch} display keys across {allProfiles.Count} profiles");
        LblStatus.Text = Loc.Get("ev_imported_bc", allProfiles.Count, totalRegular);

        if (skipped > 0)
        {
            LogEverest($"[IMP-BC] {skipped} profile(s) skipped: no free slot left.");
            MessageBox.Show(this, Loc.Get("import_some_skipped_no_slot", skipped),
                "Import from Base Camp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

        // Busy guard around the whole batch: RunHwBusy pumps the dispatcher, so the 3s
        // accessory poll keeps ticking and would read the firmware mid-write (see
        // UpdateKeyboardLayout).
        NdkSetButtonsEnabled(false);
        _ndkUploadBusy = true;
        System.Collections.Generic.List<int> uploadedKeys;
        try
        {
            uploadedKeys = RunHwBusy(busyMessage ?? Loc.Get("hw_busy_uploading_image"), () =>
            {
                _everest.FlushSaveFlash();   // see EvResetEmptyNdkSlots
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
        }
        finally
        {
            NdkSetButtonsEnabled(true);
            _ndkUploadBusy = false;
            UpdateKeyboardLayout();
            EvReArmColorStreamAfterFlashWrite();
        }
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
    /// <summary>
    /// Takes ownership of firmware profile slot <paramref name="slot"/>'s display-key
    /// namespace on behalf of a profile that is about to be created or imported there:
    /// drops every <c>ndk.{slot}.*</c> row from the store — image paths, actions and, most
    /// importantly, the <c>flashOk</c>/<c>fwBind</c> caches that record what K2 believes
    /// the keyboard's flash already holds for that slot.
    ///
    /// <para>The keyboard keeps each profile slot's 4 pictures (and their action bindings)
    /// in flash forever — nothing about creating a new K2 profile erases them — so without
    /// this the new profile inherits whatever the slot's previous occupant (an older K2
    /// profile, or Base Camp) left on the physical keys. Clearing the markers is what makes
    /// <see cref="EvResetEmptyNdkSlots(int, string?)"/> actually re-blank all 4 keys instead
    /// of skipping them as "already known clean" (user report 2026-08-21: importing/creating
    /// a second or third profile showed the old profile's icons on the display keys).</para>
    ///
    /// <para>Store-only: the hardware reset itself is left to the caller's
    /// <see cref="EvResetEmptyNdkSlots(int, string?)"/> pass, which must run with the device
    /// already landed on the slot.</para>
    /// </summary>
    private void EvClaimNdkSlot(int slot) => _evStore.DeleteSettingsWithPrefix($"ndk.{slot}.");

    private void EvResetEmptyNdkSlots(string? busyMessage = null)
        => EvResetEmptyNdkSlots(EvCurrentProfile(), busyMessage);

    /// <summary>Same as <see cref="EvResetEmptyNdkSlots(string?)"/> but on an explicit
    /// firmware profile slot, which need NOT be the active one: the reset command
    /// (<c>13 42</c>) carries one key-bitmask field PER profile, so it addresses any slot
    /// (see EverestHidNative.ResetDisplayKeyPic). Its <c>14 20</c> framing packets are NOT
    /// profile-addressed though, so callers that care about the firmware really applying
    /// the reset should still land the device on <paramref name="profile"/> first — the
    /// multi-profile Base Camp import does exactly that, one slot at a time.</summary>
    private void EvResetEmptyNdkSlots(int profile, string? busyMessage)
    {
        if (!_everest.IsOpen) return;

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
        // Same busy guard as NdkApplyImage/NdkClearDeviceImage — this IS a flash write, and
        // without the flag the 3s accessory poll (UpdateKeyboardLayout) reads the firmware
        // mid-write and used to collapse the numpad out of the UI. Retry each key like the
        // upload path does: a single attempt is not reliable (see RetryNdkWrite).
        NdkSetButtonsEnabled(false);
        _ndkUploadBusy = true;
        System.Collections.Generic.List<int> done;
        try
        {
            done = RunHwBusy(busyMessage ?? Loc.Get("hw_busy_restoring_key_images"), () =>
            {
                // A profile switch reapplies the lighting, which schedules a debounced
                // SaveFlash ~500ms later — right on top of this sequence, leaving the
                // keyboard mute and every command timing out (user report 2026-08-21).
                _everest.FlushSaveFlash();
                EvSleepUntilNdkFlashCalm();
                var okKeys = new System.Collections.Generic.List<int>(toReset.Count);
                foreach (int i in toReset)
                    if (RetryNdkWrite(() => _everest.ClearNumpadImage(i, (byte)profile), maxAttempts: 3))
                        okKeys.Add(i);
                return okKeys;
            });
        }
        finally
        {
            NdkSetButtonsEnabled(true);
            _ndkUploadBusy = false;
            UpdateKeyboardLayout();
            EvReArmColorStreamAfterFlashWrite();
        }
        foreach (int i in done)
        {
            _evStore.SetSetting($"ndk.{profile}.{i}.flashOk", "1");
            // The reset's 14 20 FF framing also puts the key back in DEFAULT mode, wiping
            // any firmware action binding (same reason NdkClearDeviceImage does this):
            // forget what we last wrote so EvSyncNdkBindingsToFw, which runs right after
            // every caller of this method, re-writes the binding of a key that has an
            // action but no icon.
            _evStore.SetSetting($"ndk.{profile}.{i}.fwBind", "");
        }
        if (done.Count > 0)
            LogEverest($"[NDK] profile {profile}: {done.Count} empty display key(s) restored to default artwork");
        if (done.Count < toReset.Count)
            LogEverest($"[NDK] profile {profile}: {toReset.Count - done.Count} display key(s) FAILED to reset " +
                       "— the keyboard rejected/ignored the sequence (firmware busy?)");
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
    private void EvSyncNdkBindingsToFw() => EvSyncNdkBindingsToFw(EvCurrentProfile());

    /// <summary>Same as <see cref="EvSyncNdkBindingsToFw()"/> for an explicit profile slot.
    /// Unlike the picture upload/reset, the binding write (<c>17 ...</c>) is NOT
    /// profile-addressed — it always lands on the ACTIVE firmware profile — so the caller
    /// MUST have switched the device to <paramref name="profile"/> first.</summary>
    private void EvSyncNdkBindingsToFw(int profile)
    {
        if (!_everest.IsOpen) return;

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

        NdkSetButtonsEnabled(false);
        _ndkUploadBusy = true;
        System.Collections.Generic.List<(int Key, string Marker)> done;
        try
        {
            done = RunHwBusy(Loc.Get("hw_busy_writing_key_bindings"), () =>
            {
                _everest.FlushSaveFlash();    // see EvResetEmptyNdkSlots
                EvSleepUntilNdkFlashCalm();   // small writes are dropped in the busy window too
                var ok = new System.Collections.Generic.List<(int Key, string Marker)>(toWrite.Count);
                foreach (var (k, at, av, marker) in toWrite)
                    if (RetryNdkWrite(() => _everest.WriteNumpadBinding(k, at, av), maxAttempts: 3))
                        ok.Add((k, marker));
                return ok;
            });
        }
        finally
        {
            NdkSetButtonsEnabled(true);
            _ndkUploadBusy = false;
            UpdateKeyboardLayout();
        }
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
            // Take over the slot's display-key namespace from scratch: the keyboard's
            // flash keeps each profile slot's last-written pictures forever, so a brand
            // new K2 profile can land on a firmware slot still full of icons from Base
            // Camp or an earlier K2 profile. Dropping the ndk.{slot}.* rows (in
            // particular the "flashOk"/"fwBind" markers, which may have survived a slot
            // that lost its profile rows without going through ClearProfile) makes the
            // EvResetEmptyNdkSlots call inside EvActivateProfileSlot below actually
            // re-blank all 4 keys instead of trusting a stale marker.
            EvClaimNdkSlot(slot);
            LogEverest($"[UI ] New empty Everest profile created: slot {slot}");
            EvRefreshProfiles();
            EvSelectProfileSlot(slot);
        }

        LogEverest($"[UI ] Everest profile selected: {slot}");
        EvActivateProfileSlot(slot);
        DeviceSyncOnProfileSwitched(SyncDeviceKind.Everest, slot);
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
    /// <param name="applyRgb">False skips re-pushing the RGB effect to the device — used by
    /// <see cref="BtnEvRestoreDefaults_Click"/> (user request 2026-08-22: wiping profiles
    /// shouldn't also force-relight the keyboard), true (default) for every normal
    /// switch/delete.</param>
    private void EvActivateProfileSlot(int slot, bool applyRgb = true)
    {
        _evStore.SetCurrentProfile(slot);
        if (_everest.IsOpen) _everest.SwitchProfile(slot);
        ReloadEverestProfile(applyRgb);
        EvResetEmptyNdkSlots();
        EvSyncNdkBindingsToFw();
        // Closes the gap where a flash write here (NDK reset) or the RGB reapply's own
        // debounced SaveFlash leaves color streaming off and the on-screen LED preview
        // stuck — same symptom/fix as EvReArmColorStreamAfterFlashWrite's other call sites
        // (icon upload). Best-effort: not reproduced/verified against real hardware.
        EvReArmColorStreamAfterFlashWrite();
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
        BtnEvCopy.IsEnabled  = hasSelection;
        BtnEvCut.IsEnabled   = hasSelection;
        BtnEvPaste.IsEnabled = hasSelection;
    }

    /// <summary>Copies the selected row's action to the app-wide clipboard (see
    /// <see cref="K2.Core.Services.ActionClipboard"/>) — works for both a regular key and an
    /// NDK display-key entry (see <see cref="EverestKey.NdkIndex"/>), which this same list
    /// mixes in (<see cref="EvAddNdkEntriesToKeyList"/>).</summary>
    private void BtnEvCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key) return;
        if (key.NdkIndex is int ndkIdx)
            K2.Core.Services.ActionClipboard.Copy(_ndkActions[ndkIdx].Type, _ndkActions[ndkIdx].Value,
                _ndkImagePaths[ndkIdx], _ndkIconSpecs[ndkIdx]);
        else
            K2.Core.Services.ActionClipboard.Copy(key.ActionType, key.ActionValue);
    }

    private void BtnEvCut_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key) return;
        if (key.NdkIndex is int ndkIdx)
        {
            K2.Core.Services.ActionClipboard.Copy(_ndkActions[ndkIdx].Type, _ndkActions[ndkIdx].Value,
                _ndkImagePaths[ndkIdx], _ndkIconSpecs[ndkIdx]);
            ClearNdkKey(ndkIdx);
        }
        else
        {
            K2.Core.Services.ActionClipboard.Copy(key.ActionType, key.ActionValue);
            BtnEvRemove_Click(sender, e);
        }
    }

    /// <summary>Pastes the clipboard's action onto the selected row — rejected (with an error)
    /// for a DisplayPad-page action, which makes no sense on Everest Max (see
    /// <see cref="K2.Core.Services.ActionClipboard.CanPasteOn"/>). An NDK entry gets its
    /// default icon auto-generated when it has no picture yet (see
    /// <see cref="NdkMnuPasteAction_Click"/>'s doc comment for the same behavior via the
    /// display key's own context menu); a regular key never has a picture at all.</summary>
    private void BtnEvPaste_Click(object sender, RoutedEventArgs e)
    {
        if (LvEvKeys.SelectedItem is not EverestKey key) return;
        if (key.NdkIndex is int ndkIdx)
        {
            NdkPasteActionByIndex(ndkIdx);
            return;
        }
        if (!K2.Core.Services.ActionClipboard.HasContent) return;
        if (!K2.Core.Services.ActionClipboard.CanPasteOn(_evActionHost))
        {
            K2.Core.Services.ActionClipboard.ShowPasteUnsupportedError(this);
            return;
        }
        key.ActionType  = K2.Core.Services.ActionClipboard.ActionType;
        key.ActionValue = K2.Core.Services.ActionClipboard.ActionValue;
        EvPersistOrDiscardKey(key);
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
        // Removing a "disabled key" entry has to re-enable it in firmware — this path
        // doesn't go through EvPersistOrDiscardKey, so it needs its own reconcile or the
        // key stays dead until some other change happens to trigger one (user report
        // 2026-07-27: "after removing the disabled action the key doesn't come back").
        PushEvDisabledKeysToDevice();
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

        PushEvDisabledKeysToDevice();
    }

    // ============================================================
    // "Disabled key" — the one binding that has to reach the firmware
    // ============================================================

    /// <summary>matrixIds K2 currently holds in a non-factory firmware output mode
    /// (disabled, or claimed by the host). Tracked because each must be UNDONE when the
    /// key loses its binding or the profile changes, and the device won't tell us what
    /// we set — see <see cref="PushEvDisabledKeysToDevice"/>.</summary>
    private readonly HashSet<int> _evFirmwareDisabledKeys = new();

    /// <summary>
    /// Reconciles each key's firmware state with the current profile: every key that
    /// carries a K2 binding is silenced so its action can run WITHOUT the key also typing
    /// its character (user report 2026-07-27: pressing "2" opened the calculator and typed
    /// "2"), and everything else goes back to its factory function. The action itself
    /// still executes host-side off the key report — the firmware write only decides what
    /// the key emits on its own.
    ///
    /// <para>Every wanted key is (re)written each time rather than only the newly-added
    /// ones. The set says what K2 asked for, NOT what the keyboard currently holds — a
    /// replug wipes the firmware side silently, and diffing against a stale set would
    /// then skip the re-push and leave the key alive. Rewriting is idempotent and only
    /// happens on user-driven events (key saved, profile switched, device opened), never
    /// per keystroke, and none of it touches flash.</para>
    /// </summary>
    private void PushEvDisabledKeysToDevice()
    {
        // Display keys (D1-D4) are excluded: they have their own firmware binding path
        // (WriteNumpadBinding) and aren't in the ordinary-key catalog at all.
        // Both a "disable" binding and an ordinary action end up in the same firmware
        // state: the key emits nothing and K2 runs whatever it's bound to off the NKRO
        // report. Same design as the Everest 60's PushEv60DisabledKeysToDevice.
        var wanted = _evKeys
            .Where(k => k.NdkIndex is null && k.HasAction)
            .Select(k => k.KeyMatrix)
            .ToHashSet();

        foreach (int matrixId in _evFirmwareDisabledKeys.Except(wanted).ToList())
        {
            bool ok = _everest.SetKeyOutputMode(matrixId, EverestHidNative.Pad.KeyOutputMode.Default);
            LogEverest($"[KEY ] key 0x{matrixId:X2} back to factory -> {ok}");
        }

        foreach (int matrixId in wanted)
        {
            bool ok = _everest.SetKeyOutputMode(matrixId, EverestHidNative.Pad.KeyOutputMode.Disabled);
            LogEverest($"[KEY ] key 0x{matrixId:X2} silenced in firmware -> {ok}");
        }

        _evFirmwareDisabledKeys.Clear();
        _evFirmwareDisabledKeys.UnionWith(wanted);
    }

    /// <summary>Puts every key K2 took over back to its factory function, on shutdown.
    /// Without it, closing K2 would leave a disabled key dead and a bound key silent
    /// until the keyboard is replugged.</summary>
    private void RestoreEvDisabledKeysOnExit()
    {
        foreach (int matrixId in _evFirmwareDisabledKeys.ToList())
            try { _everest.SetKeyOutputMode(matrixId, EverestHidNative.Pad.KeyOutputMode.Default); }
            catch { /* shutting down */ }
        _evFirmwareDisabledKeys.Clear();
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
            LogEverest($"[KEY ] {(e.FromNativeKeyReport ? "hidUsage" : "wMatrix")}=0x{rawMatrix:X2} " +
                       $"{(e.Pressed ? "down" : "up")}");

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

        // ---- HW capture / assigned dock/display/dial actions ----
        // Skipped for native key reports: those slots are stored in wMatrix space, which
        // overlaps the HID usage range numerically, so feeding usages here would capture
        // or fire the wrong slot. Dock and display keys don't come through the NKRO
        // bitmap anyway — they have their own vendor bits (Pad.DecodeNumpadButtons).
        if (!e.FromNativeKeyReport)
        {
            if (e.Pressed && TryHwCapture(rawMatrix))
                return;
            if (e.Pressed && TryExecuteHwAction(rawMatrix))
                return;
        }

        int matrix = EvTranslateMatrix(rawMatrix, e.FromNativeKeyReport);

        // Physical-press highlight — re-enabled 2026-07-27 (user request: mirror
        // MacroPad's press effect). Previously disabled 2026-07-17 because the
        // wMatrix→matrixId translation had gaps and the tint fired inconsistently; it
        // uses the Tint overlay (SetKeyTint), never Background/BorderBrush, so — unlike
        // MacroPad's IsHighlighted style trigger — it can't leave a key's keycap color
        // stuck/wrong after release. If a specific key's tint still misfires, that's a
        // s_defaultWMatrixMap/_evWMatrixToLayout gap (see EvTranslateMatrix), not this bug.
        EvHighlightKeyboardButton(matrix, e.Pressed);

        if (_evByMatrix.TryGetValue(matrix, out var key))
        {
            key.IsHighlighted = e.Pressed;
            if (e.Pressed) ExecuteEverestKeyDeduped(key);
            else _evEngine?.Release(_evKeys.IndexOf(key));
        }

        if (!e.Pressed)
            // Catch-up, same fix as Ev60's HandleEv60KeyByLed (MainWindow.Everest60.cs):
            // EvHighlightKeyboardButton's release branch writes a plain
            // ResolveEverestKeycapTextColor() legend unconditionally, ignoring the
            // "Translucent legends" setting and the key's live LED tint. Normally the
            // next LED-poll tick (OnEverestColorsUpdated) repaints the correct color
            // within ~60ms, but that poll only runs while the RGB & Lighting section
            // is active — outside it (or if this key's tick races the release write)
            // the wrong plain color is left stuck forever (user report 2026-08-17:
            // translucent legends turn white and stay white after a press).
            // Scoped to the released key only (2026-08-27): the previous whole-board
            // repaint blanked the live LED preview on every keystroke.
            RestoreEverestKeyAfterRelease(matrix);
    }

    /// <summary>Last (matrixId, moment) actually executed, for
    /// <see cref="ExecuteEverestKeyDeduped"/>.</summary>
    private (int Matrix, DateTime At) _evLastExecuted = (-1, DateTime.MinValue);

    /// <summary>
    /// Runs a key's action at most once per physical press. Once a key is claimed by the
    /// host (<c>KeyOutputMode.HostBound</c>), one press arrives as TWO down edges in the
    /// NKRO bitmap a few milliseconds apart — reported on hardware 2026-07-27 as the
    /// assigned action firing twice, and not observable before the claim was written. The
    /// bit genuinely goes set/clear/set, so the report-level edge detection in
    /// <see cref="EverestHidNative.Pad"/> can't collapse it; the guard has to be here.
    /// The window is short enough to leave deliberate fast repeats alone.
    /// </summary>
    private void ExecuteEverestKeyDeduped(EverestKey key)
    {
        var now = DateTime.UtcNow;
        if (_evLastExecuted.Matrix == key.KeyMatrix
            && (now - _evLastExecuted.At) < TimeSpan.FromMilliseconds(200))
        {
            if (AppSettings.LogLevel == K2LogLevel.Verbose)
                LogEverest($"[KEY ] key 0x{key.KeyMatrix:X2} duplicate press ignored");
            return;
        }
        _evLastExecuted = (key.KeyMatrix, now);
        ExecuteEverestKey(key, momentary: true);
    }

    /// <param name="momentary">Set only from the physical down edge (<see cref="HandleEverestKey"/>
    /// sends the matching up edge to <see cref="ButtonActionEngine.Release"/>); RPC / programmatic
    /// presses via <see cref="EvPressButton"/> stay one-shot.</param>
    private void ExecuteEverestKey(EverestKey k, bool momentary = false) =>
        _evEngine?.Execute(k.ActionType, k.ActionValue, _evKeys.IndexOf(k), momentary);

    /// <param name="applyRgb">Threaded through to <see cref="ReloadEverestRgbForProfileSwitch"/> —
    /// false skips pushing the RGB effect to the device (see <see cref="EvActivateProfileSlot"/>).</param>
    private void ReloadEverestProfile(bool applyRgb = true)
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

        // Firmware-side disabled keys follow the profile (the previous profile's may need
        // undoing) — see PushEvDisabledKeysToDevice.
        PushEvDisabledKeysToDevice();

        ReloadEverestRgbForProfileSwitch(applyRgb);
        ReloadEverestSettingsForProfileSwitch();
        ReloadEverestDialForProfileSwitch();
    }

    /// <summary>
    /// Re-loads the RGB lighting panel for the profile that just became active
    /// (device firmware is already switched to it at this point — see callers of
    /// <see cref="ReloadEverestProfile"/>) and resends the effect, so each profile
    /// keeps its own remembered lighting when "sync across profiles" is off
    /// (no-op in practice when synced: same shared keys, same values). Mirrors
    /// Everest 60/Makalu's ReloadProfile. User request 2026-07-22.
    /// </summary>
    /// <param name="applyToDevice">False loads the panel/state as usual but skips the final
    /// <see cref="ApplyCurrentEffect"/> push — used when reactivating a profile shouldn't
    /// also relight the keyboard (see <see cref="EvActivateProfileSlot"/>). User request
    /// 2026-08-22.</param>
    private void ReloadEverestRgbForProfileSwitch(bool applyToDevice = true)
    {
        if (!_evRgbInitialized) return;
        bool prev = _evRgbSuppress;
        _evRgbSuppress = true;
        try
        {
            LoadEverestRgbFromStore();
            // Custom Lighting is per-profile too since 2026-07-26 (see EvCustomPrefix) —
            // reload the painted board alongside the preset, same as the MacroPad twin.
            // BEFORE UpdateEvCapabilities, never after: that's what flips paint mode on
            // for a Custom profile, and SetCustomPaintModeActive repaints the overlays
            // from these dictionaries (an earlier ReapplyCustomOverlays here was either
            // redundant or wiped by the ClearAllOverlays of a non-Custom profile).
            LoadCustomColorsFromStore();
            UpdateEvCapabilities();
            LblEvBrightness.Text = $"{(int)SldEvBrightness.Value}%";
            ApplyColorButton(BtnEvColor1, _evColor1);
            ApplyColorButton(BtnEvColor2, _evColor2);
            ApplyColorButton(BtnEvColor3, _evColor3);
        }
        finally { _evRgbSuppress = prev; }
        if (applyToDevice) ApplyCurrentEffect();
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
            if (existing.Count == 0)
            {
            // No profile at all — fresh install, hardware factory reset or the Settings
            // tab's "Restore all defaults": recreate one instead of only showing a
            // phantom slot 1 under the generic "Profile 1" label. Named "Default
            // profile" (localized, `default_profile_name`), the same name Base Camp
            // gives its own starting profile. User request 2026-08-21.
                _evStore.SetProfileName(1, Loc.Get("default_profile_name"));
                _evStore.MarkProfileExists(1);
                existing.Add(1);
            }
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
            string kb = $"profile.{slot}";
            string? exe = _evStore.GetSetting($"{kb}.launchExe");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            string key = scope + slot;
            currentKeys.Add(key);
            int capturedSlot = slot;
            bool focusOnly = _evStore.GetSetting($"{kb}.launchFocusOnly") == "1";
            bool restoreOnClose = _evStore.GetSetting($"{kb}.launchRestoreOnClose") == "1";
            ProfileLaunchWatcher.Instance.UpdateRegistration(key, exe, focusOnly, restoreOnClose,
                capturedSlot.ToString(),
                () => _evStore.GetCurrentProfile().ToString(),
                t => EvSwitchProfile(t));
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
        var miConfigure = new MenuItem { Header = Loc.Get("configure_profile") };
        miConfigure.Click += (_, _) => { if (LstEvProfile.SelectedItem is EvProfileItem pi) EvShowProfileGear(pi); };
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
        menu.Items.Add(miConfigure);
        menu.Items.Add(new Separator());
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
        string keyBase = $"profile.{pi.Slot}";
        string currentExe = _evStore.GetSetting($"{keyBase}.launchExe") ?? "";
        bool focusOnly = _evStore.GetSetting($"{keyBase}.launchFocusOnly") == "1";
        bool restoreOnClose = _evStore.GetSetting($"{keyBase}.launchRestoreOnClose") == "1";
        var dlg = new ProfileSettingsDialog(currentName, currentExe, focusOnly, restoreOnClose) { Owner = this };
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
            _evStore.SetSetting($"{keyBase}.launchExe", "");
            LogEverest($"[UI ] Everest profile {pi.Slot} deleted (gear).");
            EvRefreshProfiles();
            int fallback = _evStore.GetExistingProfiles().DefaultIfEmpty(1).First();
            EvSelectProfileSlot(fallback);
            EvActivateProfileSlot(fallback);
            return;
        }

        _evStore.SetProfileName(pi.Slot, dlg.ProfileName);
        _evStore.SetSetting($"{keyBase}.launchExe", dlg.ExePath);
        _evStore.SetSetting($"{keyBase}.launchFocusOnly", dlg.FocusOnly ? "1" : "0");
        _evStore.SetSetting($"{keyBase}.launchRestoreOnClose", dlg.RestoreOnClose ? "1" : "0");
        LogEverest($"[UI ] Everest profile {pi.Slot} settings updated (gear).");
        EvRefreshProfiles();
        EvSelectProfileSlot(pi.Slot);
    }

    /// <summary>Wipes K2's ENTIRE saved configuration for the Everest Max — every profile,
    /// key binding, lighting preset, custom-lighting board, Display Dial page and keycap
    /// override — and starts over from a single fresh "Default profile" on K2's defaults.
    ///
    /// Until 2026-08-21 this kept the current profile alive (name and identity, content
    /// cleared) and only deleted the others, which the confirmation text ("Every saved
    /// profile, key binding and lighting setting will be erased") never promised and users
    /// read as a bug: the old profile stayed in the list under its old name. User request
    /// 2026-08-21: "anche quello deve ripulire tutto". The one profile that remains
    /// afterwards is a NEW empty one — K2 always needs a current profile, so
    /// <see cref="EvRefreshProfiles"/> recreates slot 1 when the list comes back empty.
    ///
    /// Still a K2-side reset: the keyboard's own flash is not wiped (that's the Settings
    /// section's factory reset, <see cref="BtnSettingsFactoryReset_Click"/>). What DOES
    /// reach the device here is what any profile switch pushes — the fresh default
    /// lighting/settings — plus the display-key artwork of EVERY firmware profile slot
    /// going back to factory via <see cref="EvResetAllNdkSlotsToFactory"/>, now that no
    /// key has an icon left in the store.</summary>
    private void BtnEvRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_device_confirm", Loc.Get("tab_everest_max")),
            Loc.Get("restore_defaults"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        var wiped = _evStore.ResetAllData();
        LogEverest($"[UI ] Everest restored to defaults: K2 store wiped " +
                   $"(keys={wiped.Keys} settings={wiped.Settings} keycaps={wiped.KeycapOverrides})");

        // Recreates slot 1 as a brand-new "Default profile" (the list is empty now) and
        // restores factory display-key art on every firmware slot, but does NOT force/push
        // an RGB effect (user request 2026-08-22: "Restore defaults" should only delete
        // profiles and empty the display-key slots — the keyboard keeps whatever lighting
        // it already had until the user picks something).
        EvRefreshProfiles();
        EvSelectProfileSlot(1);
        EvResetAllNdkSlotsToFactory();
        EvActivateProfileSlot(1, applyRgb: false);
    }

    /// <summary>Restores the factory display-key artwork on ALL firmware profile slots,
    /// not just the active one — the hardware half of "restore defaults".
    ///
    /// <para>The keyboard keeps each profile slot's 4 pictures in flash forever and
    /// nothing on the K2 side erases them, so wiping the store alone left slots 2..5
    /// holding the previous profiles' icons. The next profile created or imported lands
    /// in the first FREE slot (2, right after a reset) and the keyboard switches to it
    /// long before the new artwork is written, showing the old occupant's icons for the
    /// whole load — user report 2026-08-22.</para>
    ///
    /// <para>Each slot needs the device landed on it first: the reset's <c>14 20</c>
    /// framing packets are not profile-addressed (see
    /// <see cref="EvResetEmptyNdkSlots(int, string?)"/>), hence the SwitchProfile per
    /// pass — same shape as the multi-profile Base Camp import loop. The caller lands the
    /// device back on the profile it wants active (EvActivateProfileSlot switches again).
    /// Skipped entirely when the numpad isn't attached: no display keys, and every reset
    /// would just burn its retries against a missing accessory.</para></summary>
    private void EvResetAllNdkSlotsToFactory()
    {
        if (!_everest.IsOpen || !_evNumpadConnected) return;
        string busy = Loc.Get("hw_busy_restoring_defaults");
        for (int slot = 1; slot <= EverestService.ProfileCount; slot++)
        {
            _everest.SwitchProfile(slot);
            EvResetEmptyNdkSlots(slot, busy);
        }
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
            // Base Camp can carry the destination profile's NAME instead of Next/Previous/a
            // slot number (confirmed via a real user XML export, see BaseCampDbImporter's
            // TranslateAction "Profile" case) — resolved here by matching it against this
            // device's own profile names, case-insensitively, same tolerance as macro-name
            // matching elsewhere in this codebase.
            int? byName = null;
            for (int s = 1; s <= EverestService.ProfileCount; s++)
                if (string.Equals(_evStore.GetProfileName(s), t, StringComparison.OrdinalIgnoreCase)) { byName = s; break; }
            if (byName is int found) next = found;
            else
            {
                LogEverest($"[EXEC] profile: target \"{t}\" not resolved");
                return;
            }
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
        // Host-driven, not a firmware preset: the frames are computed by K2 and streamed
        // down the Custom-mode channel — see MainWindow.EvSoftwareFx.cs.
        new(EverestService.Effect.DiagonalWave, "Diagonal wave (experimental)"),
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
        // Host-driven animation: speed, brightness AND the full Single/Double/Rainbow color
        // mode — the frames are computed on the PC, so the color mode costs nothing to
        // support (unlike a firmware preset, which only offers what its effect table has).
        // No direction: the wave's diagonal is fixed for now. See MainWindow.EvSoftwareFx.cs.
        EverestService.Effect.DiagonalWave => new(2, true, true, System.Array.Empty<string>(), System.Array.Empty<int>()),
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
        // wakeDelayMs: defer the wake effect-resend off the keypress pump turn so
        // the first key after idle doesn't get repeated (~8×) while the firmware
        // stalls the HID endpoint writing the effect/flash — see BacklightIdleTimer.
        _evAutoOffTimer = new BacklightIdleTimer(Dispatcher, EvAutoOffTimeout, EvAutoOffWake, wakeDelayMs: 250);

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
    /// Key namespace for the Settings section (Game Mode/Indicator LED/Keycap
    /// Appearance) — same shared/profile-scoped split as <see cref="EvRgbPrefix"/>,
    /// but governed by its OWN flag (<c>CkSettingsSync</c>/<c>settings.sync</c>),
    /// independent of the Lighting and Display Dial sync flags (user request
    /// 2026-08-28: "il flag sync across profiles ... deve essere riferito alla
    /// sezione in cui si trova"). <c>settings.keyboard_color</c> is intentionally
    /// excluded (kept always global): it's a cosmetic "what color is my physical
    /// unit" fact, not a per-profile preference. User request 2026-07-25.
    /// </summary>
    private string EvSettingsPrefix() =>
        CkSettingsSync.IsChecked == true ? "settings." : $"settings.p{EvCurrentProfile()}.";

    /// <summary>Key namespace for the Display Dial section — same shared/profile-scoped
    /// split as <see cref="EvSettingsPrefix"/>, governed by its own
    /// <c>CkDialSync</c>/<c>dial.sync</c> flag (K2-side only — Base Camp keeps Display
    /// Dial sync out of its device flag). User request 2026-07-25 / 2026-08-28.</summary>
    private string EvDialPrefix() =>
        CkDialSync.IsChecked == true ? "dial." : $"dial.p{EvCurrentProfile()}.";

    /// <summary>Key namespace for the Custom Lighting paint state — same shared/
    /// profile-scoped split as <see cref="EvRgbPrefix"/>, of which Custom is just
    /// another effect. Made profile-scoped 2026-07-26 (it used to be device-global
    /// under bare <c>custom.*</c>, unlike the MacroPad twin which was per-profile from
    /// the start): Base Camp stores the painted board per profile, so importing a BC
    /// profile had nowhere to put it without clobbering the live board.
    /// <see cref="LoadCustomColorsFromStore"/> falls back to the legacy global keys for
    /// installs that predate the split.</summary>
    private string EvCustomPrefix() =>
        CkEvSync.IsChecked == true ? "custom." : $"custom.p{EvCurrentProfile()}.";

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
        //
        // All THREE are set explicitly (not just the selected one left to WPF's
        // GroupName auto-exclusion): this panel (PnlSecRgb) stays Collapsed until
        // the user first opens "RGB & Lighting", and a RadioButton's GroupName
        // mutual exclusion isn't reliably enforced for a control still outside the
        // live visual tree — only setting the winner true can leave a STALE true
        // on one of the other two (e.g. from InitEverestRgbPanel's default
        // RbEvColorSingle.IsChecked=true, or a previous effect's loaded state).
        // WPF then reconciles the group for real the moment the panel is finally
        // realized (first click into the section), silently flipping the checked
        // radio to whichever stale one — re-firing its Checked handler and
        // re-sending the effect with the WRONG color mode. Root-caused 2026-08-18
        // via a temporary caller-trace log: the spurious re-apply's stack trace was
        // a genuine RbSecRgb mouse click, always the first one after launch — see
        // CHANGELOG.md.
        bool rainbow = (I(p + "rainbow") ?? I(gp + "rainbow") ?? 0) != 0;
        bool colorDouble = !rainbow && (I(p + "colorDouble") ?? I(gp + "colorDouble") ?? 0) != 0;
        RbEvRainbow.IsChecked     = rainbow;
        RbEvColorDouble.IsChecked = colorDouble;
        RbEvColorSingle.IsChecked = !rainbow && !colorDouble;

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
        ApplyCurrentEffect(brightnessOverride: 0, transient: true);
        CkEvBacklight.IsChecked = false;
    }

    private void EvAutoOffWake()
    {
        LogEverest("[RGB ] auto-off wake: resend current effect");
        ApplyCurrentEffect(transient: true);
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
        => EvReapplySelectedEffect();

    /// <summary>
    /// The whole body of the effect-dropdown handler, callable directly: replays
    /// "the user picked this effect" (per-effect params → controls, realign the
    /// capabilities/paint mode, apply to the device). Also called at the end of an
    /// import so the imported profile actually reaches the keyboard and the on-screen
    /// preview — user request 2026-07-26: "devi simulare un clic sulla tendina degli
    /// effetti (di fatto un apply) perche' dopo l'import la tastiera non si aggiorna".
    /// </summary>
    private void EvReapplySelectedEffect()
    {
        if (!_evRgbInitialized) return;
        _evEffectChangedWhileOpen = true;
        // Restore the newly selected effect's own remembered parameters before
        // realigning/applying (per-effect memory — the old values were already
        // saved under the previous effect's namespace on every change). Suppressed:
        // ApplyCurrentEffect below does the single apply+save.
        if (CbEvEffect.SelectedItem is EvEffectChoice pick)
        {
            bool prev = _evRgbSuppress;
            _evRgbSuppress = true;
            try { LoadEffectParamsIntoControls(pick.Eff); }
            finally { _evRgbSuppress = prev; }
        }
        UpdateEvCapabilities();   // realign the controls to the new effect (also turns
                                  // paint mode + the overlays back on for Custom)
        ApplyCurrentEffect();
    }

    /// <summary>Debounce timer for the speed slider — see <see cref="SldEvSpeed_ValueChanged"/>.</summary>
    private DispatcherTimer? _evSpeedApplyTimer;

    /// <summary>
    /// Speed slider. The slider has 1-unit granularity (0..100, no tick snapping) so the
    /// firmware's whole speed range can be explored — useful when trying to line the Wave
    /// up against another device, whose firmware clock runs at a different rate. The wire
    /// byte is passed through raw all the way to ChangeEffect/ChangeBlockEffect, so every
    /// intermediate value is actually sent (whether the firmware resolves them all is a
    /// separate question — that's exactly what the fine slider is for).
    /// Because of that granularity a drag now produces dozens of ValueChanged events, so
    /// the apply is DEBOUNCED: without it each one would fire a ChangeEffect plus a
    /// debounced SaveFlash, flooding the device (and wearing the flash) mid-drag.
    /// </summary>
    private void SldEvSpeed_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblEvSpeed != null) LblEvSpeed.Text = $"{(int)SldEvSpeed.Value}%";

        _evSpeedApplyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _evSpeedApplyTimer.Stop();
        _evSpeedApplyTimer.Tick -= EvSpeedApplyTick;
        _evSpeedApplyTimer.Tick += EvSpeedApplyTick;
        _evSpeedApplyTimer.Start();
    }

    private void EvSpeedApplyTick(object? sender, EventArgs e)
    {
        _evSpeedApplyTimer?.Stop();
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

    /// <summary>
    /// The LIGHTING section's "sync across profiles" flag (<c>rgb.sync</c>) — independent
    /// of the Settings (<c>CkSettingsSync</c>) and Display Dial (<c>CkDialSync</c>) flags
    /// since 2026-08-28. Flipping it re-saves the on-screen effect under the namespace it
    /// just switched to (<see cref="EvRgbPrefix"/>) and, on the rising edge, replays that
    /// effect into every profile slot on the device — mirroring Base Camp
    /// (everest_flags.pcapng: SwitchProfile loop + effect/side-LED writes, since
    /// <c>SetSyncAcrossProfiles</c> alone emits nothing on the wire).
    /// </summary>
    private void CkEvSync_Click(object sender, RoutedEventArgs e)
    {
        bool on = CkEvSync.IsChecked == true;
        SaveEverestRgbToStore();
        if (!_everest.IsOpen)
        {
            LogEverest("[WARN] Everest driver not open: state saved but not applied");
            return;
        }
        _everest.SetSyncAcrossProfiles(on); // best-effort — SDKDLL flag, no wire effect observed
        if (on) ReplayEverestSectionToAllProfiles(EvSyncSection.Lighting);
    }

    private enum EvSyncSection { Lighting, Settings, Dial }

    /// <summary>
    /// Copies the currently-displayed config of <paramref name="section"/> into EVERY
    /// existing profile slot's namespace in the store, then (if the driver is open) walks
    /// each slot on the device re-applying that config — the host-side "sync across
    /// profiles" Base Camp performs (see <see cref="CkEvSync_Click"/>). The store copy
    /// keeps every slot sensible for when sync is later turned back off; the device walk
    /// makes the change visible on all profiles immediately. Runs only on the OFF→ON edge.
    /// </summary>
    private void ReplayEverestSectionToAllProfiles(EvSyncSection section)
    {
        var slots = _evStore.GetExistingProfiles();
        if (slots.Count == 0) return;
        int current = EvCurrentProfile();

        // ── Store copy: shared namespace → each slot's own namespace ──
        string family = section switch
        {
            EvSyncSection.Lighting => "rgb.",
            EvSyncSection.Settings => "settings.",
            _                      => "dial.",
        };
        foreach (var kv in _evStore.GetSettingsWithPrefix(family))
        {
            // Skip rows that already carry a slot segment (p3.foo) and keys that are
            // device-global by design, not per-profile section values: the sync flag
            // itself, the backlight auto-off timer, and the keyboard body colour.
            if (kv.Key.Length > 1 && kv.Key[0] == 'p' && char.IsDigit(kv.Key[1]) && kv.Key.Contains('.')) continue;
            if (kv.Key is "sync" or "autoOffEnable" or "autoOffSeconds" or "keyboard_color") continue;
            foreach (var s in slots)
                _evStore.SetSetting($"{family}p{s}.{kv.Key}", kv.Value);
        }

        // ── Device walk: apply the section on every slot, restore the active one ──
        // The keyboard is unresponsive for the whole SwitchProfile+apply sequence
        // (several seconds with 5 slots), so this runs behind the blocking "please wait"
        // overlay on a background thread. The per-slot apply itself reads WPF controls
        // (CbEvEffect, sliders, …) so it has to hop back to the UI thread — but SwitchProfile
        // and the SetEffect it triggers still block the pool thread, leaving the UI free
        // to paint the overlay. User request 2026-08-28.
        if (!_everest.IsOpen) return;
        bool prevBusy = _deviceSyncBusy;
        _deviceSyncBusy = true; // don't let the per-slot re-apply fan out to other devices
        try
        {
            RunHwBusy(Loc.Get("hw_busy_sync_across_profiles"), () =>
            {
                _everest.FlushSaveFlash();
                foreach (var s in slots)
                {
                    _everest.SwitchProfile(s);
                    Dispatcher.Invoke(() =>
                    {
                        switch (section)
                        {
                            case EvSyncSection.Lighting: ApplyCurrentEffect(); break;
                            case EvSyncSection.Settings: ApplyEverestSettingsToDevice(); break;
                            case EvSyncSection.Dial:     ApplyDialToDevice(); break;
                        }
                    });
                }
                _everest.SwitchProfile(current);
            });
        }
        finally { _deviceSyncBusy = prevBusy; }
        LogEverest($"[SYNC] replayed {section} to slots [{string.Join(",", slots)}], restored {current}");
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
    /// <param name="transient">Auto-off idle timer paths only: the apply is a
    /// brightness bump (idle → 0, wake → restore), NOT a user edit — so it must
    /// not schedule a SaveFlash (no point persisting idle state, and the flash
    /// write is exactly what leaves the firmware unresponsive long enough for the
    /// woke-up keypress to auto-repeat, "AAAAAAAA" — user report 2026-08-30, only
    /// reproduced with Static). Also skips the manual-toggle re-sync below.</param>
    private void ApplyCurrentEffect(int? brightnessOverride = null, bool transient = false)
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

        // Backlight was auto-off (idle) and the user just applied a real effect
        // through a panel control: the device is lit again, so clear the idle
        // timer's forced-off state and re-check the manual toggle to match
        // reality (user report 2026-08-30 — checkbox stayed off). Skipped for the
        // timeout path's own brightness=0 resend (brightnessOverride != null).
        if (!transient && brightnessOverride is null && _evAutoOffTimer?.NotifyWokenExternally() == true)
        {
            CkEvBacklight.IsChecked = true;
            LogEverest("[RGB ] effect applied while idle-off -> backlight considered on again");
        }

        // Any effect change stops a running host-driven animation first — it owns the
        // Custom-mode zone and would keep overwriting whatever we send below.
        StopEvSoftwareFx();
        if (effect == EverestService.Effect.DiagonalWave)
        {
            // Color mode read straight off the same radios every firmware preset uses, so
            // switching Single/Double/Rainbow (or picking a new color) restarts the animation
            // through this very path — every one of those handlers ends in ApplyCurrentEffect.
            StartEvSoftwareFx((int)SldEvSpeed.Value,
                              (byte)Math.Clamp(brightnessOverride ?? (int)SldEvBrightness.Value, 0, 100),
                              EvFxStyle.FromUi(RbEvRainbow.IsChecked == true,
                                               RbEvColorDouble.IsChecked == true,
                                               _evColor1, _evColor2));
            return;
        }

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

        // Speed: raw slider value (scale 0..100, 0=slow, 100=fast, 1-unit steps).
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
            colorCountOverride: colorCount,
            persist:            !transient);
        LogEverest($"[RGB ] ChangeEffect -> {ok}");

        // Cross-device lighting sync — coordinator's re-entrancy guard makes this safe
        // even when the apply was itself sync-driven. Custom / DiagonalWave return early
        // above and don't reach here (per-key paint doesn't translate across devices).
        if (EvBuildLightingSnapshot() is { } snap)
            DeviceSyncOnLightingChanged(SyncDeviceKind.Everest, snap);
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
