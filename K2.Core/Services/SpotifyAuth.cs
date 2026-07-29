using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Spotify OAuth Authorization Code flow for K2's Spotify action — same hand-rolled shape as
/// <see cref="TwitchAuth"/> (Spotify's own .NET SDKs are third-party/unofficial, and the flow
/// itself is just a standard loopback redirect + Basic-auth token exchange, not worth a
/// dependency for). Confirmed against real Base Camp's own decompiled
/// <c>BaseCamp.Spotify.SpotifyClient.Get_Token_from_RefreshToken</c> (token endpoint + Basic
/// auth header shape) — Mountain's own Client ID/Secret baked in there is THEIRS, not reused
/// here; the user registers their own app instead, same "bring your own app" model as Twitch.
/// </summary>
public static class SpotifyAuth
{
    public const int LoopbackPort = 17564;
    public static string RedirectUri => $"http://127.0.0.1:{LoopbackPort}/callback";

    private const string Scopes = "user-read-playback-state user-modify-playback-state "
        + "user-library-read user-library-modify playlist-modify-public playlist-modify-private";

    /// <summary>Runs the full flow: opens the system browser for the user to approve, waits for
    /// the loopback redirect, exchanges the code for tokens, resolves the account's display
    /// name, and saves everything via <see cref="SpotifyStore"/>. Returns an error message, or
    /// null on success.</summary>
    public static async Task<string?> ConnectAsync()
    {
        string clientId = SpotifyStore.ClientId;
        string clientSecret = SpotifyStore.ClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return "Client ID/Secret not set";

        string state = Guid.NewGuid().ToString("N");
        string authorizeUrl = "https://accounts.spotify.com/authorize"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(Scopes)}"
            + $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{LoopbackPort}/");
        try { listener.Start(); }
        catch (Exception ex) { return $"Could not start local listener on port {LoopbackPort}: {ex.Message}"; }

        Process.Start(new ProcessStartInfo { FileName = authorizeUrl, UseShellExecute = true });

        string? code = null;
        try
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3)));
            if (completed != contextTask) return "Timed out waiting for Spotify authorization";

            var context = contextTask.Result;
            var query = context.Request.QueryString;
            if (query["state"] != state)
            {
                RespondBrowser(context, false);
                return "State mismatch (possible CSRF) — try again";
            }
            code = query["code"];
            RespondBrowser(context, code is not null);
        }
        finally { listener.Stop(); }

        if (string.IsNullOrEmpty(code)) return "Spotify did not return an authorization code";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

            var tokenResp = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("code", code),
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "authorization_code"),
                new System.Collections.Generic.KeyValuePair<string, string>("redirect_uri", RedirectUri),
            }));
            if (!tokenResp.IsSuccessStatusCode) return $"Token exchange failed: {tokenResp.StatusCode}";

            using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
            string accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            int expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            using var meHttp = new HttpClient();
            meHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var meResp = await meHttp.GetAsync("https://api.spotify.com/v1/me");
            string displayName = "";
            if (meResp.IsSuccessStatusCode)
            {
                using var meDoc = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync());
                displayName = meDoc.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
            }

            SpotifyStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), displayName);
            return null;
        }
        catch (Exception ex) { return $"Spotify connect error: {ex.Message}"; }
    }

    /// <summary>Refreshes the access token if it's expired (or about to). Returns true if a
    /// valid token is available afterward.</summary>
    public static async Task<bool> EnsureFreshTokenAsync()
    {
        if (!SpotifyStore.IsConnected) return false;
        if (DateTime.UtcNow < SpotifyStore.ExpiresAtUtc) return true;

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{SpotifyStore.ClientId}:{SpotifyStore.ClientSecret}")));

            var resp = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("refresh_token", SpotifyStore.RefreshToken),
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "refresh_token"),
            }));
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            // Spotify doesn't always return a new refresh_token on refresh — keep the old one when absent.
            string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? SpotifyStore.RefreshToken : SpotifyStore.RefreshToken;
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            SpotifyStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), SpotifyStore.DisplayName);
            return true;
        }
        catch { return false; }
    }

    private static void RespondBrowser(HttpListenerContext context, bool ok)
    {
        try
        {
            string html = ok
                ? "<html><body style='font-family:sans-serif'>K2: Spotify connected — you can close this tab.</body></html>"
                : "<html><body style='font-family:sans-serif'>K2: Spotify authorization failed — you can close this tab.</body></html>";
            var buffer = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch { /* best-effort */ }
        finally { context.Response.Close(); }
    }
}
