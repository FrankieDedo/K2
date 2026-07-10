using System;
using System.Windows;
using System.Windows.Controls;
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

    private MakaluCustomRgbWindow? _mkCustomWin;

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

    private sealed record MkCaps(bool Speed, bool Color1, bool Color2, bool Direction);

    private static MkCaps CapsFor(MakaluProtocol.Effect e) => e switch
    {
        MakaluProtocol.Effect.Static       => new(false, true,  false, false),
        MakaluProtocol.Effect.Breathing    => new(true,  true,  true,  false),
        MakaluProtocol.Effect.RgbBreathing => new(true,  false, false, false),
        MakaluProtocol.Effect.Rainbow      => new(true,  false, false, true),
        MakaluProtocol.Effect.Responsive   => new(false, true,  false, false),
        MakaluProtocol.Effect.Yeti         => new(true,  true,  true,  false),
        _                                  => new(false, false, false, false), // Off
    };

    private static readonly (byte r, byte g, byte b)[] MkPresetColors =
    {
        (255,   0,   0), (204,   0,  67), (235,  64,  52), (220,  41, 188),
        (179,  53, 127), ( 71,   0, 204), (  0,  60, 204), (  0, 118, 204),
        (  0, 204, 181), ( 41, 255, 204), ( 91, 222,  98), (152, 235,  53),
    };

    private static readonly int[] DebounceSteps = MakaluProtocol.DebounceValuesMs; // {2,4,6,8,10,12}

    internal void Init(MakaluService service, Action<string> log)
    {
        _makalu = service;
        _log = log;
        _mkSuppress = true;
        try
        {
            CbMkEffect.ItemsSource = MkEffectList;
            CbMkEffect.DisplayMemberPath = "Label";
            CbMkEffect.SelectedIndex = 0; // Static

            CbMkSpeed.ItemsSource = new[] { "Slow", "Medium", "Fast" };
            CbMkSpeed.SelectedIndex = 1;

            CbMkDirection.ItemsSource = new[] { "←", "→" };
            CbMkDirection.SelectedIndex = 1;

            SldMkBrightness.Value = 100;
            LblMkBrightness.Text = "100%";

            BuildMkPresets();
            UpdateMkCapabilities();
            ApplyColorButton(BtnMkColor1, _mkColor1);
            ApplyColorButton(BtnMkColor2, _mkColor2);

            CbMkPolling.ItemsSource = new[] { "125 Hz", "250 Hz", "500 Hz", "1000 Hz" };
            CbMkPolling.SelectedIndex = 3;

            SldMkDebounce.Value = 0;
            LblMkDebounceVal.Text = "2 ms";
        }
        finally
        {
            _mkSuppress = false;
        }
        _mkInitialized = true;
    }

    /// <summary>Called by the parent whenever the detected model changes
    /// (only DpiMin actually differs by model for this control's purposes).</summary>
    internal void UpdateDeviceInfo(MakaluService.DeviceInfo info) => _mkInfo = info;

    internal void SetConnected(bool connected) => _mkConnected = connected;

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
        UpdateMkCapabilities();
        ApplyCurrentMkEffect();
    }

    private void CbMkEffectParam_Changed(object sender, SelectionChangedEventArgs e) => ApplyCurrentMkEffect();

    private void SldMkBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblMkBrightness != null) LblMkBrightness.Text = $"{(int)e.NewValue}%";
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

    /// <summary>Reads the panel and sends the effect to the firmware. No-op
    /// while still initializing or while the mouse isn't connected.</summary>
    private void ApplyCurrentMkEffect()
    {
        if (!_mkInitialized || _mkSuppress) return;
        if (CbMkEffect.SelectedItem is not MkEffectChoice pick) return;
        if (!_mkConnected)
        {
            _log("[RGB ] skip: Makalu not connected");
            return;
        }

        var caps = CapsFor(pick.Eff);
        int bright = (int)SldMkBrightness.Value;
        byte speed = (byte)(caps.Speed ? Math.Clamp(CbMkSpeed.SelectedIndex, 0, 2) : 0);
        byte dir   = (byte)(caps.Direction ? Math.Clamp(CbMkDirection.SelectedIndex, 0, 1) : 0);

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
        _mkCustomWin = new MakaluCustomRgbWindow(_makalu, _log, (int)SldMkBrightness.Value) { Owner = Window.GetWindow(this) };
        _mkCustomWin.Show();
    }

    // ------------------------------------------------------------
    // Device settings: polling rate / debounce / angle snapping / lift-off
    // ------------------------------------------------------------

    private void BtnMkPollingApply_Click(object sender, RoutedEventArgs e)
    {
        int hz = int.Parse((CbMkPolling.SelectedItem as string ?? "1000 Hz").Split(' ')[0]);
        LblMkPollingStatus.Text = "...";
        bool ok = _makalu.SetPollingRate(hz);
        _log($"[SET ] SetPollingRate({hz}) -> {ok}");
        LblMkPollingStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkPollingStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void SldMkDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(e.NewValue), 0, DebounceSteps.Length - 1)];
        if (LblMkDebounceVal != null) LblMkDebounceVal.Text = $"{ms} ms";
    }

    private void BtnMkDebounceApply_Click(object sender, RoutedEventArgs e)
    {
        int ms = DebounceSteps[Math.Clamp((int)Math.Round(SldMkDebounce.Value), 0, DebounceSteps.Length - 1)];
        LblMkDebounceStatus.Text = "...";
        bool ok = _makalu.SetDebounce(ms);
        _log($"[SET ] SetDebounce({ms}) -> {ok}");
        LblMkDebounceStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkDebounceStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void CkMkAngleSnap_Click(object sender, RoutedEventArgs e)
    {
        bool on = CkMkAngleSnap.IsChecked == true;
        LblMkAngleStatus.Text = "...";
        bool ok = _makalu.SetAngleSnapping(on);
        _log($"[SET ] SetAngleSnapping({on}) -> {ok}");
        LblMkAngleStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkAngleStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void CkMkLiftHigh_Click(object sender, RoutedEventArgs e)
    {
        bool high = CkMkLiftHigh.IsChecked == true;
        LblMkLiftStatus.Text = "...";
        bool ok = _makalu.SetLiftOff(high);
        _log($"[SET ] SetLiftOff(high={high}) -> {ok}");
        LblMkLiftStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblMkLiftStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }
}
