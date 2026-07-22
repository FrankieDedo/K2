using System.ComponentModel;
using System.Runtime.CompilerServices;
using K2.Core;

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

    /// <summary>Hardware matrix code: stable key identity. For an NDK entry (see
    /// <see cref="NdkIndex"/>) this is a negative placeholder — display keys have no
    /// real matrix code and never go through <c>_evByMatrix</c>.</summary>
    public int KeyMatrix { get; }

    /// <summary>Set (0-3) for one of the 4 numpad LCD "display keys" (NDK), surfaced in
    /// this same mapped-keys list when it carries a non-default action or image — null
    /// for a regular keyboard key. NDK state lives in EverestStore's global
    /// <c>ndk.{i}.*</c> settings, not the per-profile Keys table (see
    /// MainWindow.NumpadDisplayKeys.cs) — callers must branch on this before touching
    /// <see cref="KeyMatrix"/>-keyed persistence.</summary>
    public int? NdkIndex { get; init; }

    private bool _hasImage;
    /// <summary>True if an NDK entry has a custom icon (independent of <see cref="HasAction"/> —
    /// a display key can carry only an icon, only an action, or both). Always false for a
    /// regular key.</summary>
    public bool HasImage
    {
        get => _hasImage;
        set { if (_hasImage == value) return; _hasImage = value; OnChanged(); OnChanged(nameof(Display)); }
    }

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

    /// <summary>Key label: the user-chosen/resolved name, or a plain fallback
    /// (no hex — matrixIds outside the known layout, e.g. dock/crown, still
    /// need something displayable).</summary>
    public string Name => string.IsNullOrWhiteSpace(_label) ? $"Key {KeyMatrix}" : _label;

    /// <summary>Text shown in the key list.</summary>
    public string Display
    {
        get
        {
            string body = HasAction ? ActionSummary
                         : _hasImage ? Loc.Get("ev_display_key_icon_only")
                         : "(empty)";
            return $"{Name}  —  {body}";
        }
    }

    /// <summary>Short summary of the assigned action — mirrors DisplayPadKey.ActionSummary/
    /// MacroPadKey.ActionSummary. Without this, every action type (not just "exec") showed
    /// up as its raw internal tag (e.g. "exec", "keys") instead of something meaningful like
    /// the executable's filename (user report 2026-07-18; extended to cover every action
    /// type, e.g. "oscmd", via ActionTypeHelper.Summary — user report 2026-07-19).</summary>
    private string ActionSummary => ActionTypeHelper.Summary(_actionType, _actionValue);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
