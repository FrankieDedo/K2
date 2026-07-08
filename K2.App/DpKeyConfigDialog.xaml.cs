using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Unified dialog to configure a DisplayPad key:
/// image loading + crop/zoom + rotation + action — all in the SAME window
/// (2026-07-05: crop/zoom, previously a separate popup via ImageCropDialog, has been
/// folded in here via <see cref="CropEditor"/>, so the whole image-editing flow stays
/// in a single interface).
///
/// "User" rotation (this dialog) and "device" rotation (satellite) are
/// independent: user rotation is applied to the image saved to disk,
/// device rotation is applied at upload time via <c>ResolveForUpload</c> in the
/// satellite. So the user can load an image, rotate it 90° here to adjust
/// its orientation, and the satellite will then apply the
/// counter-rotation for a display mounted rotated.
/// </summary>
public partial class DpKeyConfigDialog : Window
{
    // ---- Outputs --------------------------------------------------------
    /// <summary>Final image path (already rotated if requested). Null = remove.</summary>
    public string? NewImagePath { get; private set; }
    /// <summary>True if the image changed (load / remove / rotation).</summary>
    public bool ImageChanged   { get; private set; }
    /// <summary>Resulting action type (null = none).</summary>
    public string? ActionType  { get; private set; }
    /// <summary>Resulting action value.</summary>
    public string? ActionValue { get; private set; }

    // ---- State ----------------------------------------------------------
    private readonly int _keyIndex;
    /// <summary>Physical mounting rotation of the DisplayPad this key lives on (0/90/180/270)
    /// — passed in so an auto-generated image (exec/folder) can be pre-counter-rotated.</summary>
    private readonly int _deviceRotation;
    /// <summary>Current image path in the dialog (not yet cropped/rotated on disk —
    /// for GIFs it stays the original file, for static images it's the source loaded
    /// into the CropEditor).</summary>
    private string? _pendingPath;
    /// <summary>Original image path when the dialog opened (to detect changes).</summary>
    private readonly string? _originalPath;
    /// <summary>Selected user rotation degrees (0 / 90 / 180 / 270).</summary>
    private int _rotation;

    // ---- Inline preview / crop (2026-07-05) -----------------------------
    // CropEditor now handles BOTH static images and animated GIFs internally (animated
    // preview + crop via a CroppedGifRef sidecar for GIFs — see CropEditor remarks), so no
    // separate GifPreview control is needed here any more.
    private readonly CropEditor _cropEditor;
    private readonly RotateTransform _previewRotate = new(0);

    private const string CacheDir_UserRotated =
        "K2.DisplayPad\\user_rotated";

    // =====================================================================
    // Constructor
    // =====================================================================

    public DpKeyConfigDialog(
        int keyIndex,
        string? currentImagePath,
        string? currentActionType,
        string? currentActionValue,
        int deviceRotation = 0)
    {
        InitializeComponent();

        _keyIndex       = keyIndex;
        _deviceRotation = deviceRotation;
        _pendingPath = currentImagePath;
        _originalPath = currentImagePath;
        ActionType   = currentActionType;
        ActionValue  = currentActionValue;

        LblHeader.Text = $"Key #{keyIndex}  —  Configure";

        // Inline crop editor — handles static images AND animated GIFs (animateGifs: true),
        // toggled via Visibility only (never reparented while "live").
        _cropEditor = new CropEditor(DpHidNative.IconSize, DpHidNative.IconSize, maxViewportPx: 170, animateGifs: true);
        _cropEditor.ViewportBorder.LayoutTransform = _previewRotate;
        _cropEditor.SetKeyGrid(1, 1);   // single-key rounded-corner outline hint

        PreviewHost.Children.Add(_cropEditor.ViewportBorder);
        PreviewHost.Children.Add(_cropEditor.ControlsPanel);

        RefreshImagePreview();
        RefreshActionSummary();
        UpdateRotationAvailability();
    }

    // =====================================================================
    // Image section
    // =====================================================================

    private void BtnLoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Choose image for key",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _pendingPath = dlg.FileName;

        _rotation    = 0;
        Rb0.IsChecked = true;       // reset rotation selector to 0°
        _previewRotate.Angle = 0;
        RefreshImagePreview();      // crop editor (static) or animated preview (GIF)
        UpdateRotationAvailability();
    }

    private void BtnRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _pendingPath = null;
        RefreshImagePreview();
    }

    /// <summary>
    /// Opens the shared "insert text" editor (<see cref="TextIconDialog"/>): plain text
    /// on a solid color, or overlaid on the image currently loaded in this dialog. If the
    /// base image being overlaid is itself an auto-generated exec/folder icon (already
    /// counter-rotated for the device, see <see cref="TryAutoGenerateKeyImage"/>), the
    /// composited result is promoted into the same auto-icon cache so it keeps being
    /// recognized as pre-rotated — otherwise <c>EffectiveDpRotation</c> in
    /// <c>MainWindow.DisplayPad.cs</c> would treat it as a normal image and apply the
    /// device counter-rotation a second time on upload, over-rotating the tile.
    /// </summary>
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        bool baseWasPreRotated = IsAutoIcon(_pendingPath);

        var dlg = new TextIconDialog(DpHidNative.IconSize, _pendingPath) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _pendingPath = baseWasPreRotated && dlg.NewImagePath is not null
            ? PromoteToAutoIconCache(dlg.NewImagePath)
            : dlg.NewImagePath;
        _rotation       = 0;
        Rb0.IsChecked   = true;
        _previewRotate.Angle = 0;
        RefreshImagePreview();
        UpdateRotationAvailability();
    }

    private void RotRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out int degrees)) return;
        _rotation = degrees;
        _previewRotate.Angle = _rotation;    // visually rotates the preview (crop or GIF)
    }

    /// <summary>
    /// User rotation bakes the rotated result into a single cached PNG (see
    /// <see cref="ApplyUserRotation"/>) — fine for a static image, but PNG can't hold
    /// multiple frames, so doing this to an animated GIF would silently freeze it on one
    /// frame. Disable the rotation picker for animated GIFs instead (device-rotation from
    /// DisplayPad orientation still applies normally, see DpGifAnimator remarks).
    /// </summary>
    private void UpdateRotationAvailability()
    {
        bool isAnimated = !string.IsNullOrEmpty(_pendingPath) && DpGifAnimator.IsAnimatedGif(_pendingPath);
        Rb0.IsEnabled = Rb90.IsEnabled = Rb180.IsEnabled = Rb270.IsEnabled = !isAnimated;
        if (isAnimated)
        {
            _rotation = 0;
            Rb0.IsChecked = true;
            _previewRotate.Angle = 0;
        }
    }

    /// <summary>
    /// Shows: "no image" placeholder, or the inline <see cref="CropEditor"/> (which handles
    /// both a static image and an animated GIF preview internally).
    /// </summary>
    private void RefreshImagePreview()
    {
        bool hasImage = !string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath);
        LblNoImage.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;

        if (!hasImage)
        {
            _cropEditor.ViewportBorder.Visibility = Visibility.Collapsed;
            _cropEditor.ControlsPanel.Visibility  = Visibility.Collapsed;
            _cropEditor.Clear();
            return;
        }

        _cropEditor.ViewportBorder.Visibility = Visibility.Visible;
        _cropEditor.ControlsPanel.Visibility  = Visibility.Visible;
        if (!_cropEditor.Load(_pendingPath!))
        {
            // unreadable file — fall back to the "no image" placeholder
            _cropEditor.ViewportBorder.Visibility = Visibility.Collapsed;
            _cropEditor.ControlsPanel.Visibility  = Visibility.Collapsed;
            LblNoImage.Text = "Cannot load image";
            LblNoImage.Visibility = Visibility.Visible;
        }
    }

    // =====================================================================
    // Action section
    // =====================================================================

    private void BtnConfigureAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ButtonActionDialog(_keyIndex, ActionType, ActionValue, (Owner as MainWindow)?._dpActionHost) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        string? oldType = ActionType, oldValue = ActionValue;
        ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                      ? null : dlg.ActionType;
        ActionValue = ActionType is null ? null : dlg.ActionValue;

        if (ActionType != oldType || ActionValue != oldValue)
            TryAutoGenerateKeyImage();

        RefreshActionSummary();
    }

    /// <summary>
    /// When the action just assigned/changed is "exec" or "folder", auto-generate the
    /// key's picture (the executable's own icon, or a folder glyph + name) instead of
    /// requiring the user to manually pick an image — mirrors <see cref="BtnLoadImage_Click"/>
    /// but with a generated source instead of a user-picked file. The result is saved under
    /// <see cref="MainWindow.DpAutoIconDir"/>, which every upload path in <c>MainWindow.DisplayPad.cs</c>
    /// recognizes (via <c>EffectiveDpRotation</c>) to skip the device counter-rotation it
    /// already has baked in — otherwise it would get rotated a second time on every reload.
    /// </summary>
    private void TryAutoGenerateKeyImage()
    {
        if (string.IsNullOrWhiteSpace(ActionValue)) return;
        if (ActionType != "exec" && ActionType != "folder") return;

        string dest = AutoIconCachePath(ActionType!, ActionValue!, _deviceRotation);
        bool ok = ActionType == "exec"
            ? IconImageGenerator.TryGenerateExecIcon(ActionValue!, DpHidNative.IconSize, dest, _deviceRotation)
            : IconImageGenerator.TryGenerateFolderIcon(ActionValue!, DpHidNative.IconSize, dest, _deviceRotation);
        if (!ok) return;

        _pendingPath    = dest;
        _rotation     = 0;
        Rb0.IsChecked = true;
        _previewRotate.Angle = 0;
        RefreshImagePreview();
        UpdateRotationAvailability();
    }

    private static string AutoIconCachePath(string kind, string sourceValue, int deviceRotation)
    {
        Directory.CreateDirectory(AutoIconCacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(sourceValue).Ticks; } catch { } }
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}|r{deviceRotation}"));
        return Path.Combine(AutoIconCacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }

    /// <summary>Matches <c>MainWindow.DpAutoIconDir</c> exactly.</summary>
    private static readonly string AutoIconCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2.DisplayPad", "auto_icons");

    private static bool IsAutoIcon(string? path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(AutoIconCacheRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies a composited image (e.g. text over an already pre-rotated auto-icon)
    /// into the auto-icon cache so it keeps being recognized as pre-rotated.</summary>
    private static string PromoteToAutoIconCache(string sourcePath)
    {
        Directory.CreateDirectory(AutoIconCacheRoot);
        string dest = Path.Combine(AutoIconCacheRoot, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, dest, overwrite: true);
        return dest;
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
            "keys"    => $"Keys: {val}",
            "exec"    => $"Run: {Path.GetFileName(val)}",
            "folder"  => $"Folder: {val}",
            "url"     => $"URL: {val}",
            "browser" => $"Browser: {val}",
            "profile" => $"Profile: {val}",
            "oscmd"   => $"Shell: {val}",
            "media"   => $"Media: {val}",
            "mouse"   => $"Mouse: {val}",
            "text"    => $"Text: {val}",
            "command" => $"Command: {val}",
            "macro"   => $"Macro: {val}",
            "pyscript"=> "Python script",
            _         => $"{ActionType}: {val}",
        };
    }

    // =====================================================================
    // OK / Cancel
    // =====================================================================

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // Bake in whatever crop/zoom (or "no crop" as-is stretch) is set in the inline
        // editor — works for both static images (a cropped PNG) and animated GIFs (a
        // CroppedGifRef sidecar — see CropEditor remarks).
        string? finalPath = _pendingPath;
        if (!string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath))
            finalPath = _cropEditor.GetResultPath() ?? _pendingPath;

        // Rotation still isn't supported for GIFs (see UpdateRotationAvailability — the
        // radios are disabled whenever _pendingPath is animated, so _rotation stays 0 in
        // that case already; this is just a defensive re-check on the FINAL path).
        bool isGif = DpGifAnimator.IsAnimatedGif(finalPath);

        // Apply user rotation to the image if needed
        if (!isGif && !string.IsNullOrEmpty(finalPath) && File.Exists(finalPath) && _rotation != 0)
        {
            try
            {
                string rotated = ApplyUserRotation(finalPath, _rotation);
                NewImagePath  = rotated;
                ImageChanged  = true;
            }
            catch
            {
                // rotation failed: use the unrotated result
                NewImagePath = finalPath;
                ImageChanged = finalPath != _originalPath;
            }
        }
        else
        {
            NewImagePath = finalPath;   // null = remove, string = unchanged or new
            ImageChanged = finalPath != _originalPath;
        }

        DialogResult = true;
    }

    // =====================================================================
    // Image rotation helper (GDI+ via WinForms transitive dependency)
    // =====================================================================

    /// <summary>
    /// Rotates the source image and saves the result to a local cache.
    /// Uses <c>System.Drawing</c> (available via the csproj's UseWindowsForms).
    /// Returns the rotated file's path (from cache if already present).
    /// </summary>
    private static string ApplyUserRotation(string sourcePath, int degrees)
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheDir_UserRotated);
        Directory.CreateDirectory(cacheRoot);

        // Cache key: path + mtime + rotation degrees (avoids collisions and
        // auto-invalidates when the source image is updated).
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(sourcePath).Ticks; } catch { }
        byte[] hashBytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourcePath}|{mtime}|ur{degrees}"));
        string name = Convert.ToHexString(hashBytes).ToLowerInvariant() + $"_ur{degrees}.png";
        string dest = Path.Combine(cacheRoot, name);
        if (File.Exists(dest)) return dest;

        // Read into a MemoryStream so the source file isn't locked.
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
