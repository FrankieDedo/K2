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
    // Esecuzione azione al press
    // ============================================================

    /// <summary>
    /// Esegue l'azione configurata su un tasto. La logica vera vive nel
    /// motore azioni condiviso <see cref="K2.Core.ButtonActionEngine"/>;
    /// qui si passa solo tipo, valore e indice del tasto.
    /// </summary>
    private void TryExecuteAction(ButtonCell cell)
        => _engine?.Execute(cell.ActionType, cell.ActionValue, cell.Index);

    /// <summary>
    /// Cambio profilo del DisplayPad — operazione device-specific invocata dal
    /// motore condiviso tramite <see cref="K2.Core.IActionHost.SwitchProfile"/>.
    /// </summary>
    private void ExecuteProfileSwitch(string target)
    {
        if (CbDevice.SelectedItem is not int id) { Log("[EXEC] profile: nessun device selezionato"); return; }
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
        else { Log($"[EXEC] profile: target \"{t}\" non risolto"); return; }

        if (next == current) { Log($"[EXEC] profile: gia' su {current}"); return; }
        CbProfile.SelectedItem = next;
        Log($"[EXEC] profile -> {next}");
    }

    // ============================================================
    // Mappa tasti
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
        Log("[MAP ] avvio procedura di rimappatura tasti");
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
    // Import profilo BaseCamp (XML)
    // ============================================================

    private void BtnImportXml_Click(object sender, RoutedEventArgs e)
    {
        if (CbDevice.SelectedItem is not int id) { Log("[WARN] Seleziona prima un device."); return; }

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
        catch (Exception ex) { Log($"[ERR ] lettura preliminare XML: {ex.Message}"); return; }

        var picker = new ImportProfileDialog(dlgOpen.FileName, defaultSlot, DisplayPadService.ProfileCount) { Owner = this };
        if (picker.ShowDialog() != true) return;
        int slot = picker.SelectedSlot;

        try
        {
            var importer = new BaseCampProfileImporter();
            // Per scrivere nel profilo FW (SetIconPic) serve AP disabilitato:
            // diciamo al firmware "stai per ricevere il tuo profilo aggiornato".
            try { _service.APEnable(id, false); } catch { /* ignore */ }

            var result = importer.Import(dlgOpen.FileName, id, slot, _service, _store, _rotation);
            Log($"[IMP ] '{result.ProfileName}' -> device {id} slot {slot}: " +
                $"{result.ImportedCells.Count} tasti, {result.Skipped.Count} skip, {result.Errors.Count} errori");
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
