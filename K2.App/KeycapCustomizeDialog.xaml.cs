using System;
using System.Windows;
using System.Windows.Media;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Per-key appearance editor, opened when "Edit individual keycaps" mode is active and the user
/// clicks a key (see MainWindow.Everest.cs/Everest60.cs/Keys.cs's keyboard click handlers).
/// Two independent overrides, both optional: a color override (replaces the device-wide keycap
/// color for just this key) and a custom image (replaces the legend entirely — "simulate a
/// different keycap"). Esc-only: a fixed "Use Mountain logo" checkbox that bypasses the file
/// picker and points at the bundled asset instead (MainWindow.MountainLogoImagePath sentinel).
///
/// Every change applies live via the <see cref="Changed"/> event (mirrors the rest of K2's
/// color-picker/paint-mode UX, which never uses an OK/Cancel commit step) — the caller persists
/// to the store and re-renders the keyboard on each event, so "Close" just closes the window.
/// </summary>
public partial class KeycapCustomizeDialog : Window
{
    /// <summary>Current color override hex, or null if none.</summary>
    public string? ColorHex { get; private set; }

    /// <summary>Current image override path (or MainWindow.MountainLogoImagePath), or null if none.</summary>
    public string? ImagePath { get; private set; }

    /// <summary>Fired after every change to ColorHex/ImagePath — the caller should persist
    /// (Store.SetKeycapOverride/ClearKeycapOverride) and re-run the device's
    /// ApplyKeycap*AppearanceToAllKeys.</summary>
    public event Action? Changed;

    private readonly int _cropTargetSize;

    public KeycapCustomizeDialog(string keyLabel, bool isEscKey, string? currentColorHex, string? currentImagePath, int cropTargetSize = 40)
    {
        InitializeComponent();

        _cropTargetSize = cropTargetSize;
        ColorHex = currentColorHex;
        ImagePath = currentImagePath;

        Title = Loc.Get("keycap_customize_title", keyLabel);
        LblHeader.Text = Title;

        CkUseMountainLogo.Visibility = isEscKey ? Visibility.Visible : Visibility.Collapsed;

        RefreshColorControls();
        RefreshImagePreview();
    }

    private void RefreshColorControls()
    {
        bool hasColor = ColorHex is { Length: > 0 };
        CkOverrideColor.IsChecked = hasColor;
        BtnColorSwatch.IsEnabled = hasColor;
        if (hasColor && TryParseColor(ColorHex!, out var c))
            BtnColorSwatch.Background = new SolidColorBrush(c);
    }

    private void RefreshImagePreview()
    {
        bool usingMountainLogo = ImagePath == MainWindow.MountainLogoImagePath;
        CkUseMountainLogo.IsChecked = usingMountainLogo;
        BtnCustomImage.IsEnabled = !usingMountainLogo;

        var bmp = ImagePath is { Length: > 0 } ? MainWindow.LoadKeycapOverrideImage(ImagePath) : null;
        ImgPreview.Source = bmp;
        ImgPreview.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
        LblNoImage.Visibility = bmp != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try { color = (Color)ColorConverter.ConvertFromString(hex)!; return true; }
        catch { color = Colors.Gray; return false; }
    }

    private void CkOverrideColor_Click(object sender, RoutedEventArgs e)
    {
        if (CkOverrideColor.IsChecked != true)
        {
            ColorHex = null;
            RefreshColorControls();
            Changed?.Invoke();
            return;
        }

        // Just checked: immediately prompt for the color, same as the device-wide "Custom" pickers.
        TryParseColor(ColorHex ?? "#404040", out var current);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true, AnyColor = true, SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            CkOverrideColor.IsChecked = ColorHex is { Length: > 0 }; // revert checkbox, nothing changed
            return;
        }

        ColorHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        RefreshColorControls();
        Changed?.Invoke();
    }

    private void BtnColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        TryParseColor(ColorHex ?? "#404040", out var current);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true, AnyColor = true, SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        ColorHex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        RefreshColorControls();
        Changed?.Invoke();
    }

    private void BtnCustomImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("crop_title", _cropTargetSize, _cropTargetSize),
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string picked = dlg.FileName;
        string? cropped = ImageCropDialog.Show(this, picked, _cropTargetSize, _cropTargetSize, Loc.Get("crop_title", _cropTargetSize, _cropTargetSize));
        if (cropped is not null) picked = cropped;

        ImagePath = picked;
        RefreshImagePreview();
        Changed?.Invoke();
    }

    private void BtnClearImage_Click(object sender, RoutedEventArgs e)
    {
        ImagePath = null;
        RefreshImagePreview();
        Changed?.Invoke();
    }

    private void CkUseMountainLogo_Click(object sender, RoutedEventArgs e)
    {
        ImagePath = CkUseMountainLogo.IsChecked == true ? MainWindow.MountainLogoImagePath : null;
        RefreshImagePreview();
        Changed?.Invoke();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        ColorHex = null;
        ImagePath = null;
        RefreshColorControls();
        RefreshImagePreview();
        Changed?.Invoke();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
