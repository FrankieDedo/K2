using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace K2.App.Models;

/// <summary>
/// Bindable state of a MacroPad key in the configuration grid.
/// Unlike the DisplayPad, MacroPad keys don't have a display,
/// so there's no image here: only the assigned action and the hardware
/// mapping.
/// </summary>
public sealed class MacroPadKey : INotifyPropertyChanged
{
    public MacroPadKey(int index) => Index = index;

    /// <summary>Logical key index (0..11).</summary>
    public int Index { get; }

    private int? _keyMatrix;
    /// <summary>Associated hardware matrix code (null if not yet mapped).</summary>
    public int? KeyMatrix
    {
        get => _keyMatrix;
        set
        {
            if (_keyMatrix == value) return;
            _keyMatrix = value;
            OnChanged();
            OnChanged(nameof(Display));
        }
    }

    private string? _actionType;
    /// <summary>Type of assigned action (url, keys, pyscript, ... ; null = none).</summary>
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
    /// <summary>Value/payload of the action.</summary>
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

    /// <summary>Text shown on the grid button.</summary>
    public string Display
    {
        get
        {
            string label = $"M{Index + 1}";
            if (!HasAction) return label;
            return _actionType switch
            {
                "keys"  => _actionValue ?? label,
                "exec"  => System.IO.Path.GetFileName(_actionValue ?? label),
                "media" => _actionValue ?? label,
                _       => _actionType ?? label,
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
