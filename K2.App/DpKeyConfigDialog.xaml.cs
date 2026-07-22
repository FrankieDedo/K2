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
        string? currentActionValue)
    {
        InitializeComponent();

        _keyIndex       = keyIndex;
        _pendingPath = currentImagePath;
        _originalPath = currentImagePath;
        ActionType   = currentActionType;
        ActionValue  = currentActionValue;

        LblHeader.Text = $"Key #{keyIndex}  —  Configure";

        // Inline crop editor — handles static images AND animated GIFs (animateGifs: true),
        // toggled via Visibility only (never reparented while "live").
        _cropEditor = new CropEditor(DpHidNative.IconSize, DpHidNative.IconSize, maxViewportPx: 170,
            animateGifs: true, bakeRoundedCorners: true);
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
    /// on a solid color, or overlaid on the image currently loaded in this dialog.
    /// </summary>
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextIconDialog(DpHidNative.IconSize, _pendingPath) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _pendingPath = dlg.NewImagePath;
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

        if (ActionType == "dp_folder") _dpFolderName = dlg.ResolvedPageName;

        // A page rename keeps ActionType/ActionValue unchanged (same page id) but still
        // needs the icon's caption regenerated — dlg.PageIconNeedsRefresh is how the
        // "Page" action type surfaces that (see ButtonActionDialog.Page.cs).
        if (ActionType != oldType || ActionValue != oldValue)
            TryAutoGenerateKeyImage(dlg.ResolvedPageName);
        else if (ActionType == "dp_folder" && dlg.PageIconNeedsRefresh)
            TryAutoGenerateKeyImage(dlg.ResolvedPageName);

        RefreshActionSummary();
    }

    /// <summary>Page name resolved the last time the "Page" action type was configured in
    /// this dialog session — used by <see cref="RefreshActionSummary"/> since <see cref="ActionValue"/>
    /// for "dp_folder" is just the page id, not a human-readable name.</summary>
    private string? _dpFolderName;

    /// <summary>
    /// When the action just assigned/changed is "exec", "folder" or "dp_folder" (a
    /// DisplayPad page), auto-generate the key's picture (the executable's own icon, a
    /// disk folder's own Windows icon, or a hand-drawn folder glyph + page name) instead
    /// of requiring the user to manually pick an image — mirrors <see cref="BtnLoadImage_Click"/>
    /// but with a generated source instead of a user-picked file. Generated upright, like
    /// any other image; the device's mounting rotation is applied at upload time same as
    /// everything else (see <c>MainWindow.DisplayPad.cs</c>'s upload paths).
    /// </summary>
    /// <param name="pageName">Resolved page name (see <c>ButtonActionDialog.ResolvedPageName</c>) —
    /// only used/required when <see cref="ActionType"/> is "dp_folder".</param>
    private void TryAutoGenerateKeyImage(string? pageName = null)
    {
        if (string.IsNullOrWhiteSpace(ActionValue)) return;
        if (ActionType != "exec" && ActionType != "folder" && ActionType != "dp_folder") return;

        string dest = AutoIconCachePath(ActionType!, ActionValue!);
        bool ok = ActionType switch
        {
            "exec"      => IconImageGenerator.TryGenerateExecIcon(ActionValue!, DpHidNative.IconSize, dest),
            "folder"    => IconImageGenerator.TryGenerateDiskFolderIcon(ActionValue!, DpHidNative.IconSize, dest),
            "dp_folder" => IconImageGenerator.TryGenerateFolderIcon(pageName ?? ActionValue!, DpHidNative.IconSize, dest),
            _           => false,
        };
        if (!ok) return;

        _pendingPath    = dest;
        _rotation     = 0;
        Rb0.IsChecked = true;
        _previewRotate.Angle = 0;
        RefreshImagePreview();
        UpdateRotationAvailability();
    }

    private static string AutoIconCachePath(string kind, string sourceValue)
    {
        Directory.CreateDirectory(AutoIconCacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(sourceValue).Ticks; } catch { } }
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}"));
        return Path.Combine(AutoIconCacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }

    /// <summary>Matches <c>MainWindow.DpAutoIconDir</c> exactly.</summary>
    private static readonly string AutoIconCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "K2.DisplayPad", "auto_icons");

    /// <summary>Removing the action also clears the key's picture — a picture with no
    /// action behind it is just a stale, misleading tile (this covers both auto-generated
    /// and manually-loaded images alike, same as removing the action from the context
    /// menu directly — see <c>MainWindow.DisplayPad.cs</c>'s <c>DpMnuRemoveAction_Click</c>).</summary>
    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        ActionType  = null;
        ActionValue = null;
        _pendingPath = null;
        RefreshImagePreview();
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
            "dp_folder" => $"Page: {_dpFolderName ?? val}",
            "url"     => $"URL: {val}",
            "browser" => $"Browser: {val}",
            "profile" => $"Profile: {val}",
            "oscmd"   => $"Shell: {val}",
            "media"   => $"Media: {val}",
            "mouse"   => $"Mouse: {val}",
            "text"    => $"Text: {val}",
            "command" => $"Command: {val}",
            "macro"   => ActionTypeHelper.MacroSummary(val),
            "pyscript"=> "Python script",
            _         => ActionTypeHelper.IsUnrecognized(ActionType) ? Loc.Get("act_unrecognized") : $"{ActionType}: {val}",
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
