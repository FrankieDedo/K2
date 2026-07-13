using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// Key Binding section content for the Everest 60 tab — see
/// Everest60KeyBindingPanel.xaml for the architecture note (firmware-side
/// remap via Everest60SdkNative, no IActionHost involvement).
/// </summary>
public partial class Everest60KeyBindingPanel : UserControl
{
    private Everest60SdkService _sdk = null!;
    private Action<string> _log = _ => { };
    private bool _suppress = true; // see Everest60RgbPanel's _ev60Suppress doc comment — same defense-in-depth reasoning

    private int _selectedLedIndex = -1;
    private int _selectedDllKeyId = -1;

    /// <summary>In-memory mirror of what's been sent to firmware for this
    /// profile — ledIndex -> (mode, target DllKeyId/media code, modifier mask).
    /// Didn't exist before profile support: every "Apply" used to be a
    /// one-shot SDK write with nothing tracked locally. Now the single source
    /// used both to persist (MakaluStore-style) and to replay the whole
    /// keymap to firmware on profile switch (<see cref="Ev60ReloadKeyBindings"/>).</summary>
    private readonly Dictionary<int, (string Mode, int Value, int Mask)> _bindings = new();

    /// <summary>Profile persistence — set once from Init, same pattern as
    /// Everest60RgbPanel._ev60Store/_ev60Slot.</summary>
    private Everest60Store? _ev60Store;
    private Func<int>? _ev60Slot;
    private int CurrentSlot => _ev60Slot?.Invoke() ?? 1;

    private sealed record RemapModeChoice(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly RemapModeChoice[] Modes =
    {
        new("key",      "Remap Key"),
        new("fn",       "Fn Layer"),
        new("shortcut", "Shortcut"),
        new("media",    "Media / OS"),
    };

    public Everest60KeyBindingPanel()
    {
        InitializeComponent();
    }

    internal void Init(Everest60SdkService sdk, Action<string> log, Everest60Store store, Func<int> currentSlot)
    {
        _sdk = sdk;
        _log = log;
        _ev60Store = store;
        _ev60Slot = currentSlot;
        _suppress = true;
        try
        {
            CbEv60RemapMode.ItemsSource = Modes;
            CbEv60RemapMode.SelectedIndex = 0;

            var keyLabels = Everest60RemapData.KeyCatalog.Keys.OrderBy(k => k).ToArray();
            CbEv60TargetKey.ItemsSource = keyLabels;
            CbEv60ShortcutKey.ItemsSource = keyLabels;
            if (keyLabels.Length > 0)
            {
                CbEv60TargetKey.SelectedIndex = 0;
                CbEv60ShortcutKey.SelectedIndex = 0;
            }

            CbEv60MediaAction.ItemsSource = Everest60RemapData.MediaActions.Select(m => m.Label).ToArray();
            if (Everest60RemapData.MediaActions.Length > 0) CbEv60MediaAction.SelectedIndex = 0;
        }
        finally { _suppress = false; }

        UpdateModeVisibility();
    }

    /// <summary>Called by MainWindow when a key on the 64-key overlay is
    /// clicked while the Key Binding section is active.</summary>
    internal void SelectKey(int ledIndex, string label)
    {
        _selectedLedIndex = ledIndex;
        var table = Everest60RemapData.LedIndexToDllKeyIdArray;
        _selectedDllKeyId = ledIndex >= 0 && ledIndex < table.Length ? table[ledIndex] : -1;
        LblEv60SelectedKey.Text = _selectedDllKeyId >= 0
            ? $"{label}  (DLLKeyId {_selectedDllKeyId})"
            : $"{label}  ({Loc.Get("ev60_key_binding_unmapped")})";
        LblEv60RemapStatus.Text = "";
    }

    private void CbEv60RemapMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        UpdateModeVisibility();
    }

    private void UpdateModeVisibility()
    {
        string mode = (CbEv60RemapMode.SelectedItem as RemapModeChoice)?.Key ?? "key";
        PnlEv60RemapTargetKey.Visibility = mode is "key" or "fn" ? Visibility.Visible : Visibility.Collapsed;
        PnlEv60RemapShortcut.Visibility  = mode == "shortcut" ? Visibility.Visible : Visibility.Collapsed;
        PnlEv60RemapMedia.Visibility     = mode == "media" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnEv60RemapApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDllKeyId < 0)
        {
            LblEv60RemapStatus.Text = Loc.Get("ev60_key_binding_select_first");
            LblEv60RemapStatus.Foreground = (Brush)FindResource("K2DangerBrush");
            return;
        }
        if (!_sdk.IsOpen)
        {
            LblEv60RemapStatus.Text = Loc.Get("ev60_key_binding_not_open");
            LblEv60RemapStatus.Foreground = (Brush)FindResource("K2DangerBrush");
            return;
        }

        string mode = (CbEv60RemapMode.SelectedItem as RemapModeChoice)?.Key ?? "key";
        bool ok;
        int value = -1, mask = 0;
        switch (mode)
        {
            case "fn":
            {
                string? label = CbEv60TargetKey.SelectedItem as string;
                int target = label != null ? Everest60RemapData.KeyCatalog.GetValueOrDefault(label, -1) : -1;
                if (target < 0) { ShowMissingTarget(); return; }
                ok = _sdk.ChangeFnKey(_selectedDllKeyId, target);
                _log($"[KeyBind] ChangeFnKey({_selectedDllKeyId} -> {target}) -> {ok}");
                value = target;
                break;
            }
            case "shortcut":
            {
                string? label = CbEv60ShortcutKey.SelectedItem as string;
                int target = label != null ? Everest60RemapData.KeyCatalog.GetValueOrDefault(label, -1) : -1;
                if (target < 0) { ShowMissingTarget(); return; }
                mask = (CkEv60ModCtrl.IsChecked  == true ? Everest60RemapData.ModCtrl  : 0)
                     | (CkEv60ModShift.IsChecked == true ? Everest60RemapData.ModShift : 0)
                     | (CkEv60ModAlt.IsChecked   == true ? Everest60RemapData.ModAlt   : 0)
                     | (CkEv60ModWin.IsChecked   == true ? Everest60RemapData.ModWin   : 0);
                ok = _sdk.ChangeShortcutKey(_selectedDllKeyId, target, mask);
                _log($"[KeyBind] ChangeShortcutKey({_selectedDllKeyId} -> {target}, mask=0x{mask:X}) -> {ok}");
                value = target;
                break;
            }
            case "media":
            {
                int idx = CbEv60MediaAction.SelectedIndex;
                if (idx < 0 || idx >= Everest60RemapData.MediaActions.Length) { ShowMissingTarget(); return; }
                int code = Everest60RemapData.MediaActions[idx].Code;
                ok = _sdk.SetMediaKey(_selectedDllKeyId, code);
                _log($"[KeyBind] SetMediaKey({_selectedDllKeyId}, code={code}) -> {ok}");
                value = code;
                break;
            }
            default: // "key"
            {
                string? label = CbEv60TargetKey.SelectedItem as string;
                int target = label != null ? Everest60RemapData.KeyCatalog.GetValueOrDefault(label, -1) : -1;
                if (target < 0) { ShowMissingTarget(); return; }
                ok = _sdk.ChangeKey(_selectedDllKeyId, target);
                _log($"[KeyBind] ChangeKey({_selectedDllKeyId} -> {target}) -> {ok}");
                value = target;
                break;
            }
        }

        if (ok)
        {
            _bindings[_selectedLedIndex] = (mode, value, mask);
            _ev60Store?.SaveKeyBinding(CurrentSlot, _selectedLedIndex, mode, value, mask);
        }

        LblEv60RemapStatus.Text = ok ? Loc.Get("ev60_remap_applied") : Loc.Get("ev60_remap_failed");
        LblEv60RemapStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void ShowMissingTarget()
    {
        LblEv60RemapStatus.Text = Loc.Get("ev60_key_binding_select_target");
        LblEv60RemapStatus.Foreground = (Brush)FindResource("K2DangerBrush");
    }

    private void BtnEv60RemapReset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDllKeyId < 0 || !_sdk.IsOpen) return;
        bool ok = _sdk.ChangeKey(_selectedDllKeyId, Everest60RemapData.DisabledKeyId);
        _log($"[KeyBind] ChangeKey({_selectedDllKeyId} -> {Everest60RemapData.DisabledKeyId} [reset]) -> {ok}");
        if (ok)
        {
            _bindings.Remove(_selectedLedIndex);
            _ev60Store?.RemoveKeyBinding(CurrentSlot, _selectedLedIndex);
        }
        LblEv60RemapStatus.Text = ok ? Loc.Get("ev60_remap_reset_done") : Loc.Get("ev60_remap_failed");
        LblEv60RemapStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    private void BtnEv60RemapSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_sdk.IsOpen) return;
        bool ok = _sdk.SaveFlash();
        _log($"[KeyBind] SaveFlash() -> {ok}");
        LblEv60RemapStatus.Text = ok ? Loc.Get("ev60_remap_applied") : Loc.Get("ev60_remap_failed");
        LblEv60RemapStatus.Foreground = ok ? (Brush)FindResource("K2AccentBrush") : (Brush)FindResource("K2DangerBrush");
    }

    // ------------------------------------------------------------
    // Profile switch: replay every stored binding of this slot to firmware.
    // Does NOT call SaveFlash (see _PROJECT_MAP.md rationale — avoids wearing
    // the keyboard's flash on every switch; permanent save stays behind the
    // manual Save button above). Called by MainWindow.Everest60.cs on combo
    // switch, module init, and whenever the Key Binding section is opened
    // (the SDK session itself opens lazily there, see Ev60Section_Changed).
    // ------------------------------------------------------------

    internal void Ev60ReloadKeyBindings(int slot)
    {
        if (_ev60Store is null) return;
        _bindings.Clear();
        var stored = _ev60Store.LoadKeyBindings(slot);
        foreach (var b in stored)
            _bindings[b.LedIndex] = (b.Mode, b.Value, b.ModifierMask);

        if (!_sdk.IsOpen)
        {
            _log("[PROFILE] reload key bindings: SDK session not open yet, will apply when opened");
            return;
        }

        int applied = 0;
        foreach (var kv in _bindings)
        {
            int ledIndex = kv.Key;
            var (mode, value, mask) = kv.Value;
            var table = Everest60RemapData.LedIndexToDllKeyIdArray;
            if (ledIndex < 0 || ledIndex >= table.Length) continue;
            int srcDllKeyId = table[ledIndex];

            bool ok = mode switch
            {
                "fn"       => _sdk.ChangeFnKey(srcDllKeyId, value),
                "shortcut" => _sdk.ChangeShortcutKey(srcDllKeyId, value, mask),
                "media"    => _sdk.SetMediaKey(srcDllKeyId, value),
                _          => _sdk.ChangeKey(srcDllKeyId, value),
            };
            if (ok) applied++;
        }
        _log($"[PROFILE] reload key bindings slot={slot}: {applied}/{_bindings.Count} applied");
    }
}
