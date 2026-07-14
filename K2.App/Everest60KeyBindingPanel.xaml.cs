using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Key Binding section content for the Everest 60 tab — see
/// Everest60KeyBindingPanel.xaml for the architecture note. As of 2026-07-14
/// (second pass, on explicit user request) this is no longer a raw firmware
/// remap: it's a K2Action like Everest Max/MacroPad/DisplayPad, using the
/// SAME <see cref="ButtonActionDialog"/> and the same action catalog, routed
/// through <see cref="Ev60ActionHost"/>/<see cref="ButtonActionEngine"/>
/// (owned by MainWindow.Everest60.cs, same split as MainWindow.Everest.cs's
/// _evActionHost/_evEngine). This panel owns the key list/lookup (mirroring
/// MainWindow.Everest.cs's _evKeys/_evByMatrix) since it's the one with the
/// UI (list + Configure/Remove); MainWindow.Everest60.cs reads it via
/// <see cref="Keys"/>/<see cref="ByLed"/> to implement the IActionHost
/// delegates and to execute an action when the physical key's SDK callback
/// fires (see MainWindow.Everest60.cs's OnEv60Key).
/// </summary>
public partial class Everest60KeyBindingPanel : UserControl
{
    private Action<string> _log = _ => { };
    private IActionHost? _actionHost;

    /// <summary>Profile persistence — set once from Init, same pattern as
    /// Everest60RgbPanel._ev60Store/_ev60Slot.</summary>
    private Everest60Store? _ev60Store;
    private Func<int>? _ev60Slot;
    private Func<KeyboardLayoutType>? _ev60Layout;
    private int CurrentSlot => _ev60Slot?.Invoke() ?? 1;

    private readonly ObservableCollection<Ev60Key> _keys = new();
    private readonly Dictionary<int, Ev60Key> _byLed = new();

    /// <summary>Every key currently in the profile's list (only keys with an
    /// assigned action — unmapped board keys aren't tracked), for
    /// Ev60ActionHost.GetButtons.</summary>
    internal IReadOnlyList<Ev60Key> Keys => _keys;

    internal Ev60Key? ByLed(int ledIndex) => _byLed.TryGetValue(ledIndex, out var k) ? k : null;

    internal int IndexOf(Ev60Key key) => _keys.IndexOf(key);

    /// <summary>Board legend for a LED index (0-63) in the current locale layout
    /// (see MainWindow.Everest60.cs's _ev60LayoutType), no hex/raw-index fallback.</summary>
    private string BoardLabelForLed(int ledIndex)
    {
        var layout = _ev60Layout?.Invoke() ?? KeyboardLayoutType.AnsiUs;
        foreach (var kd in Everest60KeyboardLayout.GetMainBoard(layout))
            if (kd.MatrixId == ledIndex) return string.IsNullOrEmpty(kd.Label) ? $"Key {ledIndex}" : kd.Label;
        return $"Key {ledIndex}";
    }

    public Everest60KeyBindingPanel()
    {
        InitializeComponent();
    }

    internal void Init(Everest60Store store, Func<int> currentSlot, Action<string> log, Func<KeyboardLayoutType> currentLayout)
    {
        _ev60Store = store;
        _ev60Slot = currentSlot;
        _ev60Layout = currentLayout;
        _log = log;
        LvEv60Keys.ItemsSource = _keys;
        UpdateListButtons();
    }

    /// <summary>Set once the shared Ev60ActionHost/ButtonActionEngine exist
    /// (constructed after this Init, since the host's delegates need
    /// <see cref="Keys"/> — see MainWindow.Everest60.cs's InitEverest60Module).</summary>
    internal void SetActionHost(IActionHost host) => _actionHost = host;

    /// <summary>Called by MainWindow when a key on the 64-key overlay is
    /// clicked while the Key Binding section is active — same trigger/flow as
    /// Everest Max's EvKeyboardButton_Click: get-or-create the key, open
    /// ButtonActionDialog directly (adding it to the profile list only if the
    /// dialog is actually confirmed with a real action).</summary>
    internal void SelectKey(int ledIndex, string label)
    {
        bool isNewKey = !_byLed.ContainsKey(ledIndex);
        if (!_byLed.TryGetValue(ledIndex, out var key))
        {
            key = new Ev60Key(ledIndex) { Label = label };
            _keys.Add(key);
            _byLed[ledIndex] = key;
            _log($"[KeyBind] new key led={ledIndex} added via overlay click");
        }

        LvEv60Keys.SelectedItem = key;

        var dlg = new ButtonActionDialog(ledIndex, key.ActionType, key.ActionValue, _actionHost)
                  { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true)
        {
            if (isNewKey && key.ActionType is null)
            {
                _keys.Remove(key);
                _byLed.Remove(ledIndex);
            }
            return;
        }

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;

        PersistOrDiscardKey(key);
    }

    private void UpdateListButtons()
    {
        bool hasSelection = LvEv60Keys.SelectedItem is not null;
        BtnEv60Configure.IsEnabled = hasSelection;
        BtnEv60Remove.IsEnabled = hasSelection;
    }

    private void LvEv60Keys_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateListButtons();

    private void BtnEv60Configure_Click(object sender, RoutedEventArgs e)
    {
        if (LvEv60Keys.SelectedItem is not Ev60Key key) return;

        var dlg = new ButtonActionDialog(key.LedIndex, key.ActionType, key.ActionValue, _actionHost)
                  { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        key.ActionType  = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                          ? null : dlg.ActionType;
        key.ActionValue = key.ActionType is null ? null : dlg.ActionValue;

        PersistOrDiscardKey(key);
    }

    private void BtnEv60Remove_Click(object sender, RoutedEventArgs e)
    {
        if (LvEv60Keys.SelectedItem is not Ev60Key key) return;
        _keys.Remove(key);
        _byLed.Remove(key.LedIndex);
        _ev60Store?.RemoveKey(CurrentSlot, key.LedIndex);
        _log($"[KeyBind] key led={key.LedIndex} removed");
    }

    /// <summary>Persists a key's current action, or — if it has no action assigned —
    /// discards it entirely (list + DB) instead of keeping an empty entry. Mirrors
    /// MainWindow.Everest.cs's EvPersistOrDiscardKey.</summary>
    private void PersistOrDiscardKey(Ev60Key key)
    {
        if (key.ActionType is null)
        {
            _keys.Remove(key);
            _byLed.Remove(key.LedIndex);
            _ev60Store?.RemoveKey(CurrentSlot, key.LedIndex);
            _log($"[KeyBind] key led={key.LedIndex} emptied, removed");
        }
        else
        {
            _ev60Store?.SaveKey(new Ev60KeyRecord(CurrentSlot, key.LedIndex, key.Label, key.ActionType, key.ActionValue));
            _log($"[KeyBind] key led={key.LedIndex} <- type={key.ActionType}");
        }
    }

    /// <summary>Clears every key of this profile (no firmware to reset —
    /// see class doc comment, actions are now software-only). Called by
    /// MainWindow.Everest60.cs's "Restore defaults" button.</summary>
    internal void RestoreDefaults()
    {
        _keys.Clear();
        _byLed.Clear();
        _ev60Store?.ResetProfileToDefaults(CurrentSlot);
        _log("[KeyBind] restore defaults: all keys cleared");
    }

    /// <summary>Reloads this profile's keys from the store. No firmware
    /// replay needed anymore (unlike the pre-K2Action design): actions
    /// execute in K2 software when the SDK reports a key press, not via a
    /// live firmware remap table — see MainWindow.Everest60.cs's OnEv60Key.</summary>
    internal void Ev60ReloadKeyBindings(int slot)
    {
        _keys.Clear();
        _byLed.Clear();
        if (_ev60Store is null) return;

        foreach (var r in _ev60Store.LoadProfile(slot))
        {
            var k = new Ev60Key(r.LedIndex)
            {
                Label       = string.IsNullOrEmpty(r.Label) ? BoardLabelForLed(r.LedIndex) : r.Label,
                ActionType  = r.ActionType,
                ActionValue = r.ActionValue,
            };
            _keys.Add(k);
            _byLed[r.LedIndex] = k;
        }
        _log($"[PROFILE] Ev60 reload keys slot={slot}: {_keys.Count} loaded");
    }
}
