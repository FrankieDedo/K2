using System;
using System.Windows;
using K2.DisplayPad.Dialogs;
using K2.DisplayPad.Models;
using K2.DisplayPad.Services;
using Microsoft.Win32;

namespace K2.DisplayPad;

public partial class MainWindow
{
    // ============================================================
    // Action execution on press
    // ============================================================

    /// <summary>
    /// Executes the action configured on a button. The actual logic lives in
    /// the shared action engine <see cref="K2.Core.ButtonActionEngine"/>;
    /// here we just pass the type, value and index of the button.
    /// </summary>
    private void TryExecuteAction(ButtonCell cell)
        => _engine?.Execute(cell.ActionType, cell.ActionValue, cell.Index);

    /// <summary>
    /// DisplayPad profile switch — device-specific operation invoked by the
    /// shared engine via <see cref="K2.Core.IActionHost.SwitchProfile"/>.
    /// </summary>
    private void ExecuteProfileSwitch(string target)
    {
        if (CbDevice.SelectedItem is not int id) { Log("[EXEC] profile: no device selected"); return; }
        int current = CurrentProfile();
        int next = current;
        var t = (target ?? "").Trim();
        if (t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Next Profile", StringComparison.OrdinalIgnoreCase))
            next = current == DisplayPadService.ProfileCount ? 1 : current + 1;
        else if (t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
                 t.Equals("Previous Profile", StringComparison.OrdinalIgnoreCase))
            next = current == 1 ? DisplayPadService.ProfileCount : current - 1;
        else if (int.TryParse(t, out var n) && n >= 1 && n <= DisplayPadService.ProfileCount)
            next = n;
        else { Log($"[EXEC] profile: target \"{t}\" not resolved"); return; }

        if (next == current) { Log($"[EXEC] profile: already on {current}"); return; }
        CbProfile.SelectedItem = next;
        Log($"[EXEC] profile -> {next}");
    }

    // ============================================================
    // Key mapping
    // ============================================================

    private void BtnMapKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_mapAwaitingIndex >= 0)
        {
            _mapAwaitingIndex = -1;
            LblMapKeysText.Text = "Rimappa tasti";
            LblStatus.Text = "Mappatura annullata, ripristinata la default.";
            ApplyDefaultKeyMap();
            return;
        }
        _matrixToIndex.Clear();
        foreach (var c in _cells) c.KeyMatrix = null;
        _mapAwaitingIndex = 0;
        LblMapKeysText.Text = "Annulla";
        LblStatus.Text = "Mappatura: premi il tasto fisico per la cella #0…";
        Log("[MAP ] starting key remapping procedure");
    }

    private void ApplyDefaultKeyMap()
    {
        _matrixToIndex.Clear();
        foreach (var (index, matrix) in DefaultKeyMap)
        {
            _matrixToIndex[matrix] = index;
            if (index < _cells.Length) _cells[index].KeyMatrix = matrix;
        }
    }

    // ============================================================
    // BaseCamp profile import (XML)
    // ============================================================

    private void BtnImportXml_Click(object sender, RoutedEventArgs e)
    {
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] Select a device first."); return; }

        var dlgOpen = new OpenFileDialog
        {
            Title  = "Apri profilo BaseCamp",
            Filter = "Profilo BaseCamp (*.xml)|*.xml|Tutti i file|*.*"
        };
        if (dlgOpen.ShowDialog(this) != true) return;

        int defaultSlot = 1;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(dlgOpen.FileName);
            if (int.TryParse(doc.Root?.Element("Id")?.Value, out var idFromXml)
                && idFromXml >= 1 && idFromXml <= DisplayPadService.ProfileCount)
                defaultSlot = idFromXml;
        }
        catch (Exception ex) { Log($"[ERR ] preliminary XML read: {ex.Message}"); return; }

        var picker = new ImportProfileDialog(dlgOpen.FileName, defaultSlot, DisplayPadService.ProfileCount) { Owner = this };
        if (picker.ShowDialog() != true) return;
        int slot = picker.SelectedSlot;

        try
        {
            var importer = new BaseCampProfileImporter();
            // Writing to the FW profile (SetIconPic) requires AP disabled:
            // this tells the firmware "you're about to receive your updated profile".
            try { _service.APEnable(id, false); } catch { /* ignore */ }

            var result = importer.Import(dlgOpen.FileName, id, slot, _service, _store, _rotation);
            Log($"[IMP ] '{result.ProfileName}' -> device {id} slot {slot}: " +
                $"{result.ImportedCells.Count} buttons, {result.Skipped.Count} skip, {result.Errors.Count} errors");
            foreach (var s in result.Skipped) Log("[IMP ]   skip: " + s);
            foreach (var er in result.Errors) Log("[IMP ]   err : " + er);

            if (picker.SwitchAfterImport)
            {
                _suppressProfileUpdate = true;
                try { CbProfile.SelectedItem = slot; }
                finally { _suppressProfileUpdate = false; }
                _service.SwitchProfile(id, slot);
                _store.SetCurrentProfile(id, slot);
            }
            ReloadCurrentProfile();
            LblStatus.Text = $"Importato '{result.ProfileName}' nello slot {slot}.";
        }
        catch (Exception ex)
        {
            Log($"[ERR ] import: {ex}");
            LblStatus.Text = "Import fallito — vedi log.";
        }
    }
}
