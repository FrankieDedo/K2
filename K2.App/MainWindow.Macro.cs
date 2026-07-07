// MainWindow.Macro.cs — partial class: "Keyboard Macro" panel.
// Top-level section (PnlMacro in MainWindow.xaml), reachable via its own
// nav button next to Settings — not tied to any single device tab.
// Handles recording, playback, and persistence of macros.

using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using K2.App.Models;
using K2.App.Services;
using K2.Core;
using Microsoft.Win32;

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

    // ─────────────────────── Init ───────────────────────

    private void InitMacroPanel()
    {
        try
        {
            _macroStore = new MacroStore();
            _macroRecorder = new MacroRecorder();
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

    // ─────────────────────── Event handlers ───────────────────────

    private void LbMacros_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        SpMacroSettings.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
        if (m is null) return;

        _macroLoading = true;
        try
        {
            TxtMacroName.Text = m.Name;
            CkMacroMouse.IsChecked = m.RecordMouse;
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
        if (_macroStore is null) return;
        var m = new MacroDefinition
        {
            Name = $"Macro {_macros.Count + 1}",
            Order = _macros.Count
        };
        m.Id = _macroStore.Insert(m);
        _macros.Add(m);
        LbMacros.SelectedItem = m;
    }

    private void BtnMacroDelete_Click(object sender, RoutedEventArgs e)
    {
        var m = SelectedMacro;
        if (m is null || _macroStore is null) return;
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
            _macroRecorder.Start(mouse, keyboard);
            BtnMacroRecord.Content = Loc.Get("macro_record_stop");
            LogEverest("[MACRO] Recording started" + (mouse ? " (with mouse)" : ""));
        }
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
        if (sender is not FrameworkElement { Tag: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex < 0 || row.SourceIndex >= m.Inputs.Count) return;
        m.Inputs.RemoveAt(row.SourceIndex);
        RebuildInputRows();
        TblMacroInputCount.Text = Loc.Get("macro_actions_count", m.Inputs.Count);
        SaveCurrentMacro();
    }

    private void BtnMacroInputMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex <= 0 || row.SourceIndex >= m.Inputs.Count) return;
        (m.Inputs[row.SourceIndex - 1], m.Inputs[row.SourceIndex]) =
            (m.Inputs[row.SourceIndex], m.Inputs[row.SourceIndex - 1]);
        RebuildInputRows();
        SaveCurrentMacro();
    }

    private void BtnMacroInputMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MacroInputRow row }) return;
        var m = SelectedMacro;
        if (m is null || row.SourceIndex < 0 || row.SourceIndex >= m.Inputs.Count - 1) return;
        (m.Inputs[row.SourceIndex + 1], m.Inputs[row.SourceIndex]) =
            (m.Inputs[row.SourceIndex], m.Inputs[row.SourceIndex + 1]);
        RebuildInputRows();
        SaveCurrentMacro();
    }

    private void BtnMacroImportBC_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select BaseCamp.db",
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            InitialDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Mountain", "BaseCamp", "resources", "bin")
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var bcMacros = MacroStore.ReadFromBaseCampDb(dlg.FileName);
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
        m.RecordKeyboard = CkMacroKeyboard.IsChecked == true;
        m.ModifiedAt = DateTime.Now;
        _macroStore.Update(m);
    }

    /// <summary>Rebuilds the recorded-inputs list shown in the INPUTS section.</summary>
    private void RebuildInputRows()
    {
        _macroInputRows.Clear();
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

    private static string KeyName(int vk)
    {
        var key = (System.Windows.Forms.Keys)vk;
        string s = key.ToString();
        // "D1".."D0" -> plain digit
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
        return s;
    }

    /// <summary>
    /// Scans the MacroPad and Everest key stores for keys whose action is
    /// this macro (ActionType == "macro", ActionValue == macro name — the
    /// same convention <see cref="BaseCampDbImporter"/> uses when importing
    /// "Macro" bindings from BaseCamp.db). K2 doesn't yet let a user pick
    /// "Macro" as an action type from <c>ButtonActionDialog</c>, so today
    /// this only surfaces assignments brought in via BC import — the query
    /// is ready for when direct in-app assignment is added.
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

        TblMacroNotAssigned.Visibility = _macroAssignments.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
