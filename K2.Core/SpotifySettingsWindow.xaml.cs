using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Spotify account settings — the user's own Spotify Developer app Client ID/Secret (K2 has no
/// registered app of its own, see <see cref="SpotifyAuth"/>) plus a "Connect" button driving the
/// OAuth flow. Opened from <c>ButtonActionDialog</c>'s "spotify" combo panel via a "Manage"
/// button, same pattern as <see cref="TwitchSettingsWindow"/>.
/// </summary>
public partial class SpotifySettingsWindow : Window
{
    public SpotifySettingsWindow()
    {
        InitializeComponent();
        TxtSpotifyClientId.Text = SpotifyStore.ClientId;
        PwdSpotifyClientSecret.Password = SpotifyStore.ClientSecret;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        TxtSpotifyStatus.Text = SpotifyStore.IsConnected
            ? $"{Loc.Get("spotify_connected_as")}: {SpotifyStore.DisplayName}"
            : Loc.Get("obs_not_connected"); // reuses the generic "not connected" string
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
}
