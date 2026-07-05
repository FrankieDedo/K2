using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace K2.DisplayPad.Models;

/// <summary>
/// Bindable state of a single cell in the DisplayPad's button grid.
/// </summary>
public sealed class ButtonCell : INotifyPropertyChanged
{
    public ButtonCell(int index) => Index = index;

    /// <summary>Button index (0..11).</summary>
    public int Index { get; }

    /// <summary>Label shown in the top-left corner of the cell.</summary>
    public string Label => $"#{Index}";

    private string? _imagePath;
    /// <summary>Local path of the last image uploaded to this button.</summary>
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

    /// <summary>Image preview — built on demand from the path.</summary>
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
    /// <summary>True while the corresponding physical button is held down.</summary>
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
    /// <summary>Hardware matrix code (if already associated with this cell).</summary>
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

    /// <summary>Label override when we know the matrix code.</summary>
    public string LabelWithMatrix =>
        _keyMatrix is int m ? $"#{Index}  0x{m:X2}" : $"#{Index}";

    private string? _actionType;
    /// <summary>Action type associated with the button: "url", "keys", "command", "text", null.</summary>
    public string? ActionType
    {
        get => _actionType;
        set { if (_actionType == value) return; _actionType = value; OnChanged(); OnChanged(nameof(HasAction)); }
    }

    private string? _actionValue;
    /// <summary>Action value (URL, command, key sequence, text).</summary>
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
