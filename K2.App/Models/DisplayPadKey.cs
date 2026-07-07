using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.App.Services;

namespace K2.App.Models;

/// <summary>
/// Bindable state of a DisplayPad grid cell in the tab integrated in K2.App.
/// Mirrors <c>K2.DisplayPad.Models.ButtonCell</c> but lives in the x86 process.
/// </summary>
public sealed class DisplayPadKey : INotifyPropertyChanged
{
    // ---- Static debug mode (toggled by MainWindow) ----
    private static bool _debugMode;
    public static bool DebugMode
    {
        get => _debugMode;
        set { _debugMode = value; }
    }

    public DisplayPadKey(int index) => Index = index;

    public int Index { get; }
    public string Label => $"#{Index}";

    private string? _imagePath;
    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (_imagePath == value) return;
            _imagePath = value;
            OnChanged();
            OnChanged(nameof(Preview));
            OnChanged(nameof(HasImage));
            OnChanged(nameof(HasImageNoAction));
            OnChanged(nameof(Display));
        }
    }

    public ImageSource? Preview
    {
        get
        {
            if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath))
                return null;
            try
            {
                // A cropped GIF's assigned path is a CroppedGifRef sidecar (JSON pointing at
                // the real source + crop rect, see that class' remarks) — not an image file
                // a BitmapImage can decode. The grid only shows a static thumbnail anyway
                // (live playback happens on the device via DpGifAnimator), so render just the
                // first frame with the saved crop.
                if (CroppedGifRef.IsCropRef(_imagePath))
                    return LoadCroppedGifPreview(_imagePath);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new System.Uri(_imagePath);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    private static ImageSource? LoadCroppedGifPreview(string sidecarPath)
    {
        var cref = CroppedGifRef.TryLoad(sidecarPath);
        if (cref is null || !File.Exists(cref.Source)) return null;

        using var img = new System.Drawing.Bitmap(cref.Source);
        var srcRect = cref.NoCrop
            ? new System.Drawing.RectangleF(0, 0, img.Width, img.Height)
            : new System.Drawing.RectangleF(cref.RectX, cref.RectY, cref.RectW, cref.RectH);

        using var frame = new System.Drawing.Bitmap(DpHidNative.IconSize, DpHidNative.IconSize);
        using (var g = System.Drawing.Graphics.FromImage(frame))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, new System.Drawing.Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize),
                srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, System.Drawing.GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        frame.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = ms;
        result.EndInit();
        result.Freeze();
        return result;
    }

    public bool HasImage => !string.IsNullOrEmpty(_imagePath);

    /// <summary>True when the key has an image but no action assigned.
    /// Used to show a warning indicator in the UI.</summary>
    public bool HasImageNoAction => HasImage && !HasAction;

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted == value) return; _isHighlighted = value; OnChanged(); }
    }

    private int? _keyMatrix;
    public int? KeyMatrix
    {
        get => _keyMatrix;
        set { if (_keyMatrix == value) return; _keyMatrix = value; OnChanged(); OnChanged(nameof(Label)); }
    }

    private string? _actionType;
    public string? ActionType
    {
        get => _actionType;
        set
        {
            if (_actionType == value) return;
            _actionType = value;
            OnChanged();
            OnChanged(nameof(HasAction));
            OnChanged(nameof(HasImageNoAction));
            OnChanged(nameof(Display));
        }
    }

    private string? _actionValue;
    public string? ActionValue
    {
        get => _actionValue;
        set { if (_actionValue == value) return; _actionValue = value; OnChanged(); OnChanged(nameof(Display)); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    /// <summary>Label shown on the overlay button.</summary>
    public string Display
    {
        get
        {
            if (HasImage) return "";  // image fills the button; warning triangle shown via XAML
            if (HasAction)
                return _actionType switch
                {
                    "keys"      => _actionValue ?? "",
                    "url"       => "URL",
                    "exec"      => Path.GetFileName(_actionValue ?? ""),
                    "dp_folder" => "▸",   // folder — label comes from image
                    "dp_back"   => "◂",   // back — label comes from image
                    _           => _actionType ?? "",
                };
            // Empty key: show index only in debug mode
            return _debugMode ? $"#{Index}" : "";
        }
    }

    /// <summary>Refreshes display-related bindings after a DebugMode change.</summary>
    public void NotifyDebugModeChanged() => OnChanged(nameof(Display));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
