using System.IO;
using System.Windows;
using K2.Core;
using Microsoft.Win32;

namespace K2.App;

/// <summary>
/// Popup opened from the gear icon on any device's profile row (see
/// MainWindow.xaml.cs's ProfileGear_Click and each device's XxShowProfileGear method).
/// Lets the user rename the profile, delete it, and link an executable path — when that
/// program starts running, <see cref="K2.Core.Services.ProfileLaunchWatcher"/> switches
/// K2 to this profile automatically (see each device's XxRefreshProfiles for the
/// registration side).
/// </summary>
public partial class ProfileSettingsDialog : Window
{
    /// <summary>Final name (Save only).</summary>
    public string ProfileName => TxtName.Text;
    /// <summary>Final linked executable path, or "" for none (Save only).</summary>
    public string ExePath => TxtExePath.Text;
    /// <summary>Show this profile only while the linked exe owns the foreground window,
    /// restoring the previous profile when it loses focus (Save only). Forced false when
    /// no executable is linked (the checkbox is disabled in that case).</summary>
    public bool FocusOnly => ChkFocusOnly.IsEnabled && ChkFocusOnly.IsChecked == true;
    /// <summary>Restore the previous profile when the linked exe exits (Save only). Forced
    /// false when no executable is linked or <see cref="FocusOnly"/> is on — focus
    /// tracking already restores, and the checkbox is disabled in both cases.</summary>
    public bool RestoreOnClose => ChkRestoreOnClose.IsEnabled && ChkRestoreOnClose.IsChecked == true;
    /// <summary>True if the user clicked "Delete profile" instead of Save — the caller
    /// should ignore <see cref="Name"/>/<see cref="ExePath"/> and run its own guarded
    /// delete flow (same as the existing Rename/Delete menu items).</summary>
    public bool DeleteRequested { get; private set; }

    public ProfileSettingsDialog(string currentName, string currentExePath,
        bool focusOnly = false, bool restoreOnClose = false)
    {
        InitializeComponent();
        TxtName.Text = currentName;
        TxtExePath.Text = currentExePath;
        ChkFocusOnly.IsChecked = focusOnly;
        ChkRestoreOnClose.IsChecked = restoreOnClose;
        UpdateFlagAvailability();
    }

    /// <summary>The focus / restore-on-close flags only make sense once an executable is
    /// linked, and restore-on-close is subsumed by focus tracking — so grey out what
    /// doesn't apply. Called on load and whenever the exe path or the focus flag changes.</summary>
    private void UpdateFlagAvailability()
    {
        // TxtExePath is declared before the checkboxes in XAML, so its TextChanged can
        // fire mid-InitializeComponent while these are still null (see _PROJECT_MAP.md's
        // XAML-load-time event hazard).
        if (ChkFocusOnly is null || ChkRestoreOnClose is null) return;
        bool hasExe = !string.IsNullOrWhiteSpace(TxtExePath.Text);
        ChkFocusOnly.IsEnabled = hasExe;
        ChkRestoreOnClose.IsEnabled = hasExe && ChkFocusOnly.IsChecked != true;
    }

    private void TxtExePath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateFlagAvailability();

    private void ChkFocusOnly_Toggled(object sender, RoutedEventArgs e)
        => UpdateFlagAvailability();

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("profile_settings_exe_label"),
            Filter = "Executable (*.exe)|*.exe|All files|*.*",
        };
        try
        {
            var dir = Path.GetDirectoryName(TxtExePath.Text);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch { /* best-effort */ }
        if (dlg.ShowDialog(this) == true)
            TxtExePath.Text = dlg.FileName;
    }

    private void BtnClearExe_Click(object sender, RoutedEventArgs e) => TxtExePath.Text = "";

    private void BtnPickRunning_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RunningProcessDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            TxtExePath.Text = dlg.SelectedPath;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text)) return;
        DialogResult = true;
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested = true;
        DialogResult = true;
    }
}
