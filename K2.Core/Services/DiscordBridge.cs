using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// Discord integration. Two transports, picked per command:
///
/// <list type="bullet">
/// <item><b>RPC</b> (<see cref="DiscordIpc"/>, local desktop client) for everything about the
/// user's own voice room — mute/deafen, input/output volume, push-to-talk vs voice activity,
/// join/leave/switch voice channel, per-user local volume/mute. Authenticated with the token
/// obtained by <see cref="DiscordAuth"/>.</item>
/// <item><b>Webhook</b> (plain HTTPS POST) for "send message": no bot to host, no OAuth — the
/// user pastes a channel webhook URL into the settings window.</item>
/// </list>
///
/// The RPC connection is kept OPEN after the first use and subscribed to
/// <c>VOICE_SETTINGS_UPDATE</c>/<c>VOICE_CHANNEL_SELECT</c>, so <see cref="Mute"/>/
/// <see cref="Deaf"/> track the real client state even when it's changed from Discord itself —
/// that's what drives the live key icons on the DisplayPad (<see cref="VoiceStateChanged"/>).
///
/// Command methods block on the pipe round trip (short timeout), same synchronous shape as
/// <see cref="ObsBridge"/>/<see cref="TwitchBridge"/>, since <c>ButtonActionEngine.Execute</c>
/// runs on the UI thread and callers already accept a brief block for a keypress.
/// </summary>
public static class DiscordBridge
{
    /// <summary>Diagnostic sink for the paths that have no per-call <c>log</c> action of their
    /// own — the connection/OAuth flow driven from <see cref="DiscordSettingsWindow"/>. Wired to
    /// the app log by the host (K2.App) at startup; left null in the standalone DisplayPad app,
    /// where it is simply a no-op. Key execution keeps using the log action ButtonActionEngine
    /// already passes down.</summary>
    public static Action<string>? Log { get; set; }

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);
    private static readonly object _lock = new();
    private static DiscordIpc? _ipc;
    private static bool _authenticated;
    private static bool _subscribed;

    /// <summary>Last known local mute state (null = unknown / not connected).</summary>
    public static bool? Mute { get; private set; }

    /// <summary>Last known local deafen state (null = unknown / not connected).</summary>
    public static bool? Deaf { get; private set; }

    /// <summary>Id of the voice channel the client is currently in, or null.</summary>
    public static string? VoiceChannelId { get; private set; }

    /// <summary>Current input mode as Discord reports it — <c>PUSH_TO_TALK</c> or
    /// <c>VOICE_ACTIVITY</c> (null = unknown). Drives the caption/glyph of the voice page's
    /// push-to-talk key, which otherwise couldn't say which mode it would switch AWAY from.</summary>
    public static string? InputMode { get; private set; }

    /// <summary>Discord user id of the authenticated account (null until AUTHENTICATE ran) —
    /// <see cref="DiscordVoiceRoom"/> uses it to pin the local user to the first roster slot.</summary>
    public static string? SelfUserId { get; private set; }

    /// <summary>True while the RPC pipe is actually open — lets a host retry
    /// <see cref="StartLiveVoiceState"/> later when Discord wasn't running yet at the first
    /// attempt (that call gives up silently and nothing else in this class retries it).</summary>
    public static bool IsRpcOpen { get { lock (_lock) return _ipc is { IsOpen: true }; } }

    /// <summary>Raised (on a background thread) whenever <see cref="Mute"/>/<see cref="Deaf"/>/
    /// <see cref="VoiceChannelId"/> change — hosts wanting live key icons must marshal to their
    /// own UI thread before repainting.</summary>
    public static event Action? VoiceStateChanged;

    // ---------------------------------------------------------------- connection

    /// <summary>Opens (or reuses) the RPC pipe and completes the handshake — NOT the OAuth
    /// authentication, which <see cref="DiscordAuth"/> drives on top of it during setup.</summary>
    internal static DiscordIpc? EnsureIpc(out string? error)
    {
        error = null;
        lock (_lock)
        {
            if (_ipc is { IsOpen: true }) return _ipc;

            _ipc?.Close();
            _authenticated = false;
            _subscribed = false;

            var ipc = new DiscordIpc();
            ipc.EventReceived += OnRpcEvent;
            ipc.Disconnected += OnRpcDisconnected;
            error = ipc.Open(DiscordStore.ClientId, TimeSpan.FromSeconds(5));
            if (error is not null) { ipc.Close(); return null; }

            _ipc = ipc;
            return ipc;
        }
    }

    /// <summary>Authenticates an already-open RPC connection with an OAuth access token.</summary>
    internal static bool Authenticate(string accessToken, out string userName, out string? error)
    {
        userName = "";
        var ipc = EnsureIpc(out error);
        if (ipc is null) return false;

        var data = ipc.Send("AUTHENTICATE", new { access_token = accessToken }, CommandTimeout, out error);
        if (data is null) return false;

        if (data.Value.ValueKind == JsonValueKind.Object && data.Value.TryGetProperty("user", out var user))
        {
            if (user.TryGetProperty("username", out var name)) userName = name.GetString() ?? "";
            // Needed by DiscordVoiceRoom to tell the local user apart from the other members of
            // the voice channel (they must always come first on the DisplayPad roster).
            if (user.TryGetProperty("id", out var uid)) SelfUserId = uid.GetString();
        }

        lock (_lock) _authenticated = true;
        SubscribeVoiceEvents(ipc);
        RefreshVoiceState(ipc);
        return true;
    }

    /// <summary>Connection state for a voice command: open pipe, authenticate with the stored
    /// token (refreshing it first when expired) and subscribe to the voice events.</summary>
    private static DiscordIpc? EnsureReady(Action<string> log)
    {
        if (!DiscordStore.IsConnected) { log("[EXEC] discord: not connected"); return null; }
        if (!DiscordAuth.EnsureFreshTokenAsync().GetAwaiter().GetResult())
        {
            log("[EXEC] discord: token refresh failed");
            return null;
        }

        var ipc = EnsureIpc(out var error);
        if (ipc is null) { log($"[EXEC] discord: {error}"); return null; }

        bool needAuth;
        lock (_lock) needAuth = !_authenticated;
        if (needAuth && !Authenticate(DiscordStore.AccessToken, out _, out var authError))
        {
            log($"[EXEC] discord: {authError}");
            return null;
        }
        return ipc;
    }

    /// <summary>Opens the RPC connection in the background so the live voice state (and the key
    /// icons driven by it) is available without waiting for the first keypress. Safe to call
    /// when Discord isn't configured or isn't running — it just gives up quietly.</summary>
    public static void StartLiveVoiceState()
    {
        if (!DiscordStore.IsConnected) return;
        System.Threading.Tasks.Task.Run(() => EnsureReady(_ => { }));
    }

    /// <summary>A connected+authenticated RPC handle for <see cref="DiscordVoiceRoom"/>'s worker
    /// thread (null when Discord isn't configured/running). Same path key execution takes, so the
    /// room never opens a second pipe of its own.</summary>
    internal static DiscordIpc? RoomIpc(Action<string> log) => EnsureReady(log);

    private static void SubscribeVoiceEvents(DiscordIpc ipc)
    {
        lock (_lock) { if (_subscribed) return; _subscribed = true; }
        ipc.Subscribe("VOICE_SETTINGS_UPDATE", null, CommandTimeout, out var settingsError);
        ipc.Subscribe("VOICE_CHANNEL_SELECT", null, CommandTimeout, out var channelError);
        if (settingsError is not null || channelError is not null)
            Log?.Invoke($"[Discord] event subscription: settings={settingsError ?? "ok"}, channel={channelError ?? "ok"}");
    }

    private static void OnRpcEvent(string evt, JsonElement data)
    {
        // The voice-room model owns the per-channel events (roster + speaking rings); it never
        // blocks here — every RPC call it needs is issued on its own worker, because a Send()
        // from THIS reader thread could never be answered (the reply is dispatched by it).
        DiscordVoiceRoom.OnRpcEvent(evt, data);

        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                break;
            case "VOICE_CHANNEL_SELECT":
                VoiceChannelId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("channel_id", out var ch)
                    ? ch.GetString() : null;
                DiscordVoiceRoom.OnChannelChanged(VoiceChannelId);
                Raise();
                break;
        }
    }

    private static void OnRpcDisconnected()
    {
        lock (_lock) { _authenticated = false; _subscribed = false; }
        Mute = Deaf = null;
        InputMode = null;
        VoiceChannelId = null;
        SelfUserId = null;
        DiscordVoiceRoom.Reset();
        Raise();
    }

    private static void Raise()
    {
        try { VoiceStateChanged?.Invoke(); } catch { /* a host repaint must never kill the reader thread */ }
    }

    private static void RefreshVoiceState(DiscordIpc ipc)
    {
        var data = ipc.Send("GET_VOICE_SETTINGS", null, CommandTimeout, out _);
        if (data is { } d) ApplyVoiceSettings(d);

        var ch = ipc.Send("GET_SELECTED_VOICE_CHANNEL", null, CommandTimeout, out _);
        VoiceChannelId = ch is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("id", out var id)
            ? id.GetString() : null;
        DiscordVoiceRoom.OnChannelChanged(VoiceChannelId);
        Raise();
    }

    private static void ApplyVoiceSettings(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return;
        if (data.TryGetProperty("mute", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False)
            Mute = m.GetBoolean();
        if (data.TryGetProperty("deaf", out var df) && df.ValueKind is JsonValueKind.True or JsonValueKind.False)
            Deaf = df.GetBoolean();
        if (data.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.Object
            && mode.TryGetProperty("type", out var modeType) && modeType.ValueKind == JsonValueKind.String)
            InputMode = modeType.GetString();
        Raise();
    }

    // ---------------------------------------------------------------- voice commands

    private static bool Run(Action<DiscordIpc> action, Action<string> log, string opName)
    {
        var ipc = EnsureReady(log);
        if (ipc is null) return false;
        try { action(ipc); return true; }
        catch (Exception ex) { log($"[EXEC] discord {opName} error: {ex.Message}"); return false; }
    }

    /// <summary>Reads the current voice settings straight from the client (not the cache) —
    /// toggles must never flip a stale value.</summary>
    private static JsonElement? CurrentSettings(DiscordIpc ipc) => ipc.Send("GET_VOICE_SETTINGS", null, CommandTimeout, out _);

    /// <summary>Applies a partial voice-settings patch. The reply carries the RESULTING
    /// settings, so the cached state (and the live key icons that follow it) is refreshed from
    /// it right away — waiting for the <c>VOICE_SETTINGS_UPDATE</c> event would leave the key
    /// showing the old state whenever that subscription isn't delivering.</summary>
    private static bool SetVoiceSettings(DiscordIpc ipc, object args, Action<string> log, string opName)
    {
        var data = ipc.Send("SET_VOICE_SETTINGS", args, CommandTimeout, out var error);
        if (error is not null) { log($"[EXEC] discord {opName}: {error}"); return false; }
        if (data is { } d) ApplyVoiceSettings(d);
        return true;
    }

    public static bool SetMute(bool value, Action<string> log) =>
        Run(ipc => SetVoiceSettings(ipc, new { mute = value }, log, "mute"), log, "mute");

    public static bool ToggleMute(Action<string> log) => Run(ipc =>
    {
        bool current = ReadBool(CurrentSettings(ipc), "mute") ?? false;
        SetVoiceSettings(ipc, new { mute = !current }, log, "mute toggle");
    }, log, "mute toggle");

    public static bool SetDeaf(bool value, Action<string> log) =>
        Run(ipc => SetVoiceSettings(ipc, new { deaf = value }, log, "deafen"), log, "deafen");

    public static bool ToggleDeaf(Action<string> log) => Run(ipc =>
    {
        bool current = ReadBool(CurrentSettings(ipc), "deaf") ?? false;
        SetVoiceSettings(ipc, new { deaf = !current }, log, "deafen toggle");
    }, log, "deafen toggle");

    /// <summary>Switches between push-to-talk and voice-activity input modes. The whole
    /// <c>mode</c> object is sent back with only <c>type</c> changed — Discord treats it as a
    /// replacement, so dropping the other fields would reset the PTT shortcut/delay.</summary>
    public static bool ToggleInputMode(Action<string> log) => Run(ipc =>
    {
        var settings = CurrentSettings(ipc);
        string type = "VOICE_ACTIVITY";
        double delay = 20;
        double threshold = -60;
        bool autoThreshold = true;

        if (settings is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("mode", out var mode)
            && mode.ValueKind == JsonValueKind.Object)
        {
            if (mode.TryGetProperty("type", out var t) && t.GetString() == "VOICE_ACTIVITY") type = "PUSH_TO_TALK";
            if (mode.TryGetProperty("delay", out var d) && d.ValueKind == JsonValueKind.Number) delay = d.GetDouble();
            if (mode.TryGetProperty("threshold", out var th) && th.ValueKind == JsonValueKind.Number) threshold = th.GetDouble();
            if (mode.TryGetProperty("auto_threshold", out var at) && at.ValueKind is JsonValueKind.True or JsonValueKind.False)
                autoThreshold = at.GetBoolean();
        }
        else type = "PUSH_TO_TALK";

        SetVoiceSettings(ipc, new { mode = new { type, auto_threshold = autoThreshold, threshold, delay } }, log, "input mode");
    }, log, "input mode");

    /// <summary>Sets the microphone volume. <paramref name="arg"/> is an absolute percentage
    /// ("70") or a relative step ("+10" / "-10"); Discord's input range is 0..100.</summary>
    public static bool SetInputVolume(string arg, Action<string> log) => Run(ipc =>
    {
        double current = ReadVolume(CurrentSettings(ipc), "input") ?? 100;
        double next = ApplyVolumeArg(arg, current, 100);
        SetVoiceSettings(ipc, new { input = new { volume = next } }, log, "input volume");
    }, log, "input volume");

    /// <summary>Sets the output (speaker) volume — Discord allows up to 200 here.</summary>
    public static bool SetOutputVolume(string arg, Action<string> log) => Run(ipc =>
    {
        double current = ReadVolume(CurrentSettings(ipc), "output") ?? 100;
        double next = ApplyVolumeArg(arg, current, 200);
        SetVoiceSettings(ipc, new { output = new { volume = next } }, log, "output volume");
    }, log, "output volume");

    /// <summary>Joins (or switches to) a voice channel. <paramref name="arg"/> is the channel
    /// id, optionally as picked from the settings list ("id  #name (guild)").</summary>
    public static bool JoinVoiceChannel(string arg, Action<string> log) => Run(ipc =>
    {
        string? channelId = ParseChannelId(arg);
        if (channelId is null) { log("[EXEC] discord: no voice channel id in the action value"); return; }
        ipc.Send("SELECT_VOICE_CHANNEL", new { channel_id = channelId, force = true }, CommandTimeout, out var error);
        if (error is not null) log($"[EXEC] discord join voice: {error}");
    }, log, "join voice");

    public static bool LeaveVoiceChannel(Action<string> log) => Run(ipc =>
    {
        ipc.Send("SELECT_VOICE_CHANNEL", new { channel_id = (string?)null, force = true }, CommandTimeout, out var error);
        if (error is not null) log($"[EXEC] discord leave voice: {error}");
    }, log, "leave voice");

    /// <summary>Sets another user's LOCAL volume (only affects this client).
    /// <paramref name="arg"/> is "userId:percent" (0..200).</summary>
    public static bool SetUserVolume(string arg, Action<string> log) => Run(ipc =>
    {
        var parts = arg.Split(new[] { ':', '=' }, 2);
        if (parts.Length < 2 || !double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
        {
            log("[EXEC] discord user volume: expected \"userId:percent\"");
            return;
        }
        ipc.Send("SET_USER_VOICE_SETTINGS",
            new { user_id = parts[0].Trim(), volume = Math.Clamp(vol, 0, 200) }, CommandTimeout, out var error);
        if (error is not null) log($"[EXEC] discord user volume: {error}");
    }, log, "user volume");

    /// <summary>Locally mutes/unmutes another user. <paramref name="arg"/> is the user id;
    /// the mute is toggled against the value the client reports back.</summary>
    public static bool ToggleUserMute(string arg, Action<string> log) => Run(ipc =>
    {
        string userId = arg.Trim();
        if (userId.Length == 0) { log("[EXEC] discord user mute: no user id"); return; }

        // There is no GET_USER_VOICE_SETTINGS: a SET carrying only the user id changes nothing
        // and returns that user's current per-user settings, which is what the toggle reads.
        var data = ipc.Send("SET_USER_VOICE_SETTINGS", new { user_id = userId }, CommandTimeout, out var readError);
        if (readError is not null) { log($"[EXEC] discord user mute: {readError}"); return; }

        bool muted = ReadBool(data, "mute") ?? false;
        ipc.Send("SET_USER_VOICE_SETTINGS", new { user_id = userId, mute = !muted }, CommandTimeout, out var error);
        if (error is not null) log($"[EXEC] discord user mute: {error}");
    }, log, "user mute");

    // ---------------------------------------------------------------- webhook

    /// <summary>Posts a message to the configured channel webhook. No RPC, no OAuth — this is
    /// the one Discord command that works without the desktop client running.</summary>
    public static bool SendWebhookMessage(string message, Action<string> log)
    {
        string url = DiscordStore.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) { log("[EXEC] discord: no webhook URL configured"); return false; }
        if (string.IsNullOrWhiteSpace(message)) { log("[EXEC] discord: empty webhook message"); return false; }

        try
        {
            using var http = new HttpClient();
            var body = new StringContent(JsonSerializer.Serialize(new { content = message }), Encoding.UTF8, "application/json");
            var resp = http.PostAsync(url, body).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { log($"[EXEC] discord webhook: {resp.StatusCode}"); return false; }
            return true;
        }
        catch (Exception ex) { log($"[EXEC] discord webhook error: {ex.Message}"); return false; }
    }

    // ---------------------------------------------------------------- pickers

    /// <summary>Voice channels the connected account can see, as
    /// <c>"id  #name (guild)"</c> entries — the id-first shape <see cref="ParseChannelId"/>
    /// reads back, so the action value stays valid even if the channel is renamed. Used to
    /// populate the "join voice channel" argument list in the action dialog; returns an empty
    /// array when Discord isn't connected/running (the combo stays free-text).</summary>
    public static string[] ListVoiceChannels()
    {
        var ipc = EnsureReady(_ => { });
        if (ipc is null) return Array.Empty<string>();

        var guilds = ipc.Send("GET_GUILDS", null, CommandTimeout, out _);
        if (guilds is not { ValueKind: JsonValueKind.Object } g || !g.TryGetProperty("guilds", out var list))
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var guild in list.EnumerateArray())
        {
            string guildId = guild.TryGetProperty("id", out var gid) ? gid.GetString() ?? "" : "";
            string guildName = guild.TryGetProperty("name", out var gname) ? gname.GetString() ?? "" : "";
            if (guildId.Length == 0) continue;

            var channels = ipc.Send("GET_CHANNELS", new { guild_id = guildId }, CommandTimeout, out _);
            if (channels is not { ValueKind: JsonValueKind.Object } c || !c.TryGetProperty("channels", out var chList))
                continue;

            foreach (var ch in chList.EnumerateArray())
            {
                // Discord channel type 2 = guild voice (4 = category, 0 = text, 13 = stage).
                if (!ch.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || type.GetInt32() != 2)
                    continue;
                string id = ch.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
                string name = ch.TryGetProperty("name", out var cname) ? cname.GetString() ?? "" : "";
                if (id.Length > 0) result.Add($"{id}  #{name} ({guildName})");
            }
        }
        return result.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>First run of digits in the stored value — accepts both a bare channel id and a
    /// whole picker entry ("id  #name (guild)").</summary>
    private static string? ParseChannelId(string value)
    {
        var digits = new string(value.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static bool? ReadBool(JsonElement? data, string name) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static double? ReadVolume(JsonElement? data, string section) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(section, out var s)
            && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("volume", out var v)
            && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    /// <summary>"70" = absolute, "+10"/"-10" = relative to <paramref name="current"/>, empty =
    /// leave as is. Clamped to 0..<paramref name="max"/>.</summary>
    private static double ApplyVolumeArg(string arg, double current, double max)
    {
        arg = (arg ?? "").Trim();
        if (arg.Length == 0) return current;

        bool relative = arg[0] is '+' or '-';
        if (!double.TryParse(arg, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)) return current;
        return Math.Clamp(relative ? current + value : value, 0, max);
    }
}
