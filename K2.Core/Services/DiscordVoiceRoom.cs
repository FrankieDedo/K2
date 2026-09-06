using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Live model of the voice channel the user is currently in: which server it belongs to, who
/// else is in it, who is muted/deafened and who is talking RIGHT NOW.
///
/// <para>
/// This is the data behind the DisplayPad's Discord voice page (see
/// <c>MainWindow.DisplayPad.DiscordRoom.cs</c>) — server icon, the two state keys, and the
/// participant circles with their speaking rings. <see cref="DiscordBridge"/> stays the owner
/// of the RPC connection; this class only rides on it:
/// <list type="bullet">
/// <item>the bridge hands every RPC event to <see cref="OnRpcEvent"/>, and every voice-channel
/// change to <see cref="OnChannelChanged"/>;</item>
/// <item>everything this class has to ASK Discord (the roster, the guild) runs on its own
/// single worker (<see cref="Post"/>). It can never run inline on the bridge's reader thread:
/// <c>DiscordIpc.Send</c> waits for a reply that only that same thread can deliver, so an inline
/// call would deadlock the whole RPC connection.</item>
/// </list>
/// </para>
///
/// <para>
/// Two separate change events on purpose. <see cref="Changed"/> means the roster/channel itself
/// changed — the page has to re-render every tile, and it also has to (re)download avatars.
/// <see cref="SpeakingChanged"/> fires several times a second while people talk and only ever
/// flips the ring around an already-rendered circle, so the page can repaint just that key.
/// </para>
/// </summary>
public static class DiscordVoiceRoom
{
    /// <summary>One member of the current voice channel.</summary>
    /// <param name="Id">Discord user id — also the speaking-state key and the avatar cache key.</param>
    /// <param name="Name">Server nickname when set, otherwise the display/user name.</param>
    /// <param name="AvatarUrl">CDN url of the user's avatar (their default one when unset).</param>
    /// <param name="Self">True for the local user, who is always pinned to the first slot.</param>
    public readonly record struct Participant(string Id, string Name, string AvatarUrl, bool Self, bool Mute, bool Deaf);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private static readonly object _gate = new();
    private static Participant[] _participants = Array.Empty<Participant>();
    private static readonly HashSet<string> _speaking = new();
    private static string? _subscribedChannel;
    private static Task _worker = Task.CompletedTask;

    /// <summary>The channel we were most recently ASKED to show (by <see cref="OnChannelChanged"/>).
    /// Unlike <see cref="ChannelId"/>, which is only committed at the tail of <see cref="Refresh"/>
    /// a whole RPC round trip later, this updates synchronously on the reader thread — so it is the
    /// only reliable "is this pass still wanted?" check while a bot bounces the user across servers
    /// faster than a Refresh completes.</summary>
    private static volatile string? _targetChannel;

    /// <summary>1 while an event-driven <see cref="Refresh"/> is already waiting on the worker.
    /// A queued pass re-reads the WHOLE channel when it runs, so anything queued behind it would
    /// only repeat the same two RPC round trips — and raise <see cref="Changed"/> (a full
    /// DisplayPad repaint) again — for the very same roster. See <see cref="PostRefresh"/>.</summary>
    private static int _refreshQueued;

    /// <summary><see cref="Environment.TickCount64"/> of the last completed refresh, for the
    /// minimum spacing enforced in <see cref="PostRefresh"/>.</summary>
    private static long _lastRefreshTicks;

    /// <summary>Shortest gap between two event-driven refreshes. A busy call (ten people, mute /
    /// volume / speaking traffic) delivers VOICE_STATE_UPDATE far faster than a GET_CHANNEL +
    /// GET_GUILD round trip, and every pass repaints twelve keys over USB — user report
    /// 2026-09-03: hundreds of identical "room:" lines per second and the whole app crawling.</summary>
    private static readonly TimeSpan MinRefreshGap = TimeSpan.FromMilliseconds(400);

    /// <summary>Channel + roster of the last committed refresh, so an unchanged re-read stays
    /// silent instead of logging and repainting again (see <see cref="Signature"/>).</summary>
    private static string? _lastSignature;

    /// <summary>How many times the current join has re-read the channel while waiting for the
    /// local user's own voice state to show up in it — see the tail of <see cref="Refresh"/>.
    /// Reset on every channel change.</summary>
    private static int _selfWaitRetries;

    /// <summary>Id of the voice channel this roster describes, or null when not in a call.</summary>
    public static string? ChannelId { get; private set; }

    /// <summary>Name of that voice channel ("General", …), empty when unknown.</summary>
    public static string ChannelName { get; private set; } = "";

    /// <summary>Name of the server the channel belongs to — empty for a DM/group call, which
    /// has no guild at all.</summary>
    public static string GuildName { get; private set; } = "";

    /// <summary>Server icon url as Discord reports it (<c>icon_url</c> of GET_GUILD), or null
    /// when the server has no icon (or the call isn't in a server).</summary>
    public static string? GuildIconUrl { get; private set; }

    /// <summary>Current members, local user first. Replaced wholesale on every refresh, so a
    /// caller can hold on to the reference while it paints.</summary>
    public static IReadOnlyList<Participant> Participants
    {
        get { lock (_gate) return _participants; }
    }

    /// <summary>True while <paramref name="userId"/> is transmitting.</summary>
    public static bool IsSpeaking(string userId)
    {
        lock (_gate) return _speaking.Contains(userId);
    }

    /// <summary>Channel / roster / mute-state change — re-render everything.</summary>
    public static event Action? Changed;

    /// <summary>Somebody started or stopped talking — only the rings changed.</summary>
    public static event Action? SpeakingChanged;

    // ---------------------------------------------------------------- input from the bridge

    /// <summary>The client joined (or left, with null) a voice channel.
    ///
    /// <para>De-dup is against <see cref="_targetChannel"/>, NOT <see cref="ChannelId"/>: the
    /// latter only catches up at the end of <see cref="Refresh"/>, so when a bot moves the user
    /// server→server faster than a Refresh round trip the incoming id can still equal the
    /// old committed <c>ChannelId</c>. Dropping it there left the page subscribed to — and
    /// forever re-reading — a channel the user had already left: empty roster (not even "you"),
    /// and no recovery when others joined later, because their VOICE_STATE events arrived on a
    /// channel we were no longer subscribed to (user report).</para></summary>
    internal static void OnChannelChanged(string? channelId)
    {
        if (channelId == _targetChannel) return;
        _targetChannel = channelId;
        if (channelId is null) { Clear(); Raise(Changed); return; }
        _selfWaitRetries = 0;
        Post(ipc => Refresh(ipc, channelId));
    }

    /// <summary>Every RPC event the bridge receives, so the per-channel subscriptions this class
    /// makes land here. Runs on the RPC reader thread — no blocking calls.</summary>
    internal static void OnRpcEvent(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "SPEAKING_START":
            case "SPEAKING_STOP":
            {
                string? user = Str(data, "user_id");
                if (user is null) return;
                bool changed;
                lock (_gate) changed = evt == "SPEAKING_START" ? _speaking.Add(user) : _speaking.Remove(user);
                if (changed) Raise(SpeakingChanged);
                return;
            }
            case "VOICE_STATE_DELETE":
                // Drop the leaver from the roster NOW, straight from the payload, instead of
                // waiting for the queued GET_CHANNEL re-read below. GET_CHANNEL keeps listing a
                // member for a second or two after they disconnect (the same server-side lag the
                // self-wait loop in Refresh works around) and when several people leave a busy
                // call at once the last DELETE is often the last event we get — so a stale
                // Refresh would leave their circles, and the scroll arrows (gated on
                // Participants.Count > 6 by the voice page), stuck on screen. The queued Refresh
                // still runs and reconciles order/nicknames.
                {
                    string? gone = Str(data, "user_id");
                    if (gone is null && data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("user", out var goneUser))
                        gone = Str(goneUser, "id");
                    if (gone is not null)
                    {
                        bool removed;
                        lock (_gate)
                        {
                            int before = _participants.Length;
                            _participants = _participants.Where(p => p.Id != gone).ToArray();
                            removed = _participants.Length != before;
                            _speaking.Remove(gone);
                        }
                        if (removed) Raise(Changed);
                    }
                }
                goto case "VOICE_STATE_UPDATE";
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
                // The event payload is one member's state; re-reading the whole channel is a
                // single cheap RPC call and keeps ordering/nicknames consistent with a join.
                //
                // Use _targetChannel (the channel we're currently meant to show) as the primary
                // source: ChannelId is only committed at the END of a Refresh, so on a fresh join
                // the local user's OWN VOICE_STATE_CREATE can land while it is still null, and
                // right after a fast server switch it can still hold the OLD channel. Both cases
                // would otherwise re-read the wrong channel (or drop the event). The bridge value
                // is the last-resort fallback for the instant before OnChannelChanged has run.
                if ((_targetChannel ?? DiscordBridge.VoiceChannelId ?? ChannelId) is string id)
                    PostRefresh(id);
                return;
        }
    }

    /// <summary>The RPC connection dropped — nothing is known any more.</summary>
    internal static void Reset()
    {
        Clear();
        Raise(Changed);
    }

    // ---------------------------------------------------------------- worker

    /// <summary>Queues one RPC round trip on the room's own serialized worker. Never call
    /// <c>DiscordIpc.Send</c> straight from <see cref="OnRpcEvent"/> — see the class remarks.</summary>
    /// <param name="started">Runs on the worker the moment this item reaches the head of the
    /// queue, whether or not an RPC handle can be obtained — <see cref="PostRefresh"/> uses it to
    /// clear its "already queued" flag without ever latching it on a failed connection.</param>
    private static void Post(Action<DiscordIpc> work, Action? started = null)
    {
        lock (_gate)
            _worker = _worker.ContinueWith(_ =>
            {
                try
                {
                    started?.Invoke();
                    // Silent sink: this runs once per queued item, so a Discord that is simply
                    // not connected (or a token refresh that keeps failing) would repeat the
                    // same "[EXEC] discord: …" line for every roster event of the session.
                    var ipc = DiscordBridge.RoomIpc(_ => { });
                    if (ipc is not null) work(ipc);
                }
                catch (Exception ex) { DiscordBridge.Log?.Invoke($"[Discord] room error: {ex.Message}"); }
            }, TaskScheduler.Default);
    }

    /// <summary>Queues an event-driven <see cref="Refresh"/>, collapsing bursts.
    ///
    /// <para>Discord sends a VOICE_STATE_UPDATE for every mute/deafen/volume/suppress change of
    /// every member, so a busy call produces them far faster than one refresh (GET_CHANNEL +
    /// GET_GUILD) completes — and each refresh raises <see cref="Changed"/>, which repaints all
    /// twelve keys of every pad over USB. Queueing one pass per event turned that into a
    /// self-sustaining flood. Two guards: at most one pass may be waiting (the one that runs
    /// re-reads the whole channel anyway, so a second would be redundant), and passes are spaced
    /// by <see cref="MinRefreshGap"/> — the wait happens BEFORE the flag is cleared, so every
    /// event arriving meanwhile folds into the pass that is about to run.</para></summary>
    private static void PostRefresh(string channelId)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;

        Post(ipc => Refresh(ipc, channelId), started: () =>
        {
            long wait = (long)MinRefreshGap.TotalMilliseconds - (Environment.TickCount64 - _lastRefreshTicks);
            if (wait > 0) Thread.Sleep((int)Math.Min(wait, (long)MinRefreshGap.TotalMilliseconds));
            Interlocked.Exchange(ref _refreshQueued, 0);
            _lastRefreshTicks = Environment.TickCount64;
        });
    }

    /// <summary>Reads the channel (roster + guild) and re-arms the per-channel subscriptions.</summary>
    private static void Refresh(DiscordIpc ipc, string channelId)
    {
        // A newer channel change was queued behind this one while it waited its turn on the
        // worker. Its own Refresh will do the work against the right channel, so anything this
        // stale pass does — resubscribe, commit the roster, keep retrying for "you" — would just
        // be undone or, worse, latch the channel the user already left.
        if (_targetChannel != channelId) return;

        Resubscribe(ipc, channelId);

        var ch = ipc.Send("GET_CHANNEL", new { channel_id = channelId }, Timeout, out var error);
        if (ch is not { ValueKind: JsonValueKind.Object } channel)
        {
            DiscordBridge.Log?.Invoke($"[Discord] room: GET_CHANNEL failed ({error ?? "no data"})");
            return;
        }

        string? self = DiscordBridge.SelfUserId;
        var list = new List<Participant>();
        if (channel.TryGetProperty("voice_states", out var states) && states.ValueKind == JsonValueKind.Array)
            foreach (var st in states.EnumerateArray())
            {
                if (!st.TryGetProperty("user", out var user) || Str(user, "id") is not string uid) continue;
                string name = Str(st, "nick") ?? Str(user, "global_name") ?? Str(user, "username") ?? uid;
                bool mute = false, deaf = false;
                if (st.TryGetProperty("voice_state", out var vs))
                {
                    mute = Flag(vs, "mute") || Flag(vs, "self_mute");
                    deaf = Flag(vs, "deaf") || Flag(vs, "self_deaf");
                }
                list.Add(new Participant(uid, name, AvatarUrl(uid, Str(user, "avatar")), uid == self, mute, deaf));
            }

        // The local user is pinned to the first slot: on a rotated/scrolled roster their own
        // circle must never move around under their finger.
        var ordered = list.OrderByDescending(p => p.Self).ToArray();

        string channelName = Str(channel, "name") ?? "";
        string? guildId = Str(channel, "guild_id");
        string guildName = "";
        string? guildIcon = null;
        if (guildId is not null)
        {
            var g = ipc.Send("GET_GUILD", new { guild_id = guildId }, Timeout, out _);
            if (g is { ValueKind: JsonValueKind.Object } guild)
            {
                guildName = Str(guild, "name") ?? "";
                guildIcon = Str(guild, "icon_url");
            }
        }

        // GET_CHANNEL + GET_GUILD above can each block for seconds; bail if the user has been
        // moved on to another channel in the meantime rather than committing a stale roster.
        if (_targetChannel != channelId) return;

        lock (_gate)
        {
            _participants = ordered;
            // Speaking flags of people who left would otherwise stay latched forever (no
            // SPEAKING_STOP is delivered for a member that disconnects mid-word).
            _speaking.RemoveWhere(u => !ordered.Any(p => p.Id == u));
        }
        ChannelId = channelId;
        ChannelName = channelName;
        GuildName = guildName;
        GuildIconUrl = guildIcon;

        // Most refreshes are triggered by somebody else's state change and come back with exactly
        // the roster we already show. Raising Changed for those repaints every key of every pad
        // for nothing (and logs a line each time) — the second half of the flood in the user
        // report above.
        string signature = Signature(channelId, guildName, channelName, ordered);
        bool unchanged = signature == _lastSignature;
        _lastSignature = signature;

        // No log line for a successful refresh on purpose: it fires on every roster/mute change
        // of every member of the call and said nothing a bug report ever needed. Only failures
        // below/above are worth a line.
        if (!unchanged) Raise(Changed);

        // A brand-new join often returns before Discord has added the local user's OWN voice
        // state to the channel: GET_CHANNEL then lists everyone but you, so the roster shows no
        // "me" tile until the next unrelated change (user report — "on the first join my face
        // isn't shown"). Re-read a few times, backing off, until you turn up.
        if (self is not null && !ordered.Any(p => p.Self) && _selfWaitRetries < 5)
        {
            int attempt = ++_selfWaitRetries;
            _ = Task.Delay(TimeSpan.FromMilliseconds(300 * attempt)).ContinueWith(_ =>
            {
                if (_targetChannel == channelId && ChannelId == channelId && !Participants.Any(p => p.Self))
                    Post(ipc2 => Refresh(ipc2, channelId));
            }, TaskScheduler.Default);
        }
        else if (ordered.Any(p => p.Self))
        {
            _selfWaitRetries = 0;
        }
    }

    /// <summary>Everything the voice page actually draws, as one comparable string: channel,
    /// server, and each member's identity/name/avatar/mute/deafen in roster order. Speaking rings
    /// are deliberately NOT part of it — they ride on <see cref="SpeakingChanged"/>.</summary>
    private static string Signature(string channelId, string guildName, string channelName,
                                    Participant[] roster) =>
        $"{channelId}|{guildName}|{channelName}|" +
        string.Join(";", roster.Select(p => $"{p.Id},{p.Name},{p.AvatarUrl},{(p.Mute ? 1 : 0)}{(p.Deaf ? 1 : 0)}"));

    private static void Resubscribe(DiscordIpc ipc, string channelId)
    {
        string? previous = Interlocked.Exchange(ref _subscribedChannel, channelId);
        if (previous == channelId) return;

        if (previous is not null)
            foreach (var evt in ChannelEvents)
                ipc.Unsubscribe(evt, new { channel_id = previous }, Timeout, out _);

        foreach (var evt in ChannelEvents)
            if (!ipc.Subscribe(evt, new { channel_id = channelId }, Timeout, out var error) && error is not null)
                DiscordBridge.Log?.Invoke($"[Discord] room: SUBSCRIBE {evt} — {error}");
    }

    private static readonly string[] ChannelEvents =
    {
        "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE", "SPEAKING_START", "SPEAKING_STOP",
    };

    private static void Clear()
    {
        lock (_gate)
        {
            _participants = Array.Empty<Participant>();
            _speaking.Clear();
        }
        _subscribedChannel = null;
        _targetChannel = null;
        _lastSignature = null;
        ChannelId = null;
        ChannelName = GuildName = "";
        GuildIconUrl = null;
    }

    /// <summary>Avatar CDN url — the user's own picture, or the default one Discord derives from
    /// the user id when they have none.</summary>
    private static string AvatarUrl(string userId, string? avatarHash)
    {
        if (!string.IsNullOrEmpty(avatarHash))
            return $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png?size=128";
        int index = ulong.TryParse(userId, out var id) ? (int)((id >> 22) % 6) : 0;
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); } catch { /* a host repaint must never kill the RPC reader */ }
    }
}
