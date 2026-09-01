using System;
using System.IO;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// Discord settings — the user's own Discord application (Client ID/Secret) plus the OAuth
/// tokens obtained through it by <see cref="DiscordAuth"/>, and the optional webhook URL used
/// by the "send message" command. Same single-global-account shape and Load/Save/lock skeleton
/// as <see cref="TwitchStore"/>; secret, tokens and webhook URL are DPAPI-encrypted
/// (<see cref="SecretProtector"/>) before touching disk (the webhook URL IS a credential —
/// anyone holding it can post to that channel).
///
/// K2 has no registered Discord app of its own to share: Discord's RPC voice scopes
/// (<c>rpc</c>, <c>rpc.voice.read</c>, <c>rpc.voice.write</c>) are whitelist-only for public
/// apps, but the app OWNER can always use them on their own app — which is exactly the
/// "bring your own app" model already used for Twitch and Youtube.
/// </summary>
public static class DiscordStore
{
    private sealed class Data
    {
        public string ClientId { get; set; } = "";
        public string ClientSecretProtected { get; set; } = "";
        public string UserName { get; set; } = "";
        public string AccessTokenProtected { get; set; } = "";
        public string RefreshTokenProtected { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public string WebhookUrlProtected { get; set; } = "";
        public string WebcamHotkey { get; set; } = DefaultWebcamHotkey;
        public string PushToTalkHotkey { get; set; } = "";
        public bool VoicePageReturnEnabled { get; set; } = true;
        public int VoicePageReturnSeconds { get; set; } = DefaultVoicePageReturnSeconds;
        public bool VoicePageBackArrow { get; set; } = true;
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "discord_account.json");

    public static string ClientId { get { EnsureLoaded(); return _data.ClientId; } }
    public static string ClientSecret { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.ClientSecretProtected); } }
    public static string UserName { get { EnsureLoaded(); return _data.UserName; } }
    public static string AccessToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.AccessTokenProtected); } }
    public static string RefreshToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.RefreshTokenProtected); } }
    public static DateTime ExpiresAtUtc { get { EnsureLoaded(); return _data.ExpiresAtUtc; } }
    public static string WebhookUrl { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.WebhookUrlProtected); } }

    /// <summary>Shortcut the voice page's webcam key sends. Discord's RPC exposes no video
    /// command at all, and — unlike mute/deafen — the camera has no default keybind either, so the
    /// only way to drive it from a key is to replay the shortcut the user assigned in Discord's own
    /// Keybinds settings. Recorded (not typed) in <see cref="DiscordSettingsWindow"/>, in the
    /// notation <see cref="SendKeysTranslator"/> parses.</summary>
    public static string WebcamHotkey
    {
        get { EnsureLoaded(); return _data.WebcamHotkey; }
        set { EnsureLoaded(); lock (_lock) { _data.WebcamHotkey = string.IsNullOrWhiteSpace(value) ? "" : value.Trim(); Save(); } }
    }

    /// <summary>Ctrl+Shift+V is free in Discord's own defaults, so proposing it as the camera
    /// shortcut can't collide with an existing binding.</summary>
    public const string DefaultWebcamHotkey = "Ctrl+Shift+V";

    /// <summary>Shortcut the voice page's push-to-talk key holds down for exactly as long as the
    /// physical key is pressed. Discord's RPC has no "transmit now" command (its voice surface is
    /// settings + channel selection only), so momentary PTT can only be driven by replaying the
    /// keybind the user set in Discord ▸ Settings ▸ Keybinds ▸ Push to Talk. No sensible default —
    /// Discord ships none — so it starts empty and the key is inert until recorded in
    /// <see cref="DiscordProfileConfigWindow"/>.</summary>
    public static string PushToTalkHotkey
    {
        get { EnsureLoaded(); return _data.PushToTalkHotkey; }
        set { EnsureLoaded(); lock (_lock) { _data.PushToTalkHotkey = string.IsNullOrWhiteSpace(value) ? "" : value.Trim(); Save(); } }
    }

    /// <summary>When true, the DisplayPad Discord voice page reopens on its own
    /// <see cref="VoicePageReturnSeconds"/> after the user has left it for a normal profile while a
    /// call is still running — a screensaver-style comeback. When false the page only returns on a
    /// new call or a key bound to <c>discord ▸ voice page</c>.</summary>
    public static bool VoicePageReturnEnabled
    {
        get { EnsureLoaded(); return _data.VoicePageReturnEnabled; }
        set { EnsureLoaded(); lock (_lock) { _data.VoicePageReturnEnabled = value; Save(); } }
    }

    /// <summary>Idle delay for <see cref="VoicePageReturnEnabled"/>, clamped to a sane range.</summary>
    public static int VoicePageReturnSeconds
    {
        get { EnsureLoaded(); return _data.VoicePageReturnSeconds; }
        set { EnsureLoaded(); lock (_lock) { _data.VoicePageReturnSeconds = Math.Clamp(value, 3, 3600); Save(); } }
    }

    public const int DefaultVoicePageReturnSeconds = 10;

    /// <summary>Whether the voice page's server/group tile wears the back-arrow badge. The key is
    /// the way out of the page either way — it was, long before the badge existed; this only
    /// controls the MARK, for someone who would rather see the server picture unobscured.</summary>
    public static bool VoicePageBackArrow
    {
        get { EnsureLoaded(); return _data.VoicePageBackArrow; }
        set { EnsureLoaded(); lock (_lock) { _data.VoicePageBackArrow = value; Save(); } }
    }

    /// <summary>True once the OAuth flow has produced a token — the voice commands need it.
    /// The webhook command works independently (see <see cref="HasWebhook"/>).</summary>
    public static bool IsConnected { get { EnsureLoaded(); return _data.AccessTokenProtected.Length > 0; } }

    public static bool HasWebhook { get { EnsureLoaded(); return _data.WebhookUrlProtected.Length > 0; } }

    public static void SetAppCredentials(string clientId, string clientSecret)
    {
        EnsureLoaded();
        lock (_lock) { _data.ClientId = clientId; _data.ClientSecretProtected = SecretProtector.Protect(clientSecret); Save(); }
    }

    public static void SetWebhookUrl(string url)
    {
        EnsureLoaded();
        lock (_lock) { _data.WebhookUrlProtected = SecretProtector.Protect(url); Save(); }
    }

    public static void SetTokens(string accessToken, string refreshToken, DateTime expiresAtUtc, string userName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.AccessTokenProtected = SecretProtector.Protect(accessToken);
            _data.RefreshTokenProtected = SecretProtector.Protect(refreshToken);
            _data.ExpiresAtUtc = expiresAtUtc;
            _data.UserName = userName;
            Save();
        }
    }

    public static void Disconnect()
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.AccessTokenProtected = "";
            _data.RefreshTokenProtected = "";
            _data.UserName = "";
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            Load();
            _loaded = true;
        }
    }

    private static void Load()
    {
        try
        {
            string path = StorePath;
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
                if (data is not null) _data = data;
            }
        }
        catch { _data = new Data(); }
    }

    private static void Save()
    {
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort persistence */ }
    }
}
