using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace K2.Core.Services;

/// <summary>
/// Wraps the Windows System Media Transport Controls (SMTC) session for
/// Spotify: exposes now-playing thumbnail/track-change notifications and
/// playback control (play/pause/next/prev/shuffle), without needing
/// Spotify's own Web API/OAuth. SMTC is push-based (MediaPropertiesChanged)
/// and works for any app that reports "now playing" to Windows, so we
/// filter sessions by SourceAppUserModelId containing "spotify".
/// </summary>
public sealed class SpotifyMediaService
{
    public static SpotifyMediaService Instance { get; } = new();

    public event Action? TrackChanged;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Task? _initTask;
    private readonly object _gate = new();

    private SpotifyMediaService() { }

    /// <summary>Resolves the SMTC session manager (once) and (re)resolves the Spotify session
    /// every call. Concurrent callers all await the SAME manager-init task rather than racing on
    /// a plain "already started" bool: <see cref="K2.App.Services.SpotifyCoverService"/>.Start
    /// fires this un-awaited AND immediately fires an awaited refresh that calls back into this
    /// same method — with a bool guard, the refresh's call could see "already started" while the
    /// FIRST call's <c>RequestAsync()</c> was still in flight and <c>_manager</c> was still null,
    /// so it skipped resolving the session and read back nothing. That raced blank read is what
    /// showed as "K2 doesn't refresh Song/Artist/Album right away when Spotify was already
    /// playing" (user report 2026-09-01) — a real SMTC session takes long enough to hand back
    /// that the window was easy to hit on every K2 startup with Spotify already open.</summary>
    public async Task EnsureStartedAsync()
    {
        Task init;
        lock (_gate) init = _initTask ??= InitManagerAsync();
        await init.ConfigureAwait(false);

        // Always (re)resolve: Spotify may have been launched since the last call and a
        // SessionsChanged event can be missed — otherwise a first "Like" press right after
        // opening Spotify finds no session and silently does nothing. Runs AFTER init is
        // guaranteed complete for every caller, not just the one that happened to start it.
        if (_manager is not null && _session is null) ResolveSpotifySession();
    }

    private async Task InitManagerAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, __) => ResolveSpotifySession();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] SMTC init failed: {ex.Message}");
        }
    }

    private void ResolveSpotifySession()
    {
        if (_manager is null) return;

        GlobalSystemMediaTransportControlsSession? found = null;
        try
        {
            found = _manager.GetSessions()
                .FirstOrDefault(s => (s.SourceAppUserModelId ?? "")
                    .Contains("spotify", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] GetSessions failed: {ex.Message}");
        }

        if (ReferenceEquals(found, _session)) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = found;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            // Play/pause/shuffle/repeat toggles don't change the TRACK, only PlaybackInfo — the
            // dedicated profile's control keys need this too, to swap their icon (play↔pause,
            // shuffle/repeat on/off) the moment the user presses one, not just on a track change
            // (user request 2026-09-01).
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            TrackChanged?.Invoke();
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => TrackChanged?.Invoke();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => TrackChanged?.Invoke();

    /// <summary>Play/pause + shuffle + repeat state of the current Windows "now playing" Spotify
    /// track, or the all-off default when there is no session. Used by the dedicated profile's
    /// Play/Pause, Shuffle and Repeat control keys to swap their icon to match reality.</summary>
    public async Task<(bool IsPlaying, bool ShuffleOn, SpotifyRepeatMode RepeatMode)> GetPlaybackStateAsync()
    {
        if (_session is null) await EnsureStartedAsync().ConfigureAwait(false);
        var session = _session;
        if (session is null) return (false, false, SpotifyRepeatMode.Off);
        try
        {
            var info = session.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool shuffle = info?.IsShuffleActive ?? false;
            var repeat = (info?.AutoRepeatMode) switch
            {
                MediaPlaybackAutoRepeatMode.Track => SpotifyRepeatMode.Track,
                MediaPlaybackAutoRepeatMode.List  => SpotifyRepeatMode.Context,
                _                                 => SpotifyRepeatMode.Off,
            };
            return (playing, shuffle, repeat);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] playback-state read failed: {ex.Message}");
            return (false, false, SpotifyRepeatMode.Off);
        }
    }

    /// <summary>Title + artist of the current Windows "now playing" Spotify track, or
    /// (null, null) when there is no Spotify SMTC session. Used to resolve a Spotify track id
    /// via Search when <c>GET /me/player</c> can't (free account / no active Connect device) —
    /// see <see cref="SpotifyBridge"/>'s Like / playlist actions.</summary>
    public async Task<(string? Title, string? Artist)> GetNowPlayingAsync()
    {
        if (_session is null) await EnsureStartedAsync().ConfigureAwait(false);
        var session = _session;
        if (session is null) return (null, null);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            return (props?.Title, props?.Artist);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] now-playing read failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>Title + artist + album of the current Windows "now playing" Spotify track,
    /// or all-null when there is no Spotify SMTC session. Album comes from SMTC's
    /// <c>AlbumTitle</c> — the desktop player fills it in, unlike the Web API it needs no
    /// account. Used by the Spotify dedicated profile's single-cover text tiles.</summary>
    public async Task<(string? Title, string? Artist, string? Album)> GetNowPlayingFullAsync()
    {
        if (_session is null) await EnsureStartedAsync().ConfigureAwait(false);
        var session = _session;
        if (session is null) return (null, null, null);
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            return (props?.Title, props?.Artist, props?.AlbumTitle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] now-playing (full) read failed: {ex.Message}");
            return (null, null, null);
        }
    }

    /// <summary>Current track thumbnail as a decoded stream, or null if no Spotify session
    /// / no thumbnail is available.</summary>
    public async Task<IRandomAccessStreamWithContentType?> GetThumbnailStreamAsync()
    {
        if (_session is null) await EnsureStartedAsync().ConfigureAwait(false);
        if (_session is null) return null;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            var thumb = props?.Thumbnail;
            if (thumb is null) return null;
            return await thumb.OpenReadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] thumbnail read failed: {ex.Message}");
            return null;
        }
    }

    public Task TogglePlayPauseAsync() => RunControlAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task SkipNextAsync() => RunControlAsync(s => s.TrySkipNextAsync().AsTask());
    public Task SkipPreviousAsync() => RunControlAsync(s => s.TrySkipPreviousAsync().AsTask());

    public Task ToggleShuffleAsync() => RunControlAsync(async s =>
    {
        var info = s.GetPlaybackInfo();
        bool current = info?.IsShuffleActive ?? false;
        await s.TryChangeShuffleActiveAsync(!current);
    });

    /// <summary>Cycles the repeat mode None -> Track -> List -> None (same order as
    /// <see cref="SpotifyBridge.RepeatCycle"/>'s off/track/context). Used as the non-Premium
    /// fallback for the "repeat_cycle" Spotify action — there is no system media key for it.</summary>
    public Task CycleRepeatAsync() => RunControlAsync(async s =>
    {
        var current = s.GetPlaybackInfo()?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
        var next = current switch
        {
            MediaPlaybackAutoRepeatMode.None  => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List,
            _                                 => MediaPlaybackAutoRepeatMode.None,
        };
        await s.TryChangeAutoRepeatModeAsync(next);
    });

    private async Task RunControlAsync(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        if (_session is null) await EnsureStartedAsync().ConfigureAwait(false);
        var session = _session;
        if (session is null) return;
        try { await action(session); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] control call failed: {ex.Message}");
        }
    }
}
