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

    private MakaluCustomRgbWindow? _mkCustomWin;
    private bool _mkCustomActive;
    private (byte r, byte g, byte b)[] _mkCustomColors = new (byte, byte, byte)[8];

    /// <summary>Profile persistence — set once from Init. Null-checked everywhere
    /// (rather than made non-nullable) so this panel keeps working standalone if
    /// ever constructed without a store (e.g. a future unit test harness).</summary>
    private MakaluStore? _mkStore;
    private Func<int>? _mkSlot;
    private int CurrentSlot => _mkSlot?.Invoke() ?? 1;

    public MakaluRgbSettingsPanel()
    {
        InitializeComponent();
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

    private static readonly MkEffectChoice[] MkEffectList =
    {
        new(MakaluProtocol.Effect.Static,       "Static"),
        new(MakaluProtocol.Effect.Breathing,    "Breathing"),
        new(MakaluProtocol.Effect.RgbBreathing, "RGB Breathing"),
        new(MakaluProtocol.Effect.Rainbow,      "Rainbow"),
        new(MakaluProtocol.Effect.Responsive,   "Responsive"),
        new(MakaluProtocol.Effect.Yeti,         "Yeti"),
        new(MakaluProtocol.Effect.Off,          "Off"),
    };

    /// <summary>internal (not private): reused by MainWindow.Makalu.cs to pick
    /// the LED ring preview's animation style from the same flags that drive
    /// this panel's own speed/direction/color2 row visibility — one place
    /// decides "what does this effect need", instead of two switches drifting
    /// apart.</summary>
    internal sealed record MkCaps(bool Speed, bool Color1, bool Color2, bool Direction);

    internal static MkCaps CapsFor(MakaluProtocol.Effect e) => e switch
    {
        MakaluProtocol.Effect.Static       => new(false, true,  false, false),
        MakaluProtocol.Effect.Breathing    => new(true,  true,  true,  false),
        MakaluProtocol.Effect.RgbBreathing => new(true,  false, false, false),
        MakaluProtocol.Effect.Rainbow      => new(true,  false, false, true),
        MakaluProtocol.Effect.Responsive   => new(false, true,  false, false),
        MakaluProtocol.Effect.Yeti         => new(true,  true,  true,  false),
        _                                  => new(false, false, false, false), // Off
    };

    /// <summary>Snapshot of the current lighting choice, for the software-only
    /// LED ring preview drawn around the wheel/DPI button on the device image
    /// (MainWindow.Makalu.cs) — the Makalu has no HID readback (unlike
    /// Everest 60's GetColorData2), so this mirrors the panel's own state
    /// instead of the real device. When <see cref="IsCustom"/> is set, the
    /// ring shows <see cref="CustomColors"/> (the 8 per-LED colors from
    /// MakaluCustomRgbWindow) instead of Effect/Color1/Color2.</summary>
    internal readonly record struct MkPreviewState(
        MakaluProtocol.Effect Effect, int Color1, int Color2, int SpeedIdx, int DirIdx, double Brightness,
        bool IsCustom, (byte r, byte g, byte b)[] CustomColors);

    /// <summary>Fires whenever anything that affects the ring preview changes
    /// (effect/speed/direction/colors/brightness) — see <see cref="ApplyCurrentMkEffect"/>.</summary>
    internal event Action? PreviewChanged;

    internal MkPreviewState GetPreviewState() => new(
        CbMkEffect.SelectedItem is MkEffectChoice pick ? pick.Eff : MakaluProtocol.Effect.Off,
        _mkColor1, _mkColor2,
        _mkSpeedIndex,
        _mkDirIndex,
        Brightness,
        _mkCustomActive, _mkCustomColors);

    private static readonly (byte r, byte g, byte b)[] MkPresetColors =
    {
        (255,   0,   0), (204,   0,  67), (235,  64,  52), (220,  41, 188),
        (179,  53, 127), ( 71,   0, 204), (  0,  60, 204), (  0, 118, 204),
        (  0, 204, 181), ( 41, 255, 204), ( 91, 222,  98), (152, 235,  53),
    };

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

            BuildMkPresets();
            UpdateMkCapabilities();
            ApplyColorButton(BtnMkColor1, _mkColor1);
            ApplyColorButton(BtnMkColor2, _mkColor2);

            SldMkPolling.Value = 3; // 1000 Hz
            LblMkPollingVal.Text = "1000 Hz";

            SldMkDebounce.Value = 0;
            LblMkDebounceVal.Text = "2 ms";

            RbMkAngleOff.IsChecked = true;
            RbMkLiftLow.IsChecked = true;

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
        bool prev = _mkSuppress;
        _mkSuppress = true;
        try
        {
            PnlMkSpeed.Visibility = caps.Speed ? Visibility.Visible : Visibility.Collapsed;
            PnlMkDirection.Visibility = caps.Direction ? Visibility.Visible : Visibility.Collapsed;
            BtnMkColor1.IsEnabled = caps.Color1;
            PnlMkColor2.Visibility = caps.Color2 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _mkSuppress = prev;
        }
    }

    // ------------------------------------------------------------
    // RGB effect panel
    // ------------------------------------------------------------

    private void BuildMkPresets()
    {
        PnlMkPresets.Children.Clear();
        foreach (var (r, g, b) in MkPresetColors)
        {
            var btn = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 2, 2),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("K2BorderBrush"),
            };
            btn.Click += (_, _) =>
            {
                _mkColor1 = (r << 16) | (g << 8) | b;
                ApplyColorButton(BtnMkColor1, _mkColor1);
                ApplyCurrentMkEffect();
            };
            PnlMkPresets.Children.Add(btn);
        }
    }

    private void CbMkEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mkCustomActive = false; // picking one of the fixed effects exits the Custom ring preview
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

    /// <summary>Serializes the current effect/color/speed/direction choice (or,
    /// when <paramref name="customActive"/>, the 8 custom LED colors) into the
    /// current profile slot. Called unconditionally (even while disconnected)
    /// so a profile edited with the mouse unplugged is still saved.</summary>
    private void MkPersistLighting(bool customActive, double? brightnessOverride = null)
    {
        if (_mkStore is null) return;
        var eff = CbMkEffect.SelectedItem is MkEffectChoice pick ? pick.Eff : MakaluProtocol.Effect.Off;
        var customInts = new int[8];
        for (int i = 0; i < 8; i++)
        {
            var (r, g, b) = _mkCustomColors[i];
            customInts[i] = (r << 16) | (g << 8) | b;
        }
        _mkStore.SaveLighting(CurrentSlot, new MakaluLightingRecord(
            (int)eff, _mkColor1, _mkColor2, _mkSpeedIndex, _mkDirIndex,
            brightnessOverride ?? Brightness, customActive, customInts));
    }

    /// <summary>Reads the panel and sends the effect to the firmware. No-op
    /// while still initializing or while the mouse isn't connected.</summary>
    private void ApplyCurrentMkEffect()
    {
        if (!_mkInitialized || _mkSuppress) return;
        if (CbMkEffect.SelectedItem is not MkEffectChoice pick) return;

        // Ring preview is software-only (no HID readback on this device), so
        // it updates regardless of connection state — unlike the actual
        // SetLighting call below.
        PreviewChanged?.Invoke();
        MkPersistLighting(customActive: false);

        if (!_mkConnected)
        {
            _log("[RGB ] skip: Makalu not connected");
            return;
        }

        var caps = CapsFor(pick.Eff);
        int bright = (int)Brightness;
        byte speed = (byte)(caps.Speed ? _mkSpeedIndex : 0);
        byte dir   = (byte)(caps.Direction ? _mkDirIndex : 0);

        static (byte, byte, byte) C(int rgb) =>
            ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

        (byte, byte, byte)? secondary = caps.Color2 ? C(_mkColor2) : null;

        LblMkRgbStatus.Text = "...";
        bool ok = _makalu.SetLighting(pick.Eff, C(_mkColor1), bright, dir, speed, secondary);
        _log($"[RGB ] apply eff={pick.Eff} speed={speed} dir={dir} bright={bright}% c1=#{_mkColor1:X6}" +
             (caps.Color2 ? $" c2=#{_mkColor2:X6}" : "") + $" -> {ok}");
        LblMkRgbStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkRgbStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void BtnMkCustomOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_mkCustomWin is { IsLoaded: true }) { _mkCustomWin.Activate(); return; }
        _mkCustomWin = new MakaluCustomRgbWindow(_makalu, _log, (int)Brightness) { Owner = Window.GetWindow(this) };
        _mkCustomWin.ColorsChanged += colors =>
        {
            _mkCustomColors = colors;
            _mkCustomActive = true;
            PreviewChanged?.Invoke();
        };
        _mkCustomWin.Applied += (colors, brightnessPct) =>
        {
            _mkCustomColors = colors;
            _mkCustomActive = true;
            MkPersistLighting(customActive: true, brightnessOverride: brightnessPct);
        };
        _mkCustomColors = _mkCustomWin.GetColors();
        _mkCustomActive = true;
        PreviewChanged?.Invoke();
        _mkCustomWin.Show();
    }

    // ------------------------------------------------------------
    // Device settings: polling rate / debounce / angle snapping / lift-off
    // ------------------------------------------------------------

    private void SldMkPolling_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int hz = PollingSteps[Math.Clamp((int)Math.Round(e.NewValue), 0, PollingSteps.Length - 1)];
        if (LblMkPollingVal != null) LblMkPollingVal.Text = $"{hz} Hz";
    }

    private void BtnMkPollingApply_Click(object sender, RoutedEventArgs e) => MkApplyPolling();

    private void MkApplyPolling()
    {
        int hz = PollingSteps[Math.Clamp((int)Math.Round(SldMkPolling.Value), 0, PollingSteps.Length - 1)];
        LblMkPollingStatus.Text = "...";
        bool ok = _makalu.SetPollingRate(hz);
        _log($"[SET ] SetPollingRate({hz}) -> {ok}");
        LblMkPollingStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkPollingStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    private void SldMkDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(e.NewValue), 0, DebounceSteps.Length - 1)];
        if (LblMkDebounceVal != null) LblMkDebounceVal.Text = $"{ms} ms";
    }

    private void BtnMkDebounceApply_Click(object sender, RoutedEventArgs e) => MkApplyDebounce();

    private void MkApplyDebounce()
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(SldMkDebounce.Value), 0, DebounceSteps.Length - 1)];
        LblMkDebounceStatus.Text = "...";
        bool ok = _makalu.SetDebounce(ms);
        _log($"[SET ] SetDebounce({ms}) -> {ok}");
        LblMkDebounceStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
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
        LblMkAngleStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
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
        LblMkLiftStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkLiftStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        MkPersistDeviceSettings();
    }

    /// <summary>Snapshots polling/debounce/angle/lift-off (one combined blob per
    /// profile) from the current controls — called after each of the four
    /// independent Apply actions above, so the saved record always reflects
    /// whichever setting the user has last touched.</summary>
    private void MkPersistDeviceSettings()
    {
        if (_mkStore is null) return;
        int pollIdx = Math.Clamp((int)Math.Round(SldMkPolling.Value), 0, PollingSteps.Length - 1);
        int debIdx  = Math.Clamp((int)Math.Round(SldMkDebounce.Value), 0, DebounceSteps.Length - 1);
        _mkStore.SaveSettings(CurrentSlot, new MakaluDeviceSettingsRecord(
            PollingSteps[pollIdx], DebounceSteps[debIdx],
            RbMkAngleOn.IsChecked == true, RbMkLiftHigh.IsChecked == true));
    }

    // ------------------------------------------------------------
    // DPI levels (right column of Settings — moved here from the old
    // standalone DPI sidebar section, see MainWindow.xaml)
    // ------------------------------------------------------------

    private readonly List<Button> _mkDpiLevelButtons = new();
    private int[] _mkDpiValues = { 400, 800, 1600, 3200, 6400 };
    private int _mkDpiActive;

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

    private void BuildMkDpiLevelButtons()
    {
        PnlMkDpiLevels.Children.Clear();
        _mkDpiLevelButtons.Clear();
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Height = 52, Margin = new Thickness(0, 0, 4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = BuildMkDpiButtonContent(i + 1, _mkDpiValues[i]),
            };
            btn.Click += (_, _) => MkDpiSelectLevel(idx);
            PnlMkDpiLevels.Children.Add(btn);
            _mkDpiLevelButtons.Add(btn);
        }
        SldMkDpi.Minimum = _mkInfo.DpiMin;
        MkUpdateDpiButtonLabels();
        SldMkDpi.Value = _mkDpiValues[_mkDpiActive];
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
        SldMkDpi.Value = _mkDpiValues[idx];
        TxtMkDpi.Text = _mkDpiValues[idx].ToString();
        MkUpdateDpiButtonLabels();
    }

    private void SldMkDpi_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mkSuppress) return;
        int dpi = MakaluProtocol.QuantizeDpiTiered((int)e.NewValue);
        _mkDpiValues[_mkDpiActive] = dpi;
        TxtMkDpi.Text = dpi.ToString();
        MkUpdateDpiButtonLabels();
    }

    private void TxtMkDpi_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) MkCommitDpiEntry();
    }

    private void TxtMkDpi_LostFocus(object sender, RoutedEventArgs e) => MkCommitDpiEntry();

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

    private void BtnMkDpiApply_Click(object sender, RoutedEventArgs e) => MkApplyDpi();

    private void MkApplyDpi()
    {
        MkCommitDpiEntry();
        LblMkDpiStatus.Text = "...";
        bool ok = _makalu.SetAllDpi(_mkDpiValues, _mkDpiActive + 1, _mkInfo.DpiMin);
        _log($"[DPI ] SetAllDpi([{string.Join(",", _mkDpiValues)}], active={_mkDpiActive + 1}) -> {ok}");
        LblMkDpiStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkDpiStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        _mkStore?.SaveDpi(CurrentSlot, new MakaluDpiRecord(_mkDpiValues, _mkDpiActive));
    }

    private void BtnMkDpiRefresh_Click(object sender, RoutedEventArgs e) => MkDpiRefreshFromDevice();

    private void MkDpiRefreshFromDevice()
    {
        var result = _makalu.GetDpi(_mkInfo.DpiMin);
        if (result is null) { _log("[DPI ] GetDpi -> not connected/failed"); return; }
        _mkDpiValues = result.Value.Levels;
        _mkDpiActive = result.Value.Active;
        MkUpdateDpiButtonLabels();
        _mkSuppress = true;
        try { SldMkDpi.Value = _mkDpiValues[_mkDpiActive]; } finally { _mkSuppress = false; }
        TxtMkDpi.Text = _mkDpiValues[_mkDpiActive].ToString();
        _log($"[DPI ] GetDpi -> levels=[{string.Join(",", _mkDpiValues)}] active={_mkDpiActive}");
    }

    // ------------------------------------------------------------
    // Profile switch: push a stored slot's lighting/DPI/settings into this
    // panel's controls, then re-apply everything to hardware (if connected).
    // Called by MainWindow.Makalu.cs on combo switch, module init, and the
    // disconnected->connected poll transition.
    // ------------------------------------------------------------

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
                int idx = Array.FindIndex(MkEffectList, x => x.Eff == eff);
                CbMkEffect.SelectedIndex = idx >= 0 ? idx : 0;
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
                UpdateMkCapabilities();
            }

            if (dpi is not null && dpi.Levels.Length == 5)
            {
                _mkDpiValues = dpi.Levels;
                _mkDpiActive = Math.Clamp(dpi.Active, 0, 4);
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
                if (settings.LiftOffHigh) RbMkLiftHigh.IsChecked = true; else RbMkLiftLow.IsChecked = true;
            }
        }
        finally { _mkSuppress = wasSuppress; }

        PreviewChanged?.Invoke();

        if (!_mkConnected)
        {
            _log("[PROFILE] reload: device not connected, UI updated only");
            return;
        }

        if (_mkCustomActive)
        {
            bool ok = _makalu.SetLightingCustom(_mkCustomColors, (int)Brightness);
            _log($"[PROFILE] reload custom lighting -> {ok}");
        }
        else
        {
            ApplyCurrentMkEffect();
        }
        if (settings is not null)
        {
            MkApplyPolling();
            MkApplyDebounce();
            MkApplyAngle(settings.AngleSnapping);
            MkApplyLiftOff(settings.LiftOffHigh);
        }
        if (dpi is not null) MkApplyDpi();
    }
}
