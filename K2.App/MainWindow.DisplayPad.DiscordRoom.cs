using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;
using K2.Core.Services;

namespace K2.App;

/// <summary>
/// MainWindow partial: the DisplayPad's <b>Discord voice page</b>.
///
/// Joining a voice channel turns the whole 2×6 panel into a live view of that call and leaving
/// it gives the panel back to the profile — nothing is stored, no profile has to be authored by
/// hand, exactly like the emoji browser and the screensaver takeover (see
/// <c>MainWindow.DisplayPad.EmojiBrowser.cs</c>, whose structure this file mirrors: the same
/// per-device state dictionary, the same visual→physical rotation mapping, the same
/// press-bounce, and the same "any full repaint drops it" rule).
///
/// <code>
///   rotation 0/180 (2 rows × 6 columns)          rotation 90/270 (6 rows × 2 columns)
///   [srv][mic][aud][ptt][ cam][hang]             [ srv ][ mic ]
///   [ u ][ u ][ u ][ u ][ u  ][ u  ]             [ aud ][ ptt ]
///                                                [cam  ][hang ]
///   with more than 6 people in the call:         [  u  ][  u  ]
///   [srv][mic][aud][ptt][ cam][hang]             [  u  ][  u  ]
///   [ ◀ ][you][ u ][ u ][ u  ][ ▶  ]             [  u  ][  u  ]
/// </code>
///
/// The roster row always starts with the local user (<see cref="DiscordVoiceRoom"/> pins them to
/// the first slot) and the arrows only ever scroll the OTHERS, so "you" never moves under the
/// user's finger. A green ring appears around whoever is transmitting
/// (<c>SPEAKING_START</c>/<c>SPEAKING_STOP</c>) and a circle disappears the moment its owner
/// leaves the channel.
///
/// Everything is keyed by device id and never touches the foreground-only <c>_dpKeys</c>/
/// <c>_currentDpPageId</c>, so a background pad behaves exactly like the visible one.
/// </summary>
public partial class MainWindow
{
    /// <summary>Roster slots when the call fits on one screen, and when it doesn't (two of the
    /// six become the scroll arrows).</summary>
    private const int DvpRosterSlots = 6;
    private const int DvpScrollSlots = 3;

    /// <summary>Open voice pages, keyed by device id. Absent = the device shows its normal page.</summary>
    private readonly Dictionary<int, DvpState> _dpDiscordRoom = new();

    private bool _dvpHooked;

    /// <summary>Retries the RPC connection while it isn't open — see
    /// <see cref="DvpEnsureHooked"/>'s remarks on why the one-shot attempt at hook time isn't
    /// enough.</summary>
    private DispatcherTimer? _dvpReconnectTimer;

    /// <summary>Devices whose user pressed the server key to send the page away. Cleared when a
    /// NEW call starts, so dismissing this call's page never mutes the next one.</summary>
    private readonly HashSet<int> _dvpDismissed = new();

    /// <summary>Channel the dismissals above belong to.</summary>
    private string? _dvpLastChannel;

    private sealed class DvpState
    {
        /// <summary>Rotation and the visual→physical key map captured when the page opened, so
        /// painting and key handling can never disagree (changing the setting repaints, which
        /// drops the page).</summary>
        public required int Rotation;
        public required int[] V2P;
        /// <summary>Index into the "everyone except me" list of the first scrolled circle.</summary>
        public int Offset;
        /// <summary>Tile currently painted on each physical key (null = blank) — the press-bounce
        /// re-uploads from here, like the emoji browser's.</summary>
        public string?[] Tiles = new string?[12];
        /// <summary>Participant id behind each physical key, for the press handler.</summary>
        public string?[] Users = new string?[12];
    }

    /// <summary>True while <paramref name="devId"/>'s panel is owned by the voice page.</summary>
    private bool DpDiscordRoomActive(int devId) => _dpDiscordRoom.ContainsKey(devId);

    // ================================================================
    // Auto open / close
    // ================================================================

    /// <summary>Subscribes to the live voice-room model once, and opens the page straight away
    /// when K2 starts while the user is already in a call. Called from the DisplayPad startup
    /// path; safe to call repeatedly.</summary>
    private void DvpEnsureHooked()
    {
        if (_dvpHooked) return;
        _dvpHooked = true;

        // All three arrive on background threads (RPC reader / download task).
        DiscordVoiceRoom.Changed += () => Dispatcher.BeginInvoke(DvpOnRoomChanged);
        DiscordVoiceRoom.SpeakingChanged += () => Dispatcher.BeginInvoke(DvpRepaintAll);
        DiscordAvatarCache.Downloaded += () => Dispatcher.BeginInvoke(DvpRepaintAll);

        // Without this the page would only ever appear on the NEXT join: the RPC connection is
        // opened lazily, and the current channel is read as part of that handshake.
        if (DiscordStore.IsConnected) DiscordBridge.StartLiveVoiceState();

        // That one attempt gives up silently when Discord's desktop client isn't running yet
        // (K2 usually starts before it) — nothing else calls StartLiveVoiceState again on its
        // own, so a call joined afterwards was never seen and the dedicated page stayed closed
        // until the user happened to open the DisplayPad tab, which retries it as a side effect
        // (user report 2026-08-26). Cheap poll: a no-op once the pipe is actually open.
        _dvpReconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _dvpReconnectTimer.Tick += (_, _) =>
        {
            if (DiscordStore.IsConnected && !DiscordBridge.IsRpcOpen) DiscordBridge.StartLiveVoiceState();
        };
        _dvpReconnectTimer.Start();
    }

    /// <summary>Channel joined/left (or the roster changed): open, close or repaint.</summary>
    private void DvpOnRoomChanged()
    {
        string? channel = DiscordVoiceRoom.ChannelId;
        if (channel != _dvpLastChannel) { _dvpLastChannel = channel; _dvpDismissed.Clear(); }

        bool inCall = channel is not null;
        foreach (int id in _dpDeviceIds.ToList())
        {
            if (inCall && DpHasDedicated(id, "Discord") && !_dvpDismissed.Contains(id)) DvpOpen(id);
            else if (!inCall) DvpExit(id);
        }
    }

    /// <summary>Sends the page away for this call and gives the panel back to the profile — the
    /// way out of the takeover, bound to the server key. The <c>discord</c> action
    /// <c>voice_page</c> brings it back (see <see cref="DvpReopen"/>).</summary>
    private void DvpDismiss(int devId)
    {
        _dvpDismissed.Add(devId);
        DvpExit(devId);
    }

    /// <summary>Manual re-entry from a key bound to <c>discord ▸ voice page</c>. Does nothing
    /// when there is no call to show — there would be nothing to paint.</summary>
    private void DvpReopen(int devId)
    {
        // Per-device: the takeover only ever arms on a pad that HAS the Discord dedicated profile.
        if (!DpHasDedicated(devId, "Discord")) return;

        if (DiscordVoiceRoom.ChannelId is null)
        {
            DpLog($"[DVP] device {devId}: voice page requested but no call is active");
            // The RPC connection may simply not be up yet; opening it makes the page appear on
            // its own as soon as the channel is known.
            DiscordBridge.StartLiveVoiceState();
            return;
        }
        _dvpDismissed.Remove(devId);
        DvpOpen(devId);
    }

    /// <summary>True for a key bound to the <c>discord ▸ voice page</c> command.</summary>
    private static bool DvpIsVoicePageAction(string? actionType, string? actionValue) =>
        string.Equals(actionType, "discord", StringComparison.OrdinalIgnoreCase)
        && (actionValue ?? "").Split('~')[0].Equals("voice_page", StringComparison.OrdinalIgnoreCase);

    /// <summary>Puts the page back after an unrelated full repaint has taken the panel (see
    /// <c>DpRequestRepaint</c>, which drops it before repainting).</summary>
    private void DvpRestoreAfterRepaint(int devId)
    {
        if (DiscordVoiceRoom.ChannelId is null || !DpHasDedicated(devId, "Discord")) return;
        if (_dvpDismissed.Contains(devId) || !_dpDeviceIds.Contains(devId)) return;
        DvpOpen(devId);
    }

    /// <summary>Keeps the "Dedicated profiles" list in step with what actually owns the panel,
    /// for the VISIBLE tab only (a background pad has no selection of its own on screen). See
    /// MainWindow.DisplayPad.Dedicated.cs.</summary>
    private void DvpSyncDedicatedUi(int devId, bool active)
    {
        if (DpSelectedDeviceId() != devId) return;
        if (!active) { DpSelectProfileSlot(_dpStore.GetCurrentProfile(devId)); return; }

        _dpSuppressProfile = true;
        try { LstDpProfile.SelectedItem = null; }
        finally { _dpSuppressProfile = false; }
        DpSelectDedicated("Discord");
    }

    private void DvpRepaintAll()
    {
        foreach (int id in _dpDiscordRoom.Keys.ToList()) DvpPaint(id);
    }

    // ================================================================
    // Open / close
    // ================================================================

    /// <summary>Takes the panel over. Re-entrant: an already-open page is only repainted, so a
    /// roster change never resets the scroll position.</summary>
    private void DvpOpen(int devId)
    {
        if (_dpDiscordRoom.ContainsKey(devId)) { DvpPaint(devId); return; }

        // The emoji browser owns the panel while it is up — a call arriving underneath must not
        // paint half of each. Everything else that keeps repainting its own tiles is stopped for
        // the same reason it is stopped for the browser/screensaver.
        if (DpEmojiBrowserActive(devId)) return;
        DpGifAnimator.StopAllForDevice(devId);
        DpFullscreenAnimator.Stop(devId);
        DpLiveTileService.Stop(devId);
        DiscordVoiceKeyService.Stop(devId);

        int rotation = _dpStore.GetRotation(devId);
        _dpDiscordRoom[devId] = new DvpState { Rotation = rotation, V2P = EmbPhysicalForVisual(rotation) };
        DpLog($"[DVP] device {devId}: Discord voice page opened ({DiscordVoiceRoom.GuildName}/{DiscordVoiceRoom.ChannelName})");
        DvpPaint(devId);
        DvpSyncDedicatedUi(devId, active: true);
    }

    /// <summary>Drops the page and repaints the device's real profile page. No-op when it isn't
    /// open, so every caller can call it blindly.</summary>
    private void DvpExit(int devId)
    {
        if (!_dpDiscordRoom.Remove(devId)) return;
        DpLog($"[DVP] device {devId}: Discord voice page closed — restoring page icons");
        DvpSyncDedicatedUi(devId, active: false);
        DpRequestRepaint(devId);
    }

    /// <summary>Forgets the page WITHOUT repainting — for the call sites that are themselves
    /// about to repaint the device (profile switch, page navigation, tab change), same split as
    /// <see cref="DpEmojiBrowserAbandon"/>.</summary>
    private void DvpAbandon(int devId)
    {
        if (_dpDiscordRoom.Remove(devId))
            DpLog($"[DVP] device {devId}: Discord voice page dropped (panel repainted elsewhere)");
    }

    // ================================================================
    // Key handling
    // ================================================================

    /// <summary>
    /// Handles one physical key while the voice page owns <paramref name="devId"/>'s panel.
    /// Called from <c>OnDpKey</c> BEFORE the normal dispatch, so no stored binding of the page
    /// underneath can fire behind the overlay.
    /// </summary>
    private void DvpKey(int devId, int btnIndex, bool pressed)
    {
        if (!_dpDiscordRoom.TryGetValue(devId, out var st)) return;

        int slot = Array.IndexOf(st.V2P, btnIndex);
        if (slot < 0) return;   // not one of ours (remapped pad)

        // Push-to-talk answers with a green tile while it is held and goes back to black on
        // release (user request) — the shrink-on-press used everywhere else would say nothing
        // about whether the mic is open right now.
        if (slot == DvpPttSlot)
        {
            string? tile = DvpControlTile("ptt", Loc.Get("dvp_ptt"), highlight: pressed);
            st.Tiles[btnIndex] = tile;
            if (tile is not null) DvpUpload(devId, tile, btnIndex, st.Rotation, shrink: false);
        }
        // Same shrink-on-press feedback as the emoji browser (see DpEmojiBrowserKey).
        else if (st.Tiles[btnIndex] is string tile && File.Exists(tile))
        {
            DvpUpload(devId, tile, btnIndex, st.Rotation, shrink: pressed);
        }

        if (!pressed) return;

        // A voice command blocks on the RPC pipe for up to a few seconds; the UI thread must
        // stay free (the panel's own press-bounce is queued above and would stutter).
        void Rpc(Action<Action<string>> command) => Task.Run(() => command(DpLogAsync));

        switch (slot)
        {
            case 0:   // server tile: also the way out — back to the profile, call still running
                DpLog($"[DVP] device {devId}: dismissed from {DiscordVoiceRoom.GuildName}/{DiscordVoiceRoom.ChannelName}");
                DvpDismiss(devId);
                return;
            case 1: Rpc(log => DiscordBridge.ToggleMute(log)); return;
            case 2: Rpc(log => DiscordBridge.ToggleDeaf(log)); return;
            case 3: Rpc(log => DiscordBridge.ToggleInputMode(log)); return;
            case 4: DvpToggleWebcam(); return;
            case 5:
                Rpc(log => DiscordBridge.LeaveVoiceChannel(log));
                return;   // the page closes itself on the VOICE_CHANNEL_SELECT that follows
        }

        // Roster half. With the arrows up, they sit on the first and last slot of the row.
        var others = DiscordVoiceRoom.Participants.Where(x => !x.Self).ToList();
        bool paged = DiscordVoiceRoom.Participants.Count > DvpRosterSlots;
        if (paged && (slot == 6 || slot == 11))
        {
            int last = Math.Max(0, (others.Count - 1) / DvpScrollSlots) * DvpScrollSlots;
            int wanted = st.Offset + (slot == 11 ? DvpScrollSlots : -DvpScrollSlots);
            int clamped = Math.Clamp(wanted, 0, last);
            if (clamped == st.Offset) return;
            st.Offset = clamped;
            DvpPaint(devId);
            return;
        }

        if (st.Users[btnIndex] is not string userId) return;
        // Own circle: mute yourself. Someone else's: mute them locally, which is what the same
        // click does in the Discord client.
        if (userId == DiscordBridge.SelfUserId) Rpc(log => DiscordBridge.ToggleMute(log));
        else Rpc(log => DiscordBridge.ToggleUserMute(userId, log));
    }

    /// <summary>Camera key: turns the webcam on/off in the current call.
    ///
    /// <para>
    /// Discord's RPC exposes no video command (its whole surface is voice settings + channel
    /// selection), and unlike mute/deafen the camera has no default keyboard shortcut either, so
    /// there is no way to drive it that works out of the box. The key therefore replays the
    /// shortcut recorded in Settings ▸ Discord (<see cref="DiscordStore.WebcamHotkey"/>), which the
    /// user assigns once in <b>Discord ▸ Settings ▸ Keybinds ▸ Toggle Camera</b>.
    /// </para></summary>
    private void DvpToggleWebcam()
    {
        string hotkey = DiscordStore.WebcamHotkey;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            DpLog("[DVP] webcam: no shortcut recorded (Settings > Discord)");
            return;
        }
        // SendInput, not SendKeys: Discord's Keybinds are watched with a low-level keyboard hook,
        // which never sees SendKeys' journal-injected keystrokes — see HotkeySender. Off the UI
        // thread because the sequence holds the modifiers down for a few ms.
        Task.Run(() =>
        {
            bool ok = HotkeySender.TrySend(hotkey, out string error);
            DpLogAsync(ok
                ? $"[DVP] webcam: sent {hotkey}"
                : $"[DVP] webcam: cannot send \"{hotkey}\" — {error}");
        });
    }

    // ================================================================
    // Painting
    // ================================================================

    /// <summary>Visual slot of the push-to-talk key, the one key with its own press feedback.</summary>
    private const int DvpPttSlot = 3;

    /// <summary>One key's picture onto the device, chained onto the same per-device upload chain as
    /// every other icon write so it can never race a repaint.</summary>
    private void DvpUpload(int devId, string tile, int btnIndex, int rotation, bool shrink)
    {
        var previous = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[devId] = previous.ContinueWith(
            _ => _dpClient.UploadImage(devId, tile, btnIndex, rotation, shrink), TaskScheduler.Default);
    }

    /// <summary>Renders the 12 tiles from the current room state and uploads them, chained onto
    /// the same per-device upload chain as every other icon write.</summary>
    private void DvpPaint(int devId)
    {
        if (!_dpDiscordRoom.TryGetValue(devId, out var st)) return;

        var all = DiscordVoiceRoom.Participants;
        var tiles = new string?[12];
        var users = new string?[12];

        // ---- control half
        tiles[st.V2P[0]] = DvpServerTile(devId);

        bool deaf = DiscordBridge.Deaf ?? false;
        bool mute = deaf || (DiscordBridge.Mute ?? false);   // deafened implies not transmitting
        tiles[st.V2P[1]] = DvpControlTile(mute ? "mic_off" : "mic_on",
            Loc.Get(mute ? "discord_key_mic_off" : "discord_key_mic_on"));
        tiles[st.V2P[2]] = DvpControlTile(deaf ? "audio_off" : "audio_on",
            Loc.Get(deaf ? "discord_key_audio_off" : "discord_key_audio_on"));
        tiles[st.V2P[3]] = DvpControlTile("ptt", Loc.Get("dvp_ptt"));
        tiles[st.V2P[4]] = DvpControlTile("webcam", Loc.Get("dvp_webcam"));
        tiles[st.V2P[5]] = DvpControlTile("disconnect", Loc.Get("dvp_disconnect"));

        // ---- roster half
        bool paged = all.Count > DvpRosterSlots;
        if (!paged)
        {
            for (int i = 0; i < DvpRosterSlots; i++)
            {
                int phys = st.V2P[6 + i];
                if (i >= all.Count) continue;
                tiles[phys] = DvpParticipantTile(devId, 6 + i, all[i]);
                users[phys] = all[i].Id;
            }
        }
        else
        {
            var self = all[0];
            var others = all.Skip(1).ToList();
            int last = Math.Max(0, (others.Count - 1) / DvpScrollSlots) * DvpScrollSlots;
            if (st.Offset > last) st.Offset = last;

            tiles[st.V2P[6]] = DvpNavTile(IconImageGenerator.NavShape.Left);
            tiles[st.V2P[11]] = DvpNavTile(IconImageGenerator.NavShape.Right);

            tiles[st.V2P[7]] = DvpParticipantTile(devId, 7, self);
            users[st.V2P[7]] = self.Id;

            for (int i = 0; i < DvpScrollSlots; i++)
            {
                int index = st.Offset + i;
                if (index >= others.Count) break;
                int phys = st.V2P[8 + i];
                tiles[phys] = DvpParticipantTile(devId, 8 + i, others[index]);
                users[phys] = others[index].Id;
            }
        }

        st.Tiles = tiles;
        st.Users = users;

        int rotation = st.Rotation;
        var previous = _dpUploadChain.TryGetValue(devId, out var p) ? p : Task.CompletedTask;
        _dpUploadChain[devId] = previous.ContinueWith(_ =>
        {
            for (int i = 0; i < 12; i++)
            {
                if (tiles[i] is string path) _dpClient.UploadImage(devId, path, i, rotation);
                else DpClearKeyOnDevice(devId, i);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Directory the page's per-slot tiles are re-rendered into. One file per device+slot
    /// (not per user): the picture changes on every ring/mute flip, so a content-addressed cache
    /// would only pile up dead files.</summary>
    private static string DvpTileDir => Path.Combine(Path.GetTempPath(), "K2.DiscordVoicePage");

    private static string DvpTilePath(int devId, int slot) =>
        Path.Combine(DvpTileDir, $"dev{devId}_slot{slot}.png");

    private static string? DvpParticipantTile(int devId, int slot, DiscordVoiceRoom.Participant p)
    {
        // Null while the avatar is still downloading: the tile renders with initials and the
        // Downloaded event repaints it with the real picture a moment later.
        string? avatar = DiscordAvatarCache.TryGet(p.AvatarUrl);
        string dest = DvpTilePath(devId, slot);
        return DiscordTileRenderer.TryRenderParticipant(
            avatar, p.Name, DiscordVoiceRoom.IsSpeaking(p.Id), p.Mute, p.Deaf, p.Self,
            DpHidNative.IconSize, dest) ? dest : null;
    }

    private static string? DvpServerTile(int devId)
    {
        string dest = DvpTilePath(devId, 0);

        // A server call: its icon, straight from GET_GUILD.
        if (DiscordVoiceRoom.GuildName.Length > 0)
        {
            string? icon = DiscordAvatarCache.TryGet(DiscordVoiceRoom.GuildIconUrl);
            return DiscordTileRenderer.TryRenderServer(icon, DpHidNative.IconSize, dest) ? dest : null;
        }

        // A DM/group call has no server, and Discord reports no picture for the channel either —
        // so the group's image is built from the faces in it, like the client does. The local user
        // is left out: "who am I talking to" is the useful half, and it is their own avatar that
        // would otherwise take a quarter of every group tile.
        var faces = DiscordVoiceRoom.Participants
            .Where(p => !p.Self)
            .Select(p => DiscordAvatarCache.TryGet(p.AvatarUrl))
            .ToList();
        return DiscordTileRenderer.TryRenderGroup(faces, DpHidNative.IconSize, dest) ? dest : null;
    }

    /// <summary>Control tile: the artwork shipped for this page (see
    /// <see cref="DiscordTileRenderer.TryRenderControl"/>) rather than a generated glyph. Cached
    /// per icon+background, since neither depends on anything live.</summary>
    /// <param name="highlight">Green background — the push-to-talk key while held.</param>
    private static string? DvpControlTile(string iconName, string caption, bool highlight = false)
    {
        string dest = DpAutoIconCachePath("dvpicon", $"{iconName}|{caption}|{(highlight ? "green" : "black")}");
        if (File.Exists(dest)) return dest;
        return DiscordTileRenderer.TryRenderControl(iconName, caption, highlight, DpHidNative.IconSize, dest)
            ? dest : null;
    }

    private static string? DvpNavTile(IconImageGenerator.NavShape shape)
    {
        string dest = DpAutoIconCachePath("dvpnav", $"{AppSettings.AccentTheme}|{AppSettings.IconColorTheme}|{shape}");
        if (File.Exists(dest)) return dest;
        return IconImageGenerator.TryGenerateNavIcon(shape, "", DpHidNative.IconSize, dest) ? dest : null;
    }
}
