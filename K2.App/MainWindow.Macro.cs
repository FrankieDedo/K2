// MainWindow.Macro.cs — partial class: "Keyboard Macro" panel.
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
    private bool _macroLoading;

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
            CbMacroDelay.SelectedIndex = (int)m.DelayOption;
            CbMacroPlayback.SelectedIndex = (int)m.PlaybackOption;
            TxtMacroCustomDelay.Text = m.CustomDelayMs.ToString();
            TxtMacroRepeatN.Text = m.RepeatCount.ToString();
            CkMacroMouse.IsChecked = m.RecordMouse;
            TblMacroInputCount.Text = Loc.Get("macro_actions_count", m.Inputs.Count);
            UpdateDelayVisibility();
            UpdatePlaybackVisibility();
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
            BtnMacroRecord.Content = Loc.Get("macro_record");
            SaveCurrentMacro();
            LogEverest($"[MACRO] Recording stopped: {inputs.Count} actions");
        }
        else
        {
            // Start recording
            bool mouse = CkMacroMouse.IsChecked == true;
            _macroRecorder.Start(mouse);
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
    }

    private void CbMacroDelay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        if (m is null) return;
        m.DelayOption = (MacroDelay)CbMacroDelay.SelectedIndex;
        UpdateDelayVisibility();
        SaveCurrentMacro();
    }

    private void CbMacroPlayback_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_macroLoading) return;
        var m = SelectedMacro;
        if (m is null) return;
        m.PlaybackOption = (MacroPlayback)CbMacroPlayback.SelectedIndex;
        UpdatePlaybackVisibility();
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
        m.ModifiedAt = DateTime.Now;
        _macroStore.Update(m);
    }

    private void UpdateDelayVisibility()
    {
        bool showCustom = CbMacroDelay.SelectedIndex == 2; // Custom
        TxtMacroCustomDelay.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
        TblMacroMs.Visibility = showCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaybackVisibility()
    {
        bool showN = CbMacroPlayback.SelectedIndex == 1; // RepeatN
        TxtMacroRepeatN.Visibility = showN ? Visibility.Visible : Visibility.Collapsed;
    }
}
