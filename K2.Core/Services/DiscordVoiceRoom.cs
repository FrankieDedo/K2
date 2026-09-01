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

    /// <summary>The client joined (or left, with null) a voice channel.</summary>
    internal static void OnChannelChanged(string? channelId)
    {
        if (channelId == ChannelId) return;
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
                // ChannelId is only committed at the END of the first Refresh, so on a fresh
                // join the local user's OWN VOICE_STATE_CREATE — the event that normally first
                // puts "you" (avatar and all) on the roster, because Discord's GET_CHANNEL
                // doesn't list your voice state until the voice session is fully up — can land
                // while ChannelId is still null. Falling back to the channel the bridge is
                // switching to stops that event from being dropped and leaving your own circle
                // blank until the next unrelated roster change (user report).
                if ((ChannelId ?? DiscordBridge.VoiceChannelId) is string id) Post(ipc => Refresh(ipc, id));
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
    private static void Post(Action<DiscordIpc> work)
    {
        lock (_gate)
            _worker = _worker.ContinueWith(_ =>
            {
                try
                {
                    var ipc = DiscordBridge.RoomIpc(msg => DiscordBridge.Log?.Invoke($"[Discord] room: {msg}"));
                    if (ipc is not null) work(ipc);
                }
                catch (Exception ex) { DiscordBridge.Log?.Invoke($"[Discord] room error: {ex.Message}"); }
            }, TaskScheduler.Default);
    }

    /// <summary>Reads the channel (roster + guild) and re-arms the per-channel subscriptions.</summary>
    private static void Refresh(DiscordIpc ipc, string channelId)
    {
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

        DiscordBridge.Log?.Invoke($"[Discord] room: {guildName}/{channelName} — {ordered.Length} member(s)");
        Raise(Changed);

        // A brand-new join often returns before Discord has added the local user's OWN voice
        // state to the channel: GET_CHANNEL then lists everyone but you, so the roster shows no
        // "me" tile until the next unrelated change (user report — "on the first join my face
        // isn't shown"). Re-read a few times, backing off, until you turn up.
        if (self is not null && !ordered.Any(p => p.Self) && _selfWaitRetries < 5)
        {
            int attempt = ++_selfWaitRetries;
            _ = Task.Delay(TimeSpan.FromMilliseconds(300 * attempt)).ContinueWith(_ =>
            {
                if (ChannelId == channelId && !Participants.Any(p => p.Self))
                    Post(ipc2 => Refresh(ipc2, channelId));
            }, TaskScheduler.Default);
        }
        else if (ordered.Any(p => p.Self))
        {
            _selfWaitRetries = 0;
        }
    }

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
