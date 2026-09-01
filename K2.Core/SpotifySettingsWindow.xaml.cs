using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Spotify account settings — the user's own Spotify Developer app Client ID/Secret (K2 has no
/// registered app of its own, see <see cref="SpotifyAuth"/>) plus a "Connect" button driving the
/// OAuth flow. Opened from <c>ButtonActionDialog</c>'s "spotify" combo panel via a "Manage"
/// button, same pattern as <see cref="TwitchSettingsWindow"/>. The playback target device is a
/// PER-KEY setting, picked in the button-action dialog's Spotify panel — not here.
/// </summary>
public partial class SpotifySettingsWindow : Window
{
    public SpotifySettingsWindow()
    {
        InitializeComponent();
        RtbSpotifyGuide.Document = SetupGuide.BuildDocument(Loc.Get("spotify_setup_guide"), this);
        TxtSpotifyClientId.Text = SpotifyStore.ClientId;
        PwdSpotifyClientSecret.Password = SpotifyStore.ClientSecret;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!SpotifyStore.IsConnected)
        {
            TxtSpotifyStatus.Text = Loc.Get("obs_not_connected"); // reuses the generic "not connected" string
        }
        else
        {
            string line = Loc.Get("spotify_connected_as"); // "Connected"
            if (!string.IsNullOrEmpty(SpotifyStore.DisplayName)) line += $" — {SpotifyStore.DisplayName}";
            TxtSpotifyStatus.Text = line;
        }

        // Display-only Web API indicator: shown only once connected, ticked when the account
        // is Premium (see SpotifyStore.WebApiPlaybackConfirmed / SpotifyAuth's product read).
        ChkSpotifyWebApi.Visibility = SpotifyStore.IsConnected
            ? Visibility.Visible : Visibility.Collapsed;
        ChkSpotifyWebApi.IsChecked = SpotifyStore.WebApiPlaybackConfirmed;
    }

    private void SaveCredentials()
        => SpotifyStore.SetAppCredentials(TxtSpotifyClientId.Text.Trim(), PwdSpotifyClientSecret.Password);

    private async void BtnSpotifyConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        TxtSpotifyStatus.Text = Loc.Get("twitch_connecting"); // reuses the generic "connecting" string
        var error = await SpotifyAuth.ConnectAsync();
        TxtSpotifyStatus.Text = error is null ? "" : error;
        RefreshStatus();
    }

    private void BtnSpotifyDisconnect_Click(object sender, RoutedEventArgs e)
    {
        SpotifyStore.Disconnect();
        RefreshStatus();
    }

    private void BtnSpotifySave_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        Close();
    }

    private void BtnSpotifyGuide_Click(object sender, RoutedEventArgs e)
    {
        new GuideWindow("spotify:account", Loc.Get("spotify_settings_title")) { Owner = this }.ShowDialog();
    }
}
