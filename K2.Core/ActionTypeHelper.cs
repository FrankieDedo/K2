using System;
using System.Collections.Generic;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// Shared helper for classifying an action's <c>ActionType</c> string — there's no central
/// enum, every device (DisplayPad, MacroPad, Everest, Everest60) just stores/reads raw
/// strings set by <see cref="ButtonActionDialog"/> or produced by import.
/// </summary>
public static class ActionTypeHelper
{
    /// <summary>
    /// True for a "bc:XYZ" action type — Base Camp's own function type preserved verbatim by
    /// import because K2 has no native equivalent for it (see BaseCampDbImporter.TranslateAction's
    /// default arm). Excludes "bc:Default", Base Camp's own "no binding" placeholder, which store
    /// loaders already filter out before it reaches any display code.
    /// </summary>
    public static bool IsUnrecognized(string? actionType) =>
        actionType is not null
        && actionType.StartsWith("bc:", StringComparison.Ordinal)
        && !string.Equals(actionType, "bc:Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Marker prefixed to a "macro" action's value by import when the referenced Base Camp
    /// macro name didn't match any macro in the user's K2 library — the original name is
    /// kept right after the marker (e.g. "***Volume ramp") so the UI can still tell the
    /// user WHICH macro the key was pointing at instead of discarding the reference.
    /// A marked value is never played (<see cref="ButtonActionEngine"/> skips it) and is
    /// shown with a yellow warning triangle rather than the red "action not found" one.
    /// </summary>
    public const string UnresolvedMacroPrefix = "***";

    /// <summary>
    /// True for a "macro" (Play Macro) action with no playable macro assigned — either no
    /// value at all, or an unresolved imported reference (value carrying the
    /// <see cref="UnresolvedMacroPrefix"/> marker; see BaseCampDbImporter.
    /// TranslateDefaultAction). Used to show a warning instead of silently doing nothing.
    /// </summary>
    public static bool IsMacroMissingTarget(string? actionType, string? actionValue) =>
        string.Equals(actionType, "macro", StringComparison.Ordinal)
        && (string.IsNullOrEmpty(actionValue) || IsUnresolvedMacroValue(actionValue));

    /// <summary>True when <paramref name="actionValue"/> carries the
    /// <see cref="UnresolvedMacroPrefix"/> marker (unresolved imported macro reference,
    /// original Base Camp name preserved after the marker).</summary>
    public static bool IsUnresolvedMacroValue(string? actionValue) =>
        actionValue is not null
        && actionValue.StartsWith(UnresolvedMacroPrefix, StringComparison.Ordinal);

    /// <summary>The original Base Camp macro name behind an
    /// <see cref="UnresolvedMacroPrefix"/>-marked value — or the value unchanged when it
    /// carries no marker.</summary>
    public static string? StripUnresolvedMacroPrefix(string? actionValue) =>
        IsUnresolvedMacroValue(actionValue)
            ? actionValue![UnresolvedMacroPrefix.Length..]
            : actionValue;

    /// <summary>Display text for a "macro" action: the assigned macro's name, or a visible
    /// "unassigned" warning when <see cref="IsMacroMissingTarget"/> (with the original Base
    /// Camp name appended when the marker preserved it) — used by every device's key-list
    /// Display/ActionSummary so an unresolved imported macro reference doesn't just show up
    /// as the raw "macro" action-type string with no indication anything is wrong.</summary>
    public static string MacroSummary(string? actionValue) =>
        string.IsNullOrEmpty(actionValue) ? Loc.Get("act_macro_unresolved")
        : IsUnresolvedMacroValue(actionValue) ? $"{Loc.Get("act_macro_unresolved")}: {StripUnresolvedMacroPrefix(actionValue)}"
        : $"{Loc.Get("act_macro")}: {actionValue}";

    /// <summary>
    /// K2's canonical <c>"media"</c> ActionValue vocabulary paired with its UI label
    /// resource — the single source of truth shared by
    /// <see cref="ButtonActionDialog"/>'s picker (<c>ButtonActionDialog.Simple</c>'s
    /// <c>MediaOptions</c>) and <see cref="MediaSummary"/> below, so a value can never
    /// show up in one place as a localized label and in the other as a bare token the
    /// picker doesn't even list.
    /// </summary>
    public static readonly (string Value, string LocKey)[] MediaKeys =
    {
        ("Play/Pause",     "media_play_pause"),
        ("Stop",           "media_stop"),
        ("Previous track", "media_prev"),
        ("Next track",     "media_next"),
        ("Volume Up",      "media_vol_up"),
        ("Volume Down",    "media_vol_down"),
        ("Mute",           "media_mute"),
        ("Shuffle",        "media_shuffle"),
    };

    /// <summary>Display text for a "media" action: the localized label of the matching
    /// <see cref="MediaKeys"/> entry, or the raw value when it matches none (a Base Camp
    /// media function K2 has no equivalent for, e.g. "Mic Mute" — preserved and shown
    /// rather than blanked). Before this, the key list printed the stored value verbatim,
    /// which for an imported profile was an internal token ("play_pause") or, when Base
    /// Camp put the function in FunctionValue instead of SubFunctionType, nothing at all
    /// (user report 2026-07-26).</summary>
    public static string MediaSummary(string? actionValue)
    {
        if (string.IsNullOrWhiteSpace(actionValue)) return Loc.Get("act_media");
        foreach (var (value, locKey) in MediaKeys)
            if (string.Equals(value, actionValue, StringComparison.OrdinalIgnoreCase))
                return Loc.Get(locKey);
        return actionValue;
    }

    /// <summary>K2's OBS Studio command vocabulary — the single source of truth shared by
    /// <see cref="ButtonActionDialog"/>'s picker (<c>ButtonActionDialog.Simple</c>'s
    /// <c>ObsOptions</c>) and <see cref="ObsSummary"/>, same pattern as <see cref="MediaKeys"/>.
    /// Values are the exact <see cref="System.ComponentModel.DescriptionAttribute"/> strings
    /// <see cref="Services.ObsBridge"/> dispatches by name.</summary>
    public static readonly (string Value, string LocKey)[] ObsCommands =
    {
        ("Start Streaming",        "obs_cmd_start_streaming"),
        ("Stop Streaming",         "obs_cmd_stop_streaming"),
        ("Start Recording",        "obs_cmd_start_recording"),
        ("Stop Recording",         "obs_cmd_stop_recording"),
        ("Pause Recording",        "obs_cmd_pause_recording"),
        ("Resume Recording",       "obs_cmd_resume_recording"),
        ("Next Profile",           "obs_cmd_next_profile"),
        ("Previous Profile",       "obs_cmd_previous_profile"),
        ("Set Current Profile",    "obs_cmd_set_current_profile"),
        ("Next Scene",             "obs_cmd_next_scene"),
        ("Previous Scene",         "obs_cmd_previous_scene"),
        ("Set Current Scene",      "obs_cmd_set_current_scene"),
        ("Set Current Source",     "obs_cmd_set_current_source"),
        ("Next Transition",        "obs_cmd_next_transition"),
        ("Previous Transition",    "obs_cmd_previous_transition"),
        ("Set Transition Duration","obs_cmd_set_transition_duration"),
        ("Set Current TransitionName", "obs_cmd_set_current_transition"),
        ("Mic Volume +",           "obs_cmd_mic_volume_up"),
        ("Mic Volume -",           "obs_cmd_mic_volume_down"),
        ("Mute Mic",               "obs_cmd_mute_mic"),
        ("Unmute Mic",             "obs_cmd_unmute_mic"),
        ("Set Mic Volume",         "obs_cmd_set_mic_volume"),
        ("Desktop Volume +",       "obs_cmd_desktop_volume_up"),
        ("Desktop Volume -",       "obs_cmd_desktop_volume_down"),
        ("Mute Desktop Volume",    "obs_cmd_mute_desktop"),
        ("Unmute Desktop Volume",  "obs_cmd_unmute_desktop"),
        ("Set Desktop Volume",     "obs_cmd_set_desktop_volume"),
        ("Enable Studio Mode",     "obs_cmd_enable_studio_mode"),
        ("Disable Studio Mode",    "obs_cmd_disable_studio_mode"),
        ("Start Replay Buffer",    "obs_cmd_start_replay_buffer"),
        ("Stop Replay Buffer",     "obs_cmd_stop_replay_buffer"),
        ("Save Replay Buffer",     "obs_cmd_save_replay_buffer"),
        ("Next Media",             "obs_cmd_next_media"),
        ("Previous Media",         "obs_cmd_previous_media"),
        ("Play Media",             "obs_cmd_play_media"),
        ("Pause Media",            "obs_cmd_pause_media"),
        ("Stop Media",             "obs_cmd_stop_media"),
        ("Open Projector",         "obs_cmd_open_projector"),
    };

    /// <summary>Display text for an "obs" action: the localized label of the matching
    /// <see cref="ObsCommands"/> entry, plus the stored argument (scene/profile/source/
    /// transition name, or a duration/volume number) when present.</summary>
    public static string ObsSummary(string? actionValue) => TildeCommandSummary(actionValue, "act_obs", ObsCommands);

    /// <summary>K2's Twitch command vocabulary — internal tags (Twitch has no existing K2
    /// import to stay compatible with, unlike OBS's Base Camp-matching Description strings),
    /// paired with the picker's loc key. Same "single source of truth" pattern as
    /// <see cref="MediaKeys"/>/<see cref="ObsCommands"/>.</summary>
    public static readonly (string Value, string LocKey)[] TwitchCommands =
    {
        ("chat_message",    "twitch_cmd_chat_message"),
        ("clear_chat",      "twitch_cmd_clear_chat"),
        ("emote_only",      "twitch_cmd_emote_only"),
        ("followers_only",  "twitch_cmd_followers_only"),
        ("slow_mode",       "twitch_cmd_slow_mode"),
        ("subscribers_only","twitch_cmd_subscribers_only"),
        ("play_ad",         "twitch_cmd_play_ad"),
        ("stream_title",    "twitch_cmd_stream_title"),
        ("stream_marker",   "twitch_cmd_stream_marker"),
        ("create_clip",     "twitch_cmd_create_clip"),
        ("open_last_clip",  "twitch_cmd_open_last_clip"),
    };

    /// <summary>Display text for a "twitch" action: the localized label of the matching
    /// <see cref="TwitchCommands"/> entry, plus the stored argument when present.</summary>
    public static string TwitchSummary(string? actionValue) => TildeCommandSummary(actionValue, "act_twitch", TwitchCommands);

    /// <summary>K2's Spotify command vocabulary — covers the actionable subset of real Base
    /// Camp's own Spotify actions (<c>_reference/decompiled/Worker/DisplayPadWorker.Helpers/
    /// SpotifyHelper.cs</c>); cover-art/"now playing" display widgets there aren't ported, same
    /// precedent as YouTube's "viewers" widget. Same "single source of truth" pattern as
    /// <see cref="TwitchCommands"/>.</summary>
    public static readonly (string Value, string LocKey)[] SpotifyCommands =
    {
        ("play_pause",       "spotify_cmd_play_pause"),
        ("next",             "spotify_cmd_next"),
        ("previous",         "spotify_cmd_previous"),
        ("like_toggle",      "spotify_cmd_like_toggle"),
        ("shuffle_toggle",   "spotify_cmd_shuffle_toggle"),
        ("repeat_cycle",     "spotify_cmd_repeat_cycle"),
        ("mute_toggle",      "spotify_cmd_mute_toggle"),
        ("volume_up",        "spotify_cmd_volume_up"),
        ("volume_down",      "spotify_cmd_volume_down"),
        ("volume_set",       "spotify_cmd_volume_set"),
        ("save_playlist",    "spotify_cmd_save_playlist"),
        ("remove_playlist",  "spotify_cmd_remove_playlist"),
    };

    /// <summary>Display text for a "spotify" action: the localized label of the matching
    /// <see cref="SpotifyCommands"/> entry, plus the stored argument when present.</summary>
    public static string SpotifySummary(string? actionValue) => TildeCommandSummary(actionValue, "act_spotify", SpotifyCommands);

    /// <summary>Shared "CommandName~arg" summary logic for the OBS/Twitch/Spotify pickers — all
    /// store their value the same way (see <see cref="ButtonActionDialog.SaveComboSpec"/>).</summary>
    private static string TildeCommandSummary(string? actionValue, string emptyLocKey, (string Value, string LocKey)[] table)
    {
        if (string.IsNullOrWhiteSpace(actionValue)) return Loc.Get(emptyLocKey);
        int tilde = actionValue.IndexOf('~');
        string cmd = tilde < 0 ? actionValue : actionValue[..tilde];
        string arg = tilde < 0 ? "" : actionValue[(tilde + 1)..];
        string label = cmd;
        foreach (var (value, locKey) in table)
            if (string.Equals(value, cmd, StringComparison.OrdinalIgnoreCase)) { label = Loc.Get(locKey); break; }
        return arg.Length > 0 ? $"{label} — {arg}" : label;
    }

    /// <summary>Short, human-readable summary for the key-list "assigned action" row —
    /// every <c>ActionType</c> must resolve to something meaningful here, never the raw
    /// internal tag on its own (e.g. "oscmd", "exec"; user report 2026-07-19). Shared by
    /// DisplayPadKey/EverestKey/MacroPadKey's list-display property so every device's key
    /// list explains what a key actually does. Callers handle their own device-specific
    /// types (DisplayPad's "dp_folder"/"dp_back" page navigation) before falling back here.</summary>
    public static string Summary(string? actionType, string? actionValue)
    {
        string val = actionValue ?? "";
        return actionType switch
        {
            "keys"     => val,
            "hotkeyswitch" => HotkeySwitchSummary(actionValue),
            "multi"    => MultiSummary(actionValue),
            "adobe" or "davinci" or "zoom" => val,
            "exec"     => System.IO.Path.GetFileName(val),
            "folder"   => FileOrFolderName(val),
            "url"      => val,
            "browser"  => BrowserSummary(val),
            "profile"  => ProfileSummary(val),
            "oscmd"    => val,
            "media"    => MediaSummary(actionValue),
            "mouse"    => val,
            "disable"  => Loc.Get("act_disable"),
            "text"     => val,
            "emoji"    => EmojiSummary(actionValue),
            "command"  => val,
            "macro"    => MacroSummary(actionValue),
            "googlehome" => GoogleHomeSummary(actionValue),
            "obs"      => ObsSummary(actionValue),
            "twitch"   => TwitchSummary(actionValue),
            "spotify"  => SpotifySummary(actionValue),
            "youtube"  => string.IsNullOrEmpty(val) ? Loc.Get("act_youtube") : val,
            "pyscript" => Loc.Get("act_pyscript"),
            _          => IsUnrecognized(actionType) ? Loc.Get("act_unrecognized") : actionType ?? "",
        };
    }

    /// <summary>Display text for an "emoji" action: the emoji itself plus its English name
    /// when the catalog knows it (key lists render text with the app font, which has no color
    /// emoji art — the name is what actually identifies it there). Public for the same reason
    /// as <see cref="GoogleHomeSummary"/>: the per-device dialogs keep their own switch.</summary>
    public static string EmojiSummary(string? actionValue)
    {
        if (string.IsNullOrEmpty(actionValue)) return Loc.Get("act_emoji");
        var entry = EmojiCatalog.Find(actionValue);
        return entry is null ? actionValue : $"{entry.Emoji}  {entry.Name}";
    }

    /// <summary>Display text for a "googlehome" action: the bound device's friendly name,
    /// or a warning when the binding no longer exists (deleted from
    /// <see cref="GoogleHomeSetupWindow"/> since the key was configured). Public (like
    /// <see cref="MacroSummary"/>) so per-device "current action" preview labels that keep
    /// their own switch instead of calling <see cref="Summary"/> outright
    /// (<c>DpKeyConfigDialog</c>/<c>NdkKeyConfigDialog</c> in K2.App) can call it directly.</summary>
    public static string GoogleHomeSummary(string? actionValue)
    {
        var binding = GoogleHomeStore.Find(actionValue);
        return binding is not null ? binding.Name : Loc.Get("gh_summary_missing");
    }

    /// <summary>Display text for a "hotkeyswitch" action: both shortcuts, so the key list
    /// shows what each press alternates between.</summary>
    public static string HotkeySwitchSummary(string? actionValue)
    {
        var spec = HotkeySwitchPayload.Parse(actionValue);
        if (spec is null || (string.IsNullOrEmpty(spec.ShortcutA) && string.IsNullOrEmpty(spec.ShortcutB)))
            return Loc.Get("act_hotkeyswitch");
        return $"{spec.ShortcutA} / {spec.ShortcutB}";
    }

    /// <summary>Display text for a "multi" action: how many steps are chained.</summary>
    public static string MultiSummary(string? actionValue)
    {
        if (string.IsNullOrWhiteSpace(actionValue)) return Loc.Get("act_multi");
        try
        {
            var steps = System.Text.Json.JsonSerializer.Deserialize<List<ActionExecutor.MultiStep>>(actionValue,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return Loc.Get("act_multi_summary", steps?.Count ?? 0);
        }
        catch (System.Text.Json.JsonException)
        {
            return Loc.Get("act_multi");
        }
    }

    private static string FileOrFolderName(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string BrowserSummary(string val)
    {
        var payload = BrowserActionPayload.Parse(val);
        if (payload is null) return val; // legacy plain URL string (or empty = launch default browser)

        string browserLabel = payload.Browser switch
        {
            "chrome"  => "Chrome",
            "edge"    => "Edge",
            "firefox" => "Firefox",
            "opera"   => "Opera",
            "brave"   => "Brave",
            _         => string.IsNullOrEmpty(payload.CustomPath) ? Loc.Get("browser_other") : FileOrFolderName(payload.CustomPath),
        };
        return string.IsNullOrEmpty(payload.Url) ? browserLabel : $"{browserLabel} — {payload.Url}";
    }

    private static string ProfileSummary(string val)
    {
        var payload = ProfileTargetPayload.Parse(val);
        if (payload is null) return val; // legacy plain "Next" | "Previous" | "1".."N"
        return payload.Targets.Count == 0 ? Loc.Get("act_profile") : string.Join(", ", payload.Targets.ConvertAll(t => t.Target));
    }

    /// <summary>Normalizes Base Camp's "OS Commands" SubFunctionType/FunctionValue (e.g.
    /// "Run task manager", "Lock computer" — confirmed verbatim against a real BC XML export,
    /// <c>Profili_BaseCamp/test/test1.xml</c>) to K2's own oscmd vocabulary
    /// (<c>ButtonActionDialog.Simple.OsCmdOptions</c>'s <c>Value</c>s: "Task Manager", "Lock",
    /// ...). Without this, importing e.g. "Lock computer" left the raw BC string as
    /// <c>ActionValue</c> — <see cref="ActionExecutor.RunOsCommand"/> still ran it fine
    /// (case-insensitive alias match), but opening the key's action dialog afterward found no
    /// matching combo entry and silently fell back to the first item ("Task Manager"), so an
    /// untouched Save would corrupt the binding (user report 2026-07-19). Falls back to the
    /// raw value unchanged for anything not in BC's known list (unrecognized OS command,
    /// preserved rather than dropped).</summary>
    public static string? NormalizeOsCommand(string? bcValue) => bcValue?.Trim().ToLowerInvariant() switch
    {
        "run task manager" or "task manager" or "taskmgr" => "Task Manager",
        "run explorer" or "explorer"                       => "Explorer",
        "calculator" or "calc"                             => "Calculator",
        "lock computer" or "lock"                           => "Lock",
        "shutdown" or "shut down"                           => "Shutdown",
        "restart"                                            => "Restart",
        "sleep"                                              => "Sleep",
        "hibernate"                                          => "Hibernate",
        _                                                    => bcValue,
    };

    /// <summary>Normalizes a Base Camp media function label (the wording differs per
    /// device family — "Next Track" on Everest, "Next track" on Makalu/Everest 60) to
    /// K2's own <see cref="MediaKeys"/> vocabulary, which is what
    /// <see cref="ActionExecutor.SendMediaKey"/> and the dialog's picker both expect.
    /// Import used to emit its own snake_case tokens ("play_pause", "volume_up") that
    /// matched NEITHER — the executor logged "key not handled" and the picker fell back
    /// to the first item, so an imported media key did nothing and, once reopened and
    /// saved, silently became Play/Pause (user report 2026-07-26). Unknown labels are
    /// passed through unchanged rather than dropped.</summary>
    public static string? NormalizeMediaKey(string? bcValue) => bcValue?.Trim().ToLowerInvariant() switch
    {
        "play/pause" or "play-pause" or "playpause" or "play_pause" => "Play/Pause",
        "stop"                                                       => "Stop",
        "previous track" or "prev track" or "previous" or "prev"
            or "prev_track" or "previous_track"                      => "Previous track",
        "next track" or "next" or "next_track"                       => "Next track",
        "volume up" or "vol up" or "volup" or "volume_up"            => "Volume Up",
        "volume down" or "vol down" or "voldown" or "volume_down"    => "Volume Down",
        "mute" or "volume mute"                                      => "Mute",
        "shuffle"                                                    => "Shuffle",
        _                                                            => bcValue,
    };
}
