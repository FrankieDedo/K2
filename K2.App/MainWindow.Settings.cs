// MainWindow.Settings.cs — partial class: centralized General Settings tab.
//
// Replaces the old per-device "Debug" checkboxes (Everest/MacroPad/DisplayPad):
// a single Debug toggle here now drives debug UI/behavior on every device via
// AppSettings.DebugMode. Logging is independent of the Debug flag — the Log
// level (Off/Normal/Verbose, default Normal) is always active/visible in this
// tab and controls logging verbosity across the app (see AppSettings.LogLevel —
// key-press logs and the LED-poll diagnostic log only fire at Verbose).

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Called once from the constructor (via Window.Loaded) — offers to import
    /// existing Base Camp profiles/settings the very first time K2 runs. Silently does
    /// nothing (no popup) if Base Camp isn't installed, so a user who never had it
    /// installed is never bothered. The flag is reset by "Restore all defaults" (see
    /// BtnAppRestoreDefaults_Click), so the prompt fires again after the following
    /// restart; it can also be forced again any time from the Settings tab.</summary>
    private void CheckFirstRunBcImport()
    {
        if (AppSettings.BcImportPromptShown) return;
        AppSettings.SetBcImportPromptShown(true);
        RunBaseCampImportPrompt(silentIfNotFound: true);
    }

    /// <summary>"Import from Base Camp" button in the Settings tab — forces the same
    /// prompt shown automatically on first run, regardless of whether it already ran.</summary>
    private void BtnAppImportFromBaseCamp_Click(object sender, RoutedEventArgs e) =>
        RunBaseCampImportPrompt(silentIfNotFound: false);

    /// <summary>Single entry-point gate: detects Base Camp's database and, if present,
    /// asks once whether to import existing profiles/settings — then hands off to each
    /// device's own (already-built) Base Camp import flow, which shows its own per-device
    /// summary/confirmation. Devices with no matching profiles in the DB, or not
    /// connected, simply no-op (see each BtnXxImportBc_Click).</summary>
    private void RunBaseCampImportPrompt(bool silentIfNotFound)
    {
        string? dbPath = BaseCampDbImporter.FindBaseCampDb();
        if (dbPath is null)
        {
            if (!silentIfNotFound)
                MessageBox.Show(this, Loc.Get("dp_bc_db_not_found"), Loc.Get("bc_import_prompt_title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var res = MessageBox.Show(this, Loc.Get("bc_import_prompt_text"), Loc.Get("bc_import_prompt_title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res != MessageBoxResult.Yes) return;

        // Macros FIRST, before any device's key bindings: a "Default" FunctionType binding
        // (BaseCampDbImporter.TranslateDefaultAction) is matched by name against the K2 macro
        // library at import time — if the library is still empty because this cascade never
        // imported macros, every named-macro reference lands unresolved (red "action not
        // found" triangle) even though the same-named macro exists right there in BaseCamp.db.
        // Reuses the same button handler (and its own confirm/count dialogs) as the standalone
        // "Import from BaseCamp" button on the Macro tab — same pattern every device below
        // already follows (each shows its own confirmation in this cascade).
        BtnMacroImportBC_Click(this, new RoutedEventArgs());

        BtnEvImportBc_Click(this, new RoutedEventArgs());
        BtnEv60ImportBc_Click(this, new RoutedEventArgs());
        BtnMkImportBc_Click(this, new RoutedEventArgs());
        BtnMpImportBc_Click(this, new RoutedEventArgs());
        // DisplayPad supports multiple simultaneous physical devices — repeat the
        // per-device import (incl. the Base Camp device picker, when needed) once per
        // connected pad instead of the single-tab BtnDpImportBc_Click.
        DpImportBcForAllDevices();
    }

    /// <summary>Loads the persisted "Base Camp DLL folder" (see
    /// <see cref="AppSettings.BaseCampDllFolder"/>) into the picker, mirrors the
    /// installer's "detected install vs. manual folder" radio choice (see
    /// K2Setup.iss's BcPage), and refreshes the found/missing status of the three
    /// non-redistributable Base Camp native DLLs (MacroPadSDK.dll, SDKDLL.dll,
    /// Everest360_USB.dll — see <see cref="NativeDependencyResolver"/>). Called once
    /// from InitAppSettingsPanel and again after the user changes the radio or
    /// browses the folder.</summary>
    private void InitBcDllFolderPanel()
    {
        if (_bcDllPanelUpdating) return; // re-entrancy guard: setting IsChecked below fires RbBcDllMode_Checked
        _bcDllPanelUpdating = true;
        try
        {
            string? detected = NativeDependencyResolver.BaseCampDirectories().FirstOrDefault();
            RbBcDllAuto.IsEnabled = detected is not null;
            TxtBcDetected.Text = detected ?? Loc.Get("settings_bc_dll_radio_auto_notfound");

            // Manual mode whenever the user has an explicit override saved, or nothing
            // was auto-detected to fall back on (same default as the installer's page).
            bool manual = !string.IsNullOrWhiteSpace(AppSettings.BaseCampDllFolder) || detected is null;
            RbBcDllManual.IsChecked = manual;
            RbBcDllAuto.IsChecked = !manual;
            TxtBcDllFolder.IsEnabled = manual;
            BtnBcDllFolderBrowse.IsEnabled = manual;

            TxtBcDllFolder.Text = AppSettings.BaseCampDllFolder ?? Loc.Get("settings_bc_dll_none");
            RefreshBcDllStatus();
        }
        finally
        {
            _bcDllPanelUpdating = false;
        }
    }

    private bool _bcDllPanelUpdating;

    private void RefreshBcDllStatus()
    {
        var parts = NativeDependencyResolver.BaseCampNativeDlls.Select(dll =>
            $"{dll}: {(NativeDependencyResolver.IsResolvable(dll) ? Loc.Get("settings_bc_dll_found") : Loc.Get("settings_bc_dll_missing"))}");
        TxtBcDllStatus.Text = string.Join("   ", parts);
    }

    /// <summary>Fires when either "Base Camp DLL folder" radio is checked. Switching to
    /// auto-detect clears any saved manual override so <see cref="NativeDependencyResolver"/>
    /// falls back to its own detection; switching to manual just unlocks the folder
    /// picker below (the folder itself is only saved once the user browses to one).</summary>
    private void RbBcDllMode_Checked(object sender, RoutedEventArgs e)
    {
        if (RbBcDllAuto.IsChecked == true)
            AppSettings.SetBaseCampDllFolder(null);
        InitBcDllFolderPanel();
    }

    /// <summary>"Browse…" in the "Base Camp DLL folder" group — lets the user point K2
    /// at a folder containing the Base Camp native DLLs (e.g. copied from another PC or
    /// extracted from the Base Camp installer) without installing Base Camp itself or
    /// setting the K2_BASECAMP_DIR environment variable by hand.</summary>
    private void BtnBcDllFolderBrowse_Click(object sender, RoutedEventArgs e)
    {
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("settings_bc_dll_browse") };
        if (folder.ShowDialog(this) != true) return;

        AppSettings.SetBaseCampDllFolder(folder.FolderName);
        InitBcDllFolderPanel();
    }

    /// <summary>Loads persisted AppSettings into the Settings tab UI and applies
    /// the debug flag to every device module. Called once from the constructor,
    /// after all Init*Module() calls so their controls/fields already exist.</summary>
    private void InitAppSettingsPanel()
    {
        bool debug = AppSettings.DebugMode;
        CkAppDebugMode.IsChecked = debug;

        switch (AppSettings.LogLevel)
        {
            case K2LogLevel.Off:     RbLogOff.IsChecked     = true; break;
            case K2LogLevel.Verbose: RbLogVerbose.IsChecked = true; break;
            default:                 RbLogNormal.IsChecked  = true; break;
        }

        CkAutoStopBaseCamp.IsChecked = AppSettings.AutoStopBaseCamp;
        CkKillBcWorker.IsChecked = AppSettings.KillBaseCampWorker;
        CkRestartBcOnClose.IsChecked = AppSettings.RestartBaseCampOnClose;
        InitBcAutostartCheckbox();

        InitBcDllFolderPanel();

        CkCloseToTray.IsChecked = AppSettings.CloseToTray;
        CkStartMinToTray.IsChecked = AppSettings.StartMinimizedToTray;
        CkK2Autostart.IsChecked = Services.K2AutostartService.IsEnabled();

        InitAppFontCombo();

        ApplyDebugModeToAllDevices(debug);
    }

    /// <summary>Populates the Font combo with <see cref="FontCatalog.Options"/> and
    /// selects the persisted choice (default Roboto). The font itself is already
    /// applied at process startup (see App.OnStartup); this only drives the UI.</summary>
    private void InitAppFontCombo()
    {
        CmbAppFont.Items.Clear();
        foreach (var opt in FontCatalog.Options)
            CmbAppFont.Items.Add(new ComboBoxItem { Content = opt.DisplayName, Tag = opt.Key });

        string current = AppSettings.AppFontFamily;
        CmbAppFont.SelectedIndex = 0;
        for (int i = 0; i < CmbAppFont.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)CmbAppFont.Items[i]).Tag == current)
            {
                CmbAppFont.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>Persists the chosen font and applies it live to every K2 window
    /// (see FontCatalog.Apply / K2Theme.xaml's K2AppFontFamily DynamicResource).</summary>
    private void CmbAppFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAppFont.SelectedItem is not ComboBoxItem item) return;
        string key = (string)item.Tag;
        AppSettings.SetAppFontFamily(key);
        FontCatalog.Apply(key);
    }

    private void CkCloseToTray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetCloseToTray(CkCloseToTray.IsChecked == true);
    }

    /// <summary>Persists the "start minimized to tray" flag. Read once at process start
    /// by App.OnStartup, so it takes effect at the next app launch.</summary>
    private void CkStartMinToTray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetStartMinimizedToTray(CkStartMinToTray.IsChecked == true);
    }

    private void CkK2Autostart_Click(object sender, RoutedEventArgs e)
    {
        Services.K2AutostartService.SetEnabled(CkK2Autostart.IsChecked == true);
    }

    /// <summary>Reflects the current Windows-autostart state of Base Camp entries
    /// (registry Run + StartupApproved). Disabled if no entry is found.</summary>
    private void InitBcAutostartCheckbox()
    {
        var entries = Services.BaseCampProcessGuard.FindAutostartEntries();
        if (entries.Count == 0)
        {
            CkBcAutostart.IsEnabled = false;
            CkBcAutostart.IsChecked = false;
            TxtBcAutostartHint.Text = Loc.Get("settings_bc_autostart_none");
            return;
        }
        CkBcAutostart.IsEnabled = true;
        CkBcAutostart.IsChecked = entries.Any(x => x.Enabled);
    }

    /// <summary>Persists the "auto-stop Base Camp on startup" flag. Takes effect at
    /// the next K2 launch (see App.OnStartup).</summary>
    private void CkAutoStopBaseCamp_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetAutoStopBaseCamp(CkAutoStopBaseCamp.IsChecked == true);
    }

    private void CkKillBcWorker_Click(object sender, RoutedEventArgs e)
    {
        bool on = CkKillBcWorker.IsChecked == true;
        AppSettings.SetKillBaseCampWorker(on);
        if (on) Services.BaseCampProcessGuard.KillDisplayPadWorkers(msg => DpLog(msg));
    }

    /// <summary>Persists the "restart Base Camp on close" flag. Read at the moment K2's
    /// window actually closes (see MainWindow.xaml.cs's OnWindowClosed).</summary>
    private void CkRestartBcOnClose_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetRestartBaseCampOnClose(CkRestartBcOnClose.IsChecked == true);
    }

    /// <summary>Copies the current app log (and crash log, if any) to a user-chosen
    /// folder — "Export log" button in the General group.</summary>
    private void BtnAppExportLog_Click(object sender, RoutedEventArgs e)
    {
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("export_pick_folder") };
        if (folder.ShowDialog(this) != true) return;

        try
        {
            string dest = System.IO.Path.Combine(folder.FolderName, System.IO.Path.GetFileName(App.LogPath));
            System.IO.File.Copy(App.LogPath, dest, overwrite: true);

            if (System.IO.File.Exists(App.CrashLogPath))
            {
                string destCrash = System.IO.Path.Combine(folder.FolderName, System.IO.Path.GetFileName(App.CrashLogPath));
                System.IO.File.Copy(App.CrashLogPath, destCrash, overwrite: true);
            }

            LblStatus.Text = Loc.Get("settings_export_log_done", folder.FolderName);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("settings_export_log_btn"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CkBcAutostart_Click(object sender, RoutedEventArgs e)
    {
        bool enable = CkBcAutostart.IsChecked == true;
        int changed = Services.BaseCampProcessGuard.SetAutostartEnabled(enable, msg => DpLog(msg));
        LblStatus.Text = Loc.Get("settings_bc_autostart_done", changed);
        // Re-read the real state (HKLM entries may have failed without admin rights).
        InitBcAutostartCheckbox();
    }

    private void CkAppDebugMode_Click(object sender, RoutedEventArgs e)
    {
        bool debug = CkAppDebugMode.IsChecked == true;
        AppSettings.SetDebugMode(debug);
        ApplyDebugModeToAllDevices(debug);
    }

    private void RbLogLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == RbLogOff)          AppSettings.SetLogLevel(K2LogLevel.Off);
        else if (sender == RbLogVerbose) AppSettings.SetLogLevel(K2LogLevel.Verbose);
        else                              AppSettings.SetLogLevel(K2LogLevel.Normal);
    }

    /// <summary>Wipes every app preference AND every saved profile/key binding/lighting/
    /// macro for every device, then restarts K2 — the "Restore all defaults" button in
    /// the Settings tab's Danger Zone. Distinct from the per-device "Restore defaults"
    /// buttons (which only reset the currently selected profile of one device and don't
    /// restart). Restarting (rather than trying to refresh a dozen open panels in place)
    /// guarantees every tab comes back up reading the freshly-blank stores from scratch —
    /// and resetting <see cref="AppSettings.BcImportPromptShown"/> means the "Import from
    /// Base Camp?" prompt (see CheckFirstRunBcImport) fires again right after.</summary>
    private void BtnAppRestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            Loc.Get("restore_defaults_app_confirm"),
            Loc.Get("restore_defaults_app"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.OK) return;

        AppSettings.ResetToDefaults();
        _evStore.ResetAllData();
        _ev60Store.ResetAllData();
        _mkStore.ResetAllData();
        _store.ResetAllData();
        _macroStore?.ResetAllData();
        _dpStore.ResetAllData();

        RestartApp();
    }

    /// <summary>Applies the centralized debug flag to every device module at once.</summary>
    private void ApplyDebugModeToAllDevices(bool debug)
    {
        ApplyDebugMode(debug);     // Everest    — MainWindow.SectionNav.cs
        ApplyMpDebugMode(debug);   // MacroPad   — MainWindow.Keys.cs
        ApplyDpDebugMode(debug);   // DisplayPad — MainWindow.DisplayPad.cs
        ApplyEv60DebugMode(debug); // Everest 60 — MainWindow.Everest60.cs
        ApplyMkDebugMode(debug);   // Makalu     — MainWindow.Makalu.cs
    }
}
