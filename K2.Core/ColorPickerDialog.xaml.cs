using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace K2.Core;

/// <summary>
/// K2-themed replacement for <see cref="System.Windows.Forms.ColorDialog"/>: a
/// saturation/value square + hue strip, RGB/hex numeric entry, and a global saved-color
/// palette (<see cref="AppSettings.SavedPickerColors"/>) that mirrors Base Camp's own
/// <c>Settings.SavedPickerColors</c> — one palette shared by every device's lighting
/// panel, not per-device. Sizes are hard-coded (matching the XAML) rather than read from
/// ActualWidth/Height, so positioning math never depends on a layout pass having run yet.
/// </summary>
public partial class ColorPickerDialog : Window
{
    private const double SvSize = 220.0;
    private const double HueW = 26.0;
    private const double HueH = 220.0;

    /// <summary>0xRRGGBB, same encoding used by every device's stored lighting color.</summary>
    public int SelectedRgb { get; private set; }

    private readonly ObservableCollection<string> _saved;
    private bool _suppress = true;
    private double _h, _s, _v; // hue 0..360, saturation/value 0..1

    public ColorPickerDialog(int initialRgb)
    {
        InitializeComponent();

        SelectedRgb = initialRgb;
        byte r = (byte)((initialRgb >> 16) & 0xFF);
        byte g = (byte)((initialRgb >> 8) & 0xFF);
        byte b = (byte)(initialRgb & 0xFF);
        (_h, _s, _v) = RgbToHsv(r, g, b);

        _saved = new ObservableCollection<string>(AppSettings.SavedPickerColors);
        IcSaved.ItemsSource = _saved;

        UpdateAllFromHsv();
        _suppress = false;
    }

    /// <summary>Opens the dialog modally and returns the picked 0xRRGGBB color, or null
    /// if the user cancelled. Drop-in replacement for the old
    /// <c>new System.Windows.Forms.ColorDialog { ... }.ShowDialog()</c> call sites.</summary>
    public static int? Pick(Window? owner, int initialRgb)
    {
        var dlg = new ColorPickerDialog(initialRgb);
        if (owner is not null) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg.SelectedRgb : null;
    }

    // ===== Saturation/Value square =====

    private void SvSquare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SvSquare.CaptureMouse();
        UpdateSvFromPoint(e.GetPosition(SvSquare));
    }

    private void SvSquare_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && SvSquare.IsMouseCaptured)
            UpdateSvFromPoint(e.GetPosition(SvSquare));
    }

    private void SvSquare_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => SvSquare.ReleaseMouseCapture();

    private void UpdateSvFromPoint(Point p)
    {
        double x = Math.Clamp(p.X, 0, SvSize);
        double y = Math.Clamp(p.Y, 0, SvSize);
        _s = x / SvSize;
        _v = 1 - y / SvSize;
        UpdateAllFromHsv();
    }

    // ===== Hue strip =====

    private void HueBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HueBar.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HueBar));
    }

    private void HueBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueBar.IsMouseCaptured)
            UpdateHueFromPoint(e.GetPosition(HueBar));
    }

    private void HueBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => HueBar.ReleaseMouseCapture();

    private void UpdateHueFromPoint(Point p)
    {
        double y = Math.Clamp(p.Y, 0, HueH);
        _h = (y / HueH) * 360.0;
        UpdateAllFromHsv();
    }

    // ===== Numeric entry =====

    private void RgbBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (!byte.TryParse(TxtR.Text, out byte r)) return;
        if (!byte.TryParse(TxtG.Text, out byte g)) return;
        if (!byte.TryParse(TxtB.Text, out byte b)) return;
        (_h, _s, _v) = RgbToHsv(r, g, b);
        UpdateAllFromHsv();
    }

    private void HexBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (!TryParseHex(TxtHex.Text, out byte r, out byte g, out byte b)) return;
        (_h, _s, _v) = RgbToHsv(r, g, b);
        UpdateAllFromHsv();
    }

    // ===== Saved palette =====

    private void SavedSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string hex) return;
        if (!TryParseHex(hex, out byte r, out byte g, out byte b)) return;
        (_h, _s, _v) = RgbToHsv(r, g, b);
        UpdateAllFromHsv();
    }

    private void BtnAddSaved_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.AddSavedPickerColor(CurrentHex());
        RefreshSavedCollection();
    }

    private void BtnRemoveSaved_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string hex) return;
        AppSettings.RemoveSavedPickerColor(hex);
        RefreshSavedCollection();
    }

    private void RefreshSavedCollection()
    {
        _saved.Clear();
        foreach (string c in AppSettings.SavedPickerColors) _saved.Add(c);
    }

    // ===== Shared update/refresh =====

    private void UpdateAllFromHsv()
    {
        var (r, g, b) = HsvToRgb(_h, _s, _v);
        SelectedRgb = (r << 16) | (g << 8) | b;

        var (hr, hg, hb) = HsvToRgb(_h, 1.0, 1.0);
        RectHueBase.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));

        Canvas.SetLeft(SvThumb, _s * SvSize - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * SvSize - SvThumb.Height / 2);
        Canvas.SetTop(HueThumb, _h / 360.0 * HueH - HueThumb.Height / 2);
        Canvas.SetLeft(HueThumb, 0);

        BdrPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

        bool wasSuppressed = _suppress;
        _suppress = true;
        TxtR.Text = r.ToString(CultureInfo.InvariantCulture);
        TxtG.Text = g.ToString(CultureInfo.InvariantCulture);
        TxtB.Text = b.ToString(CultureInfo.InvariantCulture);
        TxtHex.Text = CurrentHex();
        _suppress = wasSuppressed;
    }

    private string CurrentHex()
    {
        byte r = (byte)((SelectedRgb >> 16) & 0xFF);
        byte g = (byte)((SelectedRgb >> 8) & 0xFF);
        byte b = (byte)(SelectedRgb & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ===== Color math =====

    internal static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        return byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    private static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double h = 0;
        if (delta > 0.00001)
        {
            if (max == rd) h = 60 * (((gd - bd) / delta) % 6);
            else if (max == gd) h = 60 * (((bd - rd) / delta) + 2);
            else h = 60 * (((rd - gd) / delta) + 4);
        }
        if (h < 0) h += 360;

        double s = max <= 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0 % 2) - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0.0);
        else if (h < 120) (r, g, b) = (x, c, 0.0);
        else if (h < 180) (r, g, b) = (0.0, c, x);
        else if (h < 240) (r, g, b) = (0.0, x, c);
        else if (h < 300) (r, g, b) = (x, 0.0, c);
        else (r, g, b) = (c, 0.0, x);

        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Binds a "#RRGGBB" string (from <see cref="AppSettings.SavedPickerColors"/>)
/// straight to a <see cref="SolidColorBrush.Color"/> in the saved-palette
/// <c>DataTemplate</c> — one-way only, palette swatches are never edited in place.</summary>
public sealed class HexToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && ColorPickerDialog.TryParseHex(hex, out byte r, out byte g, out byte b))
            return Color.FromRgb(r, g, b);
        return Colors.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
