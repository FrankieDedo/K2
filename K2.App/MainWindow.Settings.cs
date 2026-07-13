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
using K2.Core;
using K2.Core.Services;

namespace K2.App;

public partial class MainWindow
{
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

        CkDpNativeEngine.IsChecked = AppSettings.DisplayPadNativeEngine;
        CkEvNativeEngine.IsChecked = AppSettings.EverestNativeEngine;
        CkAutoStopBaseCamp.IsChecked = AppSettings.AutoStopBaseCamp;
        CkKillBcWorker.IsChecked = AppSettings.KillBaseCampWorker;
        InitBcAutostartCheckbox();

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

    private void CkBcAutostart_Click(object sender, RoutedEventArgs e)
    {
        bool enable = CkBcAutostart.IsChecked == true;
        int changed = Services.BaseCampProcessGuard.SetAutostartEnabled(enable, msg => DpLog(msg));
        LblStatus.Text = Loc.Get("settings_bc_autostart_done", changed);
        // Re-read the real state (HKLM entries may have failed without admin rights).
        InitBcAutostartCheckbox();
    }

    /// <summary>Persists the DisplayPad native-engine flag. The backend is chosen when
    /// MainWindow is constructed (see _dpClient initializer), so this takes effect at
    /// the next app start — the hint text under the checkbox says so.</summary>
    private void CkDpNativeEngine_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetDisplayPadNativeEngine(CkDpNativeEngine.IsChecked == true);
    }

    /// <summary>Persists the Everest native-engine flag (Phase 1: connectivity + numpad
    /// D1-D4 buttons only — see EverestService._nativePad). Takes effect at next start.</summary>
    private void CkEvNativeEngine_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetEverestNativeEngine(CkEvNativeEngine.IsChecked == true);
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
