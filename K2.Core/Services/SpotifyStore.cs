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

        /// <summary>Spotify account tier from the last connect (<c>GET /me</c> <c>product</c>
        /// field), meaningful only when <see cref="ProductKnown"/> is true.</summary>
        public bool IsPremium { get; set; }

        /// <summary>True once a <c>GET /me</c> <c>product</c> value (or a <c>403</c> from a
        /// playback call) has actually told us the tier. Until then we do NOT assume Web API
        /// playback works — the media-key fallback is used, which is harmless for Premium too.
        /// An account connected before the <c>user-read-private</c> scope was requested stays
        /// unknown until the user reconnects.</summary>
        public bool ProductKnown { get; set; }

        /// <summary>Space-separated OAuth scopes the current token was actually granted (from the
        /// token endpoint's <c>scope</c> field). Used to tell the user, before a call 403s,
        /// that they need to reconnect because a scope was added since they last authorized.</summary>
        public string GrantedScopes { get; set; } = "";

        /// <summary>The connected account's email (from <c>GET /me</c>), shown in the settings
        /// window so the user can paste the exact address into the app's "User Management"
        /// allow-list on the Spotify dashboard.</summary>
        public string AccountEmail { get; set; } = "";

        /// <summary>Result of the connect-time <c>GET /me/tracks</c> probe: false means the
        /// account's library returned <c>403</c> (Development-mode app + account not allow-listed,
        /// or a restricted app). Like / playlist actions cannot work until that is fixed on the
        /// Spotify dashboard. Defaults true (assume OK until a probe says otherwise).</summary>
        public bool LibraryAccessOk { get; set; } = true;
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

    /// <summary>Best guess at the account tier — only trustworthy together with
    /// <see cref="WebApiPlaybackConfirmed"/>. Kept for the settings-window indicator.</summary>
    public static bool IsPremium { get { EnsureLoaded(); return _data.IsPremium; } }

    /// <summary>True only when we have POSITIVELY established that the Web API's
    /// <c>me/player/*</c> playback/volume endpoints will work for this account (tier read as
    /// "premium" from <c>GET /me</c>). Anything else — tier unknown, tier "free"/"open", a prior
    /// <c>403</c> — is false, and callers use the media-key fallback instead
    /// (<see cref="SpotifyBridge"/> / <c>ButtonActionEngine</c>).</summary>
    public static bool WebApiPlaybackConfirmed { get { EnsureLoaded(); return _data.ProductKnown && _data.IsPremium; } }

    /// <summary>Space-separated scopes the current token holds (see <see cref="Data.GrantedScopes"/>).
    /// Empty when unknown (old token from before this was tracked) — treat as "can't tell".</summary>
    public static string GrantedScopes { get { EnsureLoaded(); return _data.GrantedScopes; } }

    /// <summary>True when the granted scopes are known AND do not include <paramref name="scope"/>.
    /// False when scopes are unknown (don't nag on incomplete info) or the scope is present.</summary>
    public static bool ScopeKnownMissing(string scope)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(_data.GrantedScopes)) return false;
        return System.Array.IndexOf(_data.GrantedScopes.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), scope) < 0;
    }

    public static void SetGrantedScopes(string? scopes)
    {
        if (string.IsNullOrWhiteSpace(scopes)) return;
        EnsureLoaded();
        lock (_lock) { _data.GrantedScopes = scopes.Trim(); Save(); }
    }

    /// <summary>The connected account's email, or "" when unknown (token without
    /// <c>user-read-email</c>, i.e. connected before that scope was added).</summary>
    public static string AccountEmail { get { EnsureLoaded(); return _data.AccountEmail; } }

    public static void SetAccountEmail(string? email)
    {
        EnsureLoaded();
        lock (_lock) { _data.AccountEmail = email ?? ""; Save(); }
    }

    /// <summary>False when the connect-time <c>GET /me/tracks</c> probe got a 403 — the
    /// account's library is not reachable (dashboard fix needed). See <see cref="Data.LibraryAccessOk"/>.</summary>
    public static bool LibraryAccessOk { get { EnsureLoaded(); return _data.LibraryAccessOk; } }

    public static void SetLibraryAccessOk(bool ok)
    {
        EnsureLoaded();
        lock (_lock) { _data.LibraryAccessOk = ok; Save(); }
    }

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

    /// <summary>Records the account tier from a <c>GET /me</c> <c>product</c> value
    /// (<c>"premium"</c> / <c>"free"</c> / <c>"open"</c>) — or pass <c>"free"</c> from a playback
    /// <c>403</c>. An empty value is ignored (tier stays unknown, i.e. fallback keeps being
    /// used). Any non-empty value marks the tier as KNOWN
    /// (<see cref="WebApiPlaybackConfirmed"/>).</summary>
    public static void SetProduct(string product)
    {
        if (string.IsNullOrWhiteSpace(product)) return;
        EnsureLoaded();
        lock (_lock)
        {
            _data.IsPremium = product.Trim().Equals("premium", StringComparison.OrdinalIgnoreCase);
            _data.ProductKnown = true;
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
            _data.ProductKnown = false;   // re-detected on the next connect
            _data.IsPremium = false;
            _data.GrantedScopes = "";
            _data.AccountEmail = "";
            _data.LibraryAccessOk = true;
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
