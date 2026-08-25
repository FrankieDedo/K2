using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace K2.Core.Services;

/// <summary>
/// Discord OAuth2 for K2's Discord action. Unlike Twitch/Youtube there is no browser round
/// trip: the authorization prompt is shown by the Discord DESKTOP client itself, over the RPC
/// pipe (<c>AUTHORIZE</c> command), and the code comes straight back through it — so no local
/// <c>HttpListener</c> is needed. The code is then exchanged for tokens against the normal
/// REST token endpoint, which DOES require a <c>redirect_uri</c> matching one registered on
/// the app even though nothing is ever redirected there; <see cref="RedirectUri"/> is the
/// fixed value K2 sends, and the setup hint tells the user to register exactly that.
///
/// K2 has no shared registered Discord app: the RPC voice scopes are whitelist-only for public
/// apps but always available to the app's own owner, so the user registers their own app at
/// discord.com/developers and pastes Client ID/Secret into <see cref="DiscordSettingsWindow"/>
/// — same "bring your own app" model as <see cref="TwitchAuth"/>.
/// </summary>
public static class DiscordAuth
{
    /// <summary>Must be registered as a redirect URI on the user's own Discord application —
    /// the token exchange rejects the request otherwise (nothing ever listens on it).</summary>
    public const string RedirectUri = "http://localhost:17564/callback";

    private const string Scopes = "rpc rpc.voice.read rpc.voice.write identify";

    /// <summary>Runs the full flow: opens the RPC pipe, asks the Discord client to show the
    /// authorization prompt, exchanges the returned code for tokens, authenticates the RPC
    /// connection and saves everything via <see cref="DiscordStore"/>. Returns an error
    /// message, or null on success.</summary>
    public static async Task<string?> ConnectAsync()
    {
        string clientId = DiscordStore.ClientId;
        string clientSecret = DiscordStore.ClientSecret;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return "Client ID/Secret not set";

        DiscordBridge.Log?.Invoke($"[Discord] connect: opening RPC pipe (client {clientId})");
        var ipc = DiscordBridge.EnsureIpc(out var openError);
        if (ipc is null) { DiscordBridge.Log?.Invoke($"[Discord] connect: {openError}"); return openError; }

        // The user has to click "Authorize" inside Discord — generous timeout, off the UI thread.
        string? code = null, authError = null;
        await Task.Run(() =>
        {
            var data = ipc.Send("AUTHORIZE", new { client_id = clientId, scopes = Scopes.Split(' ') },
                                TimeSpan.FromMinutes(3), out authError);
            if (data is { } d && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("code", out var c))
                code = c.GetString();
        });
        if (string.IsNullOrEmpty(code))
        {
            DiscordBridge.Log?.Invoke($"[Discord] AUTHORIZE failed: {authError ?? "no code returned"}");
            return authError ?? "Discord did not return an authorization code";
        }
        DiscordBridge.Log?.Invoke("[Discord] AUTHORIZE ok, exchanging the code for a token");

        try
        {
            using var http = new HttpClient();
            var tokenResp = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code!),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            }));
            string tokenBody = await tokenResp.Content.ReadAsStringAsync();
            if (!tokenResp.IsSuccessStatusCode)
            {
                // Discord's body names the real cause (invalid_client / invalid_grant /
                // invalid_scope / redirect_uri mismatch) — a bare status code sends the user
                // guessing, so it goes straight into the message and the log.
                string detail = $"Token exchange failed: {(int)tokenResp.StatusCode} {tokenResp.StatusCode} — {tokenBody.Trim()}"
                    + $" (Client Secret correct? Is {RedirectUri} registered under OAuth2 > Redirects?)";
                DiscordBridge.Log?.Invoke($"[Discord] {detail}");
                return detail;
            }

            using var doc = JsonDocument.Parse(tokenBody);
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 604800;

            string userName = "";
            await Task.Run(() => DiscordBridge.Authenticate(accessToken, out userName, out authError));
            if (authError is not null)
            {
                DiscordBridge.Log?.Invoke($"[Discord] AUTHENTICATE failed: {authError}");
                return $"Discord refused the token (AUTHENTICATE): {authError}";
            }

            DiscordStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), userName);
            DiscordBridge.Log?.Invoke($"[Discord] connected as {userName}");
            return null;
        }
        catch (Exception ex)
        {
            DiscordBridge.Log?.Invoke($"[Discord] connect error: {ex}");
            return $"Discord connect error: {ex.Message}";
        }
    }

    /// <summary>Refreshes the access token if it's expired (or about to). Returns true if a
    /// valid token is available afterward.</summary>
    public static async Task<bool> EnsureFreshTokenAsync()
    {
        if (!DiscordStore.IsConnected) return false;
        if (DateTime.UtcNow < DiscordStore.ExpiresAtUtc) return true;
        if (string.IsNullOrEmpty(DiscordStore.RefreshToken)) return false;

        try
        {
            using var http = new HttpClient();
            var resp = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", DiscordStore.ClientId),
                new KeyValuePair<string, string>("client_secret", DiscordStore.ClientSecret),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", DiscordStore.RefreshToken),
            }));
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() ?? DiscordStore.RefreshToken : DiscordStore.RefreshToken;
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 604800;

            DiscordStore.SetTokens(accessToken, refreshToken, DateTime.UtcNow.AddSeconds(expiresIn - 60), DiscordStore.UserName);
            return true;
        }
        catch { return false; }
    }
}
