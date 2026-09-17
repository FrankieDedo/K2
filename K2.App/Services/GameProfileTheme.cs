using System;
using System.Drawing;
using System.IO;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// The colour a game profile's keys are drawn in.
///
/// <para>
/// For Elite Dangerous the pad rests in the game's own HUD <b>orange</b> and STAYS there: the
/// page-wide green/orange flip that flight assist used to drive was too loud a signal for a
/// state that changes several times a minute, and it made every unrelated key (the launcher, the
/// arrows, the plain shortcuts) restless. The flight-assist reading now lives entirely on its
/// OWN key, which is the only one that changes colour: <b>red while assist is OFF, green while
/// it is on</b> — see <see cref="FlightAssistBgPath"/>. Everything else uses the orange art,
/// dim/lit by its own cockpit flag as before.
/// </para>
///
/// <para>
/// The flag in <c>Status.json</c> is "FlightAssist<b>Off</b>", so the bit being SET is the RED
/// case. When the game isn't running there is no state to reflect and the key rests green, the
/// same as assist on.
/// </para>
/// </summary>
internal static class GameProfileTheme
{
    /// <summary>The game's own HUD orange — also the resting colour when nothing is known.
    /// Declared once in <see cref="GameProfileSpecs"/>, which is where a game's colours live.</summary>
    public static readonly Color EliteOrange = GameProfileSpecs.EliteOrange;

    /// <summary>Flight-assist-on green. Single constant on purpose: this is the one line to
    /// change when the exact shade is chosen.</summary>
    public static readonly Color EliteNeonGreen = Color.FromArgb(0, 255, 156);

    /// <summary>Flight-assist-OFF red, matching <c>ed_red_on.png</c>'s glow.</summary>
    public static readonly Color EliteRed = Color.FromArgb(255, 58, 48);

    /// <summary>Resting accent for the Elite profile. Constant now — only the flight-assist key
    /// itself changes colour (see the class remarks).</summary>
    public static Color EliteAccent() => EliteOrange;

    /// <summary>True while the ship's flight assist is ON — i.e. the "FlightAssistOff" bit is
    /// clear. A game that isn't running counts as ON: green is the resting reading, and a red key
    /// with no ship behind it would be a false alarm.</summary>
    public static bool FlightAssistOn()
    {
        var st = EliteStatusReader.Snapshot();
        if (!st.Valid) return true;
        bool assistOff = st.Has(EliteStatusReader.FlagFlightAssistOff);
        LogAccentChange(st.Flags, assistOff);
        return !assistOff;
    }

    /// <summary>Accent the flight-assist KEY is drawn in: red while assist is off, green while
    /// it is on.</summary>
    public static Color FlightAssistAccent(bool assistOn) => assistOn ? EliteNeonGreen : EliteRed;

    /// <summary>Background art for the flight-assist key. Always the LIT variant — the key is
    /// never "resting", it is always reporting one of two states, and the colour carries the
    /// meaning rather than the brightness.</summary>
    public static string? FlightAssistBgPath(bool assistOn, bool lit = true) =>
        ArtFor(EliteSpec, assistOn ? "green" : "red", lit);

    /// <summary>Background art for a tile that names its own colour, and the caption colour to
    /// match. <paramref name="lit"/> is still the tile's OWN cockpit flag, so a green key is dim
    /// green when its state is off and bright green when it is on — the colour is the pilot's
    /// label, the brightness is the ship's answer.</summary>
    public static string? BgImagePathForColor(ActionTypeHelper.EdTileColor color, bool lit) =>
        ArtFor(EliteSpec, ActionTypeHelper.EdTileColorName(color), lit);

    /// <summary>Caption colour for <see cref="BgImagePathForColor"/>.</summary>
    public static string TextHexForColor(ActionTypeHelper.EdTileColor color, bool lit) =>
        TextHex(AccentForColor(color), lit);

    /// <summary>The hue behind each art colour, used for the caption and for the tile accent.</summary>
    public static Color AccentForColor(ActionTypeHelper.EdTileColor color) => color switch
    {
        ActionTypeHelper.EdTileColor.Green => EliteNeonGreen,
        ActionTypeHelper.EdTileColor.Red   => EliteRed,
        _                                  => EliteOrange,
    };

    /// <summary>Caption colour matching <see cref="FlightAssistBgPath"/>.</summary>
    public static string FlightAssistTextHex(bool assistOn, bool lit = true) =>
        TextHex(FlightAssistAccent(assistOn), lit);

    private static uint _lastLoggedFlags;
    private static bool _everLogged;

    /// <summary>Writes ONE line per flight-assist flip: the raw <c>Flags</c> word, the decoded
    /// bit and the colour chosen. Deliberately <c>App.WriteLog</c> and not a debug-level logger —
    /// a user's report reaches us as this file, and the only way to tell "the bit is inverted"
    /// from "the tile is inverted" apart is to see the raw word next to what the pad did with it.
    /// Rate-limited to the flip itself: <see cref="EliteAccent"/> is called several times per
    /// tile per second.</summary>
    private static void LogAccentChange(uint flags, bool assistOff)
    {
        bool wasOff = (_lastLoggedFlags & EliteStatusReader.FlagFlightAssistOff) != 0;
        if (_everLogged && wasOff == assistOff) return;
        _everLogged = true;
        _lastLoggedFlags = flags;
        App.WriteLog($"[ED] Flags=0x{flags:X8} FlightAssistOff bit={(assistOff ? 1 : 0)} " +
                     $"-> flight assist key={(assistOff ? "RED (assist off)" : "GREEN (assist on)")}");
    }

    /// <summary>Accent for a profile by catalogue id — its <see cref="GameProfileSpec"/>'s colour.
    /// A profile with no spec falls back to the orange rather than to an unpainted key, so an
    /// in-progress game profile still looks like one.</summary>
    public static Color AccentFor(string profileId) =>
        GameProfileSpecs.ById(profileId)?.Accent ?? EliteOrange;

    /// <summary>The accent as the <c>#RRGGBB</c> string a <c>KeyIconSpec.TextColor</c> takes —
    /// how the colour reaches the keys that are NOT Elite status tiles (the plain shortcuts,
    /// the profile arrows, the launcher), so the whole page changes together.</summary>
    public static string AccentHex(string profileId)
    {
        var c = AccentFor(profileId);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>Root the curated background art ships under — Content, not a WPF Resource, so it
    /// resolves to a real filesystem path the GDI+ renderers can load (same convention as
    /// <c>IconGalleryDefaults.GalleryRoot</c>). Each game owns one sub-folder of it, named by its
    /// <see cref="GameProfileSpec.ArtFolder"/>.</summary>
    private static readonly string GameArtRoot =
        Path.Combine(AppContext.BaseDirectory, "Assets", "GameProfiles");

    /// <summary>Art for a game by colour and lit state, following the shipped naming convention
    /// <c>{prefix}_{colour}_{on|off}.png</c>. Null when the game ships no art or the file simply
    /// isn't there — a missing PNG must degrade to "no background", never throw, because the art
    /// is content on disk and a build can be missing a file the code knows about.</summary>
    private static string? ArtFor(GameProfileSpec? spec, string color, bool lit) =>
        spec is null || spec.ArtFolder.Length == 0
            ? null
            : ExistingBg(spec, $"{spec.ArtPrefix}_{color}_{(lit ? "on" : "off")}.png");

    /// <summary>Absolute path to a curated profile's cockpit-style tile background for the LIVE
    /// ship state, or null when the profile has no such art (only Elite ships one today) or the
    /// file is missing. <paramref name="lit"/> picks the bright/glowing variant vs. the
    /// dim/resting one — for Elite that's a tile's OWN cockpit flag (gear down, lights on, ...),
    /// independent of the green/orange hue, which always follows flight assist.</summary>
    public static string? BgImagePathFor(string profileId, bool lit) =>
        BgImagePathCore(profileId, lit);

    /// <summary>Same art, but always the resting orange — used by the config dialog's key-grid
    /// preview so editing a profile looks the same regardless of whether Elite happens to be
    /// running with assist on at that moment (the user asked for a deterministic preview rather
    /// than one that flickers with the live ship state).</summary>
    public static string? BgImagePathForPreview(string profileId, bool lit) =>
        BgImagePathCore(profileId, lit);

    private static string? BgImagePathCore(string profileId, bool lit)
    {
        var spec = GameProfileSpecs.ById(profileId);
        // The colour word is the PROFILE's, not a constant: Elite's art is named "orange" and
        // Zero Company's "blue", and hard-coding one made the other profile silently frameless
        // the moment its art was renamed.
        return ArtFor(spec, spec?.RestingArtColor ?? "orange", lit);
    }

    /// <summary>The profile's own scroll-arrow art, or null when it ships none — the tile then
    /// draws a plain triangle. Named <c>{prefix}_arrow_{up|down}.png</c>, alongside the tile
    /// frames in the same folder, so a profile that wants arrows just drops two files in.</summary>
    public static string? ScrollArrowPath(string profileId, bool down)
    {
        var spec = GameProfileSpecs.ById(profileId);
        return spec is null || spec.ArtFolder.Length == 0
            ? null
            : ExistingBg(spec, $"{spec.ArtPrefix}_arrow_{(down ? "down" : "up")}.png");
    }

    /// <summary>The profile's frame for the key that is CURRENTLY SELECTED in the game — a third
    /// variant beside the lit and dim ones, named <c>{prefix}_{colour}_selected.png</c>. Null when
    /// the profile ships none, and the key then wears the ordinary lit frame.</summary>
    public static string? SelectedBgPath(string profileId)
    {
        var spec = GameProfileSpecs.ById(profileId);
        return spec is null || spec.ArtFolder.Length == 0
            ? null
            : ExistingBg(spec, $"{spec.ArtPrefix}_{spec.RestingArtColor}_selected.png");
    }

    /// <summary>The profile's placeholder art for a slot the game has left EMPTY — a soldier the
    /// squad doesn't have, an action the selected one can't take. Named <c>default_op.png</c> and
    /// <c>default_ab.png</c> beside the frames. Null when the profile ships none, and the tile is
    /// then its bare frame, which is what it was before this art existed.</summary>
    public static string? PlaceholderIconPath(string profileId, bool ability)
    {
        var spec = GameProfileSpecs.ById(profileId);
        return spec is null || spec.ArtFolder.Length == 0
            ? null
            : ExistingBg(spec, ability ? "default_ab.png" : "default_op.png");
    }

    private static GameProfileSpec? EliteSpec => GameProfileSpecs.ById(GameProfileSpecs.EliteId);

    private static string? ExistingBg(GameProfileSpec spec, string fileName)
    {
        string path = Path.Combine(GameArtRoot, spec.ArtFolder, fileName);
        if (File.Exists(path)) return path;

        // A profile that came from a game-profile module ships its art beside that module, not in
        // K2's own Assets folder. Same file names and the same sub-folder-per-game layout — only
        // the root differs.
        if (GameProfileModules.ArtRootFor(spec.Id) is { } moduleRoot)
        {
            string modulePath = Path.Combine(moduleRoot, spec.ArtFolder, fileName);
            if (File.Exists(modulePath)) return modulePath;
        }

        return null;
    }

    /// <summary>Text colour hex for a profile's key, dimmed when the key's own state is "off"
    /// (the resting/inactive cockpit reading) — same hue as the border, just muted, so an unlit
    /// toggle doesn't compete visually with a lit one.</summary>
    public static string TextHexFor(string profileId, bool lit) => TextHex(AccentFor(profileId), lit);

    /// <summary>Preview counterpart of <see cref="TextHexFor"/>: the PROFILE's own resting accent,
    /// deterministic like <see cref="BgImagePathForPreview"/> — it never follows a live game
    /// state, but it does follow the game, so a blue profile isn't previewed with Elite's orange
    /// captions on its own art.</summary>
    public static string TextHexForPreview(string profileId, bool lit) =>
        TextHex(AccentFor(profileId), lit);

    private static string TextHex(Color c, bool lit)
    {
        if (!lit) c = Color.FromArgb((int)(c.R * 0.5f), (int)(c.G * 0.5f), (int)(c.B * 0.5f));
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
