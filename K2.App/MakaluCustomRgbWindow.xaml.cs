using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Per-LED color editor for the Makalu 67/Max (8 LEDs: 4 left + 4 right),
/// mirroring BaseCampLinux's <c>MakaluCustomRGBWindow</c> reference (simplified
/// to click-to-pick-color per LED rather than a multi-select canvas paint tool).
/// Physical layout (controller.py's <c>set_lighting_custom</c> doc):
/// LED0=top-left … LED3=bottom-left, LED4=bottom-right … LED7=top-right.
/// </summary>
public partial class MakaluCustomRgbWindow : Window
{
    private static readonly (double X, double Y)[] LedPos =
    {
        (10,  50), (10, 120), (10, 190), (10, 260), // left column, top→bottom
        (225, 260), (225, 190), (225, 120), (225, 50), // right column, bottom→top
    };
    private const double LedW = 65, LedH = 55;

    private readonly MakaluService _makalu;
    private readonly Action<string> _log;
    private readonly (byte r, byte g, byte b)[] _leds = new (byte, byte, byte)[8];
    private readonly Button[] _ledButtons = new Button[8];

    /// <summary>Fires with a snapshot of all 8 LED colors whenever one
    /// changes (and once up front with the initial all-black state) — lets
    /// MainWindow's LED ring preview mirror this editor live, same as how
    /// MakaluRgbSettingsPanel.PreviewChanged mirrors the main effect combo.
    /// See MainWindow.Makalu.cs / MakaluRgbSettingsPanel's Custom wiring.</summary>
    internal event Action<(byte r, byte g, byte b)[]>? ColorsChanged;

    /// <summary>Fires only when "Apply" actually succeeds, with the LEDs +
    /// brightness that were sent — lets the owning panel persist exactly what
    /// hit the device into the current profile slot (MakaluRgbSettingsPanel's
    /// MkPersistLighting), as opposed to ColorsChanged which fires on every
    /// color pick regardless of whether Apply was ever pressed.</summary>
    internal event Action<(byte r, byte g, byte b)[], int>? Applied;

    internal MakaluCustomRgbWindow(MakaluService makalu, Action<string> log, int initialBrightnessPct)
    {
        InitializeComponent();
        _makalu = makalu;
        _log = log;
        BuildLeds();
        SldBrightness.Value = initialBrightnessPct;
    }

    /// <summary>Snapshot of the current 8 LED colors — call right after
    /// construction (before anything can change them) to seed a
    /// ColorsChanged subscriber with the initial all-black state; there are
    /// no subscribers yet during the constructor itself.</summary>
    internal (byte r, byte g, byte b)[] GetColors() => ((byte, byte, byte)[])_leds.Clone();

    private void BuildLeds()
    {
        for (int i = 0; i < 8; i++)
        {
            _leds[i] = (0, 0, 0);
            int idx = i;
            var btn = new Button
            {
                Width = LedW,
                Height = LedH,
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)),
                BorderThickness = new Thickness(2),
                Content = $"LED {idx}",
                Foreground = (Brush)FindResource("K2TextMutedBrush"),
            };
            Canvas.SetLeft(btn, LedPos[idx].X);
            Canvas.SetTop(btn, LedPos[idx].Y);
            btn.Click += (_, _) => PickColor(idx);
            CvsLeds.Children.Add(btn);
            _ledButtons[idx] = btn;
        }
    }

    private void PickColor(int idx)
    {
        var (r, g, b) = _leds[idx];
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(r, g, b),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _leds[idx] = (dlg.Color.R, dlg.Color.G, dlg.Color.B);
        _ledButtons[idx].Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        _ledButtons[idx].Content = null;
        ColorsChanged?.Invoke(GetColors());
    }

    private void SldBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblBrightness != null) LblBrightness.Text = $"{(int)e.NewValue}%";
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        LblStatus.Text = "...";
        bool ok = _makalu.SetLightingCustom(_leds, (int)SldBrightness.Value);
        _log($"[CUSTOM] SetLightingCustom -> {ok}");
        LblStatus.Text = ok ? Loc.Get("makalu_applied") : Loc.Get("makalu_failed");
        LblStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
        if (ok) Applied?.Invoke(GetColors(), (int)SldBrightness.Value);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
