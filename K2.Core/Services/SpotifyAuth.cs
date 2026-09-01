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

    // user-read-private is what makes GET /me return the "product" field (premium/free) —
    // without it the profile comes back public-only and SpotifyStore.IsPremium can't be set,
    // so the non-Premium media-key fallback never engages. Adding a scope invalidates existing
    // tokens: the user must reconnect once after updating.
    // user-read-email: so the settings window can show the EXACT account email the user must
    // paste into the app's "User Management" allow-list on the Spotify dashboard.
    private const string Scopes = "user-read-playback-state user-modify-playback-state "
        + "user-read-private user-read-email user-library-read user-library-modify "
        + "playlist-modify-public playlist-modify-private";

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
            // Force the consent screen every time: without it Spotify silently reuses a
            // previous authorization, so a token granted before a scope was added to
            // Scopes never picks the new one up (root cause of "Like" 403ing on
            // me/tracks even after a reconnect — the old grant lacked user-library-*).
            + "&show_dialog=true"
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
            // Browser consent succeeding is NOT the same as this exchange succeeding — Spotify's
            // authorize step only asks the user to approve the CLIENT ID; the code-for-token
            // swap right here is a separate server-to-server call authenticated with the
            // Client ID/Secret pair, and a mismatched or rotated Secret fails HERE while the
            // browser still shows its own success page (user report 2026-09-01: "su browser va
            // a buon fine, ma su K2 risulto non connesso"). The status code alone
            // ("Token exchange failed: BadRequest") gave no way to tell that apart from a typo'd
            // Client ID or a stale redirect URI — the body names the real reason
            // (invalid_client = Secret/ID mismatch, invalid_grant = code already used/expired).
            if (!tokenResp.IsSuccessStatusCode)
                return $"Token exchange failed: HTTP {(int)tokenResp.StatusCode} {await SafeReadAsync(tokenResp).ConfigureAwait(false)}";

            using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
            string accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            int expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            using var meHttp = new HttpClient();
            meHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var meResp = await meHttp.GetAsync("https://api.spotify.com/v1/me");
            string displayName = "";
            string product = "";
            string email = "";
            if (meResp.IsSuccessStatusCode)
            {
                using var meDoc = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync());
                displayName = meDoc.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
                product = meDoc.RootElement.TryGetProperty("product", out var pr) ? pr.GetString() ?? "" : "";
                email = meDoc.RootElement.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "";
            }

            SpotifyStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), displayName);
            SpotifyStore.SetProduct(product); // gates the Web API playback/volume actions for free accounts
            SpotifyStore.SetAccountEmail(email); // shown in settings so the user can allow-list it on the dashboard
            SpotifyStore.SetGrantedScopes(tokenDoc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null);

            // Probe the library right now so the settings window can warn immediately (instead of
            // the user discovering it only when a "Like" key silently 403s). A 403 here = the
            // account isn't reachable for library/playlist calls — allow-list it under "User
            // Management" on the dashboard, or the app itself is restricted.
            try
            {
                var probe = await meHttp.GetAsync("https://api.spotify.com/v1/me/tracks?limit=1");
                SpotifyStore.SetLibraryAccessOk((int)probe.StatusCode != 403);
            }
            catch { SpotifyStore.SetLibraryAccessOk(true); /* network hiccup, don't cry wolf */ }
            return null;
        }
        catch (Exception ex) { return $"Spotify connect error: {ex.Message}"; }
    }

    /// <summary>Refreshes the access token if it's expired (or about to). Returns true if a
    /// valid token is available afterward.</summary>
    /// <param name="log">Optional — when given, a failed refresh reports Spotify's own error
    /// body (typically <c>invalid_grant</c> — the refresh token was revoked/expired — or
    /// <c>invalid_client</c> — the stored Client ID/Secret no longer matches the dashboard app)
    /// instead of silently returning false. Every caller went through <c>Run</c>'s generic
    /// "token refresh failed" log line before 2026-09-01, which gave no way to tell an actual
    /// account problem from a transient network hiccup (user report: commands stopped working,
    /// nothing in the log said why).</param>
    public static async Task<bool> EnsureFreshTokenAsync(Action<string>? log = null)
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
            if (!resp.IsSuccessStatusCode)
            {
                string body = await SafeReadAsync(resp).ConfigureAwait(false);
                log?.Invoke($"[Spotify] token refresh failed: HTTP {(int)resp.StatusCode} {body}");
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            // Spotify doesn't always return a new refresh_token on refresh — keep the old one when absent.
            string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? SpotifyStore.RefreshToken : SpotifyStore.RefreshToken;
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            SpotifyStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), SpotifyStore.DisplayName);
            // A refresh keeps the original grant's scopes; record them so a stale-grant
            // account (missing a scope added since) can be told to reconnect.
            SpotifyStore.SetGrantedScopes(doc.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() : null);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Spotify] token refresh threw: {ex.Message}");
            return false;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { return "(no body)"; }
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
