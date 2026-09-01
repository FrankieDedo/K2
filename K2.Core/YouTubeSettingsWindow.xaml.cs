using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// YouTube account settings — the user's own Google Cloud OAuth Desktop-app Client ID/Secret
/// (K2 has no registered app of its own, see <see cref="YouTubeBridge"/>) plus a "Connect"
/// button driving the OAuth flow. Opened from <c>ButtonActionDialog</c>'s "youtube" panel via
/// a "Manage" button, same pattern as <see cref="TwitchSettingsWindow"/>/<see cref="ObsSettingsWindow"/>.
/// </summary>
public partial class YouTubeSettingsWindow : Window
{
    public YouTubeSettingsWindow()
    {
        InitializeComponent();
        RtbYtGuide.Document = SetupGuide.BuildDocument(Loc.Get("youtube_setup_guide"), this);
        TxtYtClientId.Text = YouTubeStore.ClientId;
        PwdYtClientSecret.Password = YouTubeStore.ClientSecret;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        TxtYtStatus.Text = YouTubeStore.Connected
            ? $"{Loc.Get("twitch_connected_as")}: {YouTubeStore.ChannelTitle}"
            : Loc.Get("obs_not_connected");
    }

    private void SaveCredentials()
        => YouTubeStore.SetAppCredentials(TxtYtClientId.Text.Trim(), PwdYtClientSecret.Password);

    private async void BtnYtConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        TxtYtStatus.Text = Loc.Get("twitch_connecting");
        var error = await YouTubeBridge.ConnectAsync();
        TxtYtStatus.Text = error ?? "";
        RefreshStatus();
    }

    private void BtnYtDisconnect_Click(object sender, RoutedEventArgs e)
    {
        YouTubeBridge.Disconnect();
        RefreshStatus();
    }

    private void BtnYtSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        Close();
    }
}
