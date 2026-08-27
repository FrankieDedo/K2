using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Unified dialog to configure an Everest numpad display key: image + action together,
/// opened with a single click.
///
/// 2026-08-24 rework (user request: "the same interface for the Everest display keys"):
/// this is now a straight mirror of <see cref="DpKeyConfigDialog"/> — a "Default icon"
/// checkbox drives the picture (while checked it is regenerated from the key's ACTION on
/// every change, so "Load image"/"Remove image" are disabled), the inline preview is
/// view-only, and every crop/zoom/rotation/text control lives behind "Edit icon"
/// (<see cref="TextIconDialog"/>), which borrows this dialog's own <see cref="CropEditor"/>
/// for the duration. Choices are persisted as a <see cref="KeyIconSpec"/> through
/// <see cref="IconSpecJson"/>, so reopening resumes from the same settings.
/// </summary>
public partial class NdkKeyConfigDialog : Window
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
    /// <summary>Icon settings to persist for this key (<see cref="KeyIconSpec"/> JSON), so the
    /// next "configure" starts from them. Null once the key has no picture at all.</summary>
    public string? IconSpecJson { get; private set; }

    // ---- State ----------------------------------------------------------
    private readonly int _keyIndex;
    private readonly IActionHost? _host;
    /// <summary>Current image path in the dialog (not yet cropped/rotated on disk — it's the
    /// source loaded into the CropEditor).</summary>
    private string? _pendingPath;
    /// <summary>Original image path when the dialog opened (to detect changes).</summary>
    private readonly string? _originalPath;
    /// <summary>Selected user rotation degrees (0 / 90 / 180 / 270).</summary>
    private int _rotation;
    /// <summary>Live icon settings — seeded from what was persisted for this key, updated by
    /// every control in the Icon section, written back out through <see cref="IconSpecJson"/>.</summary>
    private KeyIconSpec _spec;
    /// <summary>Guards the Checked/Unchecked handlers while the constructor sets the controls
    /// up from <see cref="_spec"/> (WPF fires them synchronously during initialization).</summary>
    private bool _loadingUi = true;

    // ---- Inline preview / crop ------------------------------------------
    private readonly CropEditor _cropEditor;
    private readonly RotateTransform _previewRotate = new(0);

    private const int IconSize = 72;

    private const string CacheDir_UserRotated = "K2.App\\user_rotated";

    // =====================================================================
    // Constructor
    // =====================================================================

    public NdkKeyConfigDialog(
        int keyIndex,
        string? currentImagePath,
        string? currentActionType,
        string? currentActionValue,
        IActionHost? host = null,
        string? iconSpecJson = null)
    {
        InitializeComponent();

        _keyIndex     = keyIndex;
        _host         = host;
        _pendingPath  = currentImagePath;
        _originalPath = currentImagePath;
        ActionType    = currentActionType;
        ActionValue   = currentActionValue;

        // No stored settings = a key from before this dialog existed (or an imported one):
        // a picture already on it was put there by hand as far as we know, so DON'T claim it
        // as a default icon and silently overwrite it on open.
        _spec = KeyIconSpec.FromJson(iconSpecJson)
                ?? new KeyIconSpec { DefaultIcon = string.IsNullOrEmpty(currentImagePath) };

        LblHeader.Text = $"Display Key {keyIndex + 1}  —  Configure";

        // View-only inline preview; the SAME instance is reparented into "Edit icon" for
        // interactive drag/zoom while that dialog is open — see BtnAddText_Click.
        _cropEditor = new CropEditor(IconSize, IconSize, maxViewportPx: 170,
            bakeRoundedCorners: true, showLegacyToggles: false);
        _cropEditor.ViewportBorder.LayoutTransform = _previewRotate;
        _cropEditor.ViewportBorder.IsHitTestVisible = false;
        _cropEditor.SetKeyGrid(1, 1);   // single-key rounded-corner outline hint

        PreviewHost.Children.Add(_cropEditor.ViewportBorder);

        ChkDefaultIcon.IsChecked    = _spec.DefaultIcon;

        _loadingUi = false;

        // A default icon is regenerated on open so it always matches the action it belongs to
        // (the action can have been changed elsewhere since the picture was rendered).
        if (_spec.DefaultIcon) RegenerateDefaultIcon();

        // Restored AFTER any regeneration, which resets rotation to 0° along with the picture
        // it just replaced.
        SetRotation(_spec.Rotation);

        RefreshImagePreview();
        RefreshActionSummary();
        UpdateIconControlsAvailability();
    }

    // =====================================================================
    // Icon section — "Default icon" checkbox and control availability
    // =====================================================================

    /// <summary>
    /// Checked: the key's picture belongs to its action and is regenerated from it (loading or
    /// removing a picture by hand would immediately contradict that, so both are disabled).
    /// Unchecked: back to a manually managed picture.
    /// </summary>
    private void ChkDefaultIcon_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;

        _spec.DefaultIcon = ChkDefaultIcon.IsChecked == true;
        if (_spec.DefaultIcon)
        {
            // Re-checking starts over from the action's own icon: any caption the user typed
            // for the previous (hand-made) picture is meaningless against a fresh glyph.
            _spec.Text = null;
            RegenerateDefaultIcon();
            RefreshImagePreview();
        }
        UpdateIconControlsAvailability();
    }

    private void UpdateIconControlsAvailability()
    {
        bool isDefault = _spec.DefaultIcon;

        BtnLoadImage.IsEnabled   = !isDefault;
        BtnRemoveImage.IsEnabled = !isDefault;

        // "Edit icon" stays enabled either way — for a default icon it edits the caption/font/
        // colors/icon-source the generator honours, for a custom one the full text tile.
        BtnEditIcon.IsEnabled = true;
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

        _pendingPath = dlg.FileName;
        _spec.Text = null;          // freshly loaded image has no caption of its own yet
        _spec.DefaultIcon = false;
        ChkDefaultIcon.IsChecked = false;

        SetRotation(0);
        RefreshImagePreview();      // crop/zoom now happens inside "Edit icon"
        UpdateIconControlsAvailability();
    }

    private void BtnRemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _pendingPath = null;
        _spec.Text = null;
        RefreshImagePreview();
    }

    /// <summary>
    /// "Edit icon" — the same <see cref="TextIconDialog"/> in both flavours, differing only in
    /// what it starts FROM (see the equivalent note in <c>DpKeyConfigDialog.BtnAddText_Click</c>):
    /// - <b>default icon</b>: generator mode — the key's own default tile is re-rendered for the
    ///   settings being edited (<see cref="RenderDefaultIcon"/>), so only caption text, font,
    ///   background and text color change;
    /// - <b>custom icon</b>: the free-form text tile, with this dialog's crop editor borrowed
    ///   for interactive drag/zoom.
    /// </summary>
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        if (_spec.DefaultIcon)
        {
            if (string.IsNullOrEmpty(ActionType))
            {
                MessageBox.Show(this, Loc.Get("act_none"), Loc.Get("dp_icon_section"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var seed = _spec.Clone();
            seed.Text ??= AutoCaption();   // prefill with the caption the generator would draw
            var defDlg = new TextIconDialog(IconSize, null, seed,
                s => RenderDefaultIcon(s), initialRotation: _rotation) { Owner = this };
            if (defDlg.ShowDialog() != true) return;

            _spec = defDlg.ResultSpec;
            _spec.DefaultIcon = true;
            _pendingPath = defDlg.NewImagePath;
            SetRotation(defDlg.ResultRotation);
            RefreshImagePreview();
            return;
        }

        // ---- custom icon ------------------------------------------------
        // An already-captioned tile starts from a clean solid background ("erase and rewrite"
        // instead of stacking new text on the old one); a caption-less image keeps the
        // original "overlay text on the current picture" flow, based on the CROPPED/baked icon
        // (what the key will actually show) rather than the raw source.
        string? textBaseImage = _spec.Text is not null
            ? null
            : _cropEditor.HasImage
                ? _cropEditor.GetResultPath() ?? _pendingPath
                : _pendingPath;

        // Lend the crop viewport + zoom slider to "Edit icon" for the duration (interactive
        // there), then take them back here (view-only) once it closes, OK or Cancel alike.
        bool showCropUi = _cropEditor.HasImage;
        FrameworkElement? viewport = null, controls = null;
        EventHandler? onCropChanged = null;
        if (showCropUi)
        {
            viewport = _cropEditor.ViewportBorder;
            controls = _cropEditor.ControlsPanel;
            PreviewHost.Children.Remove(viewport);
            viewport.IsHitTestVisible = true;
        }

        var dlg = new TextIconDialog(IconSize, textBaseImage, _spec, null,
            cropViewport: viewport, cropControls: controls,
            onResetIcon: showCropUi ? _cropEditor.ResetView : null,
            initialRotation: _rotation,
            getCroppedImagePath: showCropUi ? _cropEditor.GetResultPath : null)
        { Owner = this };

        if (showCropUi)
        {
            onCropChanged = (_, _) => dlg.RefreshIconPreview();
            _cropEditor.Changed += onCropChanged;
        }

        bool ok;
        try
        {
            ok = dlg.ShowDialog() == true;
        }
        finally
        {
            if (showCropUi)
            {
                _cropEditor.Changed -= onCropChanged;
                viewport!.IsHitTestVisible = false;
                PreviewHost.Children.Add(viewport);
            }
        }
        if (!ok) return;

        _spec = dlg.ResultSpec;
        _spec.DefaultIcon = false;
        _pendingPath = dlg.NewImagePath;
        SetRotation(dlg.ResultRotation);
        RefreshImagePreview();
    }

    /// <summary>Keeps the rotation state and the (view-only) preview's transform together —
    /// every path that swaps the picture underneath starts back at 0°. The rotation PICKER
    /// itself lives in "Edit icon"; this dialog only remembers the last value chosen there.</summary>
    private void SetRotation(int degrees)
    {
        _rotation = degrees;
        _previewRotate.Angle = degrees;
    }

    /// <summary>Shows the "no image" placeholder, or the view-only <see cref="CropEditor"/>
    /// viewport.</summary>
    private void RefreshImagePreview()
    {
        bool hasImage = !string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath);
        LblNoImage.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;

        if (!hasImage)
        {
            _cropEditor.ViewportBorder.Visibility = Visibility.Collapsed;
            _cropEditor.Clear();
            return;
        }

        _cropEditor.ViewportBorder.Visibility = Visibility.Visible;
        if (!_cropEditor.Load(_pendingPath!))
        {
            // unreadable file — fall back to the "no image" placeholder
            _cropEditor.ViewportBorder.Visibility = Visibility.Collapsed;
            LblNoImage.Text = "Cannot load image";
            LblNoImage.Visibility = Visibility.Visible;
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

        if ((ActionType != oldType || ActionValue != oldValue) && _spec.DefaultIcon)
        {
            // A caption typed for the PREVIOUS action doesn't describe the new one.
            _spec.Text = null;
            RegenerateDefaultIcon();
            RefreshImagePreview();
        }

        RefreshActionSummary();
    }

    /// <summary>Removing the action also clears the key's picture — see the equivalent
    /// note in <c>DpKeyConfigDialog.BtnRemoveAction_Click</c>.</summary>
    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        ActionType  = null;
        ActionValue = null;
        _pendingPath = null;
        _spec.Text = null;
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
        LblActionSummary.Text = ActionTypeHelper.Summary(ActionType, ActionValue);
    }

    // =====================================================================
    // Default icon generation
    // =====================================================================

    /// <summary>Regenerates the default icon for the CURRENT action with the CURRENT settings
    /// and points the preview at it. No-op (keeps whatever was there) when the action has no
    /// generator.</summary>
    private void RegenerateDefaultIcon()
    {
        string? generated = RenderDefaultIcon(_spec);
        if (generated is null) return;
        _pendingPath = generated;
        SetRotation(0);
    }

    /// <summary>
    /// Renders the key's default icon — the picture that belongs to its ACTION (the executable's
    /// own icon, a disk folder's Windows icon, ported Base Camp gallery art, or the MDL2
    /// fallback tile) — styled with <paramref name="spec"/> via <see cref="IconStyleScope"/>.
    /// Returns the generated PNG's path, or null when this action type has no default icon.
    /// This is also the delegate <see cref="TextIconDialog"/> calls in "default icon" mode, so
    /// its live preview is the real tile, rendered by the real generator.
    /// </summary>
    private string? RenderDefaultIcon(KeyIconSpec spec)
    {
        if (string.IsNullOrEmpty(ActionType)) return null;

        // Only the generators that DRAW FROM the value need one; every other type can still
        // get its glyph tile from ActionIconFallback with an empty value (e.g. "disable").
        bool needsValue = ActionType is "exec" or "folder" or "googlehome" or "emoji";
        if (needsValue && string.IsNullOrWhiteSpace(ActionValue)) return null;

        bool showCaption = spec.ShowText;
        // NB: the user's own caption (typed in "Edit icon") reaches the generators through
        // IconStyleScope.OverrideCaption below, not as an argument — unlike DpKeyConfigDialog,
        // none of the action types handled here derive their caption from a literal instead.

        // Cache key includes the style, so two styled variants of the same action can't
        // collide on one cached PNG.
        string dest = AutoIconCachePath(ActionType!, $"{ActionValue ?? ""}|{spec.StyleFingerprint}");

        bool ok;
        using (IconStyleScope.Push(spec))
        {
            switch (ActionType)
            {
                case "exec":
                    ok = IconImageGenerator.TryGenerateExecIcon(ActionValue!, IconSize, dest);
                    break;
                case "folder":
                    ok = IconImageGenerator.TryGenerateDiskFolderIcon(ActionValue!, IconSize, dest, showCaption);
                    break;
                case "googlehome":
                    ok = GoogleHomeIconCatalog.TryGenerateKeyIcon(ActionValue!, IconSize, dest, showCaption);
                    break;
                case "emoji":
                    ok = EmojiGlyphRenderer.TryGenerateEmojiIcon(ActionValue!, IconSize, dest);
                    break;
                default:
                    // Base Camp's ported gallery art vs. K2's hand-drawn glyph — spec.UseK2Icons
                    // (the "Edit icon" radio pair) picks which one wins the tie; whichever side
                    // has no art for this action/value falls back to the other automatically.
                    ok = spec.UseK2Icons
                        ? ActionIconFallback.TryGenerate(ActionType!, ActionValue, IconSize, dest, showCaption)
                          || IconGalleryDefaults.TryGenerateKeyIcon(ActionType!, ActionValue, IconSize, dest)
                        : IconGalleryDefaults.TryGenerateKeyIcon(ActionType!, ActionValue, IconSize, dest)
                          || ActionIconFallback.TryGenerate(ActionType!, ActionValue, IconSize, dest, showCaption);
                    break;
            }
        }
        return ok ? dest : null;
    }

    /// <summary>The caption a default icon carries when the user hasn't typed one — what
    /// "Edit icon" prefills its text box with, so editing starts from the real wording instead
    /// of an empty box that would silently wipe the caption.</summary>
    private string? AutoCaption() => ActionType switch
    {
        "folder"          => string.IsNullOrWhiteSpace(ActionValue)
                             ? null : IconImageGenerator.GetDiskFolderCaption(ActionValue!),
        "googlehome"      => GoogleHomeIconCatalog.CaptionFor(ActionValue) ?? ActionValue,
        "exec" or "emoji" => null,   // these tiles never draw a caption
        _                 => ActionIconFallback.Caption(ActionType, ActionValue),
    };

    private static string AutoIconCachePath(string kind, string sourceValue)
    {
        string cacheRoot = Path.Combine(K2Paths.For("K2.App"), "auto_icons");
        Directory.CreateDirectory(cacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(ExecActionPayload.PathOf(sourceValue)).Ticks; } catch { } }
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}|{IconSize}"));
        return Path.Combine(cacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }

    // =====================================================================
    // OK / Cancel
    // =====================================================================

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // Bake in whatever crop/zoom was set in "Edit icon".
        string? finalPath = _pendingPath;
        if (!string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath))
            finalPath = _cropEditor.GetResultPath() ?? _pendingPath;

        if (!string.IsNullOrEmpty(finalPath) && File.Exists(finalPath) && _rotation != 0)
        {
            try
            {
                NewImagePath = DpKeyConfigDialog.ApplyUserRotation(finalPath, _rotation, CacheDir_UserRotated);
                ImageChanged = true;
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

        _spec.Rotation = _rotation;
        // No picture left = nothing to remember about how it looked.
        IconSpecJson = NewImagePath is null ? null : _spec.ToJson();

        DialogResult = true;
    }
}
