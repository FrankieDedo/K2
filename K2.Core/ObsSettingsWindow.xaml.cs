using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// OBS Studio connection settings — Host/Port/Password (mirrors <see cref="ObsStore"/>),
/// opened from <c>ButtonActionDialog</c>'s "obs" combo panel via a "Manage" button, same
/// pattern as <see cref="GoogleHomeSetupWindow"/>.
/// </summary>
public partial class ObsSettingsWindow : Window
{
    public ObsSettingsWindow()
    {
        InitializeComponent();
        TxtObsHost.Text = ObsStore.Host;
        TxtObsPort.Text = ObsStore.Port;
        PwdObsPassword.Password = ObsStore.Password;
        TxtObsStatus.Text = ObsBridge.IsConnected ? Loc.Get("obs_connected") : Loc.Get("obs_not_connected");
    }

    private void BtnObsTest_Click(object sender, RoutedEventArgs e)
    {
        ObsStore.SetConnection(TxtObsHost.Text.Trim(), TxtObsPort.Text.Trim(), PwdObsPassword.Password);
        ObsBridge.Disconnect();
        bool ok = ObsBridge.EnsureConnected();
        TxtObsStatus.Text = ok
            ? $"{Loc.Get("obs_connected")} (OBS {ObsBridge.GetVersionString()})"
            : Loc.Get("obs_not_connected");
    }

    private void BtnObsSave_Click(object sender, RoutedEventArgs e)
    {
        ObsStore.SetConnection(TxtObsHost.Text.Trim(), TxtObsPort.Text.Trim(), PwdObsPassword.Password);
        Close();
    }
}
