using System;
using System.Collections.Generic;

namespace K2.Core.Services;

/// <summary>
/// Last-resort default icon for a display key whose action has no picture of its own —
/// a Segoe MDL2 Assets glyph + the action's own localized summary, rendered by
/// <see cref="IconImageGenerator.TryGenerateGlyphIcon"/> (same tile layout as the folder/
/// back/emoji-browser tiles, so it lines up with them on the grid).
///
/// It sits BELOW the two existing auto-icon layers, and only runs when both come up empty:
/// 1. the per-type generators (exec/folder/dp_folder/googlehome/emoji — a real icon extracted
///    from the target itself), then
/// 2. <c>IconGalleryDefaults</c> (ported Base Camp gallery art, driven by <c>icon_mapping.xml</c>).
///
/// Layer 2 only covers the action types Base Camp shipped art for (mouse/macro/multi/
/// hotkeyswitch/youtube + the per-app packs), so before this class every OTHER type —
/// "oscmd" above all, but also url/browser/profile/media/keys/command/text/pyscript/disable —
/// silently got NO default icon at all on DisplayPad and on the Everest numpad display keys
/// (user report 2026-08-23): the key kept whatever picture was there before, or stayed blank.
///
/// Every codepoint below was verified by rendering it from the installed Segoe MDL2 Assets
/// and looking at the result — the same "a wrong guess is worse than no icon" concern
/// <see cref="GoogleHomeIconCatalog"/> documents. Unknown/unmapped action types return null
/// (no icon) rather than a meaningless placeholder, keeping the caller's "keeps whatever it
/// already had" contract.
/// </summary>
public static class ActionIconFallback
{
    /// <summary>Resolve + render in one step — the signature every other auto-icon layer
    /// uses (<c>IconGalleryDefaults.TryGenerateKeyIcon</c>, <c>IconImageGenerator.TryGenerate*</c>),
    /// so it can be chained after them with a plain <c>||</c>.</summary>
    public static bool TryGenerate(string? actionType, string? actionValue, int size, string outputPngPath, bool showCaption = true)
    {
        // The media transport keys and single-target "profile: Next/Previous" get the emoji
        // browser's own bold hand-drawn shapes instead of thin MDL2 outline glyphs — same
        // family used for its scroll keys (see MainWindow.DisplayPad.EmojiBrowser.cs's
        // EmbNavTile), more legible on a 102/72 px tile and visually consistent with it
        // (user request 2026-08-24, extended to play/stop/volume/mute on 2026-09-01).
        if ((ControlNavShape(actionType, actionValue) ?? ProfileNavShape(actionType, actionValue))
            is IconImageGenerator.NavShape navShape)
        {
            string caption = showCaption ? Caption(actionType, actionValue) : "";
            return IconImageGenerator.TryGenerateNavIcon(navShape, caption, size, outputPngPath);
        }

        string? glyph = ResolveGlyph(actionType, actionValue);
        return glyph is not null
            && IconImageGenerator.TryGenerateGlyphIcon(glyph, Caption(actionType, actionValue), size, outputPngPath, showCaption);
    }

    /// <summary>Solid hand-drawn shape for a transport/volume/repeat control, whether it comes
    /// as a "media" action (system media keys) or a "spotify" one (Web API command — the
    /// Spotify dedicated profile's seeded buttons switch between the two depending on the
    /// profile's Source setting, see <c>MainWindow.DpSpotifySeedsFor</c>). Null for the ones
    /// still served by an MDL2 glyph (Shuffle).</summary>
    private static IconImageGenerator.NavShape? ControlNavShape(string? actionType, string? actionValue)
    {
        string? canonical = CanonicalControlValue(actionType, actionValue);
        return canonical switch
        {
            // Double play triangles, not the single scroll arrow the profile Next/Previous
            // keys use — the universal transport symbol (user request 2026-09-01).
            "Previous track" => IconImageGenerator.NavShape.PrevTrack,
            "Next track"     => IconImageGenerator.NavShape.NextTrack,
            // Same request: the MDL2 play/stop/speaker glyphs are hollow outlines, out of place
            // next to the solid triangles above — redrawn filled, same family.
            "Play/Pause"     => IconImageGenerator.NavShape.Play,
            "Stop"           => IconImageGenerator.NavShape.Stop,
            "Volume Up"      => IconImageGenerator.NavShape.VolumeUp,
            "Volume Down"    => IconImageGenerator.NavShape.VolumeDown,
            "Mute"           => IconImageGenerator.NavShape.Mute,
            // "spotify -> repeat_cycle" only — plain "media" has no repeat key at all, so this
            // never fires for actionType "media" (CanonicalControlValue returns null for it).
            "Repeat"         => IconImageGenerator.NavShape.Repeat,
            _                => null,
        };
    }

    /// <summary>Maps a "media" value OR a "spotify" transport/volume/repeat command to the same
    /// canonical label, so one shape/glyph table serves both action types. Null for every other
    /// spotify command (like/playlist/device-scoped volume_set), which keep the generic Spotify
    /// glyph from <see cref="TypeGlyphs"/>.</summary>
    private static string? CanonicalControlValue(string? actionType, string? actionValue)
    {
        if (string.Equals(actionType, "media", StringComparison.OrdinalIgnoreCase))
            return ActionTypeHelper.NormalizeMediaKey((actionValue ?? "").Trim()) ?? (actionValue ?? "").Trim();

        if (string.Equals(actionType, "spotify", StringComparison.OrdinalIgnoreCase))
        {
            string cmd = (actionValue ?? "").Split('~')[0].Trim();
            return SpotifyControlAlias.TryGetValue(cmd, out var alias) ? alias : null;
        }
        return null;
    }

    /// <summary>"spotify" command → the "media" label with the same meaning, purely for icon
    /// lookup (execution is unaffected — the two action types dispatch completely separately in
    /// <c>ButtonActionEngine</c>).</summary>
    private static readonly Dictionary<string, string> SpotifyControlAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["previous"]       = "Previous track",
        ["next"]           = "Next track",
        ["play_pause"]     = "Play/Pause",
        ["volume_up"]      = "Volume Up",
        ["volume_down"]    = "Volume Down",
        ["mute_toggle"]    = "Mute",
        ["shuffle_toggle"] = "Shuffle",
        ["repeat_cycle"]   = "Repeat",
    };

    /// <summary>Same 7 commands as <see cref="SpotifyControlAlias"/> (repeat_cycle included) →
    /// the loc key the equivalent "media" value's own picker/caption uses, for
    /// <see cref="Caption"/>.</summary>
    private static readonly Dictionary<string, string> ControlCaptionLocKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["previous"]       = "media_prev",
        ["next"]           = "media_next",
        ["play_pause"]     = "media_play_pause",
        ["volume_up"]      = "media_vol_up",
        ["volume_down"]    = "media_vol_down",
        ["mute_toggle"]    = "media_mute",
        ["shuffle_toggle"] = "media_shuffle",
        ["repeat_cycle"]   = "media_repeat",
    };

    /// <summary>Nav-arrow shape for a single-target "profile" action whose target is the
    /// legacy Next/Previous keyword — a named profile or a multi-device payload keeps the
    /// generic profile glyph (a switch destination isn't "next" or "previous" of anything).</summary>
    private static IconImageGenerator.NavShape? ProfileNavShape(string? actionType, string? actionValue)
    {
        if (!string.Equals(actionType, "profile", StringComparison.OrdinalIgnoreCase)) return null;
        string val = (actionValue ?? "").Trim();
        var payload = ProfileTargetPayload.Parse(val);
        string target = payload is null
            ? val
            : (payload.Targets.Count == 1 ? payload.Targets[0].Target : "");
        return target switch
        {
            "Next"     => IconImageGenerator.NavShape.Right,
            "Previous" => IconImageGenerator.NavShape.Left,
            _          => null,
        };
    }

    /// <summary>True when this class has a glyph for the action — lets a caller decide
    /// whether a default icon is available without rendering it.</summary>
    public static bool CanGenerate(string? actionType, string? actionValue) =>
        ResolveGlyph(actionType, actionValue) is not null;

    /// <summary>True for a transport/volume/repeat control — "media" or the matching "spotify"
    /// command (<see cref="CanonicalControlValue"/>) — regardless of which shape/glyph ends up
    /// drawing it. Callers use this to make these specific values SKIP the Base Camp gallery
    /// entirely rather than just falling back to it last: <c>icon_mapping.xml</c> DOES have a
    /// gallery row for every one of these "spotify" commands (Previous/Play/Next/Volume/Mute/
    /// Repeat/Shuffle — Base Camp shipped art for all of them), so the normal
    /// gallery-vs-K2-glyph tie-break (<c>KeyIconSpec.UseK2Icons</c>) would keep picking the
    /// gallery's plain photo tile by default — losing the shared shape a "media" key gets, AND
    /// its caption, since gallery art never draws one (user report 2026-09-01: "l'icona di
    /// default e' sempre quella [...] non generano nemmeno il text").</summary>
    public static bool IsControl(string? actionType, string? actionValue) =>
        CanonicalControlValue(actionType, actionValue) is not null;

    /// <summary>Spotify's own brand green — the "this control is engaged" color for Shuffle/
    /// Repeat, matching the real Spotify app rather than K2's own accent theme (user request
    /// 2026-09-01: explicit hex <c>#1DB954</c>).</summary>
    private static readonly System.Drawing.Color SpotifyGreen = System.Drawing.ColorTranslator.FromHtml("#1DB954");

    /// <summary>Live Play/Pause icon for the Spotify dedicated profile's control key — the shape
    /// shows what pressing it WILL do (a pause glyph while playing, a play glyph while paused),
    /// same convention Spotify's own UI uses. Caption stays the generic "Play/Pause" either way
    /// (user request 2026-09-01).</summary>
    public static bool TryGenerateSpotifyPlayPauseIcon(bool isPlaying, int size, string outputPngPath, bool showCaption = true) =>
        IconImageGenerator.TryGenerateNavIcon(
            isPlaying ? IconImageGenerator.NavShape.Pause : IconImageGenerator.NavShape.Play,
            showCaption ? Loc.Get("media_play_pause") : "", size, outputPngPath);

    /// <summary>Live Shuffle icon: the same glyph <see cref="MediaGlyphs"/> already draws for a
    /// plain "media -> Shuffle" key, default color while off, turned <see cref="SpotifyGreen"/>
    /// while shuffle is actually on — only the arrows change, the tile itself doesn't (user
    /// request 2026-09-01: "trasforma solo le frecce in verde").</summary>
    public static bool TryGenerateSpotifyShuffleIcon(bool on, int size, string outputPngPath, bool showCaption = true) =>
        IconImageGenerator.TryGenerateGlyphIcon(MediaGlyphs["Shuffle"],
            showCaption ? Loc.Get("media_shuffle") : "", size, outputPngPath, showCaption,
            tint: on ? SpotifyGreen : null);

    /// <summary>Live Repeat icon: default color while off; <see cref="SpotifyGreen"/> plus a "1"
    /// badge while repeating just the current track; <see cref="SpotifyGreen"/> plus an "∞"
    /// badge while repeating the whole context (playlist/album/queue) — three states, one shape
    /// (user request 2026-09-01).</summary>
    public static bool TryGenerateSpotifyRepeatIcon(SpotifyRepeatMode mode, int size, string outputPngPath, bool showCaption = true) =>
        IconImageGenerator.TryGenerateNavIcon(IconImageGenerator.NavShape.Repeat,
            showCaption ? Loc.Get("media_repeat") : "", size, outputPngPath,
            tint: mode == SpotifyRepeatMode.Off ? null : SpotifyGreen,
            badge: mode switch
            {
                SpotifyRepeatMode.Track   => "1",
                SpotifyRepeatMode.Context => "∞",
                _                         => null,
            });

    /// <summary>Value-specific glyph first (an "oscmd" key is far more useful showing a
    /// padlock/power button than a generic "system command" pictogram), then the per-type
    /// one.</summary>
    private static string? ResolveGlyph(string? actionType, string? actionValue)
    {
        if (string.IsNullOrEmpty(actionType) || actionType == "none") return null;

        string type = actionType.ToLowerInvariant();
        string value = (actionValue ?? "").Trim();

        switch (type)
        {
            case "oscmd":
                // Imported Base Camp values use their own wording ("Run Task Manager",
                // "Lock Computer") — normalize to K2's vocabulary before looking up.
                string os = ActionTypeHelper.NormalizeOsCommand(value) ?? value;
                if (OsCmdGlyphs.TryGetValue(os, out var osGlyph)) return osGlyph;
                break;

            case "media":
            case "spotify":
                // Shuffle is the only control left in MediaGlyphs (everything else in
                // ControlNavShape) — CanonicalControlValue covers both action types, so a
                // "spotify -> shuffle_toggle" key gets the same glyph as a "media -> Shuffle" one.
                string? canonical = CanonicalControlValue(type, value);
                if (canonical is not null && MediaGlyphs.TryGetValue(canonical, out var mediaGlyph))
                    return mediaGlyph;
                break;

            case "discord":
                // Value is "command~arg" (see ButtonActionEngine) — the command alone decides
                // the glyph; a mic/speaker tile says far more than the generic Discord one.
                string discord = value.Split('~')[0];
                if (DiscordGlyphs.TryGetValue(discord, out var discordGlyph)) return discordGlyph;
                break;
        }

        if (TypeGlyphs.TryGetValue(type, out var typeGlyph)) return typeGlyph;
        return ActionTypeHelper.IsUnrecognized(actionType) ? "" : null; // warning triangle
    }

    /// <summary>Tile caption: the action's own key-list summary (already localized where a
    /// localized form exists) when it says something, plus a localized per-value label for
    /// the two picker-backed types whose summary is the raw stored English value. Falls back
    /// to the action type's UI name so a tile is never captionless. Public so the key-config
    /// dialogs can recompute the exact text a default icon baked in, to prefill "Add/Edit
    /// text" instead of stacking new text on top of it.</summary>
    public static string Caption(string? actionType, string? actionValue)
    {
        string type = (actionType ?? "").ToLowerInvariant();
        string value = (actionValue ?? "").Trim();

        if (type == "oscmd")
        {
            string os = ActionTypeHelper.NormalizeOsCommand(value) ?? value;
            if (OsCmdLocKeys.TryGetValue(os, out var locKey)) return Loc.Get(locKey);
        }

        // A "spotify" transport/volume/repeat command shares its shape/glyph with the
        // equivalent "media" one (see ControlNavShape) and gets the SAME short caption too:
        // the Spotify dedicated profile reseeds its control keys between "media" and "spotify"
        // depending on the Source setting, and the full command summary below ("Cycle Repeat
        // Mode") is both longer than the tile was designed for and would make the row visibly
        // change wording on every source switch (user request 2026-09-01).
        if (type == "spotify" && ControlCaptionLocKeys.TryGetValue(value.Split('~')[0].Trim(), out var ctlKey))
            return Loc.Get(ctlKey);

        string summary = ActionTypeHelper.Summary(actionType, actionValue);
        if (!string.IsNullOrWhiteSpace(summary) && !string.Equals(summary, actionType, StringComparison.Ordinal))
            return summary;

        string typeLoc = Loc.Get("act_" + type);
        return string.IsNullOrWhiteSpace(typeLoc) ? summary : typeLoc;
    }

    /// <summary>Canonical "oscmd" values (see <see cref="ActionTypeHelper.NormalizeOsCommand"/>,
    /// which is also what the picker in <c>ButtonActionDialog.Simple</c> stores).</summary>
    private static readonly Dictionary<string, string> OsCmdGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task Manager"] = "", // chart in a window
        ["Calculator"]   = "",
        ["Explorer"]     = "", // file explorer
        ["Lock"]         = "", // padlock
        ["Shutdown"]     = "", // power button
        ["Restart"]      = "", // circular arrow
        ["Sleep"]        = "", // crescent moon
        ["Hibernate"]    = "", // crescent moon (thinner — the deeper of the two)
    };

    /// <summary>Same keys as <see cref="OsCmdGlyphs"/> → the loc keys the picker itself uses,
    /// so the tile caption reads exactly like the option the user chose in the dialog (the
    /// generic <see cref="ActionTypeHelper.Summary"/> returns the raw English value here).</summary>
    private static readonly Dictionary<string, string> OsCmdLocKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Task Manager"] = "oscmd_taskmgr",
        ["Calculator"]   = "oscmd_calc",
        ["Explorer"]     = "oscmd_explorer",
        ["Lock"]         = "oscmd_lock",
        ["Shutdown"]     = "oscmd_shutdown",
        ["Restart"]      = "oscmd_restart",
        ["Sleep"]        = "oscmd_sleep",
        ["Hibernate"]    = "oscmd_hibernate",
    };

    /// <summary>Canonical "media" values (see <see cref="ActionTypeHelper.MediaKeys"/>). Only
    /// Shuffle is left here: every other media key is drawn as a solid shape by
    /// <see cref="MediaNavShape"/>, which runs before this dictionary is ever consulted.</summary>
    private static readonly Dictionary<string, string> MediaGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shuffle"]        = "",
    };

    /// <summary>Per-command glyphs for the "discord" action (see
    /// <see cref="ActionTypeHelper.DiscordCommands"/>) — rendered and eyeballed like every
    /// other codepoint in this file:  microphone,  microphone crossed out,  speaker,
    ///  speaker muted,  phone,  exit door,  people,  chat bubble.</summary>
    private static readonly Dictionary<string, string> DiscordGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mute_toggle"]       = "",
        ["mute_on"]           = "",
        ["mute_off"]          = "",
        ["deafen_toggle"]     = "",
        ["deafen_on"]         = "",
        ["deafen_off"]        = "",
        ["input_mode_toggle"] = "",
        ["input_volume"]      = "",
        ["output_volume"]     = "",
        ["join_voice"]        = "",
        ["leave_voice"]       = "",
        ["voice_page"]        = "",
        ["user_volume"]       = "",
        ["user_mute_toggle"]  = "",
        ["send_message"]      = "",
    };

    /// <summary>Per-action-type glyph, used when no value-specific one applies. Types already
    /// covered by an earlier layer (exec/folder/dp_folder/googlehome/emoji/dp_back) are absent
    /// on purpose — they never reach this class.</summary>
    private static readonly Dictionary<string, string> TypeGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["url"]          = "", // chain link
        ["browser"]      = "", // globe
        ["profile"]      = "", // person + switch arrows
        ["oscmd"]        = "", // PC
        ["media"]        = "", // music notes
        ["mouse"]        = "",
        ["keys"]         = "", // keyboard
        ["hotkeyswitch"] = "", // sync arrows
        ["multi"]        = "", // list
        ["command"]      = "", // console window
        ["text"]         = "", // pencil
        ["macro"]        = "", // clock with history arrow
        ["pyscript"]     = "", // { }
        ["disable"]      = "", // cancel
        ["obs"]          = "",
        ["twitch"]       = "",
        ["spotify"]      = "",
        ["youtube"]      = "",
        ["discord"]      = "", // headset
        ["audiodevice"]  = "", // speaker (Segoe MDL2 "Volume")
        ["adobe"]        = "",
        ["davinci"]      = "",
        ["zoom"]         = "",
    };
}
