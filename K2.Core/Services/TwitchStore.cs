using System;
using System.IO;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// Twitch account settings — the user's own Twitch Developer app (Client ID/Secret, since K2
/// has no registered app of its own to share) plus the OAuth tokens obtained through it via
/// <see cref="TwitchAuth"/>. One account for the whole app, same "single global connection"
/// shape as <see cref="ObsStore"/>. Persisted as a small JSON file, same Load/Save/lock
/// skeleton as every other K2 store — except the client secret and both tokens are DPAPI-
/// encrypted (<see cref="SecretProtector"/>) before they touch disk, since this file lives
/// under <c>%LocalAppData%\K2</c> in plain reach of anything else running as the same user.
/// </summary>
public static class TwitchStore
{
    private sealed class Data
    {
        public string ClientId { get; set; } = "";
        public string ClientSecretProtected { get; set; } = "";
        public string BroadcasterUserId { get; set; } = "";
        public string Login { get; set; } = "";
        public string AccessTokenProtected { get; set; } = "";
        public string RefreshTokenProtected { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "twitch_account.json");

    public static string ClientId { get { EnsureLoaded(); return _data.ClientId; } }
    public static string ClientSecret { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.ClientSecretProtected); } }
    public static string BroadcasterUserId { get { EnsureLoaded(); return _data.BroadcasterUserId; } }
    public static string Login { get { EnsureLoaded(); return _data.Login; } }
    public static string AccessToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.AccessTokenProtected); } }
    public static string RefreshToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.RefreshTokenProtected); } }
    public static DateTime ExpiresAtUtc { get { EnsureLoaded(); return _data.ExpiresAtUtc; } }

    public static bool IsConnected { get { EnsureLoaded(); return _data.AccessTokenProtected.Length > 0; } }

    public static void SetAppCredentials(string clientId, string clientSecret)
    {
        EnsureLoaded();
        lock (_lock) { _data.ClientId = clientId; _data.ClientSecretProtected = SecretProtector.Protect(clientSecret); Save(); }
    }

    public static void SetTokens(string accessToken, string refreshToken, DateTime expiresAtUtc, string broadcasterUserId, string login)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.AccessTokenProtected = SecretProtector.Protect(accessToken);
            _data.RefreshTokenProtected = SecretProtector.Protect(refreshToken);
            _data.ExpiresAtUtc = expiresAtUtc;
            _data.BroadcasterUserId = broadcasterUserId;
            _data.Login = login;
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
            _data.BroadcasterUserId = "";
            _data.Login = "";
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
