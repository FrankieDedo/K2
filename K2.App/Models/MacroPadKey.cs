using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace K2.App.Models;

/// <summary>
/// Stato bindabile di un tasto del MacroPad nella griglia di configurazione.
/// A differenza del DisplayPad i tasti del MacroPad non hanno un display,
/// quindi qui non c'e' immagine: solo l'azione assegnata e la mappatura
/// hardware.
/// </summary>
public sealed class MacroPadKey : INotifyPropertyChanged
{
    public MacroPadKey(int index) => Index = index;

    /// <summary>Indice logico del tasto (0..11).</summary>
    public int Index { get; }

    private int? _keyMatrix;
    /// <summary>Codice matrice hardware associato (null se non ancora mappato).</summary>
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
    /// <summary>Tipo di azione assegnata (url, keys, pyscript, ... ; null = nessuna).</summary>
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
    /// <summary>Valore/payload dell'azione.</summary>
    public string? ActionValue
    {
        get => _actionValue;
        set { if (_actionValue == value) return; _actionValue = value; OnChanged(); }
    }

    private bool _isHighlighted;
    /// <summary>True mentre il tasto fisico corrispondente e' tenuto premuto.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set { if (_isHighlighted == value) return; _isHighlighted = value; OnChanged(); }
    }

    public bool HasAction => !string.IsNullOrEmpty(_actionType);

    /// <summary>Testo mostrato sul pulsante della griglia.</summary>
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
