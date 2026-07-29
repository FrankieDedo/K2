using System.IO;
using System.Text.Json;

namespace K2.Core.Services;

/// <summary>
/// YouTube account settings — the user's own Google Cloud OAuth Desktop-app client (Client ID/
/// Secret, YouTube Data API v3 enabled), same "user supplies their own app" model as
/// <see cref="TwitchStore"/>/<see cref="TwitchAuth"/>. Unlike Twitch, the actual OAuth tokens
/// are NOT stored here — <see cref="YouTubeBridge"/> hands them to a
/// <see cref="DpapiFileDataStore"/> (<see cref="TokenCacheDir"/>) instead of
/// <c>GoogleWebAuthorizationBroker</c>'s default plaintext <c>FileDataStore</c>, which handles
/// refresh internally but encrypts nothing at rest. The client secret here is DPAPI-encrypted
/// (<see cref="SecretProtector"/>) the same way, for the same reason as
/// <see cref="TwitchStore.ClientSecret"/>.
/// </summary>
public static class YouTubeStore
{
    private sealed class Data
    {
        public string ClientId { get; set; } = "";
        public string ClientSecretProtected { get; set; } = "";
        public bool Connected { get; set; }
        public string ChannelTitle { get; set; } = "";
    }

    private static Data _data = new();
    private static bool _loaded;
    private static readonly object _lock = new();

    private static string StorePath => Path.Combine(K2Paths.Root, "youtube_settings.json");
    public static string TokenCacheDir => Path.Combine(K2Paths.Root, "youtube_tokens");

    public static string ClientId { get { EnsureLoaded(); return _data.ClientId; } }
    public static string ClientSecret { get { EnsureLoaded(); return SecretProtector.Unprotect(_data.ClientSecretProtected); } }
    public static bool Connected { get { EnsureLoaded(); return _data.Connected; } }
    public static string ChannelTitle { get { EnsureLoaded(); return _data.ChannelTitle; } }

    public static void SetAppCredentials(string clientId, string clientSecret)
    {
        EnsureLoaded();
        lock (_lock) { _data.ClientId = clientId; _data.ClientSecretProtected = SecretProtector.Protect(clientSecret); Save(); }
    }

    public static void SetConnected(bool connected, string channelTitle = "")
    {
        EnsureLoaded();
        lock (_lock) { _data.Connected = connected; _data.ChannelTitle = channelTitle; Save(); }
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
