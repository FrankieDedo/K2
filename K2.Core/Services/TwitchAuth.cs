using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Twitch OAuth Authorization Code flow for K2's Twitch action — Twitch has no shared "Base
/// Camp" app K2 could reuse (Base Camp's own <c>TwitchClientHelper</c> is closed-source and
/// tied to Mountain's own registered app), so the user registers their own app at
/// dev.twitch.tv with redirect URL <see cref="RedirectUri"/> and pastes the Client ID/Secret
/// into <see cref="TwitchSettingsWindow"/>. The redirect is caught by a short-lived local
/// <see cref="HttpListener"/> on <see cref="LoopbackPort"/> — same idea as
/// <c>GoogleWebAuthorizationBroker</c>'s internal mechanism (used by the Youtube action
/// instead, via the official Google client library), just hand-rolled here since Twitch has
/// no equivalent helper library for this.
/// </summary>
public static class TwitchAuth
{
    public const int LoopbackPort = 17563;
    public static string RedirectUri => $"http://localhost:{LoopbackPort}/callback";

    private const string Scopes = "chat:edit chat:read moderator:manage:chat_messages moderator:manage:chat_settings "
        + "channel:edit:commercial channel:manage:broadcast clips:edit user:read:email";

    /// <summary>Runs the full flow: opens the system browser for the user to approve, waits for
    /// the loopback redirect, exchanges the code for tokens, resolves the account's user id/
    /// login, and saves everything via <see cref="TwitchStore"/>. Returns an error message, or
    /// null on success.</summary>
    public static async Task<string?> ConnectAsync()
    {
        string clientId = TwitchStore.ClientId;
        string clientSecret = TwitchStore.ClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return "Client ID/Secret not set";

        string state = Guid.NewGuid().ToString("N");
        string authorizeUrl = "https://id.twitch.tv/oauth2/authorize"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(Scopes)}"
            + $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{LoopbackPort}/");
        try { listener.Start(); }
        catch (Exception ex) { return $"Could not start local listener on port {LoopbackPort}: {ex.Message}"; }

        Process.Start(new ProcessStartInfo { FileName = authorizeUrl, UseShellExecute = true });

        string? code = null;
        try
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3)));
            if (completed != contextTask) return "Timed out waiting for Twitch authorization";

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

        if (string.IsNullOrEmpty(code)) return "Twitch did not return an authorization code";

        try
        {
            using var http = new HttpClient();
            var tokenResp = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("client_id", clientId),
                new System.Collections.Generic.KeyValuePair<string, string>("client_secret", clientSecret),
                new System.Collections.Generic.KeyValuePair<string, string>("code", code),
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "authorization_code"),
                new System.Collections.Generic.KeyValuePair<string, string>("redirect_uri", RedirectUri),
            }));
            if (!tokenResp.IsSuccessStatusCode) return $"Token exchange failed: {tokenResp.StatusCode}";

            using var tokenDoc = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());
            string accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            int expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            http.DefaultRequestHeaders.Add("Client-Id", clientId);
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var userResp = await http.GetAsync("https://api.twitch.tv/helix/users");
            if (!userResp.IsSuccessStatusCode) return $"Failed to resolve account: {userResp.StatusCode}";

            using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync());
            var first = userDoc.RootElement.GetProperty("data")[0];
            string userId = first.GetProperty("id").GetString() ?? "";
            string login = first.GetProperty("login").GetString() ?? "";

            TwitchStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), userId, login);
            return null;
        }
        catch (Exception ex) { return $"Twitch connect error: {ex.Message}"; }
    }

    /// <summary>Refreshes the access token if it's expired (or about to). Returns true if a
    /// valid token is available afterward.</summary>
    public static async Task<bool> EnsureFreshTokenAsync()
    {
        if (!TwitchStore.IsConnected) return false;
        if (DateTime.UtcNow < TwitchStore.ExpiresAtUtc) return true;

        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("client_id", TwitchStore.ClientId),
                new System.Collections.Generic.KeyValuePair<string, string>("client_secret", TwitchStore.ClientSecret),
                new System.Collections.Generic.KeyValuePair<string, string>("refresh_token", TwitchStore.RefreshToken),
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "refresh_token"),
            }));
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? TwitchStore.RefreshToken : TwitchStore.RefreshToken;
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            TwitchStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), TwitchStore.BroadcasterUserId, TwitchStore.Login);
            return true;
        }
        catch { return false; }
    }

    private static void RespondBrowser(HttpListenerContext context, bool ok)
    {
        try
        {
            string html = ok
                ? "<html><body style='font-family:sans-serif'>K2: Twitch connected — you can close this tab.</body></html>"
                : "<html><body style='font-family:sans-serif'>K2: Twitch authorization failed — you can close this tab.</body></html>";
            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch { /* best-effort */ }
        finally { context.Response.Close(); }
    }
}
