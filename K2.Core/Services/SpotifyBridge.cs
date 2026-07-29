using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Spotify integration via the plain Spotify Web API (<c>api.spotify.com/v1</c>) — no NuGet
/// client library, unlike Twitch/YouTube, since the handful of endpoints needed here
/// (playback transport, like, shuffle/repeat, volume, playlist add/remove) are simple REST
/// calls not worth a dependency for. Covers the actionable subset of real Base Camp's own
/// Spotify actions (<c>_reference/decompiled/Worker/DisplayPadWorker.Helpers/SpotifyHelper.cs</c>)
/// — cover-art/"now playing" display widgets there aren't ported, same precedent as
/// <see cref="YouTubeBridge"/> skipping the "viewers" widget.
///
/// Every method blocks on its underlying Task (same synchronous-from-the-UI-thread shape as
/// <see cref="TwitchBridge"/>/<see cref="ObsBridge"/>), acceptable for a user-triggered keypress.
/// </summary>
public static class SpotifyBridge
{
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.spotify.com/v1/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SpotifyStore.AccessToken);
        return http;
    }

    /// <summary>Refreshes the token if needed, then runs <paramref name="action"/> with a ready
    /// client. Returns false without running it if not connected or the refresh failed.</summary>
    private static bool Run(Func<HttpClient, Task> action, Action<string> log, string opName)
    {
        if (!SpotifyStore.IsConnected) { log("[EXEC] spotify: not connected"); return false; }
        if (!SpotifyAuth.EnsureFreshTokenAsync().GetAwaiter().GetResult())
        {
            log("[EXEC] spotify: token refresh failed"); return false;
        }
        try
        {
            using var http = CreateClient();
            action(http).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            log($"[EXEC] spotify {opName} error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Current playback state (<c>GET /me/player</c>), or null when nothing is playing
    /// (Spotify returns 204 No Content in that case — not an error).</summary>
    private static async Task<JsonDocument?> GetPlaybackAsync(HttpClient http)
    {
        var resp = await http.GetAsync("me/player");
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
        string body = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static bool PlayPauseToggle(Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        bool isPlaying = playback?.RootElement.TryGetProperty("is_playing", out var ip) == true && ip.GetBoolean();
        var resp = await http.PutAsync(isPlaying ? "me/player/pause" : "me/player/play", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify play/pause: {resp.StatusCode}");
    }, log, "play/pause");

    public static bool Next(Action<string> log) => Run(async http =>
    {
        var resp = await http.PostAsync("me/player/next", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify next: {resp.StatusCode}");
    }, log, "next track");

    public static bool Previous(Action<string> log) => Run(async http =>
    {
        var resp = await http.PostAsync("me/player/previous", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify previous: {resp.StatusCode}");
    }, log, "previous track");

    public static bool LikeToggle(Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        string? trackId = playback?.RootElement.TryGetProperty("item", out var item) == true
            && item.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(trackId)) { log("[EXEC] spotify like: no track playing"); return; }

        var containsResp = await http.GetAsync($"me/tracks/contains?ids={trackId}");
        bool alreadyLiked = false;
        if (containsResp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await containsResp.Content.ReadAsStringAsync());
            alreadyLiked = doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
        }

        var req = new HttpRequestMessage(alreadyLiked ? HttpMethod.Delete : HttpMethod.Put, $"me/tracks?ids={trackId}");
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify like toggle: {resp.StatusCode}");
    }, log, "like toggle");

    public static bool ShuffleToggle(Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        bool current = playback?.RootElement.TryGetProperty("shuffle_state", out var ss) == true && ss.GetBoolean();
        var resp = await http.PutAsync($"me/player/shuffle?state={(!current).ToString().ToLowerInvariant()}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify shuffle: {resp.StatusCode}");
    }, log, "shuffle toggle");

    /// <summary>Cycles off → track → context → off, matching real Base Camp's own decompiled
    /// <c>SpotifyClientHelper.SongRepeateStatus</c> order.</summary>
    public static bool RepeatCycle(Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        string current = playback?.RootElement.TryGetProperty("repeat_state", out var rs) == true ? rs.GetString() ?? "off" : "off";
        string next = current switch { "off" => "track", "track" => "context", _ => "off" };
        var resp = await http.PutAsync($"me/player/repeat?state={next}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify repeat: {resp.StatusCode}");
    }, log, "repeat cycle");

    /// <summary>Session-only "volume before mute" memory (matches real Base Camp's own
    /// in-process-only <c>GlobalVarables.Volume_beforeMute</c> — not persisted there either).</summary>
    private static int _volumeBeforeMute = 50;

    public static bool MuteToggle(Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        int current = playback?.RootElement.TryGetProperty("device", out var dev) == true
            && dev.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 0;
        int target;
        if (current > 0) { _volumeBeforeMute = current; target = 0; }
        else target = _volumeBeforeMute > 0 ? _volumeBeforeMute : 50;

        var resp = await http.PutAsync($"me/player/volume?volume_percent={target}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify mute: {resp.StatusCode}");
    }, log, "mute toggle");

    private static async Task<int> GetCurrentVolumeAsync(HttpClient http)
    {
        using var playback = await GetPlaybackAsync(http);
        return playback?.RootElement.TryGetProperty("device", out var dev) == true
            && dev.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 50;
    }

    public static bool VolumeUp(string stepArg, Action<string> log) => Run(async http =>
    {
        int step = int.TryParse(stepArg, out var s) && s > 0 ? s : 10;
        int target = Math.Min(100, await GetCurrentVolumeAsync(http) + step);
        var resp = await http.PutAsync($"me/player/volume?volume_percent={target}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify volume up: {resp.StatusCode}");
    }, log, "volume up");

    public static bool VolumeDown(string stepArg, Action<string> log) => Run(async http =>
    {
        int step = int.TryParse(stepArg, out var s) && s > 0 ? s : 10;
        int target = Math.Max(0, await GetCurrentVolumeAsync(http) - step);
        var resp = await http.PutAsync($"me/player/volume?volume_percent={target}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify volume down: {resp.StatusCode}");
    }, log, "volume down");

    public static bool VolumeSet(string percentArg, Action<string> log) => Run(async http =>
    {
        int target = Math.Clamp(int.TryParse(percentArg, out var p) ? p : 50, 0, 100);
        var resp = await http.PutAsync($"me/player/volume?volume_percent={target}", null);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify volume set: {resp.StatusCode}");
    }, log, "volume set");

    public static bool SaveToPlaylist(string playlistId, Action<string> log) => Run(async http =>
    {
        if (string.IsNullOrWhiteSpace(playlistId)) { log("[EXEC] spotify save to playlist: no playlist id"); return; }
        using var playback = await GetPlaybackAsync(http);
        string? uri = playback?.RootElement.TryGetProperty("item", out var item) == true
            && item.TryGetProperty("uri", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(uri)) { log("[EXEC] spotify save to playlist: no track playing"); return; }

        var resp = await http.PostAsync($"playlists/{playlistId}/tracks", JsonBody(new { uris = new[] { uri } }));
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify save to playlist: {resp.StatusCode}");
    }, log, "save to playlist");

    public static bool RemoveFromPlaylist(string playlistId, Action<string> log) => Run(async http =>
    {
        if (string.IsNullOrWhiteSpace(playlistId)) { log("[EXEC] spotify remove from playlist: no playlist id"); return; }
        using var playback = await GetPlaybackAsync(http);
        string? uri = playback?.RootElement.TryGetProperty("item", out var item) == true
            && item.TryGetProperty("uri", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(uri)) { log("[EXEC] spotify remove from playlist: no track playing"); return; }

        var req = new HttpRequestMessage(HttpMethod.Delete, $"playlists/{playlistId}/tracks")
        {
            Content = JsonBody(new { tracks = new[] { new { uri } } }),
        };
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) log($"[EXEC] spotify remove from playlist: {resp.StatusCode}");
    }, log, "remove from playlist");
}
