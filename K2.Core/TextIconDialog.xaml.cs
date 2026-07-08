using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.Core;

/// <summary>
/// Small "insert text" editor for a key image: plain text on a solid color
/// background, or overlaid on top of the image already loaded in the caller's
/// dialog (only offered when one is present). Shared by <c>DpKeyConfigDialog</c>/
/// <c>NdkKeyConfigDialog</c> (K2.App) and <c>CellConfigDialog</c> (K2.DisplayPad) —
/// lives in K2.Core since both apps reference it. Rendering is done by
/// <see cref="TextIconGenerator"/> (pure System.Drawing, no WPF dependency).
/// </summary>
public partial class TextIconDialog : Window
{
    /// <summary>Generated PNG path — set only when the dialog returns true.</summary>
    public string? NewImagePath { get; private set; }

    private readonly int _size;
    private readonly string? _baseImagePath;
    private System.Drawing.Color _bgColor = System.Drawing.ColorTranslator.FromHtml("#1A1A1E");
    private System.Drawing.Color _textColor = System.Drawing.Color.White;

    /// <param name="size">Target icon size in pixels (102 for DisplayPad, 72 for Everest numpad display keys).</param>
    /// <param name="baseImagePath">Currently loaded key image, if any — enables the "on image" background mode.</param>
    public TextIconDialog(int size, string? baseImagePath)
    {
        InitializeComponent();

        _size = size;
        _baseImagePath = !string.IsNullOrEmpty(baseImagePath) && File.Exists(baseImagePath) ? baseImagePath : null;
        RbBgImage.IsEnabled = _baseImagePath is not null;

        ApplyColorButton(BtnBgColor, _bgColor);
        ApplyColorButton(BtnTextColor, _textColor);

        RefreshPreview();
    }

    private void TxtInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

    private void BgMode_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

    private void BtnBgColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = _bgColor,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _bgColor = dlg.Color;
        ApplyColorButton(BtnBgColor, _bgColor);
        RefreshPreview();
    }

    private void BtnTextColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
            Color = _textColor,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _textColor = dlg.Color;
        ApplyColorButton(BtnTextColor, _textColor);
        RefreshPreview();
    }

    private static void ApplyColorButton(Button btn, System.Drawing.Color c) =>
        btn.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

    // RbBgSolid's IsChecked="True" in XAML fires its Checked event synchronously
    // during InitializeComponent(), before RbBgImage (declared later in the XAML)
    // has been wired up — so this must tolerate RbBgImage still being null.
    private bool UseImageBackground => RbBgImage?.IsChecked == true && _baseImagePath is not null;

    private void RefreshPreview()
    {
        using var bmp = TextIconGenerator.TryRenderTextIcon(
            TxtInput.Text, _size, _textColor,
            UseImageBackground ? (System.Drawing.Color?)null : _bgColor,
            UseImageBackground ? _baseImagePath : null);

        ImgPreview.Source = bmp is null ? null : ToBitmapSource(bmp);
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = ms;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2", "text_icons");
        string dest = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + ".png");

        bool ok = TextIconGenerator.TryGenerateTextIcon(
            TxtInput.Text, _size, dest, _textColor,
            UseImageBackground ? (System.Drawing.Color?)null : _bgColor,
            UseImageBackground ? _baseImagePath : null);

        if (!ok)
        {
            MessageBox.Show(this, Loc.Get("txt_generate_failed"), Loc.Get("error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NewImagePath = dest;
        DialogResult = true;
    }
}
