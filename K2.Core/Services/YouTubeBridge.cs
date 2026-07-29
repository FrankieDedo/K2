using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace K2.Core.Services;

/// <summary>
/// YouTube integration via the official Google API client (<c>Google.Apis.YouTube.v3</c>/
/// <c>Google.Apis.Auth</c>). Only one real action exists in real Base Camp's own Youtube
/// support (<c>_reference/decompiled/Worker/DisplayPadWorker.Helpers/YouTubeHelper.cs</c>) —
/// sending a Live Chat message — "viewers" there is a display-only widget, not something to
/// bind to a keypress.
///
/// <see cref="GoogleWebAuthorizationBroker.AuthorizeAsync"/> handles the entire OAuth
/// loopback-listener dance internally (unlike Twitch, which needed a hand-rolled
/// <see cref="TwitchAuth"/>) and is idempotent: once a valid token is cached in
/// <see cref="YouTubeStore.TokenCacheDir"/>, calling it again just returns the cached
/// credential (refreshing if needed) without reopening a browser.
/// </summary>
public static class YouTubeBridge
{
    /// <summary>Authorizes (prompting the browser only if not already cached) and remembers the
    /// signed-in channel's title. Returns an error message, or null on success.</summary>
    public static async Task<string?> ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(YouTubeStore.ClientId) || string.IsNullOrWhiteSpace(YouTubeStore.ClientSecret))
            return "Client ID/Secret not set";
        try
        {
            var service = await GetServiceAsync();
            if (service is null) return "Authorization failed";

            var request = service.Channels.List("snippet");
            request.Mine = true;
            var channels = await request.ExecuteAsync().ConfigureAwait(false);
            string title = channels.Items?.FirstOrDefault()?.Snippet?.Title ?? "";
            YouTubeStore.SetConnected(true, title);
            return null;
        }
        catch (Exception ex) { return $"Youtube connect error: {ex.Message}"; }
    }

    public static void Disconnect()
    {
        YouTubeStore.SetConnected(false);
        try { System.IO.Directory.Delete(YouTubeStore.TokenCacheDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static async Task<YouTubeService?> GetServiceAsync()
    {
        if (string.IsNullOrWhiteSpace(YouTubeStore.ClientId) || string.IsNullOrWhiteSpace(YouTubeStore.ClientSecret))
            return null;

        UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = YouTubeStore.ClientId, ClientSecret = YouTubeStore.ClientSecret },
            new[] { YouTubeService.Scope.Youtube, YouTubeService.Scope.YoutubeForceSsl },
            "k2user",
            CancellationToken.None,
            new DpapiFileDataStore(YouTubeStore.TokenCacheDir)).ConfigureAwait(false);

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "K2",
        });
    }

    /// <summary>Sends a message to the active live broadcast's chat. Blocks on the underlying
    /// async calls — same synchronous-from-the-UI-thread shape as <see cref="ObsBridge"/>/
    /// <see cref="TwitchBridge"/>, acceptable for a user-triggered keypress.</summary>
    public static bool SendLiveChatMessage(string text, Action<string> log)
    {
        if (!YouTubeStore.Connected) { log("[EXEC] youtube: not connected"); return false; }
        try
        {
            var service = GetServiceAsync().GetAwaiter().GetResult();
            if (service is null) { log("[EXEC] youtube: authorization failed"); return false; }

            var liveChatId = ResolveActiveLiveChatId(service);
            if (string.IsNullOrEmpty(liveChatId)) { log("[EXEC] youtube: no active live broadcast"); return false; }

            var message = new LiveChatMessage
            {
                Snippet = new LiveChatMessageSnippet
                {
                    LiveChatId = liveChatId,
                    Type = "textMessageEvent",
                    TextMessageDetails = new LiveChatTextMessageDetails { MessageText = text },
                },
            };
            service.LiveChatMessages.Insert(message, "snippet").Execute();
            log($"[EXEC] youtube -> chat message \"{text}\"");
            return true;
        }
        catch (Exception ex) { log($"[EXEC] youtube error: {ex.Message}"); return false; }
    }

    private static string? ResolveActiveLiveChatId(YouTubeService service)
    {
        var request = service.LiveBroadcasts.List("snippet");
        request.Mine = true;
        request.BroadcastStatus = LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Active;
        var response = request.Execute();
        return response.Items?.FirstOrDefault()?.Snippet?.LiveChatId;
    }
}
