using System.Linq;
using System.Windows;
using System.Windows.Controls;
using K2.Core.Services;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the shared "System command" / "Media key" / "Mouse action" /
/// "Macro" / "Google Home" / "OBS Studio" / "Twitch" combo-box panel. Each type has a small
/// fixed set of values (or, for macro/googlehome, a dynamic list), so a picker replaces the
/// old free-text box. OBS/Twitch/Spotify commands needing an argument show an extra control
/// and round-trip it as <c>"CommandName~arg"</c> — the same <c>~</c>-separated wire format
/// real Base Camp already used for OBS (<c>OtherDeviceOperations.cs</c>). Most take a number
/// or free text (<c>TxtComboArg</c>), but OBS's four named-object commands ("Set Current
/// Scene/Profile/Source/TransitionName") get an editable <c>CbComboArgList</c> combo instead,
/// live-populated from the connected OBS instance — matching real Base Camp's own secondary
/// dropdown for these ("Scene" opens a scene-name submenu, not a text box).
/// </summary>
public partial class ButtonActionDialog
{
    private readonly record struct ComboOption(string Value, string LocKey);

    private static readonly ComboOption[] OsCmdOptions =
    {
        new("Task Manager", "oscmd_taskmgr"),
        new("Calculator",   "oscmd_calc"),
        new("Explorer",     "oscmd_explorer"),
        new("Lock",         "oscmd_lock"),
        new("Shutdown",     "oscmd_shutdown"),
        new("Restart",      "oscmd_restart"),
        new("Sleep",        "oscmd_sleep"),
        new("Hibernate",    "oscmd_hibernate"),
    };

    /// <summary>Built from <see cref="ActionTypeHelper.MediaKeys"/> — the same table the
    /// key-list summary reads, so picker and list can't drift apart.</summary>
    private static readonly ComboOption[] MediaOptions =
        ActionTypeHelper.MediaKeys.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    private static readonly ComboOption[] MouseOptions =
    {
        new("Left Button",   "mouse_left"),
        new("Right Button",  "mouse_right"),
        new("Middle Button", "mouse_middle"),
        new("Forward",       "mouse_forward"),
        new("Backward",      "mouse_backward"),
        new("Scroll Up",     "mouse_scroll_up"),
        new("Scroll Down",   "mouse_scroll_down"),
        new("Scroll Left",   "mouse_scroll_left"),
        new("Scroll Right",  "mouse_scroll_right"),
    };

    /// <summary>Built from <see cref="ActionTypeHelper.ObsCommands"/> — mirrors <see cref="MediaOptions"/>.
    /// Excludes the pure <c>Get*</c> query commands <see cref="Services.ObsBridge"/> also
    /// exposes — nothing meaningful to bind to a keypress.</summary>
    private static readonly ComboOption[] ObsOptions =
        ActionTypeHelper.ObsCommands.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <summary>Built from <see cref="ActionTypeHelper.TwitchCommands"/> — mirrors <see cref="MediaOptions"/>.</summary>
    private static readonly ComboOption[] TwitchOptions =
        ActionTypeHelper.TwitchCommands.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <summary>Built from <see cref="ActionTypeHelper.SpotifyCommands"/> — mirrors <see cref="MediaOptions"/>.</summary>
    private static readonly ComboOption[] SpotifyOptions =
        ActionTypeHelper.SpotifyCommands.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <summary>Built from <see cref="ActionTypeHelper.DiscordCommands"/> — mirrors <see cref="MediaOptions"/>.</summary>
    private static readonly ComboOption[] DiscordOptions =
        ActionTypeHelper.DiscordCommands.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <summary>OBS commands that take a free-text argument (scene/profile/source/transition
    /// name, or a numeric duration/volume) — see the class remarks for the wire format.</summary>
    private static readonly System.Collections.Generic.HashSet<string> ObsCommandsNeedingArg = new()
    {
        "Set Current Profile", "Set Current Scene", "Set Current Source",
        "Set Current TransitionName", "Set Transition Duration",
        "Set Mic Volume", "Set Desktop Volume",
    };

    /// <summary>Subset of <see cref="ObsCommandsNeedingArg"/> whose argument names a live OBS
    /// object (scene/profile/source/transition) — real Base Camp shows these as a secondary
    /// dropdown (e.g. "Set Current Scene" opens a scene-name submenu) rather than free text,
    /// backed by <see cref="Services.ObsBridge"/>'s <c>List*Names()</c> helpers. The remaining
    /// two (duration/volume) are numbers, not named objects, so they keep the plain textbox.</summary>
    private static readonly System.Collections.Generic.HashSet<string> ObsListArgCommands = new()
    {
        "Set Current Profile", "Set Current Scene", "Set Current Source", "Set Current TransitionName",
    };

    /// <summary>Twitch commands that take a free-text argument (message/title text, or a
    /// numeric duration in minutes/seconds) — see the class remarks for the wire format.</summary>
    private static readonly System.Collections.Generic.HashSet<string> TwitchCommandsNeedingArg = new()
    {
        "chat_message", "followers_only", "slow_mode", "play_ad", "stream_title",
    };

    /// <summary>Spotify commands that take a free-text argument (a volume step/percent, or a
    /// playlist id) — see the class remarks for the wire format.</summary>
    private static readonly System.Collections.Generic.HashSet<string> SpotifyCommandsNeedingArg = new()
    {
        "volume_up", "volume_down", "volume_set", "save_playlist", "remove_playlist",
    };

    /// <summary>Discord commands that take an argument: a volume (absolute "70" or relative
    /// "+10"/"-10"), a voice-channel id, a "userId:percent" pair, a user id, or the webhook
    /// message text — see the class remarks for the wire format.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DiscordCommandsNeedingArg = new()
    {
        "input_volume", "output_volume", "join_voice", "user_volume", "user_mute_toggle", "send_message",
    };

    /// <summary>Discord's one list-backed argument: the voice channel to join, populated live
    /// from the connected Discord client (<see cref="Services.DiscordBridge.ListVoiceChannels"/>)
    /// exactly like OBS's scene/profile/source names.</summary>
    private static readonly System.Collections.Generic.HashSet<string> DiscordListArgCommands = new()
    {
        "join_voice",
    };

    /// <summary>Built from <see cref="ActionTypeHelper.ClockModes"/>/<c>SysMonMetrics</c>/
    /// <c>SpeedTestMetrics</c> — the live DisplayPad tiles (clock face, PC monitor gauge,
    /// speed-test readout), mirroring <see cref="MediaOptions"/>.</summary>
    private static readonly ComboOption[] ClockOptions =
        ActionTypeHelper.ClockModes.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <inheritdoc cref="ClockOptions"/>
    private static readonly ComboOption[] SysMonOptions =
        ActionTypeHelper.SysMonMetrics.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    /// <inheritdoc cref="ClockOptions"/>
    private static readonly ComboOption[] SpeedTestOptions =
        ActionTypeHelper.SpeedTestMetrics.Select(m => new ComboOption(m.Value, m.LocKey)).ToArray();

    private static ComboOption[] OptionsFor(string tag) => tag switch
    {
        "dp_clock"     => ClockOptions,
        "dp_sysmon"    => SysMonOptions,
        "dp_speedtest" => SpeedTestOptions,
        "oscmd"   => OsCmdOptions,
        "media"   => MediaOptions,
        "mouse"   => MouseOptions,
        "obs"     => ObsOptions,
        "twitch"  => TwitchOptions,
        "spotify" => SpotifyOptions,
        "discord" => DiscordOptions,
        _         => System.Array.Empty<ComboOption>(),
    };

    private static string LabelKeyFor(string tag) => tag switch
    {
        "oscmd"      => "act_oscmd",
        "media"      => "act_media",
        "mouse"      => "act_mouse",
        "macro"      => "act_macro",
        "googlehome" => "act_googlehome",
        "obs"        => "act_obs",
        "twitch"     => "act_twitch",
        "spotify"    => "act_spotify",
        "discord"    => "act_discord",
        "audiodevice" => "act_audiodevice",
        "dp_clock"     => "act_dp_clock",
        "dp_sysmon"    => "act_dp_sysmon",
        "dp_speedtest" => "act_dp_speedtest",
        _            => "dlg_value",
    };

    /// <summary>Whether <paramref name="tag"/>'s picker shows the extra argument textbox for
    /// the given command value — currently "obs"/"twitch"/"spotify"/"discord" only.</summary>
    private static bool CommandNeedsArg(string tag, string command) => tag switch
    {
        "obs"     => ObsCommandsNeedingArg.Contains(command),
        "twitch"  => TwitchCommandsNeedingArg.Contains(command),
        "spotify" => SpotifyCommandsNeedingArg.Contains(command),
        "discord" => DiscordCommandsNeedingArg.Contains(command),
        _         => false,
    };

    private string? _comboPanelTag;
    private int _obsArgFetchToken;

    /// <summary>Repopulates the combo only when the type actually changed (keeps the current selection on unrelated UpdatePanels refreshes).</summary>
    private void EnsureComboPanel(string tag)
    {
        UpdateComboManageButtons(tag);
        if (_comboPanelTag == tag) { UpdateComboArgVisibility(); return; }
        _comboPanelTag = tag;
        LblComboPanel.Text = Loc.Get(LabelKeyFor(tag));
        PopulateCombo(tag, null);
        UpdateComboArgVisibility();
    }

    private void LoadComboSpec(string tag, string currentValue)
    {
        UpdateComboManageButtons(tag);
        _comboPanelTag = tag;
        LblComboPanel.Text = Loc.Get(LabelKeyFor(tag));

        if (tag is "obs" or "twitch" or "spotify" or "discord")
        {
            var (cmd, arg) = SplitComboValue(currentValue);
            PopulateCombo(tag, cmd);
            TxtComboArg.Text = arg;
            UpdateComboArgVisibility(arg);
        }
        else
        {
            PopulateCombo(tag, currentValue);
        }
    }

    private void UpdateComboManageButtons(string tag)
    {
        BtnGhManage.Visibility = tag == "googlehome" ? Visibility.Visible : Visibility.Collapsed;
        BtnObsSettings.Visibility = tag == "obs" ? Visibility.Visible : Visibility.Collapsed;
        BtnTwitchSettings.Visibility = tag == "twitch" ? Visibility.Visible : Visibility.Collapsed;
        BtnSpotifySettings.Visibility = tag == "spotify" ? Visibility.Visible : Visibility.Collapsed;
        BtnDiscordSettings.Visibility = tag == "discord" ? Visibility.Visible : Visibility.Collapsed;
        BtnAudioDeviceRefresh.Visibility = tag == "audiodevice" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static (string Command, string Arg) SplitComboValue(string value)
    {
        int i = value.IndexOf('~');
        return i < 0 ? (value, "") : (value[..i], value[(i + 1)..]);
    }

    private void CbComboValue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateComboArgVisibility();
        UpdateSubActionCrumb();
    }

    /// <summary>Shows the right argument control for the selected command — the live-list
    /// <see cref="CbComboArgList"/> for scene/profile/source/transition names (kicking off an
    /// async OBS fetch), or the plain <see cref="TxtComboArg"/> textbox for numeric args
    /// (duration/volume) and Twitch/Spotify's free-text ones. <paramref name="presetArg"/> is
    /// the value to preselect once the live list finishes loading (an existing key's saved
    /// arg on dialog-open); omit it to keep whatever's currently typed (plain re-selection).</summary>
    private void UpdateComboArgVisibility(string? presetArg = null)
    {
        string? cmd = CbComboValue.SelectedItem is ComboBoxItem ci ? ci.Tag as string : null;
        bool needsArg = _comboPanelTag is not null && cmd is not null && CommandNeedsArg(_comboPanelTag, cmd);
        bool listArg = needsArg && IsListArgCommand(_comboPanelTag!, cmd!);

        CbComboArgList.Visibility = listArg ? Visibility.Visible : Visibility.Collapsed;
        TxtComboArg.Visibility = needsArg && !listArg ? Visibility.Visible : Visibility.Collapsed;
        // The textbox hint is OBS-worded by default (it was OBS-only first) — Discord's
        // arguments are volumes/user ids/message text, so it gets its own.
        TxtComboArg.ToolTip = Loc.Get(_comboPanelTag == "discord" ? "discord_arg_hint" : "obs_arg_hint");

        if (listArg) PopulateListArg(_comboPanelTag!, cmd!, presetArg ?? CbComboArgList.Text);
    }

    /// <summary>Whether the argument for <paramref name="command"/> is picked from a list
    /// fetched live from the target app, rather than typed free-hand.</summary>
    private static bool IsListArgCommand(string tag, string command) => tag switch
    {
        "obs"     => ObsListArgCommands.Contains(command),
        "discord" => DiscordListArgCommands.Contains(command),
        _         => false,
    };

    /// <summary>Fetches the live names for an OBS list-backed command off the UI thread —
    /// <see cref="Services.ObsBridge"/>'s <c>List*Names()</c> connect on demand and can block
    /// up to the connect timeout, so this must never run inline on a UI callback (see
    /// <c>ObsBridge.EnsureConnected</c>'s remarks). <see cref="_obsArgFetchToken"/> discards a
    /// stale result if the user picks a different command before this one finishes — the combo
    /// stays editable throughout so typing a name works even while (or instead of) waiting.</summary>
    private void PopulateListArg(string tag, string command, string? preselect)
    {
        int token = ++_obsArgFetchToken;
        CbComboArgList.Text = preselect ?? "";

        System.Threading.Tasks.Task.Run(() => FetchListArg(tag, command))
            .ContinueWith(t =>
            {
                if (token != _obsArgFetchToken) return;
                CbComboArgList.ItemsSource = t.Result;
                CbComboArgList.Text = preselect ?? "";
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string[] FetchListArg(string tag, string command) => tag == "discord"
        ? DiscordBridge.ListVoiceChannels()
        : command switch
    {
        "Set Current Scene" => ObsBridge.ListSceneNames(),
        "Set Current Profile" => ObsBridge.ListProfileNames(),
        "Set Current Source" => ObsBridge.ListSourceNames(),
        "Set Current TransitionName" => ObsBridge.ListTransitionNames(),
        _ => System.Array.Empty<string>(),
    };

    private void BtnObsSettings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new ObsSettingsWindow { Owner = this };
        wnd.ShowDialog();
    }

    private void BtnTwitchSettings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new TwitchSettingsWindow { Owner = this };
        wnd.ShowDialog();
    }

    private void BtnSpotifySettings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new SpotifySettingsWindow { Owner = this };
        wnd.ShowDialog();
    }

    private void BtnDiscordSettings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new DiscordSettingsWindow { Owner = this };
        wnd.ShowDialog();
    }

    /// <summary>Re-scans Windows playback devices without closing the dialog — the list
    /// only refreshes on open/type-switch otherwise, so plugging a headset in mid-dialog
    /// would otherwise need a cancel+reopen to show up. Keeps the current selection when
    /// it's still present.</summary>
    private void BtnAudioDeviceRefresh_Click(object sender, RoutedEventArgs e)
    {
        string? current = CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag : null;
        PopulateCombo("audiodevice", current);
    }

    private void PopulateCombo(string tag, string? selectValue)
    {
        CbComboValue.Items.Clear();

        if (tag == "macro")
        {
            // Dynamic list (not a fixed enum like oscmd/media/mouse): the macro library
            // is owned by the host app (K2.App), so K2.Core asks it via IActionHost rather
            // than referencing MacroStore directly. Names are shown as-is (no Loc lookup).
            foreach (var name in _host?.ListMacroNames() ?? System.Array.Empty<string>())
                CbComboValue.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        else if (tag == "googlehome")
        {
            // Dynamic list too, but host-agnostic (unlike macro): bindings are captured
            // once via GoogleHomeSetupWindow and shared by every device/host, so K2.Core
            // reads them straight from GoogleHomeStore instead of going through IActionHost.
            // Only IsEnabled ones are offered here — unchecking a device in the setup
            // window's checklist hides it from NEW assignments without breaking a key
            // already bound to it (see PopulateCombo's "dynamicList" fallback below).
            foreach (var binding in GoogleHomeStore.List().Where(b => b.IsEnabled))
                CbComboValue.Items.Add(new ComboBoxItem { Content = binding.Name, Tag = binding.Id });
        }
        else if (tag == "audiodevice")
        {
            // Dynamic list too, but sourced live from Windows itself (not a stored
            // catalog) — Tag carries the full AudioDevicePayload JSON (id+name) so it can
            // be saved as-is, and re-matched below by name as well as by id.
            foreach (var dev in Services.AudioDeviceService.ListPlaybackDevices())
                CbComboValue.Items.Add(new ComboBoxItem
                {
                    Content = dev.Name,
                    Tag = new AudioDevicePayload { Id = dev.Id, Name = dev.Name }.ToJson(),
                });
        }
        else
        {
            foreach (var opt in OptionsFor(tag))
                CbComboValue.Items.Add(new ComboBoxItem { Content = Loc.Get(opt.LocKey), Tag = opt.Value });
        }

        // An unresolved imported macro reference ("***Name", see ActionTypeHelper.
        // UnresolvedMacroPrefix) matches by its preserved original name: if the user has
        // created a same-named macro since the import, opening the dialog pre-selects it,
        // and saving resolves the reference for good.
        if (tag == "macro")
            selectValue = ActionTypeHelper.StripUnresolvedMacroPrefix(selectValue);

        var match = CbComboValue.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, selectValue, System.StringComparison.OrdinalIgnoreCase));

        // Id-based match failed — for "audiodevice" specifically, that's the expected
        // shape of "device was unplugged and reconnected" (Windows can hand it a new
        // persistent id), not a real mismatch. Fall back to a name match among the
        // devices currently plugged in, same resolution AudioDeviceService.
        // TryResolveDeviceId uses at execution time, so the dialog's preselection agrees
        // with what pressing the key would actually do.
        if (match is null && tag == "audiodevice" && !string.IsNullOrEmpty(selectValue))
        {
            var wanted = AudioDevicePayload.Parse(selectValue);
            if (wanted is not null && wanted.Name.Length > 0)
                match = CbComboValue.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => string.Equals(
                        AudioDevicePayload.Parse((string?)i.Tag)?.Name, wanted.Name,
                        System.StringComparison.OrdinalIgnoreCase));
        }

        // For "macro"/"googlehome"/"audiodevice" (dynamic, live-sourced lists), no match is
        // a real, expected state — an imported Base Camp named-macro reference that didn't
        // resolve to any macro in the user's K2 library (see BaseCampDbImporter.
        // TranslateDefaultAction), a Google Home binding the user has since deleted, or an
        // audio device that's genuinely not connected right now. Defaulting to the first
        // item in the list here would silently bind the key to an unrelated target the
        // moment the user opens and saves the dialog without noticing. Fixed enums
        // (oscmd/media/mouse) keep the old fallback since a mismatch there shouldn't happen.
        bool dynamicList = tag is "macro" or "googlehome" or "audiodevice";
        CbComboValue.SelectedItem = match ?? (dynamicList ? null : (CbComboValue.Items.Count > 0 ? CbComboValue.Items[0] : null));
    }

    private void BtnGhManage_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new GoogleHomeSetupWindow { Owner = this };
        wnd.ShowDialog();

        // Bindings may have been added/renamed/removed: repopulate keeping the current
        // selection (by id) if it still exists.
        string? selectedId = CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag : null;
        PopulateCombo("googlehome", selectedId);
    }

    private string SaveComboSpec()
    {
        string cmd = CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";
        if (_comboPanelTag is not null && CommandNeedsArg(_comboPanelTag, cmd))
        {
            bool listArg = _comboPanelTag == "obs" && ObsListArgCommands.Contains(cmd);
            var arg = (listArg ? CbComboArgList.Text : TxtComboArg.Text)?.Trim() ?? "";
            return arg.Length > 0 ? $"{cmd}~{arg}" : cmd;
        }
        return cmd;
    }
}
