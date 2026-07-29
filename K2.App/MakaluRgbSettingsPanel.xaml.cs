using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// RGB + Settings section content for the Makalu tab — see
/// MakaluRgbSettingsPanel.xaml for why this is its own UserControl.
/// </summary>
public partial class MakaluRgbSettingsPanel : UserControl
{
    private MakaluService _makalu = null!;
    private Action<string> _log = _ => { };
    private MakaluService.DeviceInfo _mkInfo =
        new(MakaluService.Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);
    private bool _mkInitialized;
    /// <summary>Defaults to true — see the identical doc comment on
    /// MakaluDpiRemapPanel._mkSuppress. Not currently known to be hit here
    /// (this control's XAML-literal Slider Minimum values happen to match
    /// each Slider's default Value=0, so no coercion/ValueChanged fires
    /// during InitializeComponent()), but defaulting true costs nothing and
    /// avoids relying on that coincidence holding forever.</summary>
    private bool _mkSuppress = true;
    private bool _mkConnected;

    private int _mkColor1 = 0x900000;
    private int _mkColor2 = 0x000000;

    /// <summary>Backs the effect brightness — the Slider itself lives in
    /// MainWindow's shared top-right bar (BrMakalu), not in this panel (see
    /// MainWindow.SectionNav.cs). Updated via <see cref="SetBrightness"/>.</summary>
    internal double Brightness { get; private set; } = 100;

    private bool _mkCustomActive;
    private (byte r, byte g, byte b)[] _mkCustomColors = new (byte, byte, byte)[8];

    /// <summary>The current paint/brush color for Custom Lighting — NOT a per-LED
    /// selection (2026-07-27, user feedback: squares should just be clickable paint
    /// targets, not a select-then-apply flow). Clicking a square or dragging a
    /// rubber-band rectangle over several (MainWindow.Makalu.cs) paints them with
    /// whatever this is currently set to, same "click to paint" model as Everest
    /// Max/60's Custom Lighting.</summary>
    private int _mkCustomPrimaryColor = 0x900000;

    /// <summary>Whether Custom is the active Lighting effect — MainWindow.Makalu.cs reads
    /// this to decide whether to show its per-LED square overlay on the device image.</summary>
    internal bool IsCustomActive => _mkCustomActive;

    /// <summary>Paints one LED with the current brush color (in-memory only — call
    /// <see cref="CommitCustomPaint"/> once after painting one or several LEDs to
    /// persist + send to the device). Called by MainWindow.Makalu.cs's square click
    /// handler and its rubber-band multi-paint.</summary>
    internal void PaintLed(int led)
    {
        _mkCustomColors[led] = (
            (byte)((_mkCustomPrimaryColor >> 16) & 0xFF),
            (byte)((_mkCustomPrimaryColor >> 8) & 0xFF),
            (byte)(_mkCustomPrimaryColor & 0xFF));
    }

    /// <summary>Repaints the ring preview + square overlay, persists, and sends the
    /// current 8 LED colors to the device — call once after one or more
    /// <see cref="PaintLed"/> calls (a single click, or every square touched by one
    /// rubber-band drag), not per-LED, since SetLightingCustom always sends all 8
    /// colors in one packet anyway.</summary>
    internal void CommitCustomPaint()
    {
        PreviewChanged?.Invoke();
        MkPersistLighting();
        MkApplyCustomToDevice();
    }

    /// <summary>Profile persistence — set once from Init. Null-checked everywhere
    /// (rather than made non-nullable) so this panel keeps working standalone if
    /// ever constructed without a store (e.g. a future unit test harness).</summary>
    private MakaluStore? _mkStore;
    private Func<int>? _mkSlot;
    private int CurrentSlot => _mkSlot?.Invoke() ?? 1;

    public MakaluRgbSettingsPanel()
    {
        InitializeComponent();

        // DPI level tiles paint their active-selection Background/Foreground via a
        // one-shot FindResource in MkUpdateDpiButtonLabels (not a live binding), so a
        // Settings > Accent color switch would otherwise leave the currently-active
        // tile the old color until the next DPI edit/profile switch. This control lives
        // for the whole app lifetime (single static instance in MainWindow.xaml), so no
        // unsubscribe is needed — matches AccentCatalog's other subscribers.
        Core.Services.AccentCatalog.Applied += () =>
        {
            if (_mkDpiLevelButtons.Count > 0) MkUpdateDpiButtonLabels();
        };
    }

    private static void ApplyColorButton(Button btn, int rgb)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >> 8) & 0xFF);
        byte b = (byte)(rgb & 0xFF);
        btn.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        btn.ToolTip = $"#{rgb:X6}";
    }

    private sealed record MkEffectChoice(MakaluProtocol.Effect Eff, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>"RGB Breathing" is no longer its own combo entry (2026-07-27, user
    /// request: merge it into Breathing, picked via a Single/Rainbow radio instead —
    /// same "one effect + a color-mode choice" pattern Everest 60/Everest Max/MacroPad
    /// already use). <see cref="MakaluProtocol.Effect.RgbBreathing"/> is still a real
    /// wire value (see <see cref="ResolveMkWireEffect"/>) — only the combo/UI merged.</summary>
    private static readonly MkEffectChoice[] MkEffectList =
    {
        new(MakaluProtocol.Effect.Static,       "Static"),
        new(MakaluProtocol.Effect.Breathing,    "Breathing"),
        new(MakaluProtocol.Effect.Rainbow,      "Rainbow"),
        new(MakaluProtocol.Effect.Responsive,   "Responsive"),
        new(MakaluProtocol.Effect.Yeti,         "Yeti"),
        new(MakaluProtocol.Effect.Off,          "Off"),
        new(MakaluProtocol.Effect.Custom,       "Custom"),
    };

    /// <summary>internal (not private): reused by MainWindow.Makalu.cs to pick
    /// the LED ring preview's animation style from the same flags that drive
    /// this panel's own speed/direction/color2 row visibility — one place
    /// decides "what does this effect need", instead of two switches drifting
    /// apart. Color2 is now false for Breathing (was true) — its old dual-color
    /// crossfade is superseded by the Single/Rainbow radio below (Rainbow needs no
    /// user color at all, and the merge request only asked for Single vs Rainbow,
    /// not a 3-way Single/Double/Rainbow like Everest 60's Breathing).</summary>
    internal sealed record MkCaps(bool Speed, bool Color1, bool Color2, bool Direction);

    internal static MkCaps CapsFor(MakaluProtocol.Effect e) => e switch
    {
        MakaluProtocol.Effect.Static       => new(false, true,  false, false),
        MakaluProtocol.Effect.Breathing    => new(true,  true,  false, false),
        MakaluProtocol.Effect.RgbBreathing => new(true,  false, false, false),
        MakaluProtocol.Effect.Rainbow      => new(true,  false, false, true),
        MakaluProtocol.Effect.Responsive   => new(false, true,  false, false),
        MakaluProtocol.Effect.Yeti         => new(true,  true,  true,  false),
        _                                  => new(false, false, false, false), // Off / Custom
    };

    /// <summary>Whether <paramref name="e"/> is the one combo entry that offers a
    /// Single/Rainbow color-mode radio (<see cref="RbMkColorSingle"/>/
    /// <see cref="RbMkColorRainbow"/>) instead of the plain always-on swatch(es) —
    /// currently just Breathing.</summary>
    private static bool HasColorModeChoice(MakaluProtocol.Effect e) => e == MakaluProtocol.Effect.Breathing;

    /// <summary>Resolves the combo's raw selection to the actual wire effect to send/
    /// persist/preview: Breathing + the Rainbow radio sends/persists
    /// <see cref="MakaluProtocol.Effect.RgbBreathing"/> (the old separate combo entry's
    /// real value), Breathing + Single sends Breathing itself. Every other selection
    /// passes through unchanged.</summary>
    private MakaluProtocol.Effect ResolveMkWireEffect(MakaluProtocol.Effect selected) =>
        selected == MakaluProtocol.Effect.Breathing && RbMkColorRainbow.IsChecked == true
            ? MakaluProtocol.Effect.RgbBreathing
            : selected;

    /// <summary>Snapshot of the current lighting choice, for the software-only
    /// LED ring preview drawn around the wheel/DPI button on the device image
    /// (MainWindow.Makalu.cs) — the Makalu has no HID readback (unlike
    /// Everest 60's GetColorData2), so this mirrors the panel's own state
    /// instead of the real device. <see cref="Effect"/> is already the RESOLVED
    /// wire effect (see <see cref="ResolveMkWireEffect"/>) — Breathing vs Rainbow-
    /// mode Breathing show up as Breathing/RgbBreathing respectively, exactly like
    /// before the two were merged into one combo entry, so MainWindow.Makalu.cs's
    /// ring-preview switch can key off it directly. When <see cref="IsCustom"/> is
    /// set, the ring shows <see cref="CustomColors"/> (the 8 per-LED colors) instead
    /// of Effect/Color1/Color2.</summary>
    internal readonly record struct MkPreviewState(
        MakaluProtocol.Effect Effect, int Color1, int Color2, int SpeedIdx, int DirIdx, double Brightness,
        bool IsCustom, (byte r, byte g, byte b)[] CustomColors);

    /// <summary>Fires whenever anything that affects the ring preview changes
    /// (effect/speed/direction/colors/brightness) — see <see cref="ApplyCurrentMkEffect"/>.</summary>
    internal event Action? PreviewChanged;

    internal MkPreviewState GetPreviewState() => new(
        ResolveMkWireEffect(CbMkEffect.SelectedItem is MkEffectChoice pick ? pick.Eff : MakaluProtocol.Effect.Off),
        _mkColor1, _mkColor2,
        _mkSpeedIndex,
        _mkDirIndex,
        Brightness,
        _mkCustomActive, _mkCustomColors);

    private static readonly int[] DebounceSteps = MakaluProtocol.DebounceValuesMs; // {2,4,6,8,10,12}
    private static readonly int[] PollingSteps = { 125, 250, 500, 1000 };

    private static readonly string[] SpeedLabels = { "Slow", "Medium", "Fast" };

    /// <summary>0-based index backing SldMkSpeed (Slow/Medium/Fast, the raw
    /// param2 byte MakaluProtocol.SetLighting expects) and RbMkDir* (←/→) —
    /// mirrors what CbMkSpeed.SelectedIndex/CbMkDirection.SelectedIndex used
    /// to provide before those became a Slider/RadioButton group.</summary>
    private int _mkSpeedIndex = 1; // Medium
    private int _mkDirIndex = 1;   // →

    internal void Init(MakaluService service, Action<string> log, MakaluStore store, Func<int> currentSlot)
    {
        _makalu = service;
        _log = log;
        _mkStore = store;
        _mkSlot = currentSlot;
        _mkSuppress = true;
        try
        {
            CbMkEffect.ItemsSource = MkEffectList;
            CbMkEffect.DisplayMemberPath = "Label";
            CbMkEffect.SelectedIndex = 0; // Static

            SldMkSpeed.Value = 1; // Medium
            LblMkSpeedVal.Text = "Medium";
            RbMkDirRight.IsChecked = true;
            RbMkColorSingle.IsChecked = true;

            ApplyColorButton(BtnMkColor1, _mkColor1);
            ApplyColorButton(BtnMkColor2, _mkColor2);

            ApplyColorButton(BtnMkCustomPrimary, _mkCustomPrimaryColor);
            UpdateMkCapabilities();

            SldMkPolling.Value = 3; // 1000 Hz
            LblMkPollingVal.Text = "1000 Hz";

            SldMkDebounce.Value = 0;
            LblMkDebounceVal.Text = "2 ms";

            RbMkAngleOff.IsChecked = true;
            RbMkLiftLow.IsChecked = true;

            // Defaults confirmed via decompiled BaseCamp.Data.MakaluSetting's own
            // constructor (Sensitivity=10, ClickSpeed=0).
            SldMkSensitivity.Value = 10;
            LblMkSensitivityVal.Text = "10";
            SldMkClickSpeed.Value = 0;
            LblMkClickSpeedVal.Text = "0";

            BuildMkDpiLevelButtons();
        }
        finally
        {
            _mkSuppress = false;
        }
        _mkInitialized = true;
        PreviewChanged?.Invoke();
    }

    /// <summary>Called by the parent whenever the detected model changes —
    /// DpiMin differs by model, so the DPI levels need rebuilding too.</summary>
    internal void UpdateDeviceInfo(MakaluService.DeviceInfo info)
    {
        _mkInfo = info;
        BuildMkDpiLevelButtons();
        MkDpiRefreshFromDevice();
    }

    internal void SetConnected(bool connected) => _mkConnected = connected;

    /// <summary>Called by MainWindow's shared top-right brightness Slider on
    /// change: updates the stored value and re-applies the current effect,
    /// same "always live" behavior as Everest Max's SldEvBrightness_ValueChanged.</summary>
    internal void SetBrightness(double value)
    {
        Brightness = value;
        ApplyCurrentMkEffect();
    }

    private void UpdateMkCapabilities()
    {
        if (CbMkEffect.SelectedItem is not MkEffectChoice pick) return;
        var caps = CapsFor(pick.Eff);
        bool colorModeChoice = HasColorModeChoice(pick.Eff);
        bool prev = _mkSuppress;
        _mkSuppress = true;
        try
        {
            PnlMkSpeed.Visibility = caps.Speed ? Visibility.Visible : Visibility.Collapsed;
            PnlMkDirection.Visibility = caps.Direction ? Visibility.Visible : Visibility.Collapsed;

            PnlMkColorMode.Visibility = colorModeChoice ? Visibility.Visible : Visibility.Collapsed;
            if (colorModeChoice)
            {
                // First time Breathing is ever selected in this session, default to Single.
                if (RbMkColorSingle.IsChecked != true && RbMkColorRainbow.IsChecked != true)
                    RbMkColorSingle.IsChecked = true;
                bool rainbow = RbMkColorRainbow.IsChecked == true;
                PnlMkColor1.Visibility = rainbow ? Visibility.Collapsed : Visibility.Visible;
                PnlMkColor2.Visibility = Visibility.Collapsed; // no Double option for Breathing (user request 2026-07-27)
            }
            else
            {
                PnlMkColor1.Visibility = caps.Color1 ? Visibility.Visible : Visibility.Collapsed;
                PnlMkColor2.Visibility = caps.Color2 ? Visibility.Visible : Visibility.Collapsed;
            }

            // "Custom" swaps the normal primary/secondary color row for the per-LED
            // square panel — mirrors Everest 60/Everest Max/MacroPad's own
            // PnlNormalControls/PnlCustomLighting swap.
            PnlMkNormalColors.Visibility = _mkCustomActive ? Visibility.Collapsed : Visibility.Visible;
            PnlMkCustomLighting.Visibility = _mkCustomActive ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _mkSuppress = prev;
        }
    }

    private void RbMkColorMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_mkSuppress) return;
        UpdateMkCapabilities();
        ApplyCurrentMkEffect();
    }

    // ------------------------------------------------------------
    // RGB effect panel
    // ------------------------------------------------------------

    private void CbMkEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mkCustomActive = CbMkEffect.SelectedItem is MkEffectChoice pick && pick.Eff == MakaluProtocol.Effect.Custom;
        UpdateMkCapabilities();
        ApplyCurrentMkEffect();
    }

    private void SldMkSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _mkSpeedIndex = Math.Clamp((int)Math.Round(e.NewValue), 0, 2);
        if (LblMkSpeedVal != null) LblMkSpeedVal.Text = SpeedLabels[_mkSpeedIndex];
        ApplyCurrentMkEffect();
    }

    private void RbMkDirection_Checked(object sender, RoutedEventArgs e)
    {
        _mkDirIndex = ReferenceEquals(sender, RbMkDirRight) ? 1 : 0;
        ApplyCurrentMkEffect();
    }

    private void BtnMkColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        int current = tag == "1" ? _mkColor1 : _mkColor2;

        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb((current >> 16) & 0xFF, (current >> 8) & 0xFF, current & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        if (tag == "1") _mkColor1 = rgb; else _mkColor2 = rgb;
        ApplyColorButton(btn, rgb);
        ApplyCurrentMkEffect();
    }

    /// <summary>Serializes the current effect/color/speed/direction choice (plus,
    /// whenever Custom is the selected effect, the 8 custom LED colors) into the
    /// current profile slot. Called unconditionally (even while disconnected)
    /// so a profile edited with the mouse unplugged is still saved.</summary>
    private void MkPersistLighting(double? brightnessOverride = null)
    {
        if (_mkStore is null) return;
        var eff = ResolveMkWireEffect(CbMkEffect.SelectedItem is MkEffectChoice pick ? pick.Eff : MakaluProtocol.Effect.Off);
        var customInts = new int[8];
        for (int i = 0; i < 8; i++)
        {
            var (r, g, b) = _mkCustomColors[i];
            customInts[i] = (r << 16) | (g << 8) | b;
        }
        _mkStore.SaveLighting(CurrentSlot, new MakaluLightingRecord(
            (int)eff, _mkColor1, _mkColor2, _mkSpeedIndex, _mkDirIndex,
            brightnessOverride ?? Brightness, _mkCustomActive, customInts));
    }

    /// <summary>Reads the panel and sends the effect to the firmware. No-op
    /// while still initializing or while the mouse isn't connected.</summary>
    private void ApplyCurrentMkEffect()
    {
        if (!_mkInitialized || _mkSuppress) return;
        if (CbMkEffect.SelectedItem is not MkEffectChoice pick) return;

        if (pick.Eff == MakaluProtocol.Effect.Custom)
        {
            ActivateMkCustomLighting();
            return;
        }

        // Ring preview is software-only (no HID readback on this device), so
        // it updates regardless of connection state — unlike the actual
        // SetLighting call below.
        PreviewChanged?.Invoke();
        MkPersistLighting();

        if (!_mkConnected)
        {
            _log("[RGB ] skip: Makalu not connected");
            return;
        }

        var caps = CapsFor(pick.Eff);
        var wireEffect = ResolveMkWireEffect(pick.Eff);
        int bright = (int)Brightness;
        byte speed = (byte)(caps.Speed ? _mkSpeedIndex : 0);
        byte dir   = (byte)(caps.Direction ? _mkDirIndex : 0);

        static (byte, byte, byte) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        (byte, byte, byte)? secondary = caps.Color2 ? C(_mkColor2) : null;

        LblMkRgbStatus.Text = "...";
        bool ok = _makalu.SetLighting(wireEffect, C(_mkColor1), bright, dir, speed, secondary);
        _log($"[RGB ] apply eff={wireEffect} speed={speed} dir={dir} bright={bright}% c1=#{_mkColor1:X6}" +
             (caps.Color2 ? $" c2=#{_mkColor2:X6}" : "") + $" -> {ok}");
        LblMkRgbStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkRgbStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    // ------------------------------------------------------------
    // Custom (per-LED) lighting — the 8 clickable squares live on the device image
    // next to the ring (MainWindow.Makalu.cs), this panel only owns the color/selection
    // DATA plus the primary-color picker (replaces the old MakaluCustomRgbWindow popup,
    // 2026-07-27 user request).
    // ------------------------------------------------------------

    /// <summary>Brush-color picker — the Makalu's Custom mode only ever writes a flat
    /// Static color per LED (no per-LED dynamic effect: SetLightingCustom's wire format
    /// has no effect-code slot at all, unlike Everest Max/60's Custom Lighting), so
    /// there's no paint-effect dropdown here, just this one swatch. Only changes which
    /// color future square clicks/drags paint with — does NOT repaint anything by
    /// itself (2026-07-27, user feedback: squares are plain click-to-paint targets, not
    /// a select-then-apply flow), mirroring Everest Max/60's BtnCustomBrushColor_Click.</summary>
    private void BtnMkCustomPrimary_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(
                (_mkCustomPrimaryColor >> 16) & 0xFF, (_mkCustomPrimaryColor >> 8) & 0xFF, _mkCustomPrimaryColor & 0xFF),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _mkCustomPrimaryColor = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
        ApplyColorButton(BtnMkCustomPrimary, _mkCustomPrimaryColor);
    }

    /// <summary>Selecting "Custom" (or reloading a profile with it active) immediately
    /// (re)sends the remembered 8 per-LED colors — mirrors Everest 60's own Custom
    /// branch in ApplyCurrentEv60Effect. Every square-color edit already applies live
    /// via <see cref="BtnMkCustomPrimary_Click"/>, so this only covers entry into the
    /// mode itself (combo selection / profile switch / reconnect).</summary>
    private void ActivateMkCustomLighting()
    {
        PreviewChanged?.Invoke();
        MkPersistLighting();
        MkApplyCustomToDevice();
    }

    private void MkApplyCustomToDevice()
    {
        if (!_mkConnected)
        {
            _log("[CUSTOM] skip: Makalu not connected");
            return;
        }
        LblMkRgbStatus.Text = "...";
        bool ok = _makalu.SetLightingCustom(_mkCustomColors, (int)Brightness);
        _log($"[CUSTOM] SetLightingCustom -> {ok}");
        LblMkRgbStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkRgbStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    // ------------------------------------------------------------
    // Device settings: polling rate / debounce / angle snapping / lift-off
    // ------------------------------------------------------------

    private void SldMkPolling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int hz = PollingSteps[Math.Clamp((int)Math.Round(e.NewValue), 0, PollingSteps.Length - 1)];
        if (LblMkPollingVal != null) LblMkPollingVal.Text = $"{hz} Hz";
        if (_mkSuppress) return;
        MkApplyPolling();
    }

    private void MkApplyPolling()
    {
        int hz = PollingSteps[Math.Clamp((int)Math.Round(SldMkPolling.Value), 0, PollingSteps.Length - 1)];
        LblMkPollingStatus.Text = "...";
        bool ok = _makalu.SetPollingRate(hz);
        _log($"[SET ] SetPollingRate({hz}) -> {ok}");
        LblMkPollingStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkPollingStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private void SldMkDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(e.NewValue), 0, DebounceSteps.Length - 1)];
        if (LblMkDebounceVal != null) LblMkDebounceVal.Text = $"{ms} ms";
        if (_mkSuppress) return;
        MkApplyDebounce();
    }

    private void MkApplyDebounce()
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(SldMkDebounce.Value), 0, DebounceSteps.Length - 1)];
        LblMkDebounceStatus.Text = "...";
        bool ok = _makalu.SetDebounce(ms);
        _log($"[SET ] SetDebounce({ms}) -> {ok}");
        LblMkDebounceStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkDebounceStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private void RbMkAngle_Checked(object sender, RoutedEventArgs e)
    {
        if (_mkSuppress) return;
        MkApplyAngle(ReferenceEquals(sender, RbMkAngleOn));
    }

    private void MkApplyAngle(bool on)
    {
        LblMkAngleStatus.Text = "...";
        bool ok = _makalu.SetAngleSnapping(on);
        _log($"[SET ] SetAngleSnapping({on}) -> {ok}");
        LblMkAngleStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkAngleStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private void RbMkLift_Checked(object sender, RoutedEventArgs e)
    {
        if (_mkSuppress) return;
        MkApplyLiftOff(ReferenceEquals(sender, RbMkLiftHigh));
    }

    private void MkApplyLiftOff(bool high)
    {
        LblMkLiftStatus.Text = "...";
        bool ok = _makalu.SetLiftOff(high);
        _log($"[SET ] SetLiftOff(high={high}) -> {ok}");
        LblMkLiftStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkLiftStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private MakaluLodCalibrationWindow? _mkLodWin;
    private bool _mkLiftCustom;
    private byte? _mkSurfaceA, _mkSurfaceB;

    /// <summary>Opens the Custom surface calibration popup (see
    /// MakaluLodCalibrationWindow) instead of writing to the device directly —
    /// unlike Low/High, "Custom" isn't a Set_lod value, it's a whole
    /// start/draw/done flow. Applied always fires when the popup's Done
    /// closes it (see that class's doc for why "not ready" is the expected,
    /// still-successful outcome) — SurfaceA/B are only non-null on the rare
    /// confirmed-ready path.</summary>
    private void RbMkLiftCustom_Checked(object sender, RoutedEventArgs e)
    {
        if (_mkSuppress) return;
        if (_mkLodWin is { IsLoaded: true }) { _mkLodWin.Activate(); return; }
        _mkLodWin = new MakaluLodCalibrationWindow(_makalu, _log) { Owner = Window.GetWindow(this) };
        _mkLodWin.Applied += (a, b) =>
        {
            _mkLiftCustom = true;
            _mkSurfaceA = a;
            _mkSurfaceB = b;
            LblMkLiftStatus.Text = "";
            LblMkLiftStatus.Foreground = (Brush)FindResource("K2AccentBrush");
            MkPersistDeviceSettings();
        };
        _mkLodWin.Show();
    }

    private void SldMkSensitivity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMkSensitivityVal != null) LblMkSensitivityVal.Text = ((int)Math.Round(e.NewValue)).ToString();
        if (_mkSuppress) return;
        MkApplySensitivity();
    }

    private void MkApplySensitivity()
    {
        int sensitivity = Math.Clamp((int)Math.Round(SldMkSensitivity.Value), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
        LblMkSensitivityStatus.Text = "...";
        bool ok = MakaluOsMouseSettings.ApplySensitivity(sensitivity);
        _log($"[SET ] ApplySensitivity({sensitivity}) -> {ok}");
        LblMkSensitivityStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkSensitivityStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private void SldMkClickSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMkClickSpeedVal != null) LblMkClickSpeedVal.Text = ((int)Math.Round(e.NewValue)).ToString();
        if (_mkSuppress) return;
        MkApplyClickSpeed();
    }

    private void MkApplyClickSpeed()
    {
        int clickSpeed = Math.Clamp((int)Math.Round(SldMkClickSpeed.Value), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
        LblMkClickSpeedStatus.Text = "...";
        bool ok = MakaluOsMouseSettings.ApplyClickSpeed(clickSpeed);
        _log($"[SET ] ApplyClickSpeed({clickSpeed}) -> {ok}");
        LblMkClickSpeedStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkClickSpeedStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    /// <summary>Snapshots polling/debounce/angle/lift-off/sensitivity/click-speed (one
    /// combined blob per profile) from the current controls — called after each of the
    /// Apply actions above, so the saved record always reflects whichever setting the
    /// user has last touched.</summary>
    private void MkPersistDeviceSettings()
    {
        if (_mkStore is null) return;
        int pollIdx = Math.Clamp((int)Math.Round(SldMkPolling.Value), 0, PollingSteps.Length - 1);
        int debIdx  = Math.Clamp((int)Math.Round(SldMkDebounce.Value), 0, DebounceSteps.Length - 1);
        int sensitivity = Math.Clamp((int)Math.Round(SldMkSensitivity.Value), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
        int clickSpeed  = Math.Clamp((int)Math.Round(SldMkClickSpeed.Value), MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
        _mkStore.SaveSettings(CurrentSlot, new MakaluDeviceSettingsRecord(
            PollingSteps[pollIdx], DebounceSteps[debIdx],
            RbMkAngleOn.IsChecked == true, RbMkLiftHigh.IsChecked == true,
            RbMkLiftCustom.IsChecked == true && _mkLiftCustom, _mkSurfaceA, _mkSurfaceB,
            sensitivity, clickSpeed));
    }

    // ------------------------------------------------------------
    // DPI levels (right column of Settings — moved here from the old
    // standalone DPI sidebar section, see MainWindow.xaml)
    // ------------------------------------------------------------

    private readonly List<Button> _mkDpiLevelButtons = new();
    private int[] _mkDpiValues = { 400, 800, 1600, 3200, 6400 };
    private int _mkDpiActive;

    /// <summary>How many of the 5 fixed wire-format slots are actually active —
    /// mirrors <c>dpi_level_num</c> (MakaluProtocol.GetDpi/SetAllDpi resp[21]/
    /// buf[6]), the firmware field controlling how many levels the physical
    /// DPI-cycle button on the mouse steps through. Default 5 matches the
    /// behavior this panel always had before levels became addable/removable
    /// (no regression for existing saved profiles, which are always
    /// length-5). <see cref="MakaluDpiRecord.Levels"/>'s array LENGTH doubles
    /// as this count when persisted — no separate stored field needed.</summary>
    private int _mkDpiCount = 5;

    private const int MaxDpiLevels = 5;
    private static readonly int[] DefaultDpiLevels = { 400, 800, 1600, 3200, 6400 };

    /// <summary>Pixel width of one DPI level button + its right margin —
    /// how far BtnMkDpiPrev/Next scroll per click (see SvMkDpiLevels in XAML).</summary>
    private const double DpiLevelScrollStep = 114;

    /// <summary>Builds a DPI level button's two-line Content — "Level N" (muted,
    /// small) over "19000 DPI" (the value in bold, a small fixed "DPI" unit
    /// label beside it) — matching Base Camp's own DPI level entries. Widened
    /// vs. the old "L1\n800" abbreviation so both lines fit comfortably.</summary>
    private static object BuildMkDpiButtonContent(int levelNum, int dpi)
    {
        var panel = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Level {levelNum}",
            FontSize = 10,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 4),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = dpi.ToString(), FontSize = 15, FontWeight = FontWeights.Bold });
        row.Children.Add(new TextBlock
        {
            Text = "DPI", FontSize = 9, Opacity = 0.75,
            Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        });
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>Builds the trailing "+" tab that appends a new DPI level (up to
    /// MaxDpiLevels) — hidden once the wire-format's 5 slots are all in use.</summary>
    private Button BuildMkDpiAddButton()
    {
        var btn = new Button
        {
            Width = 39, Height = 64, Margin = new Thickness(0, 0, 4, 0),
            FontSize = 20, FontWeight = FontWeights.Bold,
            Content = "+",
            ToolTip = Loc.Get("makalu_dpi_add"),
        };
        btn.Click += BtnMkDpiAddLevel_Click;
        return btn;
    }

    /// <summary>Right-click "Remove level" on a DPI level tab — disabled when
    /// only one level is left (the firmware needs at least one active slot).</summary>
    private ContextMenu BuildMkDpiLevelContextMenu(int idx)
    {
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = Loc.Get("makalu_dpi_remove_level"), IsEnabled = _mkDpiCount > 1 };
        mi.Click += (_, _) => MkDpiRemoveLevel(idx);
        menu.Items.Add(mi);
        return menu;
    }

    /// <summary>Fixed-width tabs in a horizontally scrollable strip (not a
    /// UniformGrid — that used to shrink every button to fit exactly 5 in the
    /// available width; per user request, level tabs now keep a comfortable
    /// fixed size and the strip scrolls instead when they don't all fit — see
    /// SvMkDpiLevels_ScrollChanged for the prev/next arrows' visibility).</summary>
    private void BuildMkDpiLevelButtons()
    {
        PnlMkDpiLevels.Children.Clear();
        _mkDpiLevelButtons.Clear();
        for (int i = 0; i < _mkDpiCount; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Width = 110, Height = 64, Margin = new Thickness(0, 0, 4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = BuildMkDpiButtonContent(i + 1, _mkDpiValues[i]),
                ContextMenu = BuildMkDpiLevelContextMenu(idx),
            };
            btn.Click += (_, _) => MkDpiSelectLevel(idx);
            PnlMkDpiLevels.Children.Add(btn);
            _mkDpiLevelButtons.Add(btn);
        }
        if (_mkDpiCount < MaxDpiLevels)
            PnlMkDpiLevels.Children.Add(BuildMkDpiAddButton());

        SldMkDpi.Minimum = _mkInfo.DpiMin;
        if (_mkDpiActive >= _mkDpiCount) _mkDpiActive = _mkDpiCount - 1;
        MkUpdateDpiButtonLabels();
        // Suppressed: rebuilding the tab strip (Init/UpdateDeviceInfo/profile reload/Add/
        // Remove) must never itself trigger a device write — only a genuine user action
        // (tab click, slider drag, textbox commit) should, see SldMkDpi_ValueChanged.
        bool wasSuppress = _mkSuppress;
        _mkSuppress = true;
        try { SldMkDpi.Value = _mkDpiValues[_mkDpiActive]; } finally { _mkSuppress = wasSuppress; }
        TxtMkDpi.Text = _mkDpiValues[_mkDpiActive].ToString();
    }

    private void MkUpdateDpiButtonLabels()
    {
        for (int i = 0; i < _mkDpiLevelButtons.Count; i++)
        {
            _mkDpiLevelButtons[i].Content = BuildMkDpiButtonContent(i + 1, _mkDpiValues[i]);
            bool active = i == _mkDpiActive;
            _mkDpiLevelButtons[i].Background = active
                ? (Brush)FindResource("K2AccentBrush")
                : (Brush)FindResource("K2HoverBrush");
            _mkDpiLevelButtons[i].Foreground = active
                ? (Brush)FindResource("K2AccentTextBrush")
                : (Brush)FindResource("K2TextBrush");
        }
    }

    private void MkDpiSelectLevel(int idx)
    {
        _mkDpiActive = idx;
        bool wasSuppress = _mkSuppress;
        _mkSuppress = true;
        try { SldMkDpi.Value = _mkDpiValues[idx]; } finally { _mkSuppress = wasSuppress; }
        TxtMkDpi.Text = _mkDpiValues[idx].ToString();
        MkUpdateDpiButtonLabels();
        _mkDpiLevelButtons[idx].BringIntoView();
        // Selecting a level is itself a modification (activates it on the device) —
        // apply immediately, same "no Apply button" rule as the sliders above.
        MkApplyDpi();
    }

    /// <summary>Appends a new DPI level (local UI state only — like every other
    /// DPI edit here, it's only sent to the device/persisted on "Apply", see
    /// MkApplyDpi). Default value comes from the same 5-value progression the
    /// panel always defaulted to, so growing back to 5 reproduces the old
    /// fixed behavior exactly.</summary>
    private void BtnMkDpiAddLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_mkDpiCount >= MaxDpiLevels) return;
        _mkDpiValues[_mkDpiCount] = DefaultDpiLevels[_mkDpiCount];
        _mkDpiCount++;
        BuildMkDpiLevelButtons();
        MkDpiSelectLevel(_mkDpiCount - 1); // also applies to device, see MkDpiSelectLevel
        _log($"[DPI ] level {_mkDpiCount} added");
    }

    /// <summary>Removes a DPI level (shifts every later level down one slot so level
    /// numbering stays contiguous) and applies the new table immediately.</summary>
    private void MkDpiRemoveLevel(int idx)
    {
        if (_mkDpiCount <= 1 || idx < 0 || idx >= _mkDpiCount) return;
        for (int i = idx; i < _mkDpiCount - 1; i++) _mkDpiValues[i] = _mkDpiValues[i + 1];
        _mkDpiValues[_mkDpiCount - 1] = DefaultDpiLevels[_mkDpiCount - 1];
        _mkDpiCount--;
        BuildMkDpiLevelButtons();
        MkApplyDpi();
        _log($"[DPI ] level {idx + 1} removed");
    }

    /// <summary>Scrolls the level strip by one tab — only visible/relevant when
    /// SvMkDpiLevels_ScrollChanged has determined the tabs don't all fit.</summary>
    private void BtnMkDpiPrev_Click(object sender, RoutedEventArgs e) =>
        SvMkDpiLevels.ScrollToHorizontalOffset(Math.Max(0, SvMkDpiLevels.HorizontalOffset - DpiLevelScrollStep));

    private void BtnMkDpiNext_Click(object sender, RoutedEventArgs e) =>
        SvMkDpiLevels.ScrollToHorizontalOffset(SvMkDpiLevels.HorizontalOffset + DpiLevelScrollStep);

    /// <summary>Shows the prev/next arrows only when the level strip's content is
    /// actually wider than what's visible — fires on resize AND whenever a level
    /// is added/removed (both change ExtentWidth), not just on manual scrolling.</summary>
    private void SvMkDpiLevels_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool overflow = SvMkDpiLevels.ExtentWidth > SvMkDpiLevels.ViewportWidth + 0.5;
        var vis = overflow ? Visibility.Visible : Visibility.Collapsed;
        BtnMkDpiPrev.Visibility = vis;
        BtnMkDpiNext.Visibility = vis;
    }

    private void SldMkDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mkSuppress) return;
        int dpi = MakaluProtocol.QuantizeDpiTiered((int)e.NewValue);
        _mkDpiValues[_mkDpiActive] = dpi;
        TxtMkDpi.Text = dpi.ToString();
        MkUpdateDpiButtonLabels();
        MkApplyDpi();
    }

    private void TxtMkDpi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MkApplyDpi();
    }

    private void TxtMkDpi_LostFocus(object sender, RoutedEventArgs e) => MkApplyDpi();

    private void MkCommitDpiEntry()
    {
        if (!int.TryParse(TxtMkDpi.Text, out int dpi)) dpi = _mkDpiValues[_mkDpiActive];
        dpi = Math.Clamp(MakaluProtocol.QuantizeDpiTiered(dpi), _mkInfo.DpiMin, MakaluProtocol.DpiMax);
        _mkDpiValues[_mkDpiActive] = dpi;
        TxtMkDpi.Text = dpi.ToString();
        _mkSuppress = true;
        try { SldMkDpi.Value = dpi; } finally { _mkSuppress = false; }
        MkUpdateDpiButtonLabels();
    }

    private void MkApplyDpi()
    {
        MkCommitDpiEntry();
        LblMkDpiStatus.Text = "...";
        bool ok = _makalu.SetAllDpi(_mkDpiValues, _mkDpiActive + 1, _mkInfo.DpiMin, _mkDpiCount);
        _log($"[DPI ] SetAllDpi([{string.Join(",", _mkDpiValues)}], active={_mkDpiActive + 1}, count={_mkDpiCount}) -> {ok}");
        LblMkDpiStatus.Text = ok ? "" : Loc.Get("makalu_failed");
        LblMkDpiStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        _mkStore?.SaveDpi(CurrentSlot, new MakaluDpiRecord(_mkDpiValues[.._mkDpiCount], _mkDpiActive));
    }

    private void MkDpiRefreshFromDevice()
    {
        var result = _makalu.GetDpi(_mkInfo.DpiMin);
        if (result is null) { _log("[DPI ] GetDpi -> not connected/failed"); return; }
        _mkDpiValues = result.Value.Levels;
        _mkDpiCount  = Math.Clamp(result.Value.Count, 1, MaxDpiLevels);
        _mkDpiActive = Math.Clamp(result.Value.Active, 0, _mkDpiCount - 1);
        BuildMkDpiLevelButtons();
        _mkSuppress = true;
        try { SldMkDpi.Value = _mkDpiValues[_mkDpiActive]; } finally { _mkSuppress = false; }
        TxtMkDpi.Text = _mkDpiValues[_mkDpiActive].ToString();
        _log($"[DPI ] GetDpi -> levels=[{string.Join(",", _mkDpiValues)}] active={_mkDpiActive} count={_mkDpiCount}");
    }

    // ------------------------------------------------------------
    // Profile switch: push a stored slot's lighting/DPI/settings into this
    // panel's controls, then re-apply everything to hardware (if connected).
    // Called by MainWindow.Makalu.cs on combo switch, module init, and the
    // disconnected->connected poll transition.
    // ------------------------------------------------------------

    /// <summary>Resets this profile's lighting/DPI/device-settings to K2's factory
    /// defaults — the same values Init() sets up for a brand-new profile — and
    /// re-applies them to the mouse if connected. Seeds the store with explicit default
    /// records rather than clearing it, since <see cref="MkReloadProfile"/> only
    /// overwrites a control when its record is non-null. Called by
    /// MainWindow.Makalu.cs's "Restore defaults" button (button remap is reset
    /// separately, by MakaluStore.ResetKeyRemap + MakaluDpiRemapPanel.MkReloadRemap).</summary>
    internal void RestoreDefaults()
    {
        if (_mkStore is not null)
        {
            _mkStore.SaveLighting(CurrentSlot, new MakaluLightingRecord(
                (int)MakaluProtocol.Effect.Static, 0x900000, 0x000000, 1, 1,
                100, false, new int[8]));
            _mkStore.SaveDpi(CurrentSlot, new MakaluDpiRecord(new[] { 400, 800, 1600, 3200, 6400 }, 0));
            _mkStore.SaveSettings(CurrentSlot, new MakaluDeviceSettingsRecord(1000, 2, false, false));
        }
        MkReloadProfile(CurrentSlot);
    }

    internal void MkReloadProfile(int slot)
    {
        if (_mkStore is null) return;
        var lighting = _mkStore.LoadLighting(slot);
        var dpi      = _mkStore.LoadDpi(slot);
        var settings = _mkStore.LoadSettings(slot);

        bool wasSuppress = _mkSuppress;
        _mkSuppress = true;
        try
        {
            if (lighting is not null)
            {
                var eff = (MakaluProtocol.Effect)lighting.Effect;
                // RgbBreathing is no longer a combo entry of its own (2026-07-27 merge) —
                // a profile saved with it persists that literal wire value (ResolveMkWireEffect
                // in MkPersistLighting), so map it back onto Breathing + the Rainbow radio.
                bool rainbowBreathing = eff == MakaluProtocol.Effect.RgbBreathing;
                var comboEff = rainbowBreathing ? MakaluProtocol.Effect.Breathing : eff;
                int idx = Array.FindIndex(MkEffectList, x => x.Eff == comboEff);
                CbMkEffect.SelectedIndex = idx >= 0 ? idx : 0;
                if (comboEff == MakaluProtocol.Effect.Breathing)
                {
                    if (rainbowBreathing) RbMkColorRainbow.IsChecked = true; else RbMkColorSingle.IsChecked = true;
                }
                _mkColor1 = lighting.Color1;
                _mkColor2 = lighting.Color2;
                _mkSpeedIndex = Math.Clamp(lighting.SpeedIndex, 0, 2);
                _mkDirIndex = Math.Clamp(lighting.DirIndex, 0, 1);
                SldMkSpeed.Value = _mkSpeedIndex;
                LblMkSpeedVal.Text = SpeedLabels[_mkSpeedIndex];
                if (_mkDirIndex == 1) RbMkDirRight.IsChecked = true; else RbMkDirLeft.IsChecked = true;
                ApplyColorButton(BtnMkColor1, _mkColor1);
                ApplyColorButton(BtnMkColor2, _mkColor2);
                Brightness = lighting.Brightness;
                _mkCustomActive = lighting.CustomActive;
                for (int i = 0; i < 8 && i < lighting.CustomColors.Length; i++)
                {
                    int c = lighting.CustomColors[i];
                    _mkCustomColors[i] = ((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
                }
                // Self-heal: older/imported records can have CustomActive=true while
                // Effect stayed at whatever preset was active before Custom was chosen
                // (Custom only became a CbMkEffect entry itself 2026-07-27) — force the
                // combo onto Custom so its selection always agrees with _mkCustomActive.
                if (_mkCustomActive)
                {
                    int customIdx = Array.FindIndex(MkEffectList, x => x.Eff == MakaluProtocol.Effect.Custom);
                    if (customIdx >= 0) CbMkEffect.SelectedIndex = customIdx;
                }
                UpdateMkCapabilities();
            }

            if (dpi is not null && dpi.Levels.Length is >= 1 and <= MaxDpiLevels)
            {
                // Levels.Length IS the active count (see _mkDpiCount's doc comment) —
                // pad the remaining wire-format slots with the usual defaults so a
                // later "+" click has something sane to start from.
                _mkDpiCount = dpi.Levels.Length;
                Array.Copy(dpi.Levels, _mkDpiValues, dpi.Levels.Length);
                for (int i = dpi.Levels.Length; i < MaxDpiLevels; i++) _mkDpiValues[i] = DefaultDpiLevels[i];
                _mkDpiActive = Math.Clamp(dpi.Active, 0, _mkDpiCount - 1);
            }
            BuildMkDpiLevelButtons();

            if (settings is not null)
            {
                int pollIdx = Array.IndexOf(PollingSteps, settings.PollingHz);
                SldMkPolling.Value = pollIdx >= 0 ? pollIdx : 3;
                LblMkPollingVal.Text = $"{(pollIdx >= 0 ? settings.PollingHz : PollingSteps[3])} Hz";

                int debIdx = Array.IndexOf(DebounceSteps, settings.DebounceMs);
                SldMkDebounce.Value = debIdx >= 0 ? debIdx : 0;
                LblMkDebounceVal.Text = $"{(debIdx >= 0 ? settings.DebounceMs : DebounceSteps[0])} ms";

                if (settings.AngleSnapping) RbMkAngleOn.IsChecked = true; else RbMkAngleOff.IsChecked = true;
                _mkLiftCustom = settings.LiftOffCustom;
                _mkSurfaceA = settings.SurfaceA;
                _mkSurfaceB = settings.SurfaceB;
                if (settings.LiftOffCustom) RbMkLiftCustom.IsChecked = true;
                else if (settings.LiftOffHigh) RbMkLiftHigh.IsChecked = true;
                else RbMkLiftLow.IsChecked = true;

                int sensitivity = Math.Clamp(settings.Sensitivity, MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
                SldMkSensitivity.Value = sensitivity;
                LblMkSensitivityVal.Text = sensitivity.ToString();
                int clickSpeed = Math.Clamp(settings.ClickSpeed, MakaluOsMouseSettings.ScaleMin, MakaluOsMouseSettings.ScaleMax);
                SldMkClickSpeed.Value = clickSpeed;
                LblMkClickSpeedVal.Text = clickSpeed.ToString();
            }
        }
        finally { _mkSuppress = wasSuppress; }

        PreviewChanged?.Invoke();

        if (!_mkConnected)
        {
            _log("[PROFILE] reload: device not connected, UI updated only");
            return;
        }

        // CbMkEffect's selection now always agrees with _mkCustomActive (see the
        // self-heal above), so this alone covers both the Custom and preset paths —
        // ApplyCurrentMkEffect's own Custom branch calls MkApplyCustomToDevice.
        ApplyCurrentMkEffect();
        if (settings is not null)
        {
            MkApplyPolling();
            MkApplyDebounce();
            MkApplyAngle(settings.AngleSnapping);
            if (settings.LiftOffCustom)
            {
                // No confirmed way to "restore" a specific learned calibration
                // (LodSetSurface's ready path has never actually fired in
                // practice, see MakaluLodCalibrationWindow's doc) — the closest
                // real, confirmed equivalent is just re-arming Custom mode via
                // the same Lod_calibration_start the popup's own Start uses.
                if (settings.SurfaceA is { } a && settings.SurfaceB is { } b)
                    _makalu.LodSetSurface(a, b);
                else
                    _makalu.LodCalibrationStart();
            }
            else
            {
                MkApplyLiftOff(settings.LiftOffHigh);
            }
        }
        if (dpi is not null) MkApplyDpi();
    }
}
