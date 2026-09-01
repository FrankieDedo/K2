using System.Windows;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Discord settings — the user's own Discord application Client ID/Secret (K2 has no registered
/// app of its own, see <see cref="DiscordAuth"/>) plus a "Connect" button driving the RPC
/// authorization, and the optional channel webhook URL used by the "send message" command.
/// Opened from <c>ButtonActionDialog</c>'s "discord" combo panel via a "Manage" button, same
/// pattern as <see cref="TwitchSettingsWindow"/>/<see cref="ObsSettingsWindow"/>.
/// </summary>
public partial class DiscordSettingsWindow : Window
{
    public DiscordSettingsWindow()
    {
        InitializeComponent();
        TxtDiscordClientId.Text = DiscordStore.ClientId;
        PwdDiscordClientSecret.Password = DiscordStore.ClientSecret;
        TxtDiscordWebhook.Text = DiscordStore.WebhookUrl;
        RtbDiscordGuide.Document = SetupGuide.BuildDocument(Loc.Get("discord_setup_guide"), this);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        TxtDiscordStatus.Text = DiscordStore.IsConnected
            ? $"{Loc.Get("discord_connected_as")}: {DiscordStore.UserName}"
            : Loc.Get("obs_not_connected"); // reuses the generic "not connected" string
    }

    private void SaveCredentials()
    {
        DiscordStore.SetAppCredentials(TxtDiscordClientId.Text.Trim(), PwdDiscordClientSecret.Password);
        DiscordStore.SetWebhookUrl(TxtDiscordWebhook.Text.Trim());
    }

    private async void BtnDiscordConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        TxtDiscordStatus.Text = Loc.Get("discord_connecting");
        var error = await DiscordAuth.ConnectAsync();

        // On failure the reason STAYS on screen: calling RefreshStatus() here (as the Twitch
        // window does) would immediately overwrite it with a bare "not connected", which is
        // exactly what left a failed connect undiagnosable.
        if (error is null) RefreshStatus();
        else TxtDiscordStatus.Text = error;
    }

    private void BtnDiscordDisconnect_Click(object sender, RoutedEventArgs e)
    {
        DiscordStore.Disconnect();
        RefreshStatus();
    }

    private void BtnDiscordSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCredentials();
        Close();
    }
}
