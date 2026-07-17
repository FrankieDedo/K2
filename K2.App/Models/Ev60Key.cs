using System.ComponentModel;
using System.Runtime.CompilerServices;
using K2.Core;

namespace K2.App.Models;

/// <summary>
/// Bindable state of an Everest 60 key in the configuration view — mirrors
/// <see cref="EverestKey"/> (Everest Max), but identity is the board's LED
/// index (0-63, same as <see cref="Everest60KeyboardLayout"/>'s MatrixId)
/// instead of a raw SDK hardware matrix code: the 64-key overlay is a fixed
/// grid (no dynamic discovery needed like Everest Max's 100+ keys).
/// </summary>
public sealed class Ev60Key : INotifyPropertyChanged
{
    public Ev60Key(int ledIndex) => LedIndex = ledIndex;

    /// <summary>Board LED index: stable key identity (0-63).</summary>
    public int LedIndex { get; }

    private string _label = "";
    /// <summary>Key legend (e.g. "A", "Esc", "Space").</summary>
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
        set { if (_actionValue == value) return; _actionValue = value; OnChanged(); OnChanged(nameof(Display)); }
    }

    private bool _isHighlighted;
    /// <summary>True while the corresponding physical key is held down.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted == value) return; _isHighlighted = value; OnChanged(); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    public string Name => string.IsNullOrWhiteSpace(_label) ? $"Key {LedIndex}" : _label;

    /// <summary>Text shown in the key list.</summary>
    public string Display
    {
        get
        {
            string body = ActionTypeHelper.IsUnrecognized(_actionType) ? Loc.Get("act_unrecognized")
                        : string.Equals(_actionType, "macro", System.StringComparison.Ordinal) ? ActionTypeHelper.MacroSummary(_actionValue)
                        : string.IsNullOrEmpty(_actionType) ? "(empty)" : _actionType;
            return $"{Name}  —  {body}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
