using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace K2.App.Models;

/// <summary>
/// Bindable state of an Everest Max key in the configuration view.
///
/// A key's identity is its hardware MATRIX code: the keyboard has 100+ keys,
/// so no fixed grid is pre-allocated — keys are discovered on demand by pressing
/// them. The model applies equally to ISO keyboard keys, the numpad, the 4
/// programmable keys, the 5 media dock keys, and the encoder (which, having no
/// click, generates two matrix codes: clockwise and counter-clockwise rotation).
/// </summary>
public sealed class EverestKey : INotifyPropertyChanged
{
    public EverestKey(int keyMatrix) => KeyMatrix = keyMatrix;

    /// <summary>Hardware matrix code: stable key identity.</summary>
    public int KeyMatrix { get; }

    private string _label = "";
    /// <summary>User-assigned name (e.g. "F5", "Macro 1", "Encoder CW").</summary>
    public string Label
    {
        get => _label;
        set
        {
            value ??= "";
            if (_label == value) return;
            _label = value;
            OnChanged();
            OnChanged(nameof(Name));
            OnChanged(nameof(Display));
        }
    }

    private string? _actionType;
    /// <summary>Assigned action type (url, keys, pyscript, ...; null = none).</summary>
    public string? ActionType
    {
        get => _actionType;
        set
        {
            if (_actionType == value) return;
            _actionType = value;
            OnChanged();
            OnChanged(nameof(HasAction));
            OnChanged(nameof(Display));
        }
    }

    private string? _actionValue;
    /// <summary>Action value/payload.</summary>
    public string? ActionValue
    {
        get => _actionValue;
        set { if (_actionValue == value) return; _actionValue = value; OnChanged(); }
    }

    private bool _isHighlighted;
    /// <summary>True while the corresponding physical key is held down.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted == value) return; _isHighlighted = value; OnChanged(); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    /// <summary>Key label: the user-chosen name, or the matrix code.</summary>
    public string Name => string.IsNullOrWhiteSpace(_label) ? $"0x{KeyMatrix:X2}" : _label;

    /// <summary>Text shown in the key list.</summary>
    public string Display
    {
        get
        {
            string body = string.IsNullOrEmpty(_actionType) ? "(empty)" : _actionType;
            return $"{Name}  —  {body}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
