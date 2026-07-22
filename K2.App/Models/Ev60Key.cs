using System.ComponentModel;
using System.Runtime.CompilerServices;
using K2.App.Services;
using K2.Core;

namespace K2.App.Models;

/// <summary>
/// Bindable state of an Everest 60 key in the configuration view — mirrors
/// <see cref="EverestKey"/> (Everest Max), but identity is the board's LED
/// index (0-63, same as <see cref="Everest60KeyboardLayout"/>'s MatrixId)
/// instead of a raw SDK hardware matrix code: the 64-key overlay is a fixed
/// grid (no dynamic discovery needed like Everest Max's 100+ keys). Also
/// covers the 17-key numpad accessory (<see cref="NumpadIndex"/> non-null),
/// stored at <c>LedIndex = Everest60Protocol.NumpadLedIndexBase + NumpadIndex</c>
/// to share the same store table without colliding with the main board.
/// </summary>
public sealed class Ev60Key : INotifyPropertyChanged
{
    public Ev60Key(int ledIndex, int? numpadIndex = null) =>
        (LedIndex, NumpadIndex) = (ledIndex, numpadIndex);

    /// <summary>Board LED index: stable key identity (0-63 for the main
    /// board; <see cref="Everest60Protocol.NumpadLedIndexBase"/> + 0-16 for
    /// the numpad accessory, see <see cref="NumpadIndex"/>).</summary>
    public int LedIndex { get; }

    /// <summary>Non-null only for a numpad accessory key: its 0-16 identity
    /// (same as <see cref="Everest60KeyboardLayout"/>'s <c>KeyDef.NumpadIndex</c>
    /// and <see cref="Everest60Protocol"/>'s <c>NumpadLedIndex</c> array
    /// position). Null for a main-board key.</summary>
    public int? NumpadIndex { get; }

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

    public string Name => !string.IsNullOrWhiteSpace(_label) ? _label
                         : NumpadIndex is int npi ? $"Numpad {npi}"
                         : $"Key {LedIndex}";

    /// <summary>Text shown in the key list.</summary>
    public string Display
    {
        get
        {
            string body = string.IsNullOrEmpty(_actionType) ? "(empty)" : ActionSummary;
            return $"{Name}  —  {body}";
        }
    }

    /// <summary>Mirrors DisplayPadKey.ActionSummary/EverestKey.ActionSummary/MacroPadKey.
    /// ActionSummary — every action type must resolve through ActionTypeHelper.Summary
    /// instead of falling back to the raw ActionType string (e.g. "oscmd" showing up as
    /// "oscmd" instead of the assigned command; user report 2026-07-22).</summary>
    private string ActionSummary => ActionTypeHelper.Summary(_actionType, _actionValue);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
