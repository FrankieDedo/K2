using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Unified dialog to configure an Everest numpad display key: image + action together,
/// opened with a single click — mirrors <see cref="DpKeyConfigDialog"/> (DisplayPad)
/// instead of the old split "click = image, right-click = action" interaction, which
/// buried action assignment behind an undiscoverable right-click.
/// </summary>
public partial class NdkKeyConfigDialog : Window
{
    // ---- Outputs --------------------------------------------------------
    /// <summary>Final image path. Null = remove.</summary>
    public string? NewImagePath { get; private set; }
    /// <summary>True if the image changed (load / remove).</summary>
    public bool ImageChanged   { get; private set; }
    /// <summary>Resulting action type (null = none).</summary>
    public string? ActionType  { get; private set; }
    /// <summary>Resulting action value.</summary>
    public string? ActionValue { get; private set; }

    // ---- State ----------------------------------------------------------
    private readonly int _keyIndex;
    private readonly IActionHost? _host;
    private string? _pendingPath;
    private readonly string? _originalPath;

    private const int IconSize = 72;

    public NdkKeyConfigDialog(
        int keyIndex,
        string? currentImagePath,
        string? currentActionType,
        string? currentActionValue,
        IActionHost? host = null)
    {
        InitializeComponent();

        _keyIndex     = keyIndex;
        _host         = host;
        _pendingPath  = currentImagePath;
        _originalPath = currentImagePath;
        ActionType    = currentActionType;
        ActionValue   = currentActionValue;

        LblHeader.Text = $"Display Key {keyIndex + 1}  —  Configure";

        RefreshImagePreview();
        RefreshActionSummary();
    }

    // =====================================================================
    // Image section
    // =====================================================================

    private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = $"Choose image for Display Key {_keyIndex + 1}  ({IconSize}×{IconSize} px)",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        string picked = dlg.FileName;
        string? cropped = ImageCropDialog.Show(this, picked, IconSize, IconSize,
            Loc.Get("crop_title", IconSize, IconSize), bakeRoundedCorners: true);
        if (cropped is not null) picked = cropped;

        _pendingPath = picked;
        RefreshImagePreview();
    }

    private void BtnRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _pendingPath = null;
        RefreshImagePreview();
    }

    /// <summary>
    /// Opens the shared "insert text" editor (<see cref="TextIconDialog"/>): plain text
    /// on a solid color, or overlaid on the image currently loaded in this dialog.
    /// </summary>
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextIconDialog(IconSize, _pendingPath) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _pendingPath = dlg.NewImagePath;
        RefreshImagePreview();
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

    // =====================================================================
    // Action section
    // =====================================================================

    private void BtnConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ButtonActionDialog(_keyIndex, ActionType, ActionValue, _host) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string? oldType = ActionType, oldValue = ActionValue;
        ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                      ? null : dlg.ActionType;
        ActionValue = ActionType is null ? null : dlg.ActionValue;

        if (ActionType != oldType || ActionValue != oldValue)
            TryAutoGenerateImage();

        RefreshActionSummary();
    }

    /// <summary>Removing the action also clears the key's picture — see the equivalent
    /// note in <c>DpKeyConfigDialog.BtnRemoveAction_Click</c>.</summary>
    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        ActionType  = null;
        ActionValue = null;
        _pendingPath = null;
        RefreshImagePreview();
        RefreshActionSummary();
    }

    /// <summary>
    /// When the action just assigned/changed is "exec" or "folder", auto-generate the
    /// key's picture (the executable's own icon, or a folder glyph + name) instead of
    /// requiring the user to manually pick+crop an image.
    /// </summary>
    private void TryAutoGenerateImage()
    {
        if (string.IsNullOrWhiteSpace(ActionValue)) return;
        if (ActionType != "exec" && ActionType != "folder") return;

        string dest = AutoIconCachePath(ActionType!, ActionValue!);
        bool ok = ActionType == "exec"
            ? IconImageGenerator.TryGenerateExecIcon(ActionValue!, IconSize, dest)
            : IconImageGenerator.TryGenerateDiskFolderIcon(ActionValue!, IconSize, dest);
        if (!ok) return;

        _pendingPath = dest;
        RefreshImagePreview();
    }

    private static string AutoIconCachePath(string kind, string sourceValue)
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2.App", "auto_icons");
        Directory.CreateDirectory(cacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(sourceValue).Ticks; } catch { } }
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}|{IconSize}"));
        return Path.Combine(cacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
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
            "macro"    => ActionTypeHelper.MacroSummary(val),
            "pyscript" => "Python script",
            _          => ActionTypeHelper.IsUnrecognized(ActionType) ? Loc.Get("act_unrecognized") : $"{ActionType}: {val}",
        };
    }

    // =====================================================================
    // OK / Cancel
    // =====================================================================

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        NewImagePath = _pendingPath;
        ImageChanged = _pendingPath != _originalPath;
        DialogResult = true;
    }
}
