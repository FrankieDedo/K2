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
///   [srv][mic][aud][ptt][ cam][hang]             [ u ][srv ]
///   [ u ][ u ][ u ][ u ][ u  ][ u  ]             [ u ][mic ]
///                                                [ u ][aud ]
///   with more than 6 people in the call:         [ u ][ptt ]
///   [srv][mic][aud][ptt][ cam][hang]             [ u ][cam ]
///   [ ◀ ][you][ u ][ u ][ u  ][ ▶  ]             [ u ][hang]
///
///   The rotated pad keeps the very same two strips — controls on one, roster on the other,
///   in the same order — standing up instead of lying down (only the tiles are turned);
///   the scroll arrows become ▲/▼ and sit at the ends of the roster strip.
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

    /// <summary>The three <see cref="DiscordVoiceRoom"/>/<see cref="DiscordAvatarCache"/>
    /// subscriptions, kept so <see cref="DvpUnhook"/> can take them off again at teardown.</summary>
    private Action? _dvpOnChanged, _dvpOnSpeaking, _dvpOnAvatar;

    /// <summary>Retries the RPC connection while it isn't open — see
    /// <see cref="DvpEnsureHooked"/>'s remarks on why the one-shot attempt at hook time isn't
    /// enough.</summary>
    private DispatcherTimer? _dvpReconnectTimer;

    /// <summary>Polls the foreground window once a second while
    /// <see cref="DiscordStore.VoicePageForegroundOnly"/> is on: shows the voice page only while
    /// the Discord app is in front and hides it again the moment it isn't (see
    /// <see cref="DvpPollForeground"/>). Runs unconditionally — cheap, and a no-op unless the flag
    /// is set and a call is running.</summary>
    private DispatcherTimer? _dvpForegroundTimer;

    /// <summary>Tracks whether the foreground-only flag was on last tick, so turning it OFF
    /// mid-call can bring the page straight back instead of waiting for the next roster change.</summary>
    private bool _dvpForegroundWasEnabled;

    /// <summary>How many times <see cref="DvpReconcileAll"/> has forced a fresh voice-state read
    /// after finding an open pipe with no known channel. Bounded so a pad that has the Discord
    /// dedicated profile but simply isn't in a call doesn't fire a GET every reconcile forever —
    /// once a call turns up <see cref="DiscordVoiceRoom.ChannelId"/> is non-null and the forced
    /// read stops on its own.</summary>
    private int _dvpForcedVoiceReads;
    private const int DvpMaxForcedVoiceReads = 8;

    /// <summary>Process name of Discord's desktop client (no extension — matched against
    /// <see cref="ProfileLaunchWatcher.CurrentForegroundProcessName"/>).</summary>
    private const string DiscordExeName = "Discord";

    /// <summary>Devices whose user pressed the server key to send the page away. Cleared when a
    /// NEW call starts, so dismissing this call's page never mutes the next one.</summary>
    private readonly HashSet<int> _dvpDismissed = new();

    /// <summary>Channel the dismissals above belong to.</summary>
    private string? _dvpLastChannel;

    /// <summary>Per-device one-shot timers that bring the voice page back on their own after the
    /// user left it for a normal profile mid-call — the screensaver-style comeback gated by
    /// <see cref="DiscordStore.VoicePageReturnEnabled"/>/<see cref="DiscordStore.VoicePageReturnSeconds"/>
    /// (set in <c>DiscordProfileConfigWindow</c>). Armed by <see cref="DvpDismiss"/>, cancelled the
    /// moment the page is shown again or the call ends.</summary>
    private readonly Dictionary<int, DispatcherTimer> _dvpReturnTimers = new();

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

        // All three arrive on background threads (RPC reader / download task), so their queued
        // callbacks can still be sitting in the dispatcher queue while the window closes and
        // CleanupDisplayPad disposes the store underneath them — every one of them reads it (the
        // dedicated-profile check). Unhooked there, and each callback re-checks the flag because
        // BeginInvoke items posted just before the unhook still run afterwards (user report
        // 2026-09-03: "ExecuteReader can only be called when the connection is open", repeated
        // for as long as the app took to exit).
        _dvpOnChanged = () => Dispatcher.BeginInvoke(() => { if (!_dpTornDown) DvpOnRoomChanged(); });
        _dvpOnSpeaking = () => Dispatcher.BeginInvoke(() => { if (!_dpTornDown) DvpRepaintAll(); });
        _dvpOnAvatar = () => Dispatcher.BeginInvoke(() => { if (!_dpTornDown) DvpRepaintAll(); });
        DiscordVoiceRoom.Changed += _dvpOnChanged;
        DiscordVoiceRoom.SpeakingChanged += _dvpOnSpeaking;
        DiscordAvatarCache.Downloaded += _dvpOnAvatar;

        // Without this the page would only ever appear on the NEXT join: the RPC connection is
        // opened lazily, and the current channel is read as part of that handshake.
        if (DiscordStore.IsConnected) DiscordBridge.StartLiveVoiceState();

        // That one attempt gives up silently when Discord's desktop client isn't running yet
        // (K2 usually starts before it) — nothing else calls StartLiveVoiceState again on its
        // own, so a call joined afterwards was never seen and the dedicated page stayed closed
        // until the user happened to open the DisplayPad tab, which retries it as a side effect
        // (user report 2026-08-26). Cheap poll: a no-op once the pipe is actually open.
        //
        // Once the pipe IS open it does a second job — DvpReconcileAll — because the event path
        // (DvpOnRoomChanged) only opens the page on the pads that exist WHEN Changed fires: a
        // call already in progress when K2 starts raises its one Changed before the DisplayPads
        // have enumerated, and Discord + K2 launched together can hand back a null channel on
        // the first read. The reconcile replays both (user report 2026-09-06: "non parte, né con
        // chiamata già in corso né aprendo Discord dopo").
        _dvpReconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dvpReconnectTimer.Tick += (_, _) =>
        {
            if (_dpTornDown) return;
            if (DiscordStore.IsConnected && !DiscordBridge.IsRpcOpen) DiscordBridge.StartLiveVoiceState();
            else DvpReconcileAll();
        };
        _dvpReconnectTimer.Start();

        _dvpForegroundTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _dvpForegroundTimer.Tick += (_, _) => DvpPollForeground();
        _dvpForegroundTimer.Start();
    }

    /// <summary>False when the user set the voice page to "show only when selected": no path may
    /// open it on its own then — it comes up only from <see cref="DvpReopen"/> (the Discord row or
    /// a <c>voice page</c> key). See <see cref="DiscordStore.VoicePageActivationOnSelect"/>.</summary>
    private static bool DvpAutoActivateEnabled() => !DiscordStore.VoicePageActivationOnSelect;

    /// <summary>False only when the foreground-only flag is on AND Discord is not the app in
    /// front — the gate every "open the voice page" path checks before opening.</summary>
    private static bool DvpForegroundGateOpen() =>
        !DiscordStore.VoicePageForegroundOnly || DvpDiscordInForeground();

    private static bool DvpDiscordInForeground() =>
        string.Equals(ProfileLaunchWatcher.CurrentForegroundProcessName(), DiscordExeName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// While <see cref="DiscordStore.VoicePageForegroundOnly"/> is on and a call is running, keeps
    /// the voice page visible exactly while the Discord client owns the foreground window: opens it
    /// when Discord comes to the front, hides it (back to the normal profile) when it leaves. A
    /// page the user sent away with the server key (<see cref="_dvpDismissed"/>) stays away — this
    /// only drives the focus-following behaviour, not the explicit dismiss.
    /// </summary>
    private void DvpPollForeground()
    {
        if (_dpTornDown) return;
        // Foreground-follow is a sub-option of the auto behaviour — off entirely in "show only
        // when selected" mode.
        if (!DvpAutoActivateEnabled()) return;
        bool enabled = DiscordStore.VoicePageForegroundOnly;
        bool wasEnabled = _dvpForegroundWasEnabled;
        _dvpForegroundWasEnabled = enabled;

        if (DiscordVoiceRoom.ChannelId is null) return;   // no call → nothing to gate

        // Flag just turned OFF mid-call: undo any hide it caused so the page isn't stuck away
        // until the next roster change.
        if (!enabled)
        {
            if (!wasEnabled) return;
            foreach (int id in _dpDeviceIds.ToList())
            {
                if (!DpHasDedicated(id, "Discord") || _dvpDismissed.Contains(id)) continue;
                if (!_dpDiscordRoom.ContainsKey(id) && !DpEmojiBrowserActive(id)) DvpOpen(id);
            }
            return;
        }

        bool inFront = DvpDiscordInForeground();
        foreach (int id in _dpDeviceIds.ToList())
        {
            if (!DpHasDedicated(id, "Discord") || _dvpDismissed.Contains(id)) continue;

            if (inFront)
            {
                if (!_dpDiscordRoom.ContainsKey(id) && !DpEmojiBrowserActive(id)) DvpOpen(id);
            }
            else if (_dpDiscordRoom.ContainsKey(id))
            {
                DpLog($"[DVP] device {id}: Discord left the foreground — hiding voice page");
                DvpExit(id);   // repaints the real profile; does NOT mark the page dismissed
            }
        }
    }

    /// <summary>
    /// Brings the voice page into agreement with the live call state for every pad that has the
    /// Discord dedicated profile: opens it where a call is running and the gates allow, closes it
    /// where there is no call. <see cref="DvpOnRoomChanged"/> already does this, but only for the
    /// pads that exist WHEN its <c>Changed</c> event fires — so a call already in progress at K2
    /// startup (its one <c>Changed</c> raised before the DisplayPads enumerated) is never replayed,
    /// and neither is the case where Discord handed back a null channel on the first read because
    /// it was still starting up alongside K2. This is the catch-up: cheap, idempotent, and safe to
    /// call on the reconnect timer and after every profile refresh.
    /// </summary>
    private void DvpReconcileAll()
    {
        if (_dpTornDown) return;

        // Pipe open but still no channel: ask the client once more, a bounded number of times,
        // in case the initial GET_SELECTED_VOICE_CHANNEL lost the race with Discord's own startup.
        if (DiscordBridge.IsRpcOpen && DiscordVoiceRoom.ChannelId is null
            && _dvpForcedVoiceReads < DvpMaxForcedVoiceReads
            && _dpDeviceIds.Any(id => DpHasDedicated(id, "Discord")))
        {
            _dvpForcedVoiceReads++;
            DiscordBridge.RefreshVoiceStateNow();
        }

        bool inCall = DiscordVoiceRoom.ChannelId is not null;
        foreach (int id in _dpDeviceIds.ToList())
        {
            if (!DpHasDedicated(id, "Discord")) continue;

            if (inCall)
            {
                if (!DvpAutoActivateEnabled()) continue;
                if (_dvpDismissed.Contains(id) || !DvpForegroundGateOpen()) continue;
                if (!_dpDiscordRoom.ContainsKey(id) && !DpEmojiBrowserActive(id)
                    && !_dpScreensaverShowing.Contains(id))
                    DvpOpen(id);
            }
            else if (_dpDiscordRoom.ContainsKey(id))
            {
                DvpExit(id);
            }
        }
    }

    /// <summary>Takes the voice page off the live model and stops its timers — called from
    /// <c>CleanupDisplayPad</c> BEFORE the store/engines are disposed.</summary>
    private void DvpUnhook()
    {
        if (_dvpOnChanged is not null) DiscordVoiceRoom.Changed -= _dvpOnChanged;
        if (_dvpOnSpeaking is not null) DiscordVoiceRoom.SpeakingChanged -= _dvpOnSpeaking;
        if (_dvpOnAvatar is not null) DiscordAvatarCache.Downloaded -= _dvpOnAvatar;
        _dvpOnChanged = _dvpOnSpeaking = _dvpOnAvatar = null;

        _dvpForegroundTimer?.Stop();
        _dvpForegroundTimer = null;
        DvpCancelAllReturnTimers();
    }

    /// <summary>Channel joined/left (or the roster changed): open, close or repaint.</summary>
    private void DvpOnRoomChanged()
    {
        string? channel = DiscordVoiceRoom.ChannelId;
        if (channel != _dvpLastChannel)
        {
            _dvpLastChannel = channel;
            _dvpDismissed.Clear();
            // Dismissals of the previous call are gone — any pending "bring it back" belongs to
            // that call and must not fire onto this one.
            DvpCancelAllReturnTimers();
        }

        bool inCall = channel is not null;
        foreach (int id in _dpDeviceIds.ToList())
        {
            if (inCall && DvpAutoActivateEnabled() && DpHasDedicated(id, "Discord")
                && !_dvpDismissed.Contains(id) && DvpForegroundGateOpen()) DvpOpen(id);
            else if (!inCall) DvpExit(id);   // DvpExit also drops any pending return timer
        }
    }

    /// <summary>Sends the page away for this call and gives the panel back to the profile — the
    /// way out of the takeover, bound to the server key. The <c>discord</c> action
    /// <c>voice_page</c> brings it back (see <see cref="DvpReopen"/>).</summary>
    private void DvpDismiss(int devId)
    {
        _dvpDismissed.Add(devId);
        DvpExit(devId);
        DvpArmReturnTimer(devId);
    }

    /// <summary>Starts (or restarts) the screensaver-style return countdown for <paramref name="devId"/>.
    /// A no-op unless the feature is on and a call is actually running — there would be nothing to
    /// come back to otherwise.</summary>
    private void DvpArmReturnTimer(int devId)
    {
        DvpCancelReturnTimer(devId);
        if (!DvpAutoActivateEnabled() || !DiscordStore.VoicePageReturnEnabled
            || DiscordVoiceRoom.ChannelId is null) return;
        if (!DpHasDedicated(devId, "Discord")) return;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, DiscordStore.VoicePageReturnSeconds)),
        };
        timer.Tick += (_, _) =>
        {
            DvpCancelReturnTimer(devId);
            // Conditions may have moved on while the clock ran (call ended, page already back,
            // pad gone). DvpReopen re-checks the call/dedicated state; guard the rest here.
            if (!_dvpDismissed.Contains(devId) || !_dpDeviceIds.Contains(devId)) return;
            if (_dpDiscordRoom.ContainsKey(devId) || DpEmojiBrowserActive(devId)) return;
            DpLog($"[DVP] device {devId}: return timer elapsed — reopening voice page");
            DvpReopen(devId);
        };
        _dvpReturnTimers[devId] = timer;
        timer.Start();
    }

    private void DvpCancelReturnTimer(int devId)
    {
        if (_dvpReturnTimers.Remove(devId, out var timer)) timer.Stop();
    }

    private void DvpCancelAllReturnTimers()
    {
        foreach (var timer in _dvpReturnTimers.Values) timer.Stop();
        _dvpReturnTimers.Clear();
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
        if (!DvpForegroundGateOpen()) return;   // foreground-only: the poll reopens it once Discord is in front
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
        // However the page comes back — new call, manual key, or the return timer — a pending
        // countdown has done its job.
        DvpCancelReturnTimer(devId);

        if (_dpDiscordRoom.ContainsKey(devId)) { DvpPaint(devId); return; }

        // The emoji browser owns the panel while it is up — a call arriving underneath must not
        // paint half of each. Everything else that keeps repainting its own tiles is stopped for
        // the same reason it is stopped for the browser/screensaver.
        if (DpEmojiBrowserActive(devId)) return;
        // Same rule for the screensaver: while its image owns the panel nothing live may paint
        // over it, dedicated profiles included (user request 2026-09-06). DpScreensaverWake
        // repaints on the first key press, and DvpRestoreAfterRepaint brings the page back then.
        if (_dpScreensaverShowing.Contains(devId)) return;

        // The voice page is an overlay ON TOP of an ordinary profile — the pad "keeps working
        // normally underneath" until the call ends. That contract breaks when the profile
        // underneath is the OTHER dedicated one (Spotify): its 2×2 cover block keeps painting
        // through the call, and the profile grid keeps resolving to the Spotify slot (user report
        // 2026-09-07, both dedicated set to "show only when selected"). Move the pad off the
        // Spotify slot onto a real profile and stop its cover overlay before taking over; the
        // call ending then repaints that ordinary profile, not Spotify.
        if (_dpStore.GetProfileName(devId, _dpStore.GetCurrentProfile(devId)) == SpotifyProfileName)
        {
            SpotifyCoverService.Stop(devId);
            var ordinary = DpOrdinaryProfiles(devId);
            if (ordinary.Count > 0)
            {
                _dpStore.SetCurrentProfile(devId, ordinary[0]);
                _dpBgPageId[devId] = 0;
                if (_dpBgPageHistory.TryGetValue(devId, out var hist)) hist.Clear();
                DpLog($"[DVP] device {devId}: was on Spotify dedicated slot — moved to ordinary profile {ordinary[0]} before voice-page takeover");
            }
        }

        DpGifAnimator.StopAllForDevice(devId);
        DpFullscreenAnimator.Stop(devId);
        DpLiveTileService.Stop(devId);
        DiscordVoiceKeyService.Stop(devId);
        DpSpotifyCoverKeyService.Stop(devId);

        int rotation = _dpStore.GetRotation(devId);
        _dpDiscordRoom[devId] = new DvpState { Rotation = rotation, V2P = DvpPhysicalForVisual(rotation) };
        DpLog($"[DVP] device {devId}: Discord voice page opened ({DiscordVoiceRoom.GuildName}/{DiscordVoiceRoom.ChannelName})");
        DvpPaint(devId);
        DvpSyncDedicatedUi(devId, active: true);
    }

    /// <summary>Drops the page and repaints the device's real profile page. No-op when it isn't
    /// open, so every caller can call it blindly.</summary>
    private void DvpExit(int devId)
    {
        // DvpDismiss re-arms straight after this call; every other caller (call ended, profile
        // deleted, tab teardown) wants any pending countdown gone.
        DvpCancelReturnTimer(devId);

        // A push-to-talk key still held when the page goes away would otherwise stay pressed
        // system-wide.
        if (_dvpPttHeld) DvpPushToTalk(false);

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

        // Push-to-talk: hold Discord's PTT keybind for exactly as long as the physical key is
        // down. It answers with a green tile while held (the shrink-on-press used everywhere else
        // would say nothing about whether the mic is open right now) and never falls through to
        // the press-only switch below — the key-up edge matters here.
        if (slot == DvpPttSlot)
        {
            string? tile = DvpControlTile("ptt", Loc.Get("dvp_ptt"), highlight: pressed);
            st.Tiles[btnIndex] = tile;
            if (tile is not null) DvpUpload(devId, tile, btnIndex, st.Rotation, shrink: false);
            DvpPushToTalk(pressed);
            return;
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
            // slot 3 (push-to-talk) is fully handled above, on both key edges.
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
    /// shortcut recorded in the Discord profile config popup (<see cref="DiscordStore.WebcamHotkey"/>),
    /// which the user assigns once in <b>Discord ▸ Settings ▸ Keybinds ▸ Toggle Camera</b>.
    /// </para></summary>
    private void DvpToggleWebcam()
    {
        string hotkey = DiscordStore.WebcamHotkey;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            DpLog("[DVP] webcam: no shortcut recorded (Discord profile config)");
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

    /// <summary>State-tracking for <see cref="DvpPushToTalk"/> so a key held when the page closes
    /// can still be released.</summary>
    private bool _dvpPttHeld;

    /// <summary>Serializes the PTT down/up SendInput bursts in submission order — a fast tap must
    /// not let the release run before the press.</summary>
    private Task _dvpPttChain = Task.CompletedTask;

    /// <summary>
    /// Push-to-talk key: holds Discord's PTT keybind down while the physical key is pressed and
    /// releases it on key-up, so the mic is open for exactly that long.
    ///
    /// <para>Discord's RPC has no "transmit now" command — its whole voice surface is settings +
    /// channel selection — so momentary PTT can only be driven by replaying the keybind the user
    /// set in <b>Discord ▸ Settings ▸ Keybinds ▸ Push to Talk</b>, recorded once in the Discord
    /// profile config popup (<see cref="DiscordStore.PushToTalkHotkey"/>). SendInput, not SendKeys:
    /// Discord watches the input stream with a low-level hook — see <see cref="HotkeySender"/>.</para>
    /// </summary>
    private void DvpPushToTalk(bool pressed)
    {
        string hotkey = DiscordStore.PushToTalkHotkey;
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            if (pressed) DpLog("[DVP] push-to-talk: no shortcut recorded (Discord profile config)");
            return;
        }

        _dvpPttHeld = pressed;
        _dvpPttChain = _dvpPttChain.ContinueWith(_ =>
        {
            bool ok = pressed
                ? HotkeySender.TryHoldDown(hotkey, out string error)
                : HotkeySender.TryHoldUp(hotkey, out error);
            if (!ok)
                DpLogAsync($"[DVP] push-to-talk: cannot {(pressed ? "press" : "release")} \"{hotkey}\" — {error}");
        }, TaskScheduler.Default);
    }

    // ================================================================
    // Painting
    // ================================================================

    /// <summary>Visual slot of the push-to-talk key, the one key with its own press feedback.</summary>
    private const int DvpPttSlot = 3;

    /// <summary>
    /// Physical key index for each of this page's 12 slots.
    ///
    /// <para>Unlike the emoji browser (<see cref="EmbPhysicalForVisual"/>), the voice page does
    /// NOT reflow into the rotated grid's reading order: the six controls stay on one physical
    /// row of six and the roster on the other, exactly as they sit on an unrotated pad, so a
    /// vertically mounted panel shows the same two strips — just standing up (user request).
    /// Only the ORDER along each strip is flipped so slot 0 is always the top one, and the tiles
    /// themselves are turned by <c>UploadImage</c>'s rotation argument as everywhere else.</para>
    /// </summary>
    private static int[] DvpPhysicalForVisual(int rotation)
    {
        // Landscape mounts read as the plain 2×6 grid (and 180° flips it), which is what the
        // shared map already does.
        if (rotation is not (90 or 270)) return EmbPhysicalForVisual(rotation);

        const int cols = 6;
        var map = new int[12];
        for (int i = 0; i < map.Length; i++)
        {
            int row = i / cols, col = i % cols;
            // At 90° physical column 0 lands at the top; at 270° it lands at the bottom.
            map[i] = row * cols + (rotation == 90 ? col : cols - 1 - col);
        }
        return map;
    }

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
        // A roster/speaking event mid-screensaver must not tick tiles into the image — the page
        // is put back wholesale on wake (see DvpOpen's screensaver guard).
        if (_dpScreensaverShowing.Contains(devId)) return;

        var all = DiscordVoiceRoom.Participants;
        var tiles = new string?[12];
        var users = new string?[12];

        // ---- control half
        tiles[st.V2P[0]] = DvpServerTile();

        bool deaf = DiscordBridge.Deaf ?? false;
        bool mute = deaf || (DiscordBridge.Mute ?? false);   // deafened implies not transmitting
        tiles[st.V2P[1]] = DvpControlTile(mute ? "mic_off" : "mic_on",
            Loc.Get(mute ? "discord_key_mic_off" : "discord_key_mic_on"));
        tiles[st.V2P[2]] = DvpControlTile(deaf ? "audio_off" : "audio_on",
            Loc.Get(deaf ? "discord_key_audio_off" : "discord_key_audio_on"));
        // Keep the key green for as long as it is physically held: a repaint triggered by any
        // incoming RPC event (speaking start/stop fires constantly while transmitting) would
        // otherwise wipe the highlight mid-hold.
        tiles[st.V2P[3]] = DvpControlTile("ptt", Loc.Get("dvp_ptt"), highlight: _dvpPttHeld);
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
                tiles[phys] = DvpParticipantTile(all[i]);
                users[phys] = all[i].Id;
            }
        }
        else
        {
            var self = all[0];
            var others = all.Skip(1).ToList();
            int last = Math.Max(0, (others.Count - 1) / DvpScrollSlots) * DvpScrollSlots;
            if (st.Offset > last) st.Offset = last;

            // The roster strip runs top-to-bottom on a rotated pad, so the scroll arrows have to
            // point that way too (the tiles are turned with the panel, not with the strip).
            bool tall = st.Rotation is 90 or 270;
            tiles[st.V2P[6]] = DvpNavTile(tall ? IconImageGenerator.NavShape.Up : IconImageGenerator.NavShape.Left);
            tiles[st.V2P[11]] = DvpNavTile(tall ? IconImageGenerator.NavShape.Down : IconImageGenerator.NavShape.Right);

            tiles[st.V2P[7]] = DvpParticipantTile(self);
            users[st.V2P[7]] = self.Id;

            for (int i = 0; i < DvpScrollSlots; i++)
            {
                int index = st.Offset + i;
                if (index >= others.Count) break;
                int phys = st.V2P[8 + i];
                tiles[phys] = DvpParticipantTile(others[index]);
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

    /// <summary>
    /// One participant circle, as a <b>content-addressed</b> file under the shared auto-icon
    /// cache — the file name is a hash of everything the picture depends on (avatar, name,
    /// speaking ring, mute/deaf badge, "you" outline, size), exactly like every other tile on
    /// this page (<see cref="DvpControlTile"/>/<see cref="DvpNavTile"/>) and the whole emoji
    /// browser.
    ///
    /// <para>It used to render over one fixed file per device+slot. That raced the deferred
    /// upload: <see cref="DvpPaint"/> rewrites the tile files synchronously, then queues the
    /// <c>UploadImage</c> calls on <c>_dpUploadChain</c>. When several people leave a busy call
    /// at once, a burst of repaints (each <c>VOICE_STATE_DELETE</c> → <c>Changed</c>, plus
    /// <c>DiscordAvatarCache.Downloaded</c> and <c>SpeakingChanged</c>) overwrote a slot file
    /// before the previous paint's upload had read it — so a lagging upload pushed the wrong
    /// face onto a key (or the same face onto two keys once the paged↔unpaged shift moved which
    /// participant maps to which slot), and the renderer's <c>Save</c> could collide with the
    /// upload's read. Immutable per-content files remove the race and dedupe for free.</para>
    /// </summary>
    private static string? DvpParticipantTile(DiscordVoiceRoom.Participant p)
    {
        // Null while the avatar is still downloading: the tile renders with initials and the
        // Downloaded event repaints it with the real picture a moment later.
        string? avatar = DiscordAvatarCache.TryGet(p.AvatarUrl);
        bool speaking = DiscordVoiceRoom.IsSpeaking(p.Id);

        string key = string.Join("|", avatar ?? "noavatar", p.Id, p.Name,
            speaking ? "spk" : "-", p.Mute ? "m" : "-", p.Deaf ? "d" : "-", p.Self ? "self" : "-",
            DpHidNative.IconSize);
        string dest = DpAutoIconCachePath("dvpface", key);
        if (File.Exists(dest)) return dest;
        return DiscordTileRenderer.TryRenderParticipant(
            avatar, p.Name, speaking, p.Mute, p.Deaf, p.Self, DpHidNative.IconSize, dest) ? dest : null;
    }

    private static string? DvpWaitingTile()
    {
        bool arrow = DiscordStore.VoicePageBackArrow;
        string dest = DpAutoIconCachePath("dvpwaiting", $"{DpHidNative.IconSize}|{arrow}");
        if (File.Exists(dest)) return dest;
        return DiscordTileRenderer.TryRenderWaiting(DpHidNative.IconSize, dest, arrow) ? dest : null;
    }

    /// <summary>Server / group tile — content-addressed for the same reason as
    /// <see cref="DvpParticipantTile"/>.</summary>
    private static string? DvpServerTile()
    {
        // Both flavours below carry (or not) the same badge — see DiscordStore.VoicePageBackArrow.
        bool arrow = DiscordStore.VoicePageBackArrow;

        // A server call: its icon, straight from GET_GUILD.
        if (DiscordVoiceRoom.GuildName.Length > 0)
        {
            string? icon = DiscordAvatarCache.TryGet(DiscordVoiceRoom.GuildIconUrl);
            if (icon is not null)
            {
                string d = DpAutoIconCachePath("dvpserver", $"{icon}|{DpHidNative.IconSize}|{arrow}");
                if (File.Exists(d)) return d;
                return DiscordTileRenderer.TryRenderServer(icon, DpHidNative.IconSize, d, arrow) ? d : null;
            }

            // Icon exists but hasn't downloaded yet: show the "loading" dots until the
            // Downloaded event repaints. A server with no icon at all has nothing coming,
            // so it keeps the blank key.
            return string.IsNullOrEmpty(DiscordVoiceRoom.GuildIconUrl) ? null : DvpWaitingTile();
        }

        // A DM/group call has no server, and Discord reports no picture for the channel either —
        // so the group's image is built from the faces in it, like the client does. The local user
        // is left out: "who am I talking to" is the useful half, and it is their own avatar that
        // would otherwise take a quarter of every group tile.
        var faces = DiscordVoiceRoom.Participants
            .Where(p => !p.Self)
            .Select(p => DiscordAvatarCache.TryGet(p.AvatarUrl))
            .ToList();
        if (faces.Count > 0 && faces.All(f => f is null)) return DvpWaitingTile();

        string key = $"grp|{DpHidNative.IconSize}|{arrow}|{string.Join(",", faces.Select(f => f ?? "-"))}";
        string dest = DpAutoIconCachePath("dvpgroup", key);
        if (File.Exists(dest)) return dest;
        return DiscordTileRenderer.TryRenderGroup(faces, DpHidNative.IconSize, dest, arrow) ? dest : null;
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
