using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using K2.Core;
using Microsoft.Win32;

namespace K2.App.Services;

/// <summary>
/// Shared "export profiles" flow for DisplayPad/MacroPad/Everest tabs: opens
/// <see cref="ExportProfilesDialog"/> (checkbox picker + format choice), then either
/// prompts for a single file name (one profile selected) or a target folder and writes
/// one file per profile named "{device}_{profile}.xml" (multiple profiles selected).
/// </summary>
internal static class ExportProfileHelper
{
    public static void Run(
        Window owner,
        string deviceLabel,
        IReadOnlyList<(int Slot, string Name)> profiles,
        int? currentSlot,
        Func<int, string, bool, string, (int Exported, int SkippedActions, IReadOnlyList<string> SkipReasons)> exportOne,
        Action<string> log,
        Action<string> setStatus)
    {
        if (profiles.Count == 0) { setStatus(Loc.Get("dp_export_no_profile")); return; }

        var dlg = new ExportProfilesDialog(profiles, currentSlot) { Owner = owner };
        if (dlg.ShowDialog() != true) return;

        var selected = dlg.SelectedProfiles.ToList();
        if (selected.Count == 0) return;
        bool bcCompatible = dlg.BcCompatible;

        void RunOne(int slot, string name, string path)
        {
            try
            {
                var (exported, skipped, reasons) = exportOne(slot, name, bcCompatible, path);
                if (bcCompatible)
                {
                    setStatus(Loc.Get("dp_exported_bc", name, exported, skipped));
                    log($"[EXP-BC] '{name}' -> {path}: {exported} actions, {skipped} skipped");
                    foreach (var reason in reasons) log($"[EXP-BC] skip: {reason}");
                }
                else
                {
                    setStatus(Loc.Get("dp_exported_k2", name, exported));
                    log($"[EXP-K2] '{name}' -> {path}: {exported} actions");
                }
            }
            catch (Exception ex)
            {
                log($"[ERR] export XML: {ex.Message}");
            }
        }

        if (selected.Count == 1)
        {
            var p = selected[0];
            var save = new SaveFileDialog
            {
                Title    = bcCompatible ? Loc.Get("dp_save_bc_profile") : Loc.Get("dp_save_k2_profile"),
                Filter   = Loc.Get("dp_filter_bc_xml"),
                FileName = $"{SanitizeFileName(p.Name)}.xml",
            };
            if (save.ShowDialog(owner) != true) return;
            RunOne(p.Slot, p.Name, save.FileName);
        }
        else
        {
            var folder = new OpenFolderDialog { Title = Loc.Get("export_pick_folder") };
            if (folder.ShowDialog(owner) != true) return;
            string safeDevice = SanitizeFileName(deviceLabel);
            foreach (var p in selected)
            {
                string path = Path.Combine(folder.FolderName, $"{safeDevice}_{SanitizeFileName(p.Name)}.xml");
                RunOne(p.Slot, p.Name, path);
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
