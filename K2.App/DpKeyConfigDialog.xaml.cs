using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Unified dialog to configure a DisplayPad key: action + picture (loading, crop/zoom,
/// rotation, text) — all in the SAME window.
///
/// 2026-08-24 rework (user request): the picture is now driven by a "Default icon" checkbox,
/// on by default. While it is checked the icon is (re)generated from the key's ACTION on every
/// change — "Load image"/"Remove image" are disabled, because a hand-picked picture and
/// "always match the action" are mutually exclusive; unchecking it hands the key back to the
/// old manual flow. Every choice made here (default-icon flag, caption text, font, colors,
/// rotation) is persisted as a <see cref="KeyIconSpec"/> next to the key's action, so
/// reopening this dialog resumes from the same settings instead of only inheriting the
/// rendered PNG.
///
/// "User" rotation (this dialog) and "device" rotation (satellite) are independent: user
/// rotation is applied to the image saved to disk, device rotation is applied at upload time
/// via <c>ResolveForUpload</c> in the satellite.
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
    /// <summary>Icon settings to persist for this key (<see cref="KeyIconSpec"/> JSON), so the
    /// next "configure" starts from them. Null once the key has no picture at all.</summary>
    public string? IconSpecJson { get; private set; }

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
    /// <summary>Live icon settings — seeded from what was persisted for this key, updated by
    /// every control in the Icon section, written back out through <see cref="IconSpecJson"/>.</summary>
    private KeyIconSpec _spec;
    /// <summary>Guards the Checked/Unchecked handlers while the constructor sets the controls
    /// up from <see cref="_spec"/> (WPF fires them synchronously during initialization).</summary>
    private bool _loadingUi = true;

    // ---- Inline preview / crop ------------------------------------------
    // CropEditor handles BOTH static images and animated GIFs internally (animated preview +
    // crop via a CroppedGifRef sidecar for GIFs — see CropEditor remarks).
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
        string? iconSpecJson = null)
    {
        InitializeComponent();

        _keyIndex     = keyIndex;
        _pendingPath  = currentImagePath;
        _originalPath = currentImagePath;
        ActionType    = currentActionType;
        ActionValue   = currentActionValue;

        // No stored settings = a key from before this dialog existed (or an imported one):
        // a picture already on it was put there by hand as far as we know, so DON'T claim it
        // as a default icon and silently overwrite it on open.
        _spec = KeyIconSpec.FromJson(iconSpecJson)
                ?? new KeyIconSpec { DefaultIcon = string.IsNullOrEmpty(currentImagePath) };

        LblHeader.Text = $"Key #{keyIndex}  —  Configure";

        // Inline crop editor — handles static images AND animated GIFs (animateGifs: true).
        // 2026-08-24 rework: this dialog's own copy is VIEW-ONLY now (no drag/zoom, no
        // "insert as-is"/"show key outline" toggles — both removed from this popup); the SAME
        // instance is reparented into "Edit icon" (TextIconDialog) for interactive editing
        // while that dialog is open, then reparented back here — see BtnAddText_Click.
        _cropEditor = new CropEditor(DpHidNative.IconSize, DpHidNative.IconSize, maxViewportPx: 170,
            animateGifs: true, bakeRoundedCorners: true, showLegacyToggles: false);
        _cropEditor.ViewportBorder.LayoutTransform = _previewRotate;
        _cropEditor.ViewportBorder.IsHitTestVisible = false;
        _cropEditor.SetKeyGrid(1, 1);   // single-key rounded-corner outline hint

        PreviewHost.Children.Add(_cropEditor.ViewportBorder);
        // ControlsPanel (zoom slider) stays out of this popup entirely — only reparented into
        // "Edit icon" on demand.

        ChkDefaultIcon.IsChecked   = _spec.DefaultIcon;
        ChkSpotifyCover.IsChecked  = _spec.SpotifyCover;

        _loadingUi = false;

        // A default icon is regenerated on open so it always matches the action it belongs to
        // (the action can have been changed elsewhere — context menu, import, drag & drop —
        // since the picture was rendered).
        if (_spec.DefaultIcon) RegenerateDefaultIcon();

        // Restored AFTER any regeneration, which resets the picker to 0° along with the
        // picture it just replaced.
        SetRotation(_spec.Rotation);

        RefreshImagePreview();
        RefreshActionSummary();
        UpdateIconControlsAvailability();
        UpdateLiveTimer();

        // Owner is assigned by the caller AFTER the constructor returns (object-initializer
        // syntax), and resolving a "dp_folder" page id into its name needs the owner's
        // action host — so the summary (and a folder icon's caption) is refreshed once more
        // when the window is loaded.
        Loaded += (_, _) =>
        {
            // EditIconDisabled/TextStyleOnlyRenderer are assigned by the caller AFTER the
            // constructor (object-initializer syntax), so the button state is settled here.
            UpdateIconControlsAvailability();
            RefreshActionSummary();
            if (_spec.DefaultIcon && ActionType == "dp_folder")
            {
                RegenerateDefaultIcon();
                RefreshImagePreview();
            }
        };
        Closed += (_, _) => { _liveTimer?.Stop(); _liveTimer = null; };
    }

    // =====================================================================
    // Live preview (clock / PC monitor / speed test) — the config popup's
    // picture is a real render (RenderDefaultIcon), so while one of these
    // action types is selected it has to keep ticking like the key itself
    // does once assigned, or the popup shows a picture that's already stale
    // by the time "OK" is clicked.
    // =====================================================================

    /// <summary>Set by the caller for a key whose picture belongs to a live overlay service
    /// rather than to an action (today: the Spotify dedicated profile's 3 track-text tiles).
    /// Renders the tile for a candidate <see cref="KeyIconSpec"/>, and its presence switches
    /// "Edit icon" to the font+color-only popup. Null for every ordinary key.</summary>
    internal Func<KeyIconSpec, string?>? TextStyleOnlyRenderer { get; set; }

    /// <summary>Set by the caller for a key whose picture belongs to a live overlay and has
    /// NOTHING to edit — the Spotify block's cover tile, and every tile of the 4-tile layout:
    /// the picture is album art, there is no caption and no glyph. "Edit icon" is greyed out
    /// there instead of opening a popup that can only answer "none" (user report 2026-09-01).</summary>
    internal bool EditIconDisabled { get; set; }

    private DispatcherTimer? _liveTimer;

    private static bool IsLiveActionType(string? type) =>
        type is "dp_clock" or "dp_sysmon" or "dp_speedtest";

    /// <summary>Starts/stops the 1 Hz preview refresh to match whether the CURRENT action is a
    /// live type with its default icon active — called after every change that could flip
    /// either of those (action type, "Default icon" checkbox).</summary>
    private void UpdateLiveTimer()
    {
        bool shouldRun = _spec.DefaultIcon && IsLiveActionType(ActionType);

        // Warm LHM up off-thread if the preview needs it (a "PC monitor" key on CPU/GPU
        // temperature, a specific disk, or a "Choose sensor…" pick) — the resolvers return null
        // until it's up, so the preview just shows "—" for a beat instead of freezing.
        string v = ActionValue ?? "";
        if (shouldRun && ActionType == "dp_sysmon" && (v.Contains(':') || v.StartsWith('/')))
            System.Threading.Tasks.Task.Run(Services.HardwareSensors.Start);

        if (shouldRun)
        {
            if (_liveTimer is not null) return;
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += (_, _) =>
            {
                RegenerateDefaultIcon();
                RefreshImagePreview();
            };
            _liveTimer.Start();
        }
        else
        {
            _liveTimer?.Stop();
            _liveTimer = null;
        }
    }

    // =====================================================================
    // Icon section — "Default icon" checkbox and control availability
    // =====================================================================

    /// <summary>
    /// Checked: the key's picture belongs to its action and is regenerated from it (loading or
    /// removing a picture by hand would immediately contradict that, so both are disabled).
    /// Unchecked: back to a manually managed picture.
    /// </summary>
    /// <summary>"Use Spotify album cover" — on a "spotify" key, overlay the currently-playing
    /// track's cover, live (see <see cref="DpSpotifyCoverKeyService"/>). Independent of "Default
    /// icon": whatever picture the key has (generated or hand-picked) stays as the fallback for
    /// when nothing is playing / Spotify is closed.</summary>
    private void ChkSpotifyCover_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _spec.SpotifyCover = ChkSpotifyCover.IsChecked == true;
    }

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
        UpdateLiveTimer();
    }

    private void UpdateIconControlsAvailability()
    {
        bool isDefault = _spec.DefaultIcon;

        BtnLoadImage.IsEnabled   = !isDefault;
        BtnRemoveImage.IsEnabled = !isDefault;

        // "Edit icon" stays enabled either way — for a default icon it edits the caption/font/
        // colors/icon-source the generator honours, for a custom one the full text tile — unless
        // the caller says this key's picture isn't editable at all (see EditIconDisabled).
        BtnEditIcon.IsEnabled = !EditIconDisabled;

        // "Use Spotify album cover" is only meaningful on a "spotify" action. When the action
        // is something else, hide it and drop a stale flag so it can't linger on the key.
        bool spotify = ActionType == "spotify";
        ChkSpotifyCover.Visibility = spotify ? Visibility.Visible : Visibility.Collapsed;
        if (!spotify && _spec.SpotifyCover)
        {
            _spec.SpotifyCover = false;
            ChkSpotifyCover.IsChecked = false;
        }
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
        _spec.Text = null;          // freshly loaded image has no caption of its own yet
        _spec.DefaultIcon = false;
        ChkDefaultIcon.IsChecked = false;

        SetRotation(0);
        RefreshImagePreview();      // crop editor (static) or animated preview (GIF)
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
    /// what it starts FROM (user request 2026-08-24: one popup, not a different editor for
    /// default icons):
    /// - <b>default icon</b>: the dialog runs in generator mode — it re-renders the key's own
    ///   default tile for the settings being edited (<see cref="RenderDefaultIcon"/>), so the
    ///   glyph stays put and only the caption text, font, background and text color change.
    ///   Position/"on top of image" are disabled there, since that layout is the generator's;
    /// - <b>custom icon</b>: the free-form text tile — text placed anywhere, on a solid color
    ///   or over the loaded picture.
    /// </summary>
    private void BtnAddText_Click(object sender, RoutedEventArgs e)
    {
        // A key painted by a live overlay (the Spotify block's title/artist/album tiles): the
        // words are the track's and the layout is the generator's, so the only things left to
        // edit are the font and the text color. Before this, "Edit icon" on one of those keys
        // just said "none" (user report 2026-09-01).
        if (TextStyleOnlyRenderer is not null)
        {
            var styleDlg = new TextIconDialog(DpHidNative.IconSize, null, _spec.Clone(),
                s => TextStyleOnlyRenderer(s), initialRotation: 0, textStyleOnly: true) { Owner = this };
            if (styleDlg.ShowDialog() != true) return;
            _spec = styleDlg.ResultSpec;
            return;   // no picture to keep: the overlay repaints the key itself
        }

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
            // No croppable source to hand over — the generator owns the glyph's framing, so
            // the Icon section shows only the rotation picker (no crop viewport).
            var defDlg = new TextIconDialog(DpHidNative.IconSize, null, seed,
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
        // original "overlay text on the current picture" flow. For a static image that base is
        // the CROPPED/baked icon (what the key will actually show), not the raw uncropped
        // source — otherwise "on top of image" would stretch-distort it. GIFs keep using the
        // original file (no baked PNG exists for them — see CropEditor.IsGif).
        string? textBaseImage = _spec.Text is not null
            ? null
            : _cropEditor.HasImage && !_cropEditor.IsGif
                ? _cropEditor.GetResultPath() ?? _pendingPath
                : _pendingPath;

        // 2026-08-24 rework: drag/zoom and rotation moved OUT of this popup and INTO "Edit
        // icon" — reparent the SAME CropEditor viewport/controls into TextIconDialog for the
        // duration of that dialog (interactive there), then reparent them back here (view-only)
        // once it closes, whether OK or Cancel.
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

        bool isAnimated = !string.IsNullOrEmpty(_pendingPath) && DpGifAnimator.IsAnimatedGif(_pendingPath);

        var dlg = new TextIconDialog(DpHidNative.IconSize, textBaseImage, _spec, null,
            cropViewport: viewport, cropControls: controls,
            onResetIcon: showCropUi ? _cropEditor.ResetView : null,
            initialRotation: _rotation, rotationEnabled: !isAnimated,
            getCroppedImagePath: showCropUi && !_cropEditor.IsGif ? _cropEditor.GetResultPath : null)
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
        SetRotation(isAnimated ? 0 : dlg.ResultRotation);
        RefreshImagePreview();
    }

    /// <summary>Keeps the rotation state and the (view-only) preview's transform together —
    /// every path that swaps the picture underneath (load, generate, edit) starts back at 0°.
    /// The rotation PICKER itself now lives in "Edit icon" (<see cref="TextIconDialog"/>,
    /// 2026-08-24) — this dialog only remembers the last value chosen there.</summary>
    private void SetRotation(int degrees)
    {
        _rotation = degrees;
        _previewRotate.Angle = degrees;
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
        bool actionChanged = ActionType != oldType || ActionValue != oldValue;
        if (actionChanged || (ActionType == "dp_folder" && dlg.PageIconNeedsRefresh))
        {
            if (_spec.DefaultIcon)
            {
                // A caption typed for the PREVIOUS action doesn't describe the new one.
                if (actionChanged) _spec.Text = null;
                RegenerateDefaultIcon(dlg.ResolvedPageName);
                RefreshImagePreview();
            }
        }

        RefreshActionSummary();
        UpdateIconControlsAvailability();   // action may have changed to/from "spotify"
        UpdateLiveTimer();
    }

    /// <summary>Page name resolved the last time the "Page" action type was configured in
    /// this dialog session — used by <see cref="RefreshActionSummary"/> since <see cref="ActionValue"/>
    /// for "dp_folder" is just the page id, not a human-readable name.</summary>
    private string? _dpFolderName;

    /// <summary>Removing the action also clears the key's picture — a picture with no
    /// action behind it is just a stale, misleading tile (this covers both auto-generated
    /// and manually-loaded images alike, same as removing the action from the context
    /// menu directly — see <c>MainWindow.DisplayPad.cs</c>'s <c>DpMnuRemoveAction_Click</c>).</summary>
    private void BtnRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        ActionType  = null;
        ActionValue = null;
        _pendingPath = null;
        _spec.Text = null;
        RefreshImagePreview();
        RefreshActionSummary();
        UpdateLiveTimer();
    }

    private void RefreshActionSummary()
    {
        if (string.IsNullOrEmpty(ActionType) || ActionType == "none")
        {
            LblActionSummary.Text = Loc.Get("act_none");
            return;
        }

        // NB: the resolved name is NOT cached back into _dpFolderName — the first call
        // happens from the constructor, where Owner (and therefore the action host) is
        // still null, so caching the numeric fallback there would stick forever.
        LblActionSummary.Text = ActionType == "dp_folder"
            ? $"Page: {_dpFolderName ?? ResolvePageName(ActionValue) ?? ActionValue}"
            : ActionTypeHelper.Summary(ActionType, ActionValue);
    }

    /// <summary>
    /// Looks up a DisplayPad page id (the raw <see cref="ActionValue"/> of a "dp_folder"
    /// action) in the owner's page list, so a dialog OPENED on an existing page action
    /// shows the page's name rather than its bare numeric id — <see cref="_dpFolderName"/>
    /// is otherwise only filled in when the action is (re)configured in this session.
    /// </summary>
    private string? ResolvePageName(string? pageIdText)
    {
        if (!int.TryParse(pageIdText, out int pageId)) return null;
        IActionHost? host = (Owner as MainWindow)?._dpActionHost
                            ?? (Application.Current?.MainWindow as MainWindow)?._dpActionHost;
        if (host is null) return null;
        foreach (var (id, name) in host.ListPages())
            if (id == pageId) return name;
        return null;
    }

    // =====================================================================
    // Default icon generation
    // =====================================================================

    /// <summary>Regenerates the default icon for the CURRENT action with the CURRENT settings
    /// and points the preview at it. No-op (keeps whatever was there) when the action has no
    /// generator, matching the old "Default icon" button's contract.</summary>
    private void RegenerateDefaultIcon(string? pageName = null)
    {
        string? generated = RenderDefaultIcon(_spec, pageName);
        if (generated is null) return;
        _pendingPath = generated;
        SetRotation(0);
    }

    /// <summary>
    /// Renders the key's default icon — the picture that belongs to its ACTION (the executable's
    /// own icon, a disk folder's Windows icon, a hand-drawn folder/back/nav glyph, ported Base
    /// Camp gallery art, or the MDL2 fallback tile) — styled with <paramref name="spec"/>.
    ///
    /// The style (background/text color, font, caption text) reaches the generators through
    /// <see cref="IconStyleScope"/> rather than ~10 extra parameters; see that class.
    /// Returns the generated PNG's path, or null when this action type has no default icon.
    /// This is also the delegate <see cref="TextIconDialog"/> calls in "default icon" mode, so
    /// its live preview is the real tile, rendered by the real generator.
    /// </summary>
    private string? RenderDefaultIcon(KeyIconSpec spec, string? pageName = null)
    {
        if (string.IsNullOrEmpty(ActionType)) return null;

        // Caption for the two payload-less action types (see ButtonActionDialog for
        // "dp_emojibrowser"; "dp_back" is placed by DpEnsureDefaultBackButton and never
        // carries a value), which therefore have nothing to draw a caption FROM.
        string? caption = ActionType switch
        {
            "dp_emojibrowser" => Loc.Get("emb_caption"),
            "dp_back"         => Loc.Get("dp_back"),
            _                 => null,
        };
        // Only the generators that DRAW FROM the value need one; every other type can still
        // get its glyph tile from ActionIconFallback with an empty value (e.g. "disable").
        bool needsValue = ActionType is "exec" or "folder" or "dp_folder" or "googlehome" or "emoji";
        if (needsValue && string.IsNullOrWhiteSpace(ActionValue)) return null;

        // ActionValue is the bare page id — without this the tile would read "2407".
        if (ActionType == "dp_folder")
            pageName ??= _dpFolderName ?? ResolvePageName(ActionValue);

        bool showCaption = spec.ShowText;
        // The user's own caption (typed in "Edit icon") replaces whatever the generator would
        // draw; it also reaches the generators that derive their caption internally through
        // IconStyleScope.OverrideCaption.
        string? userText = string.IsNullOrWhiteSpace(spec.Text) ? null : spec.Text;

        // Cache key includes the style, so two styled variants of the same action can't
        // collide on one cached PNG.
        string dest = AutoIconCachePath(ActionType!,
            $"{caption ?? ActionValue ?? ""}|{pageName}|{spec.StyleFingerprint}");

        bool ok;
        using (IconStyleScope.Push(spec))
        {
            switch (ActionType)
            {
                case "dp_back":
                    ok = IconImageGenerator.TryGenerateBackIcon(userText ?? caption!, DpHidNative.IconSize, dest, showCaption);
                    break;
                // A real (color) emoji rather than a thin MDL2 outline, captioned just "Emoji":
                // the full action name would be ellipsized on a 102 px tile.
                case "dp_emojibrowser":
                    ok = EmojiGlyphRenderer.TryGenerateEmojiIcon(
                        "\U0001F600", DpHidNative.IconSize, dest, showCaption ? userText ?? caption! : "");
                    break;
                case "exec":
                    ok = IconImageGenerator.TryGenerateExecIcon(ActionValue!, DpHidNative.IconSize, dest);
                    break;
                case "folder":
                    ok = IconImageGenerator.TryGenerateDiskFolderIcon(ActionValue!, DpHidNative.IconSize, dest, showCaption);
                    break;
                case "dp_folder":
                    ok = IconImageGenerator.TryGenerateFolderIcon(pageName ?? ActionValue!, DpHidNative.IconSize, dest, showCaption);
                    break;
                case "googlehome":
                    ok = GoogleHomeIconCatalog.TryGenerateKeyIcon(ActionValue!, DpHidNative.IconSize, dest, showCaption);
                    break;
                case "emoji":
                    ok = EmojiGlyphRenderer.TryGenerateEmojiIcon(ActionValue!, DpHidNative.IconSize, dest);
                    break;
                // Live tiles (clock / PC monitor / speed test): what's rendered here is only the
                // PREVIEW — on the hardware these keys are repainted continuously by
                // DpLiveTileService, which owns them (see its remarks). Drawn with the real
                // renderer and the real current values, so the preview is what the key will
                // actually look like a second from now.
                case "dp_clock":
                    ok = LiveTileRenderer.TryRenderClock(ActionValue, DateTime.Now,
                            showCaption ? userText ?? "" : "", DpHidNative.IconSize, dest);
                    break;
                case "dp_sysmon":
                {
                    var (text, fraction) = DpLiveTileService.TileValue(ActionType!, ActionValue);
                    ok = LiveTileRenderer.TryRenderGauge(text, fraction,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(ActionType!, ActionValue) : "",
                            DpHidNative.IconSize, dest);
                    break;
                }
                case "dp_speedtest":
                {
                    var (text, fraction) = DpLiveTileService.TileValue(ActionType!, ActionValue);
                    bool isPing = ActionValue == "ping";
                    ok = LiveTileRenderer.TryRenderSpeedTile(text, fraction,
                            showCaption ? userText ?? DpLiveTileService.TileCaption(ActionType!, ActionValue) : "",
                            showCaption ? DpLiveTileService.SpeedTestUnit(ActionValue ?? "") : "",
                            DpHidNative.IconSize, dest,
                            ownValueSize: isPing, valueTopPad: isPing ? 0.06f : 0f);
                    break;
                }
                default:
                    // A transport/volume/repeat control — "media" or the equivalent "spotify"
                    // Web API command — always gets K2's own solid shape, bypassing the gallery
                    // tie-break entirely: icon_mapping.xml has a Base Camp row for every one of
                    // these "spotify" commands, which would otherwise win by default (UseK2Icons
                    // off) and cost the tile both its shared shape and its caption (gallery art
                    // never draws one) — user report 2026-09-01.
                    if (ActionIconFallback.IsControl(ActionType, ActionValue))
                    {
                        ok = ActionIconFallback.TryGenerate(ActionType!, ActionValue, DpHidNative.IconSize, dest, showCaption);
                        break;
                    }
                    // Everything else: Base Camp's ported gallery art vs. K2's hand-drawn glyph —
                    // spec.UseK2Icons (the "Edit icon" radio pair) picks which one wins the tie;
                    // whichever side has no art for this action/value falls back to the other.
                    ok = spec.UseK2Icons
                        ? ActionIconFallback.TryGenerate(ActionType!, ActionValue, DpHidNative.IconSize, dest, showCaption)
                          || IconGalleryDefaults.TryGenerateKeyIcon(ActionType!, ActionValue, DpHidNative.IconSize, dest)
                        : IconGalleryDefaults.TryGenerateKeyIcon(ActionType!, ActionValue, DpHidNative.IconSize, dest)
                          || ActionIconFallback.TryGenerate(ActionType!, ActionValue, DpHidNative.IconSize, dest, showCaption);
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
        "dp_emojibrowser" => Loc.Get("emb_caption"),
        "dp_back"         => Loc.Get("dp_back"),
        "folder"          => string.IsNullOrWhiteSpace(ActionValue)
                             ? null : IconImageGenerator.GetDiskFolderCaption(ActionValue!),
        "dp_folder"       => _dpFolderName ?? ResolvePageName(ActionValue) ?? ActionValue,
        "googlehome"      => GoogleHomeIconCatalog.CaptionFor(ActionValue) ?? ActionValue,
        // Live tiles: the short symbol/abbreviation the tile carries by default ("CPU", "download"),
        // so "Edit icon" starts from the real wording. A clock face has none — it needs no label.
        "dp_clock" or "dp_sysmon" or "dp_speedtest"
                          => DpLiveTileService.TileCaption(ActionType!, ActionValue) is { Length: > 0 } c ? c : null,
        "exec" or "emoji" => null,   // these tiles never draw a caption
        _                 => ActionIconFallback.Caption(ActionType, ActionValue),
    };

    private static string AutoIconCachePath(string kind, string sourceValue)
    {
        Directory.CreateDirectory(AutoIconCacheRoot);

        long mtime = 0;
        if (kind == "exec") { try { mtime = File.GetLastWriteTimeUtc(ExecActionPayload.PathOf(sourceValue)).Ticks; } catch { } }
        byte[] hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{kind}|{sourceValue}|{mtime}"));
        return Path.Combine(AutoIconCacheRoot, Convert.ToHexString(hash).ToLowerInvariant() + $"_{kind}.png");
    }

    /// <summary>Matches <c>MainWindow.DpAutoIconDir</c> exactly.</summary>
    private static readonly string AutoIconCacheRoot = Path.Combine(
        K2Paths.For("K2.DisplayPad"), "auto_icons");

    // =====================================================================
    // OK / Cancel
    // =====================================================================

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        // Bake in whatever crop/zoom was set in "Edit icon" — works for both static images
        // (a cropped PNG) and animated GIFs (a CroppedGifRef sidecar — see CropEditor remarks).
        string? finalPath = _pendingPath;
        if (!string.IsNullOrEmpty(_pendingPath) && File.Exists(_pendingPath))
            finalPath = _cropEditor.GetResultPath() ?? _pendingPath;

        // Rotation still isn't supported for GIFs ("Edit icon"'s picker passes
        // rotationEnabled: false whenever _pendingPath is animated, so _rotation stays 0 in
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

        _spec.Rotation = isGif ? 0 : _rotation;
        // No picture left = nothing to remember about how it looked — UNLESS "use Spotify cover"
        // is on, which is a picture in its own right (painted live by DpSpotifyCoverKeyService).
        IconSpecJson = (NewImagePath is null && !_spec.SpotifyCover) ? null : _spec.ToJson();

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
    /// <param name="cacheSubdir">Cache folder under <see cref="K2Paths.Root"/> to write into —
    /// defaults to this dialog's own; <c>NdkKeyConfigDialog</c> passes its own so the Everest
    /// display keys' rotated PNGs don't land in the DisplayPad's tree (2026-08-24, when that
    /// dialog was rebuilt on this one's interface).</param>
    internal static string ApplyUserRotation(string sourcePath, int degrees, string? cacheSubdir = null)
    {
        string cacheRoot = Path.Combine(K2Paths.Root, cacheSubdir ?? CacheDir_UserRotated);
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
