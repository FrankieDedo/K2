using System;
using System.Diagnostics;
using System.Linq;
using TwitchLib.Api;
using TwitchLib.Api.Helix.Models.Channels.ModifyChannelInformation;
using TwitchLib.Api.Helix.Models.Channels.SendChatMessage;
using TwitchLib.Api.Helix.Models.Channels.StartCommercial;
using TwitchLib.Api.Helix.Models.Chat.ChatSettings;
using TwitchLib.Api.Helix.Models.Streams.CreateStreamMarker;

namespace K2.Core.Services;

/// <summary>
/// Twitch integration via TwitchLib.Api's Helix REST wrapper, using the account connected
/// through <see cref="TwitchAuth"/>/<see cref="TwitchStore"/>. Covers the actionable subset of
/// real Base Camp's Twitch actions (<c>_reference/decompiled/Worker/DisplayPadWorker.Helpers/
/// OtherDeviceOperations.cs</c>'s Twitch <c>SubFunctionType</c> chain) — "viewers" was a
/// display-only widget there, not something to bind to a keypress, so it's not ported.
///
/// Every method blocks on its underlying Task (same synchronous-from-the-UI-thread shape as
/// <see cref="ObsBridge"/>'s connect busy-wait) since <c>ButtonActionEngine.Execute</c> is
/// documented as UI-thread-only and callers already accept a brief block for a network round
/// trip — acceptable for a user-triggered keypress, not a hot path.
/// </summary>
public static class TwitchBridge
{
    private static TwitchAPI CreateClient()
    {
        var api = new TwitchAPI();
        api.Settings.ClientId = TwitchStore.ClientId;
        api.Settings.AccessToken = TwitchStore.AccessToken;
        return api;
    }

    /// <summary>Refreshes the token if needed, then runs <paramref name="action"/>. Returns
    /// false without running it if not connected or the refresh failed.</summary>
    private static bool Run(Action<TwitchAPI, string> action, Action<string> log, string opName)
    {
        if (!TwitchStore.IsConnected) { log($"[EXEC] twitch: not connected"); return false; }
        if (!TwitchAuth.EnsureFreshTokenAsync().GetAwaiter().GetResult())
        {
            log("[EXEC] twitch: token refresh failed"); return false;
        }
        try
        {
            action(CreateClient(), TwitchStore.BroadcasterUserId);
            return true;
        }
        catch (Exception ex)
        {
            log($"[EXEC] twitch {opName} error: {ex.Message}");
            return false;
        }
    }

    public static bool SendChatMessage(string message, Action<string> log) => Run((api, userId) =>
        api.Helix.Chat.SendChatMessage(new SendChatMessageRequest
        {
            BroadcasterId = userId,
            SenderId = userId,
            Message = message,
        }).GetAwaiter().GetResult(),
        log, "chat message");

    public static bool ClearChat(Action<string> log) => Run((api, userId) =>
        api.Helix.Moderation.DeleteChatMessagesAsync(userId, userId).GetAwaiter().GetResult(),
        log, "clear chat");

    public static bool ToggleEmoteOnly(Action<string> log) => Run((api, userId) =>
    {
        var current = api.Helix.Chat.GetChatSettingsAsync(userId, userId).GetAwaiter().GetResult();
        bool next = current.Data.Length == 0 || current.Data[0].EmoteMode != true;
        api.Helix.Chat.UpdateChatSettingsAsync(userId, userId, new ChatSettings { EmoteMode = next }).GetAwaiter().GetResult();
    }, log, "emote-only toggle");

    public static bool ToggleSubscribersOnly(Action<string> log) => Run((api, userId) =>
    {
        var current = api.Helix.Chat.GetChatSettingsAsync(userId, userId).GetAwaiter().GetResult();
        bool next = current.Data.Length == 0 || current.Data[0].SubscriberMode != true;
        api.Helix.Chat.UpdateChatSettingsAsync(userId, userId, new ChatSettings { SubscriberMode = next }).GetAwaiter().GetResult();
    }, log, "subscribers-only toggle");

    /// <summary>Enables followers-only mode with the given minimum-follow-time in minutes (0 =
    /// disable). Matches real Base Camp's <c>Twitch_Followers(FunctionValue)</c> arg shape.</summary>
    public static bool SetFollowersOnly(string minutesArg, Action<string> log) => Run((api, userId) =>
    {
        int minutes = int.TryParse(minutesArg, out var m) ? m : 0;
        api.Helix.Chat.UpdateChatSettingsAsync(userId, userId, new ChatSettings
        {
            FollowerMode = minutes > 0,
            FollowerModeDuration = minutes,
        }).GetAwaiter().GetResult();
    }, log, "followers-only");

    /// <summary>Enables slow mode with the given wait time in seconds (0 = disable). Matches
    /// real Base Camp's <c>Twitch_SlowModeOn(FunctionValue)</c> arg shape.</summary>
    public static bool SetSlowMode(string secondsArg, Action<string> log) => Run((api, userId) =>
    {
        int seconds = int.TryParse(secondsArg, out var s) ? s : 0;
        api.Helix.Chat.UpdateChatSettingsAsync(userId, userId, new ChatSettings
        {
            SlowMode = seconds > 0,
            SlowModeWaitTime = seconds,
        }).GetAwaiter().GetResult();
    }, log, "slow mode");

    /// <summary>Runs a commercial break for the given length in seconds (Twitch only accepts
    /// specific lengths — 30/60/90/120/150/180 — invalid values are rejected by Twitch itself).</summary>
    public static bool PlayAd(string secondsArg, Action<string> log) => Run((api, userId) =>
    {
        int seconds = int.TryParse(secondsArg, out var s) && s > 0 ? s : 60;
        api.Helix.Channels.StartCommercialAsync(new StartCommercialRequest { BroadcasterId = userId, Length = seconds })
            .GetAwaiter().GetResult();
    }, log, "play ad");

    public static bool SetStreamTitle(string title, Action<string> log) => Run((api, userId) =>
        api.Helix.Channels.ModifyChannelInformationAsync(userId, new ModifyChannelInformationRequest { Title = title })
            .GetAwaiter().GetResult(),
        log, "set stream title");

    public static bool CreateStreamMarker(Action<string> log) => Run((api, userId) =>
        api.Helix.Streams.CreateStreamMarkerAsync(new CreateStreamMarkerRequest { UserId = userId })
            .GetAwaiter().GetResult(),
        log, "create stream marker");

    public static bool CreateClip(Action<string> log) => Run((api, userId) =>
        api.Helix.Clips.CreateClipAsync(userId).GetAwaiter().GetResult(),
        log, "create clip");

    /// <summary>Opens the most recent clip in the system browser.</summary>
    public static bool OpenLastClip(Action<string> log) => Run((api, userId) =>
    {
        var clips = api.Helix.Clips.GetClipsAsync(broadcasterId: userId, first: 1).GetAwaiter().GetResult();
        var url = clips.Clips.FirstOrDefault()?.Url;
        if (string.IsNullOrEmpty(url)) { log("[EXEC] twitch: no clips found"); return; }
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }, log, "open last clip");
}
