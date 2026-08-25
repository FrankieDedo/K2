using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// <summary>The text that was in the box when OK was clicked (empty string counts as
    /// "no text") — set only when the dialog returns true. Callers remember this to prefill
    /// the box next time the same icon's text is reopened for editing.</summary>
    public string? EnteredText { get; private set; }

    /// <summary>Every choice made in this dialog (text, font, size, colors, position, mode),
    /// so the caller can PERSIST it and reopen the dialog on the same settings later instead
    /// of only keeping the rendered PNG — see <see cref="KeyIconSpec"/>.</summary>
    public KeyIconSpec ResultSpec { get; private set; } = new();

    /// <summary>Rotation (0/90/180/270) chosen in the Icon section — set only when the dialog
    /// returns true. The caller applies it the same way it did with its own former rotation
    /// picker (now moved here, 2026-08-24).</summary>
    public int ResultRotation { get; private set; }

    private readonly int _size;
    /// <summary>Canvas size fed to <see cref="TextIconGenerator.TryRenderTextIcon"/> for the
    /// LIVE preview only — <see cref="_size"/> (72/102px, the actual device icon) once
    /// up-scaled ~2.4x by the Image control to fill the ~170px on-screen viewport, looked
    /// visibly blurry/blocky (user report 2026-08-25: "sgranata"). Matches the embedded crop
    /// viewport's own on-screen pixel size instead, so GDI+'s HighQualityBicubic does the
    /// up-scale once at render time rather than WPF doing it raw on every repaint. The actual
    /// SAVED file (<see cref="BtnOk_Click"/>) always renders at <see cref="_size"/>, unaffected.</summary>
    private readonly int _previewSize;
    private readonly string? _baseImagePath;
    /// <summary>Caller-owned "get the current cropped source" callback (typically
    /// <c>CropEditor.GetResultPath</c>) — used instead of the static <see cref="_baseImagePath"/>
    /// for "on top of image" text compositing once an interactive crop editor is embedded here,
    /// so the preview reflects drag/zoom changes live. Null falls back to the static path
    /// (callers with no crop editor to embed, e.g. <c>NdkKeyConfigDialog</c>).</summary>
    private readonly Func<string?>? _getCroppedImagePath;
    /// <summary>Caller-owned "reset pan/zoom" callback (<c>CropEditor.ResetView</c>) invoked by
    /// the "Reset icon" button — null when there's no crop editor embedded (Icon section shows
    /// no crop UI at all in that case).</summary>
    private readonly Action? _onResetIcon;
    /// <summary>Proposed background for an icon that carries no stored color of its own: pure
    /// black, the same <c>IconImageGenerator.DefaultFolderBackgroundColor</c> every generated
    /// tile clears to — so a hand-made tile sits on the same ground as the default icons next
    /// to it on the device (user request 2026-08-24). Overwritten by
    /// <see cref="ApplySpecToState"/> whenever the spec HAS a color.</summary>
    private System.Drawing.Color _bgColor = System.Drawing.Color.Black;

    /// <summary>Proposed text color, likewise only until the spec overrides it: the icon accent
    /// from Settings &gt; Accent color &gt; Icon color (<c>AppSettings.IconColorTheme</c>,
    /// falling back to the app-wide accent), matching the glyph color the generators paint
    /// their default icons with rather than a fixed white.</summary>
    private System.Drawing.Color _textColor = IconImageGenerator.TileAccent;
    private string _fontFamily = "Segoe UI";
    private bool _autoSize = true;
    private double _manualFontSize = 24;
    private TextIconGenerator.TextAnchor _anchor = TextIconGenerator.TextAnchor.MiddleCenter;
    /// <summary>"With text" / "Without text" — default-icon mode only (<see cref="DefaultIconOptionsPanel"/>);
    /// a custom icon keeps deriving <c>ShowText</c> from whether the text box is empty, same as
    /// before this pair existed.</summary>
    private bool _showText = true;
    /// <summary>"Base Camp" / "K2" — which icon set a default icon prefers; see
    /// <see cref="KeyIconSpec.UseK2Icons"/>. Ignored for a custom icon.</summary>
    private bool _useK2Icons;

    /// <summary>Non-null = "default icon" mode: the picture is not composed here at all, it is
    /// (re)generated from the key's ACTION by the caller for a given <see cref="KeyIconSpec"/>
    /// (see <c>DpKeyConfigDialog.RenderDefaultIcon</c>), and this dialog only edits the style
    /// knobs that generator honours — text, font, background and text color. Position and
    /// background-mode are fixed by the generator's own layout, so their controls are disabled
    /// (user request 2026-08-24).</summary>
    private readonly Func<KeyIconSpec, string?>? _defaultIconRenderer;

    /// <summary>Style the dialog opened on — cloned and updated into <see cref="ResultSpec"/>.</summary>
    private readonly KeyIconSpec _spec;

    /// <param name="size">Target icon size in pixels (102 for DisplayPad, 72 for Everest numpad display keys).</param>
    /// <param name="baseImagePath">Currently loaded key image, if any — enables the "on image" background mode.
    /// Pass null to force a clean solid-color start (e.g. re-editing a default icon's caption — see the
    /// callers' "Add/Edit text" handling, which erases the old caption instead of stacking on top of it).</param>
    /// <param name="initialText">Text to prefill the box with — the caption already associated with this
    /// icon (auto-generated or previously typed here), so re-opening the dialog edits it instead of starting
    /// blank.</param>
    public TextIconDialog(int size, string? baseImagePath, string? initialText = null)
        : this(size, baseImagePath, new KeyIconSpec { DefaultIcon = false, Text = initialText }, null)
    {
    }

    /// <summary>
    /// Full form: opens on a persisted <paramref name="spec"/> (so re-editing an icon starts
    /// from the settings it was built with, not from the defaults) and, when
    /// <paramref name="defaultIconRenderer"/> is given, runs in "default icon" mode — see
    /// <see cref="_defaultIconRenderer"/>.
    /// </summary>
    /// <param name="cropViewport">The caller's <c>CropEditor.ViewportBorder</c> — reparented
    /// (must already be detached from the caller's own layout) into this dialog's Icon
    /// section for interactive editing, and reparented back by the caller once this dialog
    /// closes. Null hides the Icon section's crop UI entirely (no croppable source — default
    /// icon mode, or a caption-only tile).</param>
    /// <param name="cropControls">The matching <c>CropEditor.ControlsPanel</c> (zoom slider +
    /// hint) — same reparenting contract as <paramref name="cropViewport"/>.</param>
    /// <param name="onResetIcon">Invoked by the "Reset icon" button — typically
    /// <c>CropEditor.ResetView</c>. Null when <paramref name="cropViewport"/> is null.</param>
    /// <param name="initialRotation">Rotation this dialog's picker starts on — see <see cref="ResultRotation"/>.</param>
    /// <param name="rotationEnabled">False disables the rotation picker entirely (e.g. the
    /// source is an animated GIF, which can't be rotation-baked — see the caller's own
    /// rotation-availability check).</param>
    /// <param name="getCroppedImagePath">See <see cref="_getCroppedImagePath"/>.</param>
    public TextIconDialog(int size, string? baseImagePath, KeyIconSpec spec,
                          Func<KeyIconSpec, string?>? defaultIconRenderer,
                          FrameworkElement? cropViewport = null,
                          FrameworkElement? cropControls = null,
                          Action? onResetIcon = null,
                          int initialRotation = 0,
                          bool rotationEnabled = true,
                          Func<string?>? getCroppedImagePath = null)
    {
        InitializeComponent();

        _spec = spec.Clone();
        _defaultIconRenderer = defaultIconRenderer;
        _onResetIcon = onResetIcon;
        _getCroppedImagePath = getCroppedImagePath;
        string? initialText = spec.Text;

        _size = size;
        _previewSize = cropViewport is not null ? Math.Max(size, (int)Math.Round(cropViewport.Width)) : size;
        _baseImagePath = !string.IsNullOrEmpty(baseImagePath) && File.Exists(baseImagePath) ? baseImagePath : null;
        RbBgImage.IsEnabled = _baseImagePath is not null || _getCroppedImagePath is not null;
        // Default to "on top of the image" whenever one is loaded — otherwise the dialog
        // opens on RbBgSolid's XAML default (solid color) even with an icon already in
        // place, and clicking OK without noticing the radio buttons silently replaces the
        // icon with a plain-background text tile (user report 2026-07-25: "add text elimina
        // l'icona"). No-op when there's no base image (RbBgImage stays disabled).
        if (RbBgImage.IsEnabled) RbBgImage.IsChecked = true;

        if (!string.IsNullOrEmpty(initialText)) TxtInput.Text = initialText;

        ApplySpecToState(spec);
        ApplyColorButton(BtnBgColor, _bgColor);
        ApplyColorButton(BtnTextColor, _textColor);

        _showText = spec.ShowText;
        _useK2Icons = spec.UseK2Icons;
        RbShowTextOn.IsChecked  = _showText;
        RbShowTextOff.IsChecked = !_showText;
        RbIconSourceBaseCamp.IsChecked = !_useK2Icons;
        RbIconSourceK2.IsChecked       = _useK2Icons;

        PopulateFontFamilies();
        _manualFontSize = spec.FontSize > 0 ? spec.FontSize : Math.Round(size * 0.42);
        SldFontSize.Minimum = 8;
        SldFontSize.Maximum = Math.Max(9, size * 0.9);
        SldFontSize.Value = _manualFontSize;
        if (spec.FontSize > 0) ChkAutoSize.IsChecked = false;

        if (_defaultIconRenderer is not null)
        {
            // A default icon's glyph and caption strip are laid out by the generator itself —
            // there is nothing to move and no "over the picture" mode to pick.
            BgModePanel.IsEnabled = false;
            AnchorPanel.IsEnabled = false;
        }
        else
        {
            // The reverse: "with/without text" and "icon source" only steer a default icon's
            // own generator — meaningless for a free-form custom tile.
            DefaultIconOptionsPanel.IsEnabled = false;
        }

        // ---- Icon section: crop viewport (drag/zoom, moved here 2026-08-24) + rotation ----
        if (cropViewport is not null)
        {
            // Single preview: the crop viewport slides in UNDERNEATH PreviewFrame, whose
            // ImgPreview shows the fully composited tile (picture + text) re-rendered from the
            // live crop. PreviewFrame is IsHitTestVisible="False" in XAML, so drag/scroll fall
            // through to the viewport; its own 140×140 box and chrome are dropped here so it
            // sits exactly over the viewport instead of forcing a second, differently-sized
            // picture next to it.
            IconViewportHost.Children.Insert(0, cropViewport);
            PreviewFrame.Width = double.NaN;
            PreviewFrame.Height = double.NaN;
            PreviewFrame.Background = Brushes.Transparent;
            PreviewFrame.BorderThickness = new Thickness(0);
            ImgPreview.Margin = new Thickness(0);

            if (cropControls is not null)
            {
                // Pins the zoom slider + hint to the picture's own width, so they stay centred
                // under it instead of stretching the column (the hint is a single long line).
                cropControls.Width = cropViewport.Width;
                IconEditorHost.Children.Add(cropControls);
            }
            BtnResetIcon.IsEnabled = _onResetIcon is not null;

            // Hand the borrowed elements back before ShowDialog returns: a FrameworkElement can
            // only have one parent, so the caller's "reparent them into my own layout again"
            // step would throw if they were still children of this window's panels.
            Closed += (_, _) =>
            {
                IconViewportHost.Children.Remove(cropViewport);
                if (cropControls is not null) IconEditorHost.Children.Remove(cropControls);
            };
        }
        else
        {
            BtnResetIcon.Visibility = Visibility.Collapsed;
        }

        (initialRotation switch { 90 => Rb90, 180 => Rb180, 270 => Rb270, _ => Rb0 }).IsChecked = true;
        ResultRotation = initialRotation;
        Rb0.IsEnabled = Rb90.IsEnabled = Rb180.IsEnabled = Rb270.IsEnabled = rotationEnabled;

        RefreshPreview();
    }

    /// <summary>Applies the colors/font/anchor carried by the spec the dialog opened on, so
    /// reopening "Edit icon" shows the icon exactly as it was last saved.</summary>
    private void ApplySpecToState(KeyIconSpec spec)
    {
        if (KeyIconSpec.ParseColor(spec.BgColor) is System.Drawing.Color bg) _bgColor = bg;
        if (KeyIconSpec.ParseColor(spec.TextColor) is System.Drawing.Color fg) _textColor = fg;
        if (!string.IsNullOrWhiteSpace(spec.FontFamily)) _fontFamily = spec.FontFamily!;
        if (!string.IsNullOrWhiteSpace(spec.Anchor)
            && Enum.TryParse<TextIconGenerator.TextAnchor>(spec.Anchor, out var a))
        {
            _anchor = a;
            foreach (var rb in AnchorPanel.Children.OfType<UniformGrid>()
                                          .SelectMany(u => u.Children.OfType<RadioButton>()))
                rb.IsChecked = (rb.Tag as string) == spec.Anchor;
        }
    }

    /// <summary>Snapshot of the current controls — what gets rendered, and what the caller
    /// persists when OK is clicked.</summary>
    private KeyIconSpec BuildSpec()
    {
        var s = _spec.Clone();
        s.Text        = TxtInput.Text;
        s.ShowText    = _defaultIconRenderer is not null ? _showText : !string.IsNullOrWhiteSpace(TxtInput.Text);
        s.FontFamily  = _fontFamily;
        s.FontSize    = _autoSize ? 0 : _manualFontSize;
        s.BgColor     = KeyIconSpec.ToHex(_bgColor);
        s.TextColor   = KeyIconSpec.ToHex(_textColor);
        s.Anchor      = _anchor.ToString();
        s.TextOnImage = UseImageBackground;
        s.UseK2Icons  = _useK2Icons;
        return s;
    }

    /// <summary>Populates the font picker from the fonts installed on this PC, defaulting
    /// to "Segoe UI" (the previous fixed look) when present.</summary>
    private void PopulateFontFamilies()
    {
        var families = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CbFontFamily.ItemsSource = families;
        CbFontFamily.SelectedItem = families.Contains(_fontFamily) ? _fontFamily : families.FirstOrDefault();
    }

    private void TxtInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

    private void BgMode_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

    /// <summary>Default-icon mode only — whether the generator draws its caption strip at all,
    /// independent of the text box's own content (unlike a custom icon, where typing/clearing
    /// the box is itself the with/without-text toggle).</summary>
    private void ShowText_Changed(object sender, RoutedEventArgs e)
    {
        _showText = RbShowTextOff?.IsChecked != true;
        RefreshPreview();
    }

    /// <summary>Default-icon mode only — Base Camp's ported gallery art vs. K2's hand-drawn
    /// glyph, see <see cref="KeyIconSpec.UseK2Icons"/>.</summary>
    private void IconSource_Changed(object sender, RoutedEventArgs e)
    {
        _useK2Icons = RbIconSourceK2?.IsChecked == true;
        RefreshPreview();
    }

    /// <summary>"Reset icon" — resets the embedded crop editor's pan/zoom (replaces the old
    /// "insert as-is" checkbox, 2026-08-24) and refreshes this dialog's own composited
    /// preview, since <see cref="_onResetIcon"/> changes what <see cref="_getCroppedImagePath"/>
    /// returns.</summary>
    private void BtnResetIcon_Click(object sender, RoutedEventArgs e)
    {
        _onResetIcon?.Invoke();
        RefreshPreview();
    }

    private void RotRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (int.TryParse(tag, out int degrees)) ResultRotation = degrees;
    }

    /// <summary>Called by the caller (via the reparented <c>CropEditor</c>'s <c>Changed</c>
    /// event) whenever drag/zoom inside the Icon section changes the crop — re-renders the
    /// Text section's composited preview so it stays in sync live instead of only on Reset.</summary>
    public void RefreshIconPreview() => RefreshPreview();

    /// <summary>3x3 anchor grid (see TextIconDialog.xaml) — each RadioButton's Tag is the
    /// matching <see cref="TextIconGenerator.TextAnchor"/> name. MiddleCenter's IsChecked="True"
    /// fires this synchronously during InitializeComponent() (same gotcha as RbBgSolid/
    /// ChkAutoSize above), which is fine here since RefreshPreview only reaches into controls
    /// declared EARLIER in the XAML (preview image, text box, font picker) that are already
    /// wired up by the time this row is reached.</summary>
    private void Anchor_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (Enum.TryParse<TextIconGenerator.TextAnchor>(tag, out var anchor)) _anchor = anchor;
        RefreshPreview();
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CbFontFamily.SelectedItem is string f) _fontFamily = f;
        RefreshPreview();
    }

    /// <summary>
    /// ChkAutoSize has IsChecked="True" in XAML, so WPF fires this Checked event
    /// synchronously during InitializeComponent() (same RadioButton/ToggleButton gotcha as
    /// TextIconDialog's own RbBgSolid) — at that point SldFontSize, declared later in the
    /// XAML, isn't wired up yet.
    /// </summary>
    private void AutoSize_Changed(object sender, RoutedEventArgs e)
    {
        if (SldFontSize is null) return;
        _autoSize = ChkAutoSize.IsChecked == true;
        SldFontSize.IsEnabled = !_autoSize;
        RefreshPreview();
    }

    /// <summary>Slider.Value gets coerced up to a new Minimum during InitializeComponent()
    /// (RangeBase re-coerces Value when Minimum/Maximum change), firing this before
    /// LblFontSizeValue — declared later in the XAML — is wired up.</summary>
    private void SldFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LblFontSizeValue is null) return;
        _manualFontSize = e.NewValue;
        LblFontSizeValue.Text = ((int)Math.Round(e.NewValue)).ToString();
        RefreshPreview();
    }

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
    private bool UseImageBackground => RbBgImage?.IsChecked == true
        && (_baseImagePath is not null || _getCroppedImagePath is not null);

    private float? CurrentFontSize => _autoSize ? null : (float)_manualFontSize;

    /// <summary><see cref="CurrentFontSize"/> rescaled from <see cref="_size"/> (what it was
    /// picked against) to <see cref="_previewSize"/> (what <see cref="RefreshPreview"/> actually
    /// renders at) — a manually-chosen pixel size must stay the same FRACTION of the canvas in
    /// the preview as it will be in the saved icon, not the same raw pixel count.</summary>
    private float? PreviewFontSize => _autoSize ? null : (float)(_manualFontSize * _previewSize / (double)_size);

    /// <summary>The image to composite text onto — the caller's LIVE crop (via
    /// <see cref="_getCroppedImagePath"/>) when an interactive crop editor is embedded here,
    /// otherwise the static <see cref="_baseImagePath"/> the dialog was opened with.</summary>
    private string? CurrentBaseImagePath()
    {
        string? live = _getCroppedImagePath?.Invoke();
        return live is not null && File.Exists(live) ? live : _baseImagePath;
    }

    private void RefreshPreview()
    {
        if (_defaultIconRenderer is not null)
        {
            string? path = _defaultIconRenderer(BuildSpec());
            ImgPreview.Source = path is not null && File.Exists(path) ? LoadUnlocked(path) : null;
            return;
        }

        // No text to composite and the live crop viewport is already showing the picture,
        // full-resolution, right underneath — skip rasterizing a redundant copy of it on top
        // (that copy is baked at the device icon's native 72/102px and would otherwise get
        // stretched ~2.4x by the Image control to fill this dialog's larger preview frame,
        // which is what made a freshly loaded, caption-less icon look "sgranata" here even
        // though the CropEditor's own live viewport renders it crisp).
        if (string.IsNullOrWhiteSpace(TxtInput.Text) && UseImageBackground)
        {
            ImgPreview.Source = null;
            return;
        }

        using var bmp = TextIconGenerator.TryRenderTextIcon(
            TxtInput.Text, _previewSize, _textColor,
            UseImageBackground ? (System.Drawing.Color?)null : _bgColor,
            UseImageBackground ? CurrentBaseImagePath() : null,
            _fontFamily, PreviewFontSize, _anchor);

        ImgPreview.Source = bmp is null ? null : ToBitmapSource(bmp);
    }

    /// <summary>Decodes a PNG into memory first — the generated default-icon file is
    /// rewritten on every keystroke, and a BitmapImage bound straight to its URI would keep
    /// the file locked for the next render.</summary>
    private static BitmapSource? LoadUnlocked(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
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
        var spec = BuildSpec();

        if (_defaultIconRenderer is not null)
        {
            string? generated = _defaultIconRenderer(spec);
            if (generated is null || !File.Exists(generated))
            {
                MessageBox.Show(this, Loc.Get("txt_generate_failed"), Loc.Get("error"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            NewImagePath = generated;
            EnteredText  = TxtInput.Text;
            ResultSpec   = spec;
            DialogResult = true;
            return;
        }

        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "K2", "text_icons");
        string dest = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + ".png");

        bool ok = TextIconGenerator.TryGenerateTextIcon(
            TxtInput.Text, _size, dest, _textColor,
            UseImageBackground ? (System.Drawing.Color?)null : _bgColor,
            UseImageBackground ? CurrentBaseImagePath() : null,
            _fontFamily, CurrentFontSize, _anchor);

        if (!ok)
        {
            MessageBox.Show(this, Loc.Get("txt_generate_failed"), Loc.Get("error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        NewImagePath = dest;
        EnteredText  = TxtInput.Text;
        ResultSpec   = spec;
        DialogResult = true;
    }
}
