namespace K2.Core.Services;

/// <summary>Where the DisplayPad "Spotify" dedicated profile reads the current track from.
/// <see cref="Local"/> is the Windows System Media Transport Controls session (works with
/// just the desktop player open, no account); <see cref="WebApi"/> is the Spotify Web API
/// (<c>GET /me/player</c>) which carries the album name and a higher-resolution cover, but
/// needs the account connected in the Spotify settings — it silently falls back to
/// <see cref="Local"/> when nothing comes back.</summary>
public enum SpotifyCoverSource { Local, WebApi }

/// <summary>How the profile's left 2×2 block is used. <see cref="Quad"/> paints the album
/// art across all four tiles (the original behaviour); <see cref="Single"/> puts the cover
/// on one tile and fills the other three with the track's title, artist and album.</summary>
public enum SpotifyCoverLayout { Quad, Single }

/// <summary>How an <see cref="SpotifyCoverLayout.Single"/> text tile renders a value that
/// is wider than the tile. <see cref="Static"/> keeps one line, shrinking the font down to
/// a floor and then trimming with an ellipsis; <see cref="Marquee"/> scrolls it
/// horizontally like a ticker display.</summary>
public enum SpotifyTextMode { Static, Marquee }

/// <summary>Which pair of columns the 2×2 cover block occupies on the 2×6 grid — the other 8
/// keys (transport/volume/repeat) fill whatever columns are left. <see cref="Left"/> is the
/// original placement (columns 0-1); <see cref="Center"/> and <see cref="Right"/> were added on
/// user request 2026-09-01, each with its own hand-picked control-button order (see
/// <c>MainWindow.DpSpotifyLayoutFor</c>) rather than a plain reflow of Left's.</summary>
public enum SpotifyCoverPosition { Left, Center, Right }

/// <summary>The knobs of the Spotify dedicated profile's configuration popup
/// (<c>K2.Core.SpotifyProfileConfigWindow</c>), persisted per DisplayPad by
/// <c>MainWindow.DisplayPad</c> and consumed by <c>K2.App.Services.SpotifyCoverService</c>.</summary>
/// <param name="ReturnEnabled">Come back to the Spotify profile on this pad
/// <paramref name="ReturnSeconds"/> after the cover tile's back arrow was used to leave it — the
/// same screensaver-style comeback the Discord voice page has
/// (<c>DiscordStore.VoicePageReturnEnabled</c>), but per pad, like the rest of this config.
/// On by default.</param>
/// <param name="BackArrow">Whether the cover tile wears the back-arrow badge. Purely the MARK:
/// the key hands the panel back either way, exactly like the voice page's server key, which was
/// the way out long before it carried the badge.</param>
/// <param name="ForegroundOnly">Show this profile only while the Spotify app owns the foreground
/// window, and give the pad back to the previous profile as soon as it doesn't — the same
/// condition-driven takeover the Discord voice page does for "in a call", built on the
/// focus-only mode ordinary profiles already have (<c>ProfileLaunchWatcher</c>). Off by default:
/// it takes the profile out of the user's hands, which nobody should get without asking.</param>
/// <param name="Device">Spotify Connect device id the profile's control keys target when
/// <paramref name="Source"/> is <see cref="SpotifyCoverSource.WebApi"/> — same picker and same
/// empty-means-"whichever is active" convention as a single key's own "spotify" action
/// (<c>ButtonActionDialog</c>'s device combo). Meaningless for <see cref="SpotifyCoverSource.Local"/>:
/// "media" actions are plain system keys with no device concept at all.</param>
public readonly record struct SpotifyCoverConfig(
    SpotifyCoverSource Source,
    SpotifyCoverLayout Layout,
    SpotifyTextMode TextMode,
    bool ReturnEnabled = true,
    // 10 = DefaultReturnSeconds; a const declared in the body is not in scope in the primary
    // constructor's own parameter list, so the literal is spelled out here.
    int ReturnSeconds = 10,
    bool BackArrow = true,
    bool ForegroundOnly = false,
    SpotifyCoverPosition Position = SpotifyCoverPosition.Left,
    string Device = "")
{
    public const int DefaultReturnSeconds = 10;

    /// <summary>Same range the Discord counterpart clamps to.</summary>
    public static int ClampReturnSeconds(int seconds) => System.Math.Clamp(seconds, 3, 3600);

    public static readonly SpotifyCoverConfig Default =
        new(SpotifyCoverSource.Local, SpotifyCoverLayout.Quad, SpotifyTextMode.Static);

    /// <summary>Reads one of the two ON-BY-DEFAULT flags: a pad that predates the setting has no
    /// value stored for it, and must get the default rather than a silent false.</summary>
    private static bool ParseFlagDefaultOn(string? s) => s is null || s.Length == 0 || s == "1";

    public static SpotifyCoverSource ParseSource(string? s) =>
        string.Equals(s, "webapi", System.StringComparison.OrdinalIgnoreCase)
            ? SpotifyCoverSource.WebApi : SpotifyCoverSource.Local;

    public static SpotifyCoverLayout ParseLayout(string? s) =>
        string.Equals(s, "single", System.StringComparison.OrdinalIgnoreCase)
            ? SpotifyCoverLayout.Single : SpotifyCoverLayout.Quad;

    public static SpotifyTextMode ParseTextMode(string? s) =>
        string.Equals(s, "marquee", System.StringComparison.OrdinalIgnoreCase)
            ? SpotifyTextMode.Marquee : SpotifyTextMode.Static;

    public string SourceToken => Source == SpotifyCoverSource.WebApi ? "webapi" : "local";
    public string LayoutToken => Layout == SpotifyCoverLayout.Single ? "single" : "quad";
    public string TextModeToken => TextMode == SpotifyTextMode.Marquee ? "marquee" : "static";
    public string ReturnEnabledToken => ReturnEnabled ? "1" : "0";
    public string BackArrowToken => BackArrow ? "1" : "0";
    public string ForegroundOnlyToken => ForegroundOnly ? "1" : "0";

    public static bool ParseForegroundOnly(string? s) => s == "1";

    public static SpotifyCoverPosition ParsePosition(string? s) => s switch
    {
        "center" => SpotifyCoverPosition.Center,
        "right"  => SpotifyCoverPosition.Right,
        _        => SpotifyCoverPosition.Left,
    };

    public string PositionToken => Position switch
    {
        SpotifyCoverPosition.Center => "center",
        SpotifyCoverPosition.Right  => "right",
        _                           => "left",
    };

    public static bool ParseReturnEnabled(string? s) => ParseFlagDefaultOn(s);
    public static bool ParseBackArrow(string? s) => ParseFlagDefaultOn(s);

    public static int ParseReturnSeconds(string? s) =>
        int.TryParse(s, out int n) ? ClampReturnSeconds(n) : DefaultReturnSeconds;

    public static string ParseDevice(string? s) => s ?? "";
}
