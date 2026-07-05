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
        CkKillBcWorker.IsChecked = AppSettings.KillBaseCampWorker;
        InitBcAutostartCheckbox();

        ApplyDebugModeToAllDevices(debug);
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

    /// <summary>Applies the centralized debug flag to Everest, MacroPad and DisplayPad at once.</summary>
    private void ApplyDebugModeToAllDevices(bool debug)
    {
        ApplyDebugMode(debug);     // Everest   — MainWindow.SectionNav.cs
        ApplyMpDebugMode(debug);   // MacroPad  — MainWindow.Keys.cs
        ApplyDpDebugMode(debug);   // DisplayPad — MainWindow.DisplayPad.cs
    }
}
