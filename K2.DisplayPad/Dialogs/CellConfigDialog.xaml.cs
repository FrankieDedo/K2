using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.Core;
using K2.DisplayPad.Models;
using Microsoft.Win32;

namespace K2.DisplayPad.Dialogs;

/// <summary>
/// Unified dialog to configure a standalone DisplayPad cell:
/// image loading + rotation + action.
/// </summary>
public partial class CellConfigDialog : Window
{
    // ---- Outputs --------------------------------------------------------
    public string?  NewImagePath { get; private set; }
    public bool     ImageChanged  { get; private set; }
    public string?  ActionType    { get; private set; }
    public string?  ActionValue   { get; private set; }

    // ---- State ----------------------------------------------------------
    private readonly int    _cellIndex;
    private string?         _pendingPath;
    private readonly string? _originalPath;
    private int             _rotation;

    private const string CacheDir = "K2.DisplayPad\\user_rotated";

    public CellConfigDialog(
        int     cellIndex,
        string? currentImagePath,
        string? currentActionType,
        string? currentActionValue)
    {
        InitializeComponent();

        _cellIndex    = cellIndex;
        _pendingPath  = currentImagePath;
        _originalPath = currentImagePath;
        ActionType    = currentActionType;
        ActionValue   = currentActionValue;

        LblHeader.Text = $"Key #{cellIndex}  —  Configure";

        RefreshImagePreview();
        RefreshActionSummary();
    }

    // ================================================================
    // Image section
    // ================================================================

    private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Choose image for key",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _pendingPath  = dlg.FileName;
        _rotation     = 0;
        Rb0.IsChecked = true;
        RefreshImagePreview();
    }

    private void BtnRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _pendingPath = null;
        RefreshImagePreview();
    }

    private void RotRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out int degrees)) return;
        _rotation = degrees;
        PreviewRotate.Angle = _rotation;
    }

    private void RefreshImagePreview()
    {
        bool hasImage = !string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath);
        LblNoImage.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        ImgPreview.Visibility = hasImage ? Visibility.Visible   : Visibility.Collapsed;

        if (!hasImage) { ImgPreview.Source = null; return; }

        try
        {
            byte[] bytes = File.ReadAllBytes(_pendingPath!);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            ImgPreview.Source = bmp;
        }
        catch
        {
            ImgPreview.Source     = null;
            LblNoImage.Text       = "Cannot load image";
            LblNoImage.Visibility = Visibility.Visible;
            ImgPreview.Visibility = Visibility.Collapsed;
        }
    }

    // ================================================================
    // Action section
    // ================================================================

    private void BtnConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ButtonActionDialog(_cellIndex, ActionType, ActionValue) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                      ? null : dlg.ActionType;
        ActionValue = ActionType is null ? null : dlg.ActionValue;
        RefreshActionSummary();
    }

    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        ActionType  = null;
        ActionValue = null;
        RefreshActionSummary();
    }

    private void RefreshActionSummary()
    {
        if (string.IsNullOrEmpty(ActionType) || ActionType == "none")
        {
            LblActionSummary.Text = Loc.Get("act_none");
            return;
        }

        string val = ActionValue ?? "";
        LblActionSummary.Text = ActionType switch
        {
            "keys"     => $"Keys: {val}",
            "exec"     => $"Run: {Path.GetFileName(val)}",
            "folder"   => $"Folder: {val}",
            "url"      => $"URL: {val}",
            "browser"  => $"Browser: {val}",
            "profile"  => $"Profile: {val}",
            "oscmd"    => $"Shell: {val}",
            "media"    => $"Media: {val}",
            "mouse"    => $"Mouse: {val}",
            "text"     => $"Text: {val}",
            "command"  => $"Command: {val}",
            "pyscript" => "Python script",
            _          => $"{ActionType}: {val}",
        };
    }

    // ================================================================
    // OK / Cancel
    // ================================================================

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath) && _rotation != 0)
        {
            try
            {
                NewImagePath = ApplyUserRotation(_pendingPath, _rotation);
                ImageChanged = true;
            }
            catch
            {
                NewImagePath = _pendingPath;
                ImageChanged = _pendingPath != _originalPath;
            }
        }
        else
        {
            NewImagePath = _pendingPath;
            ImageChanged = _pendingPath != _originalPath;
        }

        DialogResult = true;
    }

    // ================================================================
    // Image rotation helper
    // ================================================================

    private static string ApplyUserRotation(string sourcePath, int degrees)
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheDir);
        Directory.CreateDirectory(cacheRoot);

        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(sourcePath).Ticks; } catch { }
        byte[] hashBytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourcePath}|{mtime}|ur{degrees}"));
        string name = Convert.ToHexString(hashBytes).ToLowerInvariant() + $"_ur{degrees}.png";
        string dest = Path.Combine(cacheRoot, name);
        if (File.Exists(dest)) return dest;

        byte[] bytes = File.ReadAllBytes(sourcePath);
        using var ms  = new MemoryStream(bytes);
        using var bmp = new System.Drawing.Bitmap(ms);

        var flipType = degrees switch
        {
            90  => System.Drawing.RotateFlipType.Rotate90FlipNone,
            180 => System.Drawing.RotateFlipType.Rotate180FlipNone,
            270 => System.Drawing.RotateFlipType.Rotate270FlipNone,
            _   => System.Drawing.RotateFlipType.RotateNoneFlipNone,
        };
        bmp.RotateFlip(flipType);
        bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png);
        return dest;
    }
}
