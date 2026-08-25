using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
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
        RtbDiscordGuide.Document = BuildGuideDocument(Loc.Get("discord_setup_guide"));
        RefreshStatus();
    }

    /// <summary>Any developer-portal address inside the setup guide. Deliberately narrow:
    /// the guide also names <c>http://localhost:17564/callback</c>, which is a value to copy
    /// into the portal, not somewhere to navigate to — turning it into a link would only
    /// offer the user a dead page.</summary>
    private static readonly Regex GuideLinkPattern =
        new(@"(?:https?://)?discord\.com/[^\s""']+", RegexOptions.IgnoreCase);

    /// <summary>Renders the localized setup guide into a flow document: one paragraph per
    /// line, with every developer-portal address turned into a clickable link. Kept in code
    /// rather than XAML because the text is a single localized string whose links can only be
    /// found by scanning it.</summary>
    private FlowDocument BuildGuideDocument(string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };

        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            int last = 0;

            foreach (Match match in GuideLinkPattern.Matches(line))
            {
                if (match.Index > last) paragraph.Inlines.Add(new Run(line[last..match.Index]));

                string url = match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? match.Value : "https://" + match.Value;
                var link = new Hyperlink(new Run(match.Value))
                {
                    NavigateUri = new Uri(url),
                    // Both hosts merge K2Theme.xaml, but a missing brush must not take the
                    // whole settings window down over a link color.
                    Foreground = TryFindResource("K2AccentBrush") as System.Windows.Media.Brush
                                 ?? System.Windows.Media.Brushes.CornflowerBlue,
                };
                link.RequestNavigate += GuideLink_RequestNavigate;
                paragraph.Inlines.Add(link);
                last = match.Index + match.Length;
            }

            if (last < line.Length) paragraph.Inlines.Add(new Run(line[last..]));
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private void GuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true }); }
        catch { /* no default browser — nothing useful to do here */ }
        e.Handled = true;
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
