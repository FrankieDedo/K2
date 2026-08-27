using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
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
        TxtDiscordWebcamHotkey.Text = DiscordStore.WebcamHotkey;
        RtbDiscordGuide.Document = BuildGuideDocument(Loc.Get("discord_setup_guide"));
        RefreshStatus();
    }

    // ---------------------------------------------------------------- hotkey recorder

    /// <summary>True between "Record" and the first non-modifier key: every keystroke is captured
    /// instead of reaching the window.</summary>
    private bool _recordingHotkey;

    /// <summary>Text shown in the box while recording, remembered so Esc can put back whatever was
    /// there before.</summary>
    private string _hotkeyBeforeRecording = "";

    private void BtnDiscordRecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        _hotkeyBeforeRecording = TxtDiscordWebcamHotkey.Text;
        TxtDiscordWebcamHotkey.Text = Loc.Get("hotkey_recording");
        // Keyboard focus has to leave the button, or Space/Enter would "press" it again instead
        // of being recorded.
        Keyboard.ClearFocus();
        Focus();
    }

    /// <summary>
    /// Captures the combination while recording. Modifier-only presses are ignored (they are read
    /// from <see cref="Keyboard.Modifiers"/> when the real key lands), Esc cancels, and the result
    /// is written in the same "Ctrl+Shift+V" notation <see cref="SendKeysTranslator"/> parses.
    ///
    /// <para>Handled at the WINDOW level rather than on the box itself: the box is not focusable
    /// (it must never be typed into), so the keystrokes never reach it.</para>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recordingHotkey) { base.OnPreviewKeyDown(e); return; }

        // Alt-combinations arrive as Key.System, with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        _recordingHotkey = false;

        if (key == Key.Escape)
        {
            TxtDiscordWebcamHotkey.Text = _hotkeyBeforeRecording;
            return;
        }

        var mods = Keyboard.Modifiers;
        var parts = new System.Collections.Generic.List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(HotkeyKeyName(key));
        TxtDiscordWebcamHotkey.Text = string.Join("+", parts);
    }

    /// <summary>WPF key → the name <see cref="SendKeysTranslator"/> understands. Digits come back
    /// as <c>D4</c>/<c>NumPad4</c> from <see cref="Key"/>, which that translator would send as a
    /// literal word instead of a digit.</summary>
    private static string HotkeyKeyName(Key key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7) return name[6].ToString();
        return key switch
        {
            Key.Return => "Enter",
            Key.Next => "PgDn",
            Key.Prior => "PgUp",
            Key.Back => "Backspace",
            Key.Capital => "CapsLock",
            _ => name,
        };
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
        DiscordStore.WebcamHotkey = TxtDiscordWebcamHotkey.Text.Trim();
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
