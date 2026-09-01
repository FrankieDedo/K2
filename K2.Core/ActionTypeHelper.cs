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

    /// <summary>Action types that only make sense on a host with DisplayPad-style sub-pages
    /// (<see cref="IActionHost.SupportsPages"/>). Shared by <c>ButtonActionDialog</c>'s picker
    /// (hides these entirely on a host with no page concept) and
    /// <c>Services.ActionClipboard.CanPasteOn</c> (blocks pasting one
    /// onto such a host, with an error, instead of silently accepting a dead action). "dp_back"
    /// is included even though it never appears as a picker item (set only via the DisplayPad
    /// key context menu's "Set as back") since it is just as DisplayPad-page-specific as
    /// "dp_folder".</summary>
    public static readonly string[] PageOnlyActionTypes =
        { "dp_folder", "dp_back", "dp_emojibrowser", "dp_clock", "dp_sysmon", "dp_speedtest" };

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

    /// <summary>Display text for an "audiodevice" action: the saved device's friendly
    /// name, or a generic label when no device is configured yet — never the raw JSON
    /// payload.</summary>
    public static string AudioDeviceSummary(string? actionValue)
    {
        var payload = AudioDevicePayload.Parse(actionValue);
        return payload is not null && payload.Name.Length > 0 ? payload.Name : Loc.Get("act_audiodevice");
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

    /// <summary>Commands that hit endpoints Spotify blocks for Development-mode apps (library /
    /// playlist writes — see the "Web API + Development app" notes) and so <b>cannot work</b>
    /// without Extended Access on the developer app. Kept in <see cref="SpotifyCommands"/> for
    /// label/summary lookup and still executed by <see cref="ButtonActionEngine"/> (a
    /// BC-imported or previously-saved key keeps working the day access is granted), but hidden
    /// from the action picker so a new binding can't be made to something that just 403s.</summary>
    public static readonly System.Collections.Generic.HashSet<string> SpotifyCommandsUnavailable = new()
    {
        "like_toggle", "save_playlist", "remove_playlist",
    };

    /// <summary>The <see cref="SpotifyCommands"/> entries offered in the action picker — i.e.
    /// minus <see cref="SpotifyCommandsUnavailable"/>.</summary>
    public static System.Collections.Generic.IEnumerable<(string Value, string LocKey)> SpotifyCommandsPickable
        => System.Linq.Enumerable.Where(SpotifyCommands, c => !SpotifyCommandsUnavailable.Contains(c.Value));

    /// <summary>Display text for a "spotify" action: the localized label of the matching
    /// <see cref="SpotifyCommands"/> entry, plus the stored argument. The optional 3rd field
    /// (<c>command[~arg][~deviceId]</c>, the per-key target device) is not shown here — it is
    /// visible in the action dialog and would only be an opaque id in this one-line summary.</summary>
    public static string SpotifySummary(string? actionValue)
    {
        if (string.IsNullOrWhiteSpace(actionValue)) return Loc.Get("act_spotify");
        var p = actionValue.Split('~');
        string upToArg = p.Length > 1 && p[1].Length > 0 ? $"{p[0]}~{p[1]}" : p[0];
        return TildeCommandSummary(upToArg, "act_spotify", SpotifyCommands);
    }

    /// <summary>K2's Discord command vocabulary — internal tags (Base Camp has no Discord
    /// action at all, so there's nothing to stay import-compatible with). Everything except
    /// <c>send_message</c> (channel webhook) goes through the local RPC pipe, see
    /// <see cref="Services.DiscordBridge"/>. Same "single source of truth" pattern as
    /// <see cref="TwitchCommands"/>.</summary>
    public static readonly (string Value, string LocKey)[] DiscordCommands =
    {
        ("mute_toggle",       "discord_cmd_mute_toggle"),
        ("mute_on",           "discord_cmd_mute_on"),
        ("mute_off",          "discord_cmd_mute_off"),
        ("deafen_toggle",     "discord_cmd_deafen_toggle"),
        ("deafen_on",         "discord_cmd_deafen_on"),
        ("deafen_off",        "discord_cmd_deafen_off"),
        ("input_mode_toggle", "discord_cmd_input_mode_toggle"),
        ("input_volume",      "discord_cmd_input_volume"),
        ("output_volume",     "discord_cmd_output_volume"),
        ("join_voice",        "discord_cmd_join_voice"),
        ("leave_voice",       "discord_cmd_leave_voice"),
        ("user_volume",       "discord_cmd_user_volume"),
        ("user_mute_toggle",  "discord_cmd_user_mute_toggle"),
        ("send_message",      "discord_cmd_send_message"),
        // DisplayPad only: reopens the live voice page after it has been dismissed
        // (see MainWindow.DisplayPad.DiscordRoom.cs). A no-op on every other device.
        ("voice_page",        "discord_cmd_voice_page"),
    };

    /// <summary>Display text for a "discord" action: the localized label of the matching
    /// <see cref="DiscordCommands"/> entry, plus the stored argument when present.</summary>
    public static string DiscordSummary(string? actionValue) => TildeCommandSummary(actionValue, "act_discord", DiscordCommands);

    // ───────────────── Live DisplayPad tiles (K2-only, see LiveTileRenderer) ─────────────────
    //
    // Base Camp has no DisplayPad clock/monitor key to stay import-compatible with (its own
    // CPU/RAM/GPU/disk/network readouts and clock are Everest Max Media Dock / Display Dial
    // pages drawn by the keyboard firmware — see MainWindow.MediaDock.cs), so these three
    // vocabularies are K2's own, in the same (Value, LocKey) shape as every table above.

    /// <summary>Clock faces a <c>dp_clock</c> key can show. The single-unit ones
    /// (<c>hours</c>/<c>minutes</c>/<c>seconds</c>) exist so three adjacent keys can spell out
    /// one clock across the pad — the layout this whole action was requested for.</summary>
    public static readonly (string Value, string LocKey)[] ClockModes =
    {
        ("analog",    "clock_mode_analog"),
        ("digital24", "clock_mode_digital24"),
        ("digital12", "clock_mode_digital12"),
        ("vert24",    "clock_mode_vert24"),
        ("vert12",    "clock_mode_vert12"),
        ("hours",     "clock_mode_hours"),
        ("hours12",   "clock_mode_hours12"),
        ("minutes",   "clock_mode_minutes"),
        ("seconds",   "clock_mode_seconds"),
        ("date",      "clock_mode_date"),
    };

    /// <summary>Metrics a <c>dp_sysmon</c> key can show — the same set (and the same
    /// measurement sources) the Everest Max dock's PC Info pages feed from, see
    /// <c>K2.App.Services.SystemMonitor</c>.</summary>
    public static readonly (string Value, string LocKey)[] SysMonMetrics =
    {
        ("cpu",      "sysmon_cpu"),
        ("ram",      "sysmon_ram"),
        ("gpu",      "sysmon_gpu"),
        ("disk",     "sysmon_disk"),
        ("net_down", "sysmon_net_down"),
        ("net_up",   "sysmon_net_up"),
    };

    /// <summary>Readouts a <c>dp_speedtest</c> key can show. Unlike <see cref="SysMonMetrics"/>
    /// these are not sampled continuously: the key shows the LAST result and pressing it runs a
    /// new test (all three figures at once, so a ping/down/up trio of keys refreshes together —
    /// see <c>K2.App.Services.SpeedTestService</c>).</summary>
    public static readonly (string Value, string LocKey)[] SpeedTestMetrics =
    {
        ("down", "speedtest_down"),
        ("up",   "speedtest_up"),
        ("ping", "speedtest_ping"),
    };

    /// <summary>Splits a <c>dp_sysmon</c> value bound to a specific hardware sensor —
    /// <c>"&lt;lhm-id&gt;|&lt;stat&gt;|&lt;label&gt;"</c>, where <c>stat</c> is
    /// <c>cur|min|max|avg</c> and <c>label</c> is the human name captured when the sensor was
    /// picked. Returns null for the six legacy tokens (cpu/ram/gpu/disk/net_*), which have no
    /// id — LHM identifiers always start with <c>'/'</c>.</summary>
    public static (string Id, string Stat, string Label)? ParseSensorValue(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0 || v[0] != '/') return null;
        var parts = v.Split('|');
        return (parts[0],
                parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "cur",
                parts.Length > 2 ? string.Join("|", parts[2..]) : parts[0]);
    }

    /// <summary>Localized name of a sensor statistic (<c>cur|min|max|avg</c>) for the tile
    /// summary and the sensor picker's combo item.</summary>
    public static string SensorStatLabel(string? stat) => stat switch
    {
        "min" => Loc.Get("sensor_stat_min"),
        "max" => Loc.Get("sensor_stat_max"),
        "avg" => Loc.Get("sensor_stat_avg"),
        _     => Loc.Get("sensor_stat_cur"),
    };

    /// <summary>Display text for a "dp_clock"/"dp_sysmon"/"dp_speedtest" action: the localized
    /// name of the picked mode/metric, so the key list says "Analog clock" rather than
    /// "analog".</summary>
    public static string LiveTileSummary(string? actionType, string? actionValue)
    {
        string value = (actionValue ?? "").Trim();

        if (actionType == "dp_sysmon")
        {
            // A specific hardware sensor picked via "Choose sensor…".
            if (ParseSensorValue(value) is { } sensor)
                return $"{sensor.Label} · {SensorStatLabel(sensor.Stat)}";

            // "PC monitor" preset refinements: "cpu:temp" / "gpu:temp" / "disk:<id>|<name>".
            int colon = value.IndexOf(':');
            if (colon > 0)
            {
                string basePart = value[..colon];
                string arg = value[(colon + 1)..];
                if (arg == "temp" && basePart is "cpu" or "gpu")
                    return Loc.Get(basePart == "cpu" ? "sysmon_cpu_temp" : "sysmon_gpu_temp");
                if (basePart == "disk")
                {
                    int bar = arg.IndexOf('|');
                    string name = bar >= 0 ? arg[(bar + 1)..] : arg;
                    return string.Format(Loc.Get("sysmon_disk_pick_fmt"), name);
                }
            }
        }

        var table = actionType switch
        {
            "dp_clock"     => ClockModes,
            "dp_sysmon"    => SysMonMetrics,
            "dp_speedtest" => SpeedTestMetrics,
            _              => Array.Empty<(string Value, string LocKey)>(),
        };
        foreach (var (v, locKey) in table)
            if (string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) return Loc.Get(locKey);
        // An empty value means "never configured": show what the key WILL do, since the
        // pickers (and the live service) both fall back to the first entry.
        return value.Length == 0 && table.Length > 0 ? Loc.Get(table[0].LocKey) : value;
    }

    /// <summary>Shared "CommandName~arg" summary logic for the OBS/Twitch/Spotify/Discord pickers — all
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
            "exec"     => System.IO.Path.GetFileName(ExecActionPayload.PathOf(val)),
            "folder"   => FileOrFolderName(val),
            "url"      => val,
            "browser"  => BrowserSummary(val),
            "profile"  => ProfileSummary(val),
            "oscmd"    => val,
            "media"    => MediaSummary(actionValue),
            "mouse"    => val,
            "audiodevice" => AudioDeviceSummary(actionValue),
            "disable"  => Loc.Get("act_disable"),
            "text"     => val,
            "emoji"    => EmojiSummary(actionValue),
            "command"  => val,
            "macro"    => MacroSummary(actionValue),
            "googlehome" => GoogleHomeSummary(actionValue),
            "obs"      => ObsSummary(actionValue),
            "twitch"   => TwitchSummary(actionValue),
            "spotify"  => SpotifySummary(actionValue),
            "discord"  => DiscordSummary(actionValue),
            "youtube"  => string.IsNullOrEmpty(val) ? Loc.Get("act_youtube") : val,
            "pyscript" => Loc.Get("act_pyscript"),
            "dp_emojibrowser" => Loc.Get("act_emojibrowser"),
            "dp_clock" or "dp_sysmon" or "dp_speedtest" => LiveTileSummary(actionType, actionValue),
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
