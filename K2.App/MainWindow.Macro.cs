// MainWindow.Macro.cs — partial class: "Keyboard Macro" panel.
// Top-level section (PnlMacro in MainWindow.xaml), reachable via its own
// nav button next to Settings — not tied to any single device tab.
// Handles recording, playback, and persistence of macros.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    private MacroStore? _macroStore;
    private MacroRecorder? _macroRecorder;
    private MacroPlayer? _macroPlayer;
    private readonly ObservableCollection<MacroDefinition> _macros = new();
    private readonly ObservableCollection<MacroInputRow> _macroInputRows = new();
    private readonly ObservableCollection<MacroAssignment> _macroAssignments = new();
    private bool _macroLoading;
    private bool _macroShowPressRelease = true;
    private MacroDefinition? _recordingMacro;

    // ─────────────────────── Init ───────────────────────

    private void InitMacroPanel()
    {
        try
        {
            _macroStore = new MacroStore();
            _macroRecorder = new MacroRecorder();
            _macroRecorder.InputRecorded += OnMacroInputRecorded;
            _macroPlayer = new MacroPlayer();

            _macroPlayer.PlaybackStarted += () => Dispatcher.Invoke(() =>
            {
                BtnMacroPlay.IsEnabled = false;
                BtnMacroStop.IsEnabled = true;
            });
            _macroPlayer.PlaybackStopped += () => Dispatcher.Invoke(() =>
            {
                BtnMacroPlay.IsEnabled = true;
                BtnMacroStop.IsEnabled = false;
            });

            LbMacros.ItemsSource = _macros;
            LvMacroInputs.ItemsSource = _macroInputRows;
            LvMacroAssignments.ItemsSource = _macroAssignments;
            CkMacroShowPressRelease.IsChecked = _macroShowPressRelease;
            RefreshMacroList();
            BtnMacroStop.IsEnabled = false;
        }
        catch (Exception ex)
        {
            LogEverest($"[MACRO] Init error: {ex.Message}");
        }
    }

    private void RefreshMacroList()
    {
        _macroLoading = true;
        try
        {
            _macros.Clear();
            if (_macroStore is null) return;
            foreach (var m in _macroStore.GetAll())
                _macros.Add(m);
        }
        finally
        {
            _macroLoading = false;
        }
    }

    private MacroDefinition? SelectedMacro =>
        LbMacros.SelectedItem as MacroDefinition;

    /// <summary>Called when the Macro section is opened (<c>BtnMacroTab_Click</c>) —
    /// always jumps to the first macro in the library. No-op while a recording
    /// is in progress, so opening the tab mid-capture can't yank the selection
    /// out from under the recorder.</summary>
    internal void SelectFirstMacro()
    {
        if (_macroRecorder?.IsRecording == true) return;
        if (_macros.Count > 0)
            LbMacros.SelectedIndex = 0;
    }

    // ─────────────────────── Event handlers ───────────────────────

    private void LbMacros_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_macroLoading) return;
        if (_macroRecorder?.IsRecording == true)
        {
            // Can't switch macros mid-recording — Stop() assigns the capture
            // to whichever macro is selected at that point. Revert silently
            // rather than disabling the list (no visual state change).
            _macroLoading = true;
            LbMacros.SelectedItem = _recordingMacro;
            _macroLoading = false;
            return;
        }
        var m = SelectedMacro;
        SpMacroSettings.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
        if (m is null) return;

        _macroLoading = true;
        try
        {
            TxtMacroName.Text = m.Name;
            CkMacroMouse.IsChecked = m.RecordMouse;
            CkMacroMouseMovement.IsChecked = m.RecordMouseMovement;
            CkMacroMouseMovement.IsEnabled = m.RecordMouse;
            CkMacroKeyboard.IsChecked = m.RecordKeyboard;

            switch (m.DelayOption)
            {
                case MacroDelay.Custom:  RbMacroDelayCustom.IsChecked  = true; break;
                case MacroDelay.NoDelay: RbMacroDelayNone.IsChecked    = true; break;
                default:                 RbMacroDelayRecorded.IsChecked = true; break;
            }
            TxtMacroCustomDelay.Text = m.CustomDelayMs.ToString();

            switch (m.PlaybackOption)
            {
                case MacroPlayback.RepeatN:   RbMacroPlaybackRepeat.IsChecked = true; break;
                case MacroPlayback.WhileHeld: RbMacroPlaybackHold.IsChecked   = true; break;
                case MacroPlayback.Toggle:    RbMacroPlaybackToggle.IsChecked = true; break;
                default:                      RbMacroPlaybackOnce.IsChecked   = true; break;
            }
            TxtMacroRepeatN.Text = m.RepeatCount.ToString();

            TblMacroInputCount.Text = Loc.Get("macro_actions_count", m.Inputs.Count);
            RebuildInputRows();
            RefreshMacroAssignments();
        }
        finally
        {
            _macroLoading = false;
        }
    }

    private void BtnMacroNew_Click(object sender, RoutedEventArgs e)
    {
        if (_macroStore is null || _macroRecorder?.IsRecording == true) return;
        var m = new MacroDefinition
        {
            Name = $"Macro {_macros.Count + 1}",
            Order = _macros.Count
        };
        m.Id = _macroStore.Insert(m);
        _macros.Add(m);
        LbMacros.SelectedItem = m;
    }

    private void BtnMacroDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var m = SelectedMacro;
        if (m is null || _macroStore is null || _macroRecorder?.IsRecording == true) return;
        var copy = m.Clone(Loc.Get("macro_duplicate_name", m.Name));
        copy.Order = _macros.Count;
        copy.Id = _macroStore.Insert(copy);
        _macros.Add(copy);
        LbMacros.SelectedItem = copy;
    }

    private void BtnMacroDelete_Click(object sender, RoutedEventArgs e)
    {
        var m = SelectedMacro;
        if (m is null || _macroStore is null || _macroRecorder?.IsRecording == true) return;
        _macroStore.Delete(m.Id);
        _macros.Remove(m);
        SpMacroSettings.Visibility = Visibility.Collapsed;
    }

    private void BtnMacroRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRecorder is null) return;
        var m = SelectedMacro;
        if (m is null)
        {
            MessageBox.Show(Loc.Get("macro_select_first"),
                "K2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_macroRecorder.IsRecording)
        {
            // Stop recording
            var inputs = _macroRecorder.Stop();
            m.Inputs = inputs;
            _recordingMacro = null;
            TblMacroInputCount.Text = Loc.Get("macro_actions_count", inputs.Count);
            BtnMacroRecord.Content = Loc.Get("macro_start_recording");
            RebuildInputRows();
            RefreshMacroAssignments();
            SaveCurrentMacro();
            LogEverest($"[MACRO] Recording stopped: {inputs.Count} actions");
        }
        else
        {
            // Start recording
            bool mouse = CkMacroMouse.IsChecked == true;
            bool keyboard = CkMacroKeyboard.IsChecked == true;
            bool mouseMovement = CkMacroMouseMovement.IsChecked == true;
            _recordingMacro = m;
            _macroInputRows.Clear();
            TblMacroInputCount.Text = Loc.Get("macro_actions_count", 0);
            _macroRecorder.SetOwnerWindow(_hWnd);
            _macroRecorder.Start(mouse, keyboard, mouseMovement);
            BtnMacroRecord.Content = Loc.Get("macro_record_stop");
            LogEverest("[MACRO] Recording started" + (mouse ? " (with mouse)" : ""));
        }
    }

    /// <summary>Live feed for the INPUTS list while recording — appends each
    /// captured input as it happens. Runs on the UI thread: the low-level
    /// keyboard/mouse hooks fire on the thread that installed them, which is
    /// always the UI thread here (recording is only ever started from a
    /// button click).</summary>
    private void OnMacroInputRecorded(MacroInput input)
    {
        if (_macroRecorder is null) return;
        int index = _macroRecorder.Inputs.Count - 1;
        if (index < 0) return;
        var row = BuildInputRow(input, index);
        _macroInputRows.Add(row);
        TblMacroInputCount.Text = Loc.Get("macro_actions_count", _macroRecorder.Inputs.Count);
        if (LvMacroInputs.Items.Count > 0)
            LvMacroInputs.ScrollIntoView(LvMacroInputs.Items[^1]);
    }

    private void BtnMacroPlay_Click(object sender, RoutedEventArgs e)
    {
        var m = SelectedMacro;
        if (m is null || _macroPlayer is null) return;
        if (m.Inputs.Count == 0)
        {
            MessageBox.Show(Loc.Get("macro_no_actions"),
                "K2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _macroPlayer.Play(m);
        LogEverest($"[MACRO] Playback: {m.Name} ({m.Inputs.Count} actions)");
    }

    private void BtnMacroStop_Click(object sender, RoutedEventArgs e)
    {
        _macroPlayer?.Stop();
        LogEverest("[MACRO] Playback stopped");
    }

    private void TxtMacroName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        if (m is null) return;
        m.Name = TxtMacroName.Text;
        // Update display in the list
        int idx = LbMacros.SelectedIndex;
        if (idx >= 0)
        {
            _macros[idx] = m; // trigger refresh
            LbMacros.SelectedIndex = idx;
        }
        SaveCurrentMacro();
        RefreshMacroAssignments();
    }

    private void CkMacroDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_macroLoading) return;
        CkMacroMouseMovement.IsEnabled = CkMacroMouse.IsChecked == true;
        SaveCurrentMacro();
    }

    private void RbMacroDelay_Checked(object sender, RoutedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        if (m is null) return;
        if (ReferenceEquals(sender, RbMacroDelayCustom)) m.DelayOption = MacroDelay.Custom;
        else if (ReferenceEquals(sender, RbMacroDelayNone)) m.DelayOption = MacroDelay.NoDelay;
        else m.DelayOption = MacroDelay.Recorded;
        SaveCurrentMacro();
    }

    private void TxtMacroCustomDelay_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_macroLoading) return;
        SaveCurrentMacro();
    }

    private void RbMacroPlayback_Checked(object sender, RoutedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        if (m is null) return;
        if (ReferenceEquals(sender, RbMacroPlaybackRepeat)) m.PlaybackOption = MacroPlayback.RepeatN;
        else if (ReferenceEquals(sender, RbMacroPlaybackHold)) m.PlaybackOption = MacroPlayback.WhileHeld;
        else if (ReferenceEquals(sender, RbMacroPlaybackToggle)) m.PlaybackOption = MacroPlayback.Toggle;
        else m.PlaybackOption = MacroPlayback.Once;
        SaveCurrentMacro();
    }

    private void TxtMacroRepeatN_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_macroLoading) return;
        SaveCurrentMacro();
    }

    private void CkMacroShowPressRelease_Click(object sender, RoutedEventArgs e)
    {
        _macroShowPressRelease = CkMacroShowPressRelease.IsChecked == true;
        RebuildInputRows();
    }

    private void BtnMacroInputDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRecorder?.IsRecording == true) return;
        if (sender is not Button { CommandParameter: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex < 0 || row.SourceIndex >= m.Inputs.Count) return;
        m.Inputs.RemoveAt(row.SourceIndex);
        RebuildInputRows();
        TblMacroInputCount.Text = Loc.Get("macro_actions_count", m.Inputs.Count);
        SaveCurrentMacro();
    }

    private void BtnMacroInputMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRecorder?.IsRecording == true) return;
        if (sender is not Button { CommandParameter: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex <= 0 || row.SourceIndex >= m.Inputs.Count) return;
        (m.Inputs[row.SourceIndex - 1], m.Inputs[row.SourceIndex]) =
            (m.Inputs[row.SourceIndex], m.Inputs[row.SourceIndex - 1]);
        RebuildInputRows();
        SaveCurrentMacro();
    }

    private void BtnMacroInputMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRecorder?.IsRecording == true) return;
        if (sender is not Button { CommandParameter: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex < 0 || row.SourceIndex >= m.Inputs.Count - 1) return;
        (m.Inputs[row.SourceIndex + 1], m.Inputs[row.SourceIndex]) =
            (m.Inputs[row.SourceIndex], m.Inputs[row.SourceIndex + 1]);
        RebuildInputRows();
        SaveCurrentMacro();
    }

    private void BtnMacroImportBC_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRecorder?.IsRecording == true) return;
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            LogEverest("[MACRO] BaseCamp.db not found.");
            MessageBox.Show(Loc.Get("dp_bc_db_not_found"), Loc.Get("error"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var bcMacros = MacroStore.ReadFromBaseCampDb(dbPath);
            if (bcMacros.Count == 0)
            {
                MessageBox.Show(Loc.Get("macro_none_in_bc"),
                    "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                Loc.Get("macro_import_confirm", bcMacros.Count),
                "Import from BaseCamp", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            _macroStore?.DeleteAll();
            int imported = _macroStore?.ImportAll(bcMacros) ?? 0;
            RefreshMacroList();
            LogEverest($"[MACRO] Imported {imported} macros from BaseCamp.db");
            MessageBox.Show(Loc.Get("macro_imported", imported), "Import", MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            LogEverest($"[MACRO] Import error: {ex.Message}");
            MessageBox.Show(Loc.Get("macro_import_error", ex.Message), Loc.Get("error"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─────────────────────── Helpers ───────────────────────

    private void SaveCurrentMacro()
    {
        var m = SelectedMacro;
        if (m is null || _macroStore is null) return;
        if (int.TryParse(TxtMacroCustomDelay.Text, out int cd))
            m.CustomDelayMs = cd;
        if (int.TryParse(TxtMacroRepeatN.Text, out int rn))
            m.RepeatCount = rn;
        m.RecordMouse = CkMacroMouse.IsChecked == true;
        m.RecordMouseMovement = CkMacroMouseMovement.IsChecked == true;
        m.RecordKeyboard = CkMacroKeyboard.IsChecked == true;
        m.ModifiedAt = DateTime.Now;
        _macroStore.Update(m);
    }

    /// <summary>Rebuilds the recorded-inputs list shown in the INPUTS section.
    /// While recording, rebuilds from the recorder's own live capture instead
    /// of <c>SelectedMacro.Inputs</c> (which is stale until <c>Stop()</c>
    /// returns) — so toggling "Show press/release" mid-recording doesn't
    /// blank out the list that's being filled in real time.</summary>
    private void RebuildInputRows()
    {
        _macroInputRows.Clear();
        if (_macroRecorder?.IsRecording == true)
        {
            for (int i = 0; i < _macroRecorder.Inputs.Count; i++)
                _macroInputRows.Add(BuildInputRow(_macroRecorder.Inputs[i], i));
            return;
        }
        var m = SelectedMacro;
        if (m is null) return;
        for (int i = 0; i < m.Inputs.Count; i++)
            _macroInputRows.Add(BuildInputRow(m.Inputs[i], i));
    }

    private MacroInputRow BuildInputRow(MacroInput inp, int index)
    {
        bool isPress = inp.Type is "keydown" or "mousedown";
        string glyph = inp.Type switch
        {
            "keydown" or "keyup" => "",                        // keyboard
            "mousedown" or "mouseup" or "mousemove" => "",     // mouse
            "text" => "",                                      // text
            _ => ""
        };
        string label = inp.Type switch
        {
            "keydown" or "keyup" => KeyName(inp.Key),
            "mousedown" or "mouseup" => inp.Key switch
            {
                1 => "Left Click",
                2 => "Right Click",
                _ => $"Mouse {inp.Key}"
            },
            "mousemove" => $"Move ({inp.X},{inp.Y})",
            "text" => $"\"{inp.Text}\"",
            _ => inp.Type
        };
        return new MacroInputRow
        {
            Number = index + 1,
            Glyph = glyph,
            Label = label,
            DelayMs = inp.DelayMs,
            IsPress = isPress,
            ShowIndicator = _macroShowPressRelease,
            SourceIndex = index
        };
    }

    /// <summary>
    /// Friendly, keyboard-printed key names — not .NET's internal
    /// <see cref="System.Windows.Forms.Keys"/> spelling, which calls Alt
    /// "Menu"/"LMenu"/"RMenu", Ctrl "ControlKey"/"LControlKey"/"RControlKey",
    /// Enter "Return", Backspace "Back", etc. Right Alt in particular is
    /// what users know as "Alt Gr" on ISO/international keyboards, not
    /// "RMenu" — showing the system name there is what prompted this fix.
    /// </summary>
    private static string KeyName(int vk)
    {
        switch (vk)
        {
            case 0x12: case 0xA4: return "Alt";        // VK_MENU / VK_LMENU
            case 0xA5: return "Alt Gr";                // VK_RMENU
            case 0x11: case 0xA2: case 0xA3: return "Ctrl";   // VK_CONTROL/L/R
            case 0x10: case 0xA0: case 0xA1: return "Shift";  // VK_SHIFT/L/R
            case 0x5B: case 0x5C: return "Win";        // VK_LWIN / VK_RWIN
            case 0x5D: return "Menu";                  // VK_APPS (context menu key)
            case 0x0D: return "Enter";
            case 0x1B: return "Esc";
            case 0x08: return "Backspace";
            case 0x14: return "Caps Lock";
            case 0x90: return "Num Lock";
            case 0x91: return "Scroll Lock";
            case 0x2C: return "Print Screen";
            case 0x21: return "Page Up";
            case 0x22: return "Page Down";
        }

        var key = (System.Windows.Forms.Keys)vk;
        string s = key.ToString();
        // "D1".."D0" -> plain digit
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
        return s;
    }

    /// <summary>
    /// Scans the MacroPad, Everest and DisplayPad key stores for keys whose action is
    /// this macro (ActionType == "macro", ActionValue == macro name — the same convention
    /// <see cref="BaseCampDbImporter"/> uses when importing "Macro" bindings from
    /// BaseCamp.db, and that <see cref="ButtonActionDialog"/>'s "macro" action type now
    /// also writes when assigned directly in-app).
    /// </summary>
    private void RefreshMacroAssignments()
    {
        _macroAssignments.Clear();
        var m = SelectedMacro;
        if (m is null || string.IsNullOrWhiteSpace(m.Name)) return;

        try
        {
            foreach (var (deviceId, profile, keyIndex) in _store.GetKeysByAction("macro", m.Name))
            {
                string profileName = _store.GetProfileName(deviceId, profile) ?? Loc.Get("profile_n", profile);
                _macroAssignments.Add(new MacroAssignment
                {
                    KeyLabel = $"M{keyIndex + 1}",
                    Subtitle = $"{Loc.Get("tab_macropad")} #{deviceId} · {profileName}"
                });
            }
        }
        catch (Exception ex)
        {
            LogEverest($"[MACRO] Assignment lookup (MacroPad) failed: {ex.Message}");
        }

        try
        {
            foreach (var (profile, keyMatrix, label) in _evStore.GetKeysByAction("macro", m.Name))
            {
                string profileName = _evStore.GetProfileName(profile) ?? Loc.Get("profile_n", profile);
                string keyLabel = string.IsNullOrWhiteSpace(label) ? $"0x{keyMatrix:X2}" : label;
                _macroAssignments.Add(new MacroAssignment
                {
                    KeyLabel = keyLabel,
                    Subtitle = $"{Loc.Get("tab_everest")} · {profileName}"
                });
            }
        }
        catch (Exception ex)
        {
            LogEverest($"[MACRO] Assignment lookup (Everest) failed: {ex.Message}");
        }

        try
        {
            foreach (var (deviceId, profile, pageId, keyIndex) in _dpStore.GetKeysByAction("macro", m.Name))
            {
                string profileName = _dpStore.GetProfileName(deviceId, profile) ?? Loc.Get("profile_n", profile);
                string page = pageId == 0 ? "" : $" · {_dpStore.GetFolderName(pageId) ?? $"Page {pageId}"}";
                _macroAssignments.Add(new MacroAssignment
                {
                    KeyLabel = $"D{keyIndex + 1}",
                    Subtitle = $"{Loc.Get("tab_displaypad")} #{deviceId} · {profileName}{page}"
                });
            }
        }
        catch (Exception ex)
        {
            LogEverest($"[MACRO] Assignment lookup (DisplayPad) failed: {ex.Message}");
        }

        TblMacroNotAssigned.Visibility = _macroAssignments.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────── IActionHost bridge (macro playback) ───────────────────────

    /// <summary>Macro names available to <see cref="ButtonActionDialog"/>'s "macro" action
    /// picker — see <see cref="IActionHost.ListMacroNames"/>.</summary>
    internal IReadOnlyList<string> ListAllMacroNames() =>
        _macroStore is null
            ? Array.Empty<string>()
            : _macroStore.GetAll()
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

    /// <summary>Plays back the macro named <paramref name="macroName"/> — see
    /// <see cref="IActionHost.PlayMacro"/>, invoked by <see cref="ButtonActionEngine"/>
    /// when a key's action is "macro".</summary>
    internal void PlayMacroByName(string macroName)
    {
        if (_macroStore is null || _macroPlayer is null)
        {
            LogEverest($"[MACRO] not ready, can't play \"{macroName}\"");
            return;
        }
        var macro = _macroStore.GetAll().FirstOrDefault(m => m.Name == macroName);
        if (macro is null)
        {
            LogEverest($"[MACRO] \"{macroName}\" not found");
            return;
        }
        if (macro.Inputs.Count == 0)
        {
            LogEverest($"[MACRO] \"{macroName}\" has no recorded actions");
            return;
        }
        _macroPlayer.Play(macro);
        LogEverest($"[MACRO] Playback: {macro.Name} ({macro.Inputs.Count} actions)");
    }
}
