using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Twitch account settings — the user's own Twitch Developer app Client ID/Secret (K2 has no
/// registered app of its own, see <see cref="TwitchAuth"/>) plus a "Connect" button driving the
/// OAuth flow. Opened from <c>ButtonActionDialog</c>'s "twitch" combo panel via a "Manage"
/// button, same pattern as <see cref="GoogleHomeSetupWindow"/>/<see cref="ObsSettingsWindow"/>.
/// </summary>
public partial class TwitchSettingsWindow : Window
{
    public TwitchSettingsWindow()
    {
        InitializeComponent();
        RtbTwitchGuide.Document = SetupGuide.BuildDocument(Loc.Get("twitch_setup_guide"), this);
        TxtTwitchClientId.Text = TwitchStore.ClientId;
        PwdTwitchClientSecret.Password = TwitchStore.ClientSecret;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        TxtTwitchStatus.Text = TwitchStore.IsConnected
            ? $"{Loc.Get("twitch_connected_as")}: {TwitchStore.Login}"
            : Loc.Get("obs_not_connected"); // reuses the generic "not connected" string
    }

    private void SaveCredentials()
        => TwitchStore.SetAppCredentials(TxtTwitchClientId.Text.Trim(), PwdTwitchClientSecret.Password);

    private async void BtnTwitchConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        TxtTwitchStatus.Text = Loc.Get("twitch_connecting");
        var error = await TwitchAuth.ConnectAsync();
        TxtTwitchStatus.Text = error is null ? "" : error;
        RefreshStatus();
    }

    private void BtnTwitchDisconnect_Click(object sender, RoutedEventArgs e)
    {
        TwitchStore.Disconnect();
        RefreshStatus();
    }

    private void BtnTwitchSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        Close();
    }
}
