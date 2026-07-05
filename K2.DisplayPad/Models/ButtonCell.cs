using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.DisplayPad.Models;

/// <summary>
/// Stato bindabile di una singola cella della griglia tasti del DisplayPad.
/// </summary>
public sealed class ButtonCell : INotifyPropertyChanged
{
    public ButtonCell(int index) => Index = index;

    /// <summary>Indice tasto (0..11).</summary>
    public int Index { get; }

    /// <summary>Etichetta visibile in alto a sinistra nella cella.</summary>
    public string Label => $"#{Index}";

    private string? _imagePath;
    /// <summary>Path locale dell'ultima immagine caricata su questo tasto.</summary>
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
        }
    }

    /// <summary>Anteprima dell'immagine — costruita on demand dal path.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath))
                return null;
            try
            {
                // Load immediately into memory so the file is not kept locked
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new System.Uri(_imagePath);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool HasImage => !string.IsNullOrEmpty(_imagePath);

    private bool _isHighlighted;
    /// <summary>True quando il tasto fisico corrispondente e' tenuto premuto.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            OnChanged();
        }
    }

    private int? _keyMatrix;
    /// <summary>Codice matrix hardware (se gia' associato a questa cella).</summary>
    public int? KeyMatrix
    {
        get => _keyMatrix;
        set
        {
            if (_keyMatrix == value) return;
            _keyMatrix = value;
            OnChanged();
            OnChanged(nameof(Label));
        }
    }

    /// <summary>Override del Label se conosciamo il matrix code.</summary>
    public string LabelWithMatrix =>
        _keyMatrix is int m ? $"#{Index}  0x{m:X2}" : $"#{Index}";

    private string? _actionType;
    /// <summary>Tipo di azione associato al tasto: "url", "keys", "command", "text", null.</summary>
    public string? ActionType
    {
        get => _actionType;
        set { if (_actionType == value) return; _actionType = value; OnChanged(); OnChanged(nameof(HasAction)); }
    }

    private string? _actionValue;
    /// <summary>Valore dell'azione (URL, comando, sequenza tasti, testo).</summary>
    public string? ActionValue
    {
        get => _actionValue;
        set { if (_actionValue == value) return; _actionValue = value; OnChanged(); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
