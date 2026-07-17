using System.ComponentModel;
using System.Runtime.CompilerServices;
using K2.Core;

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
        set
        {
            if (_actionValue == value) return;
            _actionValue = value;
            OnChanged();
            OnChanged(nameof(Display));
        }
    }

    private bool _isHighlighted;
    /// <summary>True while the corresponding physical key is held down.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted == value) return; _isHighlighted = value; OnChanged(); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    /// <summary>Static identity shown on the physical key button — unlike the
    /// old Display, this never changes based on the assigned action (that's
    /// what the Key Binding section's mapped-keys list is for, see
    /// <see cref="Display"/>). Mirrors Everest Max/Everest 60, whose keycaps
    /// likewise always show their own name, never the assigned action.</summary>
    public string KeyLabel => $"M{Index + 1}";

    /// <summary>Text shown in the Key Binding section's mapped-keys list —
    /// mirrors EverestKey.Display (identity + a short action summary).</summary>
    public string Display
    {
        get
        {
            string body = HasAction ? ActionSummary : "(empty)";
            return $"{KeyLabel}  —  {body}";
        }
    }

    private string ActionSummary => _actionType switch
    {
        "keys"  => _actionValue ?? _actionType!,
        "exec"  => System.IO.Path.GetFileName(_actionValue ?? _actionType!),
        "media" => _actionValue ?? _actionType!,
        "macro" => ActionTypeHelper.MacroSummary(_actionValue),
        _       => ActionTypeHelper.IsUnrecognized(_actionType) ? Loc.Get("act_unrecognized") : _actionType ?? "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
