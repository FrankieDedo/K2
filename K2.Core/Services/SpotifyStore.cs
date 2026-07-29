using System;
using System.IO;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// Spotify account settings — same "bring your own app" model as <see cref="TwitchStore"/>:
/// real Base Camp's own Spotify integration (<c>_reference/BaseCamp_Decompiled/BaseCamp.Spotify</c>)
/// has Mountain's own Client ID/Secret baked into the binary, which K2 has no right to reuse, so
/// the user registers their own app at developer.spotify.com/dashboard (redirect URI
/// <see cref="SpotifyAuth.RedirectUri"/>) and pastes its Client ID/Secret here. Client secret and
/// both OAuth tokens are DPAPI-encrypted at rest (<see cref="SecretProtector"/>) from the start —
/// unlike <see cref="TwitchStore"/>, which only got this after the fact.
/// </summary>
public static class SpotifyStore
{
    private sealed class Data
    {
        public string ClientId { get; set; } = "";
        public string ClientSecretProtected { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AccessTokenProtected { get; set; } = "";
        public string RefreshTokenProtected { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "spotify_account.json");

    public static string ClientId { get { EnsureLoaded(); return _data.ClientId; } }
    public static string ClientSecret { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.ClientSecretProtected); } }
    public static string DisplayName { get { EnsureLoaded(); return _data.DisplayName; } }
    public static string AccessToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.AccessTokenProtected); } }
    public static string RefreshToken { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.RefreshTokenProtected); } }
    public static DateTime ExpiresAtUtc { get { EnsureLoaded(); return _data.ExpiresAtUtc; } }

    public static bool IsConnected { get { EnsureLoaded(); return _data.AccessTokenProtected.Length > 0; } }

    public static void SetAppCredentials(string clientId, string clientSecret)
    {
        EnsureLoaded();
        lock (_lock) { _data.ClientId = clientId; _data.ClientSecretProtected = SecretProtector.Protect(clientSecret); Save(); }
    }

    public static void SetTokens(string accessToken, string refreshToken, DateTime expiresAtUtc, string displayName)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _data.AccessTokenProtected = SecretProtector.Protect(accessToken);
            _data.RefreshTokenProtected = SecretProtector.Protect(refreshToken);
            _data.ExpiresAtUtc = expiresAtUtc;
            _data.DisplayName = displayName;
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
            _data.DisplayName = "";
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
