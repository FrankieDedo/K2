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
/// Every method is <b>fire-and-forget</b>: the network round-trip runs on the thread pool and
/// the call returns immediately ("dispatched", not "succeeded" — the real outcome is logged
/// asynchronously). It is invoked from the UI thread (<see cref="ButtonActionEngine"/>) and the
/// per-command lambdas below <c>await</c> without <c>ConfigureAwait(false)</c>, so the previous
/// blocking shape (<c>.GetAwaiter().GetResult()</c>, copied from
/// <see cref="TwitchBridge"/>/<see cref="ObsBridge"/> whose library code opts out of context
/// capture internally) dead-locked the whole app on any Spotify keypress. The caller passes a
/// log delegate that marshals back to the UI thread.
///
/// Playback-transport and volume commands hit the Web API's <c>me/player/*</c> endpoints, which
/// Spotify only honours for <b>Premium</b> accounts (free accounts get
/// <c>403 Player command failed: Premium required</c>). That is detected once at connect from
/// the account's <c>product</c> field (<see cref="SpotifyStore.IsPremium"/>) and such commands
/// are blocked here before any HTTP call when the account is known to be free. "Like" and
/// playlist add/remove are library operations, not playback, and work on free accounts.
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
    /// client — on the thread pool, without blocking the caller. Returns false immediately (and
    /// runs nothing) when not connected, or when <paramref name="needsPremium"/> is set and the
    /// account is known to be free; otherwise returns true meaning "dispatched" (the token
    /// refresh / request outcome is reported later through <paramref name="log"/>).</summary>
    private static bool Run(Func<HttpClient, Task> action, Action<string> log, string opName, bool needsPremium = false)
    {
        if (!SpotifyStore.IsConnected) { log("[EXEC] spotify: not connected"); return false; }
        if (needsPremium && !SpotifyStore.WebApiPlaybackConfirmed)
        {
            log("[EXEC] spotify: Web API playback not confirmed for this account — skipped (media-key fallback handles it)");
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await SpotifyAuth.EnsureFreshTokenAsync(m => log($"[EXEC] {m}")).ConfigureAwait(false))
                {
                    log("[EXEC] spotify: token refresh failed"); return;
                }
                using var http = CreateClient();
                await action(http).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log($"[EXEC] spotify {opName} error: {ex.Message}");
            }
        });
        return true;
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

    /// <summary>Human explanation for a non-2xx Spotify response, tuned for the cases K2 hits:
    /// a bare <c>403 "Forbidden"</c> on a token that already holds the needed scopes almost
    /// always means the Spotify account is not on the app's allow-list (Development-mode apps
    /// only work for accounts added under <b>User Management</b> in the Spotify dashboard).</summary>
    private static string ExplainError(int status, string body, string scopeNeeded)
    {
        if (body.Contains("PREMIUM_REQUIRED", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Premium required", StringComparison.OrdinalIgnoreCase))
            return "Spotify Premium required";
        if (body.Contains("Insufficient client scope", StringComparison.OrdinalIgnoreCase)
            || (status == 403 && SpotifyStore.ScopeKnownMissing(scopeNeeded)))
            return $"token is missing the '{scopeNeeded}' scope — Disconnect + Connect in the Spotify settings";
        if (status == 403)
            return "403 Forbidden with the scope present — Spotify blocks the library / playlist-modify "
                 + "endpoints for Development-mode apps (2025 change). Playback (play/pause/next/volume) "
                 + "still works; Like / Save-to-playlist need the app to be granted Extended Access on "
                 + "the Spotify dashboard, or a Base-Camp-style shared app. Not fixable from K2.";
        if (status == 401)
            return "401 — token rejected; Disconnect + Connect in the Spotify settings";
        return $"{status}";
    }

    /// <summary>Keeps <see cref="SpotifyStore.LibraryAccessOk"/> in sync with what the library /
    /// playlist endpoints actually return, so the settings window's warning appears/clears
    /// without waiting for the next reconnect probe. A <c>403</c> sets it false; any success
    /// sets it true; other statuses (network, 5xx) leave it alone.</summary>
    private static void NoteLibrary(System.Net.Http.HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) { if (!SpotifyStore.LibraryAccessOk) SpotifyStore.SetLibraryAccessOk(true); }
        else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) { if (SpotifyStore.LibraryAccessOk) SpotifyStore.SetLibraryAccessOk(false); }
    }

    /// <summary>Appends <c>?device_id=</c> / <c>&amp;device_id=</c> to a <c>me/player/*</c> URL
    /// when the key's Spotify action carries a target Spotify Connect device (picked per-key in
    /// the button-action dialog). Empty = leave the URL alone and let Spotify use the active
    /// device, exactly as before. Mirrors Base Camp's own <c>?device_id=</c> on every player
    /// call.</summary>
    private static string Dev(string url, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return url;
        return url + (url.Contains('?') ? "&" : "?") + "device_id=" + Uri.EscapeDataString(deviceId);
    }

    /// <summary>The user's Spotify Connect devices (<c>GET /me/player/devices</c>) for the
    /// settings-window picker. Awaited directly (not fire-and-forget): the caller is a dialog,
    /// not a keypress. Returns an empty list when not connected or on any error.</summary>
    /// <param name="log">Optional — see <see cref="SpotifyAuth.EnsureFreshTokenAsync"/>; without
    /// it a broken refresh token (or a 401/403 on the devices call itself) just comes back as an
    /// empty list with nothing in the log to explain "no devices found" (user report
    /// 2026-09-01).</param>
    public static async Task<System.Collections.Generic.List<(string Id, string Name, string Type, bool IsActive)>> GetDevicesAsync(Action<string>? log = null)
    {
        var list = new System.Collections.Generic.List<(string, string, string, bool)>();
        if (!SpotifyStore.IsConnected) return list;
        if (!await SpotifyAuth.EnsureFreshTokenAsync(log).ConfigureAwait(false)) return list;
        try
        {
            using var http = CreateClient();
            var resp = await http.GetAsync("me/player/devices").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                log?.Invoke($"[Spotify] GET devices failed: HTTP {(int)resp.StatusCode}");
                return list;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                foreach (var d in devs.EnumerateArray())
                    list.Add((
                        d.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                        d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True));
        }
        catch { /* return whatever we have */ }
        return list;
    }

    /// <summary>Resolves the currently-playing track's id + uri. Tries <c>GET /me/player</c>
    /// first (works when there's an active Spotify Connect device), then falls back to the
    /// Windows "now playing" (SMTC) title/artist resolved through <c>GET /search</c> — that
    /// fallback is what makes Like / Save-to-playlist work on a <b>free</b> account, or any
    /// time nothing is the "active device", where <c>/me/player</c> returns 204 and the old
    /// code just gave up with "no track playing".</summary>
    private static async Task<(string? Id, string? Uri)> ResolveCurrentTrackAsync(HttpClient http, Action<string> log)
    {
        using (var playback = await GetPlaybackAsync(http).ConfigureAwait(false))
        {
            if (playback?.RootElement.TryGetProperty("item", out var item) == true
                && item.ValueKind == JsonValueKind.Object)
            {
                string? pid = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string? puri = item.TryGetProperty("uri", out var uriEl) ? uriEl.GetString() : null;
                if (!string.IsNullOrEmpty(pid))
                {
                    log($"[EXEC] spotify: current track from /me/player -> {pid}");
                    return (pid, puri);
                }
            }
        }

        var (title, artist) = await SpotifyMediaService.Instance.GetNowPlayingAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
        {
            log("[EXEC] spotify: no current track (no active device, and Windows reports no Spotify 'now playing' — is the Spotify desktop app running and playing?)");
            return (null, null);
        }
        log($"[EXEC] spotify: now playing (Windows) = \"{title}\" / \"{artist}\" — resolving id via search");

        // Plain free-text query (title + artist), NOT the track:/artist: field filters: those
        // only take the first word of a multi-word value, so "Blinding Lights" would search for
        // "Blinding" and often miss. limit=1 + the exact now-playing string is accurate enough.
        string q = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var searchResp = await http.GetAsync($"search?q={Uri.EscapeDataString(q)}&type=track&limit=1").ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode)
        {
            log($"[EXEC] spotify search: {searchResp.StatusCode}");
            return (null, null);
        }
        using var doc = JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("tracks", out var tracks)
            && tracks.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var t = items[0];
            string? sid = t.TryGetProperty("id", out var i) ? i.GetString() : null;
            log($"[EXEC] spotify: search matched -> {sid ?? "?"}");
            return (sid, t.TryGetProperty("uri", out var u) ? u.GetString() : null);
        }
        log($"[EXEC] spotify search: no match for \"{title}\"");
        return (null, null);
    }

    /// <summary>Handles a playback-endpoint response: logs anything non-2xx, and — ONLY when the
    /// body actually says so (<c>reason: PREMIUM_REQUIRED</c> / "Premium required") — records the
    /// account as non-Premium (<see cref="SpotifyStore.SetProduct"/>) so the next keypress takes
    /// the media-key fallback. A bare <c>403</c> / <c>404 NO_ACTIVE_DEVICE</c> / a transient
    /// restriction is NOT treated as "free": doing so permanently un-ticked the "Web API active"
    /// flag for real Premium users whenever Spotify happened to have no active device (user
    /// report). Reconnecting re-reads the real tier from <c>GET /me</c> and fixes a bad mark.</summary>
    private static async Task NotePlayback(System.Net.Http.HttpResponseMessage resp, Action<string> log, string op)
    {
        if (resp.IsSuccessStatusCode)
        {
            // A playback command that actually worked proves this account has Web API playback —
            // re-affirm it, so a flag wrongly cleared by an earlier stray 403 heals itself.
            if (!SpotifyStore.WebApiPlaybackConfirmed) { SpotifyStore.SetProduct("premium"); log($"[EXEC] spotify {op}: ok — Web API playback confirmed"); }
            return;
        }

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }

        bool premiumRequired = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
            && (body.Contains("PREMIUM_REQUIRED", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Premium required", StringComparison.OrdinalIgnoreCase));

        if (premiumRequired)
        {
            SpotifyStore.SetProduct("free");
            log($"[EXEC] spotify {op}: Premium required — account marked non-Premium, media-key fallback from now on");
        }
        else
        {
            log($"[EXEC] spotify {op}: {(int)resp.StatusCode} {resp.StatusCode}{(body.Length > 0 ? " — " + body : "")}");
        }
    }

    public static bool PlayPauseToggle(string deviceId, Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        bool isPlaying = playback?.RootElement.TryGetProperty("is_playing", out var ip) == true && ip.GetBoolean();
        var resp = await http.PutAsync(Dev(isPlaying ? "me/player/pause" : "me/player/play", deviceId), null);
        await NotePlayback(resp, log, "play/pause");
    }, log, "play/pause", needsPremium: true);

    public static bool Next(string deviceId, Action<string> log) => Run(async http =>
    {
        var resp = await http.PostAsync(Dev("me/player/next", deviceId), null);
        await NotePlayback(resp, log, "next track");
    }, log, "next track", needsPremium: true);

    public static bool Previous(string deviceId, Action<string> log) => Run(async http =>
    {
        var resp = await http.PostAsync(Dev("me/player/previous", deviceId), null);
        await NotePlayback(resp, log, "previous track");
    }, log, "previous track", needsPremium: true);

    public static bool LikeToggle(Action<string> log) => Run(async http =>
    {
        var (trackId, _) = await ResolveCurrentTrackAsync(http, log);
        if (string.IsNullOrEmpty(trackId)) return; // ResolveCurrentTrackAsync already logged why
        log($"[EXEC] spotify like: track id = {trackId}");

        var containsResp = await http.GetAsync($"me/tracks/contains?ids={trackId}");
        bool alreadyLiked = false;
        if (containsResp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await containsResp.Content.ReadAsStringAsync());
            alreadyLiked = doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
        }
        else
        {
            string cb = await containsResp.Content.ReadAsStringAsync();
            log($"[EXEC] spotify like: contains-check failed — {ExplainError((int)containsResp.StatusCode, cb, "user-library-read")}");
            // don't give up: still attempt the write below, its own error is the useful one
        }

        // The ids-in-body form with an explicit Content-Type, per Spotify's docs — more
        // reliable than the ?ids= query param on PUT/DELETE (which some setups reject).
        var method = alreadyLiked ? HttpMethod.Delete : HttpMethod.Put;
        var req = new HttpRequestMessage(method, "me/tracks") { Content = JsonBody(new { ids = new[] { trackId } }) };
        var resp = await http.SendAsync(req);
        NoteLibrary(resp);
        if (resp.IsSuccessStatusCode)
        {
            log($"[EXEC] spotify like -> {(alreadyLiked ? "removed" : "added")} ({trackId})");
        }
        else
        {
            string body = await resp.Content.ReadAsStringAsync();
            log($"[EXEC] spotify like toggle: {ExplainError((int)resp.StatusCode, body, "user-library-modify")}  [{body}]");
        }
    }, log, "like toggle");

    public static bool ShuffleToggle(string deviceId, Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        bool current = playback?.RootElement.TryGetProperty("shuffle_state", out var ss) == true && ss.GetBoolean();
        var resp = await http.PutAsync(Dev($"me/player/shuffle?state={(!current).ToString().ToLowerInvariant()}", deviceId), null);
        await NotePlayback(resp, log, "shuffle toggle");
    }, log, "shuffle toggle", needsPremium: true);

    /// <summary>Cycles off → track → context → off, matching real Base Camp's own decompiled
    /// <c>SpotifyClientHelper.SongRepeateStatus</c> order.</summary>
    public static bool RepeatCycle(string deviceId, Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        string current = playback?.RootElement.TryGetProperty("repeat_state", out var rs) == true ? rs.GetString() ?? "off" : "off";
        string next = current switch { "off" => "track", "track" => "context", _ => "off" };
        var resp = await http.PutAsync(Dev($"me/player/repeat?state={next}", deviceId), null);
        await NotePlayback(resp, log, "repeat cycle");
    }, log, "repeat cycle", needsPremium: true);

    /// <summary>Session-only "volume before mute" memory (matches real Base Camp's own
    /// in-process-only <c>GlobalVarables.Volume_beforeMute</c> — not persisted there either).</summary>
    private static int _volumeBeforeMute = 50;

    public static bool MuteToggle(string deviceId, Action<string> log) => Run(async http =>
    {
        using var playback = await GetPlaybackAsync(http);
        int current = playback?.RootElement.TryGetProperty("device", out var dev) == true
            && dev.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 0;
        int target;
        if (current > 0) { _volumeBeforeMute = current; target = 0; }
        else target = _volumeBeforeMute > 0 ? _volumeBeforeMute : 50;

        var resp = await http.PutAsync(Dev($"me/player/volume?volume_percent={target}", deviceId), null);
        await NotePlayback(resp, log, "mute toggle");
    }, log, "mute toggle", needsPremium: true);

    private static async Task<int> GetCurrentVolumeAsync(HttpClient http)
    {
        using var playback = await GetPlaybackAsync(http);
        return playback?.RootElement.TryGetProperty("device", out var dev) == true
            && dev.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 50;
    }

    public static bool VolumeUp(string stepArg, string deviceId, Action<string> log) => Run(async http =>
    {
        int step = int.TryParse(stepArg, out var s) && s > 0 ? s : 10;
        int target = Math.Min(100, await GetCurrentVolumeAsync(http) + step);
        var resp = await http.PutAsync(Dev($"me/player/volume?volume_percent={target}", deviceId), null);
        await NotePlayback(resp, log, "volume up");
    }, log, "volume up", needsPremium: true);

    public static bool VolumeDown(string stepArg, string deviceId, Action<string> log) => Run(async http =>
    {
        int step = int.TryParse(stepArg, out var s) && s > 0 ? s : 10;
        int target = Math.Max(0, await GetCurrentVolumeAsync(http) - step);
        var resp = await http.PutAsync(Dev($"me/player/volume?volume_percent={target}", deviceId), null);
        await NotePlayback(resp, log, "volume down");
    }, log, "volume down", needsPremium: true);

    public static bool VolumeSet(string percentArg, string deviceId, Action<string> log) => Run(async http =>
    {
        int target = Math.Clamp(int.TryParse(percentArg, out var p) ? p : 50, 0, 100);
        var resp = await http.PutAsync(Dev($"me/player/volume?volume_percent={target}", deviceId), null);
        await NotePlayback(resp, log, "volume set");
    }, log, "volume set", needsPremium: true);

    public static bool SaveToPlaylist(string playlistId, Action<string> log) => Run(async http =>
    {
        if (string.IsNullOrWhiteSpace(playlistId)) { log("[EXEC] spotify save to playlist: no playlist id"); return; }
        var (_, uri) = await ResolveCurrentTrackAsync(http, log);
        if (string.IsNullOrEmpty(uri)) return; // already logged

        var resp = await http.PostAsync($"playlists/{playlistId}/tracks", JsonBody(new { uris = new[] { uri } }));
        NoteLibrary(resp);
        if (resp.IsSuccessStatusCode) log("[EXEC] spotify save to playlist: ok");
        else { string b = await resp.Content.ReadAsStringAsync(); log($"[EXEC] spotify save to playlist: {ExplainError((int)resp.StatusCode, b, "playlist-modify-private")}  [{b}]"); }
    }, log, "save to playlist");

    public static bool RemoveFromPlaylist(string playlistId, Action<string> log) => Run(async http =>
    {
        if (string.IsNullOrWhiteSpace(playlistId)) { log("[EXEC] spotify remove from playlist: no playlist id"); return; }
        var (_, uri) = await ResolveCurrentTrackAsync(http, log);
        if (string.IsNullOrEmpty(uri)) return; // already logged

        var req = new HttpRequestMessage(HttpMethod.Delete, $"playlists/{playlistId}/tracks")
        {
            Content = JsonBody(new { tracks = new[] { new { uri } } }),
        };
        var resp = await http.SendAsync(req);
        NoteLibrary(resp);
        if (resp.IsSuccessStatusCode) log("[EXEC] spotify remove from playlist: ok");
        else { string b = await resp.Content.ReadAsStringAsync(); log($"[EXEC] spotify remove from playlist: {ExplainError((int)resp.StatusCode, b, "playlist-modify-private")}  [{b}]"); }
    }, log, "remove from playlist");
}
