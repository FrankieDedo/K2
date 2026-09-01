using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Configuration popup for the DisplayPad's <b>Spotify dedicated profile</b>, opened from that
/// profile's gear ▸ Configure. Three knobs, all held in <see cref="SpotifyCoverConfig"/>:
/// <list type="bullet">
/// <item><b>Source</b> — local Windows "now playing" vs the Spotify Web API (needs the account
///   connected; falls back to local on its own).</item>
/// <item><b>Cover layout</b> — album art across all 4 tiles of the left 2×2 block, or the cover
///   on one tile with title / artist / album on the other three.</item>
/// <item><b>Text mode</b> — for the 1-tile layout: static (ellipsis) or scrolling text. Disabled
///   while the 4-tile layout is selected.</item>
/// <item><b>Come back on its own</b> — whether, and after how many seconds, the pad returns to
///   this profile once the cover tile's back arrow has left it (<c>MainWindow.DpSpotifyLeave</c>).</item>
/// <item><b>Show back arrow</b> — whether the cover tile wears the badge. The key still leaves the
///   profile when it is off; only the mark goes away.</item>
/// <item><b>Only while Spotify is in front</b> — the pad shows this profile exactly while the
///   Spotify app owns the foreground window (<c>ProfileLaunchWatcher</c>'s focus-only mode).</item>
/// <item><b>Block position</b> — which pair of grid columns the 2×2 block sits in
///   (<see cref="SpotifyCoverPosition"/>); the 8 control keys' layout changes with it (each
///   position has its own hand-picked order, see <c>MainWindow.ControlLayout</c>). Changing it
///   (or Source) re-seeds the control keys on save.</item>
/// <item><b>Device</b> — the Spotify Connect device the 7 transport/volume keys and Repeat
///   target, meaningful only while Source is Web API (a "media" key has no device concept at
///   all). Same picker/refresh as a single key's own "spotify" action in
///   <see cref="ButtonActionDialog"/>.</item>
/// </list>
/// Follows the <see cref="ProfileSettingsDialog"/> value-passing pattern (no K2.App reference):
/// the caller passes the current config in and reads <see cref="Result"/> back when
/// <see cref="Saved"/> is true. The Spotify <i>account</i> (Client ID/Secret, Connect) lives in
/// the separate <see cref="SpotifySettingsWindow"/>, reached from here through a button.
/// </summary>
public partial class SpotifyProfileConfigWindow : Window
{
    /// <summary>True only when the user pressed Save (not on close / Esc).</summary>
    public bool Saved { get; private set; }

    /// <summary>The chosen config — meaningful only when <see cref="Saved"/>.</summary>
    public SpotifyCoverConfig Result { get; private set; }

    /// <summary>The device stored in the config this window opened on — the picker's initial
    /// preselect once its async load comes back (see <see cref="LoadSpotifyDevicesAsync"/>).</summary>
    private readonly string _initialDevice;

    public SpotifyProfileConfigWindow(SpotifyCoverConfig current)
    {
        InitializeComponent();
        _initialDevice = current.Device;

        RbSourceWebApi.IsChecked = current.Source == SpotifyCoverSource.WebApi;
        RbSourceLocal.IsChecked = !RbSourceWebApi.IsChecked;

        RbLayoutSingle.IsChecked = current.Layout == SpotifyCoverLayout.Single;
        RbLayoutQuad.IsChecked = !RbLayoutSingle.IsChecked;

        RbTextMarquee.IsChecked = current.TextMode == SpotifyTextMode.Marquee;
        RbTextStatic.IsChecked = !RbTextMarquee.IsChecked;

        CkSpotifyReturn.IsChecked = current.ReturnEnabled;
        CkSpotifyBackArrow.IsChecked = current.BackArrow;
        CkSpotifyForeground.IsChecked = current.ForegroundOnly;
        TxtSpotifyReturnSec.Text = SpotifyCoverConfig.ClampReturnSeconds(current.ReturnSeconds)
            .ToString(CultureInfo.InvariantCulture);

        CbSpotifyPosition.SelectedIndex = current.Position switch
        {
            SpotifyCoverPosition.Center => 1,
            SpotifyCoverPosition.Right  => 2,
            _                           => 0,
        };

        UpdateTextModeEnabled();
        UpdateDeviceRowVisibility();
        if (RbSourceWebApi.IsChecked == true) _ = LoadSpotifyDevicesAsync(_initialDevice);
    }

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDeviceRowVisibility();
        // First time this pad's popup shows Web API selected, load the account's devices —
        // subsequent toggles back and forth reuse whatever the combo already has (refresh
        // button re-fetches on demand, same as the per-key picker).
        if (RbSourceWebApi.IsChecked == true && CbSpotifyDevice.Items.Count == 0)
            _ = LoadSpotifyDevicesAsync(_initialDevice);
    }

    /// <summary>The device picker only means anything for Web API — a "media" key has no device
    /// concept at all.</summary>
    private void UpdateDeviceRowVisibility()
    {
        bool webApi = RbSourceWebApi.IsChecked == true;
        var vis = webApi ? Visibility.Visible : Visibility.Collapsed;
        if (PnlSpotifyDevice is not null) PnlSpotifyDevice.Visibility = vis;
        if (RowSpotifyDevice is not null) RowSpotifyDevice.Visibility = vis;
    }

    private void BtnSpotifyDeviceRefresh_Click(object sender, RoutedEventArgs e)
        => _ = LoadSpotifyDevicesAsync(SelectedSpotifyDeviceId());

    private string SelectedSpotifyDeviceId()
        => CbSpotifyDevice.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";

    /// <summary>Same picker/refresh logic as <c>ButtonActionDialog.LoadSpotifyDevicesAsync</c>
    /// (a single key's own "spotify" action) — kept as its own copy rather than shared, since the
    /// two dialogs have no common base to hang it on and the whole method is self-contained.</summary>
    private int _spotifyDeviceLoadToken;

    private async System.Threading.Tasks.Task LoadSpotifyDevicesAsync(string? preselectId)
    {
        int token = ++_spotifyDeviceLoadToken;

        System.Collections.Generic.List<(string Id, string Name, string Type, bool IsActive)> devices;
        try { devices = await SpotifyBridge.GetDevicesAsync(); }
        catch { return; }

        if (token != _spotifyDeviceLoadToken) return;

        CbSpotifyDevice.Items.Clear();
        CbSpotifyDevice.Items.Add(new ComboBoxItem { Content = Loc.Get("spotify_device_auto"), Tag = "" });

        int selectIndex = 0;
        foreach (var (id, name, type, isActive) in devices)
        {
            if (string.IsNullOrEmpty(id)) continue;
            string label = name;
            if (!string.IsNullOrEmpty(type)) label += $"  ·  {type}";
            if (isActive) label += $"  ({Loc.Get("spotify_device_active")})";
            CbSpotifyDevice.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            if (id == preselectId) selectIndex = CbSpotifyDevice.Items.Count - 1;
        }

        if (!string.IsNullOrEmpty(preselectId) && selectIndex == 0)
        {
            CbSpotifyDevice.Items.Add(new ComboBoxItem
            {
                Content = $"{preselectId}  ({Loc.Get("spotify_device_offline")})",
                Tag = preselectId,
            });
            selectIndex = CbSpotifyDevice.Items.Count - 1;
        }
        CbSpotifyDevice.SelectedIndex = selectIndex;
    }

    private void Layout_Changed(object sender, RoutedEventArgs e) => UpdateTextModeEnabled();

    /// <summary>The text-mode choice only applies to the 1-tile layout — grey it out otherwise
    /// so it doesn't look like a setting that does nothing.</summary>
    private void UpdateTextModeEnabled()
    {
        bool single = RbLayoutSingle.IsChecked == true;
        // A TextBlock has no disabled look of its own, so the label is dimmed by hand to match
        // the radios (whose template gained an IsEnabled=False trigger on 2026-09-01 — before
        // that the pair only STOPPED RESPONDING when greyed out, with nothing to show for it).
        if (LblTextMode is not null) { LblTextMode.IsEnabled = single; LblTextMode.Opacity = single ? 1.0 : 0.45; }
        if (PnlTextMode is not null) PnlTextMode.IsEnabled = single;
    }

    private static readonly Regex NonDigit = new(@"[^0-9]", RegexOptions.Compiled);

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = NonDigit.IsMatch(e.Text);

    private void BtnSpotifyAccount_Click(object sender, RoutedEventArgs e)
    {
        new SpotifySettingsWindow { Owner = this }.ShowDialog();
    }

    private void BtnGuide_Click(object sender, RoutedEventArgs e)
    {
        new GuideWindow("dedicated:spotify", Loc.Get("spotify_profile_config_title")) { Owner = this }.ShowDialog();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        int sec = int.TryParse(TxtSpotifyReturnSec.Text.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed)
            ? parsed : SpotifyCoverConfig.DefaultReturnSeconds;

        Result = new SpotifyCoverConfig(
            RbSourceWebApi.IsChecked == true ? SpotifyCoverSource.WebApi : SpotifyCoverSource.Local,
            RbLayoutSingle.IsChecked == true ? SpotifyCoverLayout.Single : SpotifyCoverLayout.Quad,
            RbTextMarquee.IsChecked == true ? SpotifyTextMode.Marquee : SpotifyTextMode.Static,
            CkSpotifyReturn.IsChecked == true,
            SpotifyCoverConfig.ClampReturnSeconds(sec),
            CkSpotifyBackArrow.IsChecked == true,
            CkSpotifyForeground.IsChecked == true,
            CbSpotifyPosition.SelectedIndex switch
            {
                1 => SpotifyCoverPosition.Center,
                2 => SpotifyCoverPosition.Right,
                _ => SpotifyCoverPosition.Left,
            },
            RbSourceWebApi.IsChecked == true ? SelectedSpotifyDeviceId() : "");
        Saved = true;
        Close();
    }
}
