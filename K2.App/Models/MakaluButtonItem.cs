using System.ComponentModel;
using System.Runtime.CompilerServices;
using K2.App.Services;
using K2.Core;

namespace K2.App.Models;

/// <summary>
/// Bindable row for a Makalu physical button in the Key Binding list
/// (MakaluDpiRemapPanel's LvMkButtons) — mirrors Ev60Key/EverestKey/
/// MacroPadKey's role for their own devices, but simpler: a mouse button
/// always has SOME function assigned (no "empty" state, unlike a keyboard
/// key that may have no action), so there's no ActionType/ActionValue
/// split, just the raw remap string MakaluRemapData already speaks
/// ("left", "dpi+", "sniper:800", ...). No IsHighlighted either — unlike
/// the keyboard devices, Makalu has no physical-press callback to drive it
/// (buttons are remapped entirely in firmware, see MainWindow.Makalu.cs's
/// architectural note), so there is nothing to highlight live.
/// </summary>
public sealed class MakaluButtonItem : INotifyPropertyChanged
{
    public MakaluButtonItem(int index, string nameKey) => (Index, NameKey) = (index, nameKey);

    /// <summary>1-based physical button index (MakaluRemapData/MakaluProtocol convention).</summary>
    public int Index { get; }

    /// <summary>Loc key for this button's name (e.g. "makalu_remap_btn_left").</summary>
    public string NameKey { get; }

    public string BaseLabel => Loc.Get(NameKey);

    private string _assignment = "left";
    /// <summary>Raw assignment string — a plain function key ("left", "dpi+", ...),
    /// "sniper:{dpi}", or a K2 action (<see cref="MakaluRemapData.MakeAction"/>).</summary>
    public string Assignment
    {
        get => _assignment;
        set
        {
            if (_assignment == value) return;
            _assignment = value;
            OnChanged();
            OnChanged(nameof(Display));
        }
    }

    /// <summary>Text shown in the key list.</summary>
    public string Display => $"{BaseLabel}:   {MakaluRemapData.ListLabel(Assignment)}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
