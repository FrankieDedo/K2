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
    /// <summary>True if the user clicked "Delete profile" instead of Save — the caller
    /// should ignore <see cref="Name"/>/<see cref="ExePath"/> and run its own guarded
    /// delete flow (same as the existing Rename/Delete menu items).</summary>
    public bool DeleteRequested { get; private set; }

    public ProfileSettingsDialog(string currentName, string currentExePath)
    {
        InitializeComponent();
        TxtName.Text = currentName;
        TxtExePath.Text = currentExePath;
    }

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
