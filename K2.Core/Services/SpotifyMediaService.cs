using System;
using System.Linq;
using System.Threading.Tasks;
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
    private bool _started;
    private readonly object _gate = new();

    private SpotifyMediaService() { }

    public async Task EnsureStartedAsync()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, __) => ResolveSpotifySession();
            ResolveSpotifySession();
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
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;

        _session = found;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            TrackChanged?.Invoke();
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => TrackChanged?.Invoke();

    /// <summary>Current track thumbnail as a decoded stream, or null if no Spotify session
    /// / no thumbnail is available.</summary>
    public async Task<IRandomAccessStreamWithContentType?> GetThumbnailStreamAsync()
    {
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

    private async Task RunControlAsync(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        var session = _session;
        if (session is null) return;
        try { await action(session); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spotify] control call failed: {ex.Message}");
        }
    }
}
