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
/// Dialog unificato per configurare un tasto DisplayPad:
/// caricamento immagine + crop/zoom + rotazione + azione — tutto nella STESSA finestra
/// (2026-07-05: il crop/zoom, prima un popup separato via ImageCropDialog, è stato
/// incorporato qui tramite <see cref="CropEditor"/>, così l'intero flusso di modifica
/// dell'immagine resta in un'unica interfaccia).
///
/// Rotazione "utente" (questo dialog) e rotazione "device" (satellite) sono
/// indipendenti: la rotazione utente si applica all'immagine salvata su disco,
/// quella device si applica in fase di upload via <c>ResolveForUpload</c> nel
/// satellite. Quindi l'utente può caricare un'immagine, ruotarla di 90° qui
/// per aggiustarne l'orientamento, e il satellite poi applicherà la
/// counter-rotation per il display montato ruotato.
/// </summary>
public partial class DpKeyConfigDialog : Window
{
    // ---- Outputs --------------------------------------------------------
    /// <summary>Path finale dell'immagine (già ruotata se richiesto). Null = rimuovere.</summary>
    public string? NewImagePath { get; private set; }
    /// <summary>True se l'immagine è cambiata (load / remove / rotazione).</summary>
    public bool ImageChanged   { get; private set; }
    /// <summary>Tipo azione risultante (null = nessuna).</summary>
    public string? ActionType  { get; private set; }
    /// <summary>Valore azione risultante.</summary>
    public string? ActionValue { get; private set; }

    // ---- State ----------------------------------------------------------
    private readonly int _keyIndex;
    /// <summary>Path immagine corrente nel dialog (non ancora croppata/ruotata su disco —
    /// per le GIF resta il file originale, per le statiche è la sorgente caricata nel
    /// CropEditor).</summary>
    private string? _pendingPath;
    /// <summary>Path immagine originale all'apertura (per rilevare se è cambiata).</summary>
    private readonly string? _originalPath;
    /// <summary>Gradi di rotazione utente selezionati (0 / 90 / 180 / 270).</summary>
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
    // Costruttore
    // =====================================================================

    public DpKeyConfigDialog(
        int keyIndex,
        string? currentImagePath,
        string? currentActionType,
        string? currentActionValue)
    {
        InitializeComponent();

        _keyIndex    = keyIndex;
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

    private void RotRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out int degrees)) return;
        _rotation = degrees;
        _previewRotate.Angle = _rotation;    // ruota visualmente il preview (crop o GIF)
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
        var dlg = new ButtonActionDialog(_keyIndex, ActionType, ActionValue) { Owner = this };
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

        // Applica rotazione utente sull'immagine se necessaria
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
                // rotazione fallita: usa il risultato non ruotato
                NewImagePath = finalPath;
                ImageChanged = finalPath != _originalPath;
            }
        }
        else
        {
            NewImagePath = finalPath;   // null = rimuovere, stringa = invariata o nuova
            ImageChanged = finalPath != _originalPath;
        }

        DialogResult = true;
    }

    // =====================================================================
    // Image rotation helper (GDI+ via WinForms transitive dependency)
    // =====================================================================

    /// <summary>
    /// Ruota l'immagine sorgente e salva il risultato in una cache locale.
    /// Usa <c>System.Drawing</c> (disponibile tramite UseWindowsForms del csproj).
    /// Restituisce il path del file ruotato (dalla cache se già presente).
    /// </summary>
    private static string ApplyUserRotation(string sourcePath, int degrees)
    {
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheDir_UserRotated);
        Directory.CreateDirectory(cacheRoot);

        // Chiave cache: path + mtime + grado rotazione (evita collisioni e si
        // auto-invalida quando l'immagine sorgente viene aggiornata).
        long mtime = 0;
        try { mtime = File.GetLastWriteTimeUtc(sourcePath).Ticks; } catch { }
        byte[] hashBytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourcePath}|{mtime}|ur{degrees}"));
        string name = Convert.ToHexString(hashBytes).ToLowerInvariant() + $"_ur{degrees}.png";
        string dest = Path.Combine(cacheRoot, name);
        if (File.Exists(dest)) return dest;

        // Legge in MemoryStream per non bloccare il file sorgente.
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
