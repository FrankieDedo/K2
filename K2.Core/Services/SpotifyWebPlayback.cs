using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Read-only "what is playing right now" over the Spotify Web API (<c>GET /me/player</c>),
/// for the DisplayPad "Spotify" dedicated profile when its source is set to
/// <see cref="SpotifyCoverSource.WebApi"/>. Unlike <see cref="SpotifyBridge"/> (fire-and-forget
/// transport commands) this is <b>awaited</b> — the caller is a background refresh, not a
/// keypress — and mirrors <see cref="SpotifyBridge.GetDevicesAsync"/>'s auth/client shape.
///
/// Returns <c>null</c> for every "no data" case (not connected, token refresh failed,
/// HTTP 204 "nothing playing", any error) so the caller can fall back to the local SMTC
/// source without inspecting why.
/// </summary>
/// <summary>Spotify's own 3-way repeat state (<c>GET /me/player</c>'s <c>repeat_state</c> /
/// <c>PUT /me/player/repeat</c>'s <c>state</c> query param): <see cref="Off"/>, <see cref="Track"/>
/// (repeat the current song), <see cref="Context"/> (repeat the playlist/album/queue).</summary>
public enum SpotifyRepeatMode { Off, Track, Context }

public static class SpotifyWebPlayback
{
    public readonly record struct NowPlaying(string? Title, string? Artist, string? Album, string? ArtUrl,
        bool IsPlaying = false, bool ShuffleOn = false, SpotifyRepeatMode RepeatMode = SpotifyRepeatMode.Off);

    public static async Task<NowPlaying?> GetNowPlayingAsync(Action<string> log)
    {
        if (!SpotifyStore.IsConnected) return null;
        if (!await SpotifyAuth.EnsureFreshTokenAsync(log).ConfigureAwait(false))
        {
            log("[Spotify] web now-playing: token refresh failed");
            return null;
        }

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("https://api.spotify.com/v1/") };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SpotifyStore.AccessToken);

            var resp = await http.GetAsync("me/player").ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
                return null;

            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return null;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                return null;

            string? title = item.TryGetProperty("name", out var n) ? n.GetString() : null;

            string? artist = null;
            if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                artist = string.Join(", ", artists.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

            string? album = null, artUrl = null;
            if (item.TryGetProperty("album", out var alb) && alb.ValueKind == JsonValueKind.Object)
            {
                album = alb.TryGetProperty("name", out var albn) ? albn.GetString() : null;
                if (alb.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array
                    && imgs.GetArrayLength() > 0
                    && imgs[0].TryGetProperty("url", out var u))
                    artUrl = u.GetString();
            }

            bool isPlaying = doc.RootElement.TryGetProperty("is_playing", out var ip) && ip.ValueKind == JsonValueKind.True;
            bool shuffleOn = doc.RootElement.TryGetProperty("shuffle_state", out var sh) && sh.ValueKind == JsonValueKind.True;
            string? repeatStr = doc.RootElement.TryGetProperty("repeat_state", out var rp) ? rp.GetString() : null;
            var repeat = repeatStr switch
            {
                "track"   => SpotifyRepeatMode.Track,
                "context" => SpotifyRepeatMode.Context,
                _         => SpotifyRepeatMode.Off,
            };

            return new NowPlaying(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(artist) ? null : artist,
                string.IsNullOrWhiteSpace(album) ? null : album,
                string.IsNullOrWhiteSpace(artUrl) ? null : artUrl,
                isPlaying, shuffleOn, repeat);
        }
        catch (Exception ex)
        {
            log($"[Spotify] web now-playing error: {ex.Message}");
            return null;
        }
    }
}
