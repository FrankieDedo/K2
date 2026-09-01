using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using K2.Core;
using K2.Core.Services;
using Windows.Storage.Streams;

namespace K2.App.Services;

/// <summary>
/// Drives the DisplayPad "Spotify" dedicated profile's 2×2 cover block — 4 physical keys whose
/// pair of grid columns depends on <see cref="SpotifyCoverConfig.Position"/> (left by default,
/// see <see cref="BlockKeysFor"/>/<see cref="BaseQuadrant"/>; see MainWindow.DisplayPad.cs for
/// the 8 control keys around it). Several things are configurable per pad from that profile's
/// gear ▸ Configure (<see cref="SpotifyCoverConfig"/>, persisted by MainWindow.DisplayPad):
///
/// <list type="bullet">
/// <item><b>Source</b> — <see cref="SpotifyCoverSource.Local"/> reads the current track from
///   the Windows SMTC session (<see cref="SpotifyMediaService"/>, no account needed);
///   <see cref="SpotifyCoverSource.WebApi"/> reads it from the Spotify Web API
///   (<see cref="SpotifyWebPlayback"/>) for the album name + a higher-res cover, falling back
///   to Local whenever the API returns nothing.</item>
/// <item><b>Layout</b> — <see cref="SpotifyCoverLayout.Quad"/> splits the cover across all 4
///   tiles (the original behaviour); <see cref="SpotifyCoverLayout.Single"/> puts the cover on
///   one tile and fills the other three with title / artist / album, either static (wrapped +
///   ellipsis) or scrolling (<see cref="SpotifyTextMode.Marquee"/>, a ~11 fps timer).</item>
/// </list>
///
/// Same integration shape as <see cref="DpSpotifyCoverKeyService"/> / <see cref="DpLiveTileService"/>:
/// a transient overlay pushed straight to the hardware (raw-BGR, file-upload fallback), never
/// persisted in DisplayPadStore. The repaint paths exclude <see cref="Owns"/> keys from their
/// blank pass and call <see cref="Repaint"/> at the TAIL of the upload batch so the profile's
/// blanks can't land on top; the app's own key grid gets a still preview from
/// <see cref="RenderUiPreview"/> / <see cref="UiPreviewPath"/> (a separate file the hardware
/// path never touches), refreshed on <see cref="PreviewChanged"/>.
/// </summary>
internal static class SpotifyCoverService
{
    /// <summary>Raised (with the device id) after the block has been (re)painted — the WPF side
    /// listens to refresh its still preview of keys 0/1/6/7.</summary>
    public static event Action<int>? PreviewChanged;

    /// <summary>The 2×2 block's 4 physical keys (TL,TR,BL,BR at device rotation 0) for each
    /// <see cref="SpotifyCoverPosition"/> — which pair of grid columns it sits in. The 8 control
    /// keys fill whatever is left over; see <c>MainWindow.DpSpotifyLayoutFor</c>.</summary>
    private static (int Tl, int Tr, int Bl, int Br) BaseQuadrant(SpotifyCoverPosition position) => position switch
    {
        SpotifyCoverPosition.Center => (2, 3, 8, 9),
        SpotifyCoverPosition.Right  => (4, 5, 10, 11),
        _                            => (0, 1, 6, 7),
    };

    /// <summary>The block's 4 physical keys, unordered — <see cref="Owns"/>'s set membership
    /// test, independent of rotation. Public so <c>MainWindow.DisplayPad.cs</c> can mark the
    /// same keys "live overlay" and read their per-key icon spec.</summary>
    public static int[] BlockKeysFor(SpotifyCoverPosition position)
    {
        var (tl, tr, bl, br) = BaseQuadrant(position);
        return new[] { tl, tr, bl, br };
    }

    // Maps the image's own (unrotated) TL/TR/BL/BR order to the physical key that should
    // receive each, so the tiles still read as one picture once the pad's mounting rotation is
    // accounted for. In Single layout the same order is reused: [0]=cover, [1]=title,
    // [2]=artist, [3]=album. Derived from "rotate a 2×2 matrix N×90° clockwise", parameterized by
    // the position's own 4 base keys so a 90°/180°/270° device rotation still swaps within the
    // SAME 2×2 corner rather than jumping to a different one.
    private static int[] QuadrantButtonsFor(int rotation, SpotifyCoverPosition position)
    {
        var (tl, tr, bl, br) = BaseQuadrant(position);
        return rotation switch
        {
            90  => new[] { bl, tl, br, tr },
            180 => new[] { br, bl, tr, tl },
            270 => new[] { tr, br, tl, bl },
            _   => new[] { tl, tr, bl, br },
        };
    }

    /// <summary>The block key that carries the "back" badge and leaves the profile when pressed:
    /// the cover tile in the 1+3 layout, and the BOTTOM-LEFT quadrant in the 4-tile one — which
    /// puts the badge in the same corner of the picture either way, since the album art is one
    /// image spread over the four keys there. Null when the pad is not on the profile.</summary>
    public static int? BackKeyOf(int deviceId)
    {
        lock (_gate)
            return _devices.TryGetValue(deviceId, out var ctx) ? BackKeyOf(ctx) : null;
    }

    private static int BackKeyOf(DeviceCtx ctx) =>
        QuadrantButtonsFor(ctx.Rotation, ctx.Cfg.Position)[ctx.Cfg.Layout == SpotifyCoverLayout.Single ? 0 : 2];

    /// <summary>Which Single-layout field a physical key carries on this rotation/position: -1 =
    /// the cover tile, 0/1/2 = title/artist/album, -2 = not part of the block at all. Public so
    /// the key-config popup can say WHICH text a "no action" block key is showing.</summary>
    public static int FieldIndexOf(int rotation, SpotifyCoverPosition position, int btn)
    {
        int i = Array.IndexOf(QuadrantButtonsFor(rotation, position), btn);
        return i < 0 ? -2 : i - 1;
    }

    /// <summary>The text a Single-layout block key is showing right now (title/artist/album), or
    /// null for the cover tile / a key outside the block / no track. Lets the key's "Edit icon"
    /// preview render the REAL tile instead of a placeholder.</summary>
    public static string? FieldTextOf(int deviceId, int btn)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var ctx)) return null;
            int f = FieldIndexOf(ctx.Rotation, ctx.Cfg.Position, btn);
            if (f < 0 || !_rt.TryGetValue(deviceId, out var rt)) return null;
            return f switch { 0 => rt.Track.Title, 1 => rt.Track.Artist, _ => rt.Track.Album };
        }
    }

    /// <summary>The small caption ("Song"/"Artist"/"Album") a text tile draws above its value —
    /// index-matched to <see cref="TrackData"/>'s Title/Artist/Album order, so a short value
    /// sitting alone on its own tile still says which field it is (user request 2026-09-01).
    /// A method, not a cached array: <see cref="Loc"/>'s language can change at runtime
    /// (<c>Loc.SetLanguage</c>), and a `static readonly` snapshot would go stale until restart.</summary>
    private static string[] FieldLabels() =>
        new[] { Loc.Get("spotify_field_song"), Loc.Get("spotify_field_artist"), Loc.Get("spotify_field_album") };

    /// <summary>Renders one text tile the way the device will draw it, into
    /// <paramref name="outputPngPath"/> — the live preview behind the restricted "Edit icon"
    /// popup (font + text color only). Upright, never rotated: the popup shows the tile the way
    /// the user reads it.</summary>
    public static bool RenderTextTilePreview(int deviceId, int btn, KeyIconSpec? spec, string outputPngPath)
    {
        string text;
        string? label;
        lock (_gate)
        {
            text = FieldTextOf(deviceId, btn) ?? "";
            bool haveCtx = _devices.TryGetValue(deviceId, out var ctx);
            int f = haveCtx ? FieldIndexOf(ctx.Rotation, ctx.Cfg.Position, btn) : -2;
            // Scroll-only, same as everywhere else the label shows up (user request 2026-09-01).
            bool marquee = haveCtx && ctx.Cfg.TextMode == SpotifyTextMode.Marquee;
            label = f is >= 0 and < 3 && marquee ? FieldLabels()[f] : null;
        }
        using (IconStyleScope.Push(TextStyleOf(spec)))
            return IconImageGenerator.TryGenerateTrackTextTile(text, DpHidNative.IconSize, outputPngPath, out _, label);
    }

    /// <summary>The ONLY two things a block text tile takes from its key's icon spec — the text
    /// on those tiles is the track's, so a stored caption/background must never leak in.</summary>
    private static KeyIconSpec? TextStyleOf(KeyIconSpec? spec) =>
        spec is null ? null : new KeyIconSpec { FontFamily = spec.FontFamily, TextColor = spec.TextColor };

    /// <summary>Style scope for the text tile a given physical key carries.</summary>
    private static IDisposable PushTextStyle(DeviceCtx ctx, int btn) =>
        IconStyleScope.Push(TextStyleOf(btn >= 0 && btn < ctx.TileSpecs.Length ? ctx.TileSpecs[btn] : null));

    private static readonly string CacheDir   = Path.Combine(Path.GetTempPath(), "K2.SpotifyCover");
    private static readonly string UiCacheDir = Path.Combine(Path.GetTempPath(), "K2.SpotifyCover.ui");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <param name="TileSpecs">Per-physical-key <see cref="KeyIconSpec"/> of the 4 block keys
    /// (index = key number, nulls elsewhere) — only the font and text color are read, and only
    /// for the 3 Single-layout text tiles: that is what the key's "Edit icon" popup edits for a
    /// tile with no action of its own (user request 2026-09-01).</param>
    private readonly record struct DeviceCtx(IDisplayPadClient Client, Action<string> Log, int Rotation,
        SpotifyCoverConfig Cfg, KeyIconSpec?[] TileSpecs);

    private readonly record struct TrackData(byte[]? Art, string? Title, string? Artist, string? Album,
        bool IsPlaying = false, bool ShuffleOn = false, SpotifyRepeatMode RepeatMode = SpotifyRepeatMode.Off)
    {
        public bool HasArt => Art is { Length: > 0 };
    }

    /// <summary>Play/pause + shuffle + repeat, as reflected by the Play/Pause, Shuffle and
    /// Repeat control keys of the Spotify dedicated profile (user request 2026-09-01: "capire
    /// quando lo shuffle è impostato... e cambiare l'icona di conseguenza").</summary>
    public readonly record struct PlaybackState(bool IsPlaying, bool ShuffleOn, SpotifyRepeatMode RepeatMode);

    /// <summary>Raised whenever the resolved playback state changes for a device on its Spotify
    /// profile — <c>MainWindow.DisplayPad.cs</c> uses it to re-render and re-upload whichever of
    /// the Play/Pause/Shuffle/Repeat control keys still hold their stock seeded action.</summary>
    public static event Action<int, PlaybackState>? PlaybackStateChanged;

    /// <summary>Mutable per-device runtime: the last resolved track and, for the marquee
    /// timer, the per-field scroll offsets.</summary>
    private sealed class DeviceRt
    {
        public TrackData Track;
        public int[] ScrollPx = new int[3];
        /// <summary>How many resolves in a row came back with no title/artist/album at all — see
        /// the "blank read" guard in <see cref="RefreshAsync"/>.</summary>
        public int BlankStreak;
    }

    private static readonly object _gate = new();
    /// <summary>Serializes every GDI+ render this service does. System.Drawing is not
    /// thread-safe across concurrent calls even on unrelated Bitmap/Graphics instances — this
    /// service can be entered from the marquee timer (its own thread-pool timer), the WebApi
    /// poll timer, <c>OnTrackChanged</c>'s delayed follow-up, AND a background continuation of
    /// MainWindow's per-device upload chain (<see cref="Repaint"/>, called whenever ANYTHING
    /// else on the profile is edited) all at once. Two of those racing inside GDI+ intermittently
    /// threw inside <c>RenderMarqueeFrame</c>/<c>TryGenerateTrackTextTile</c>, which (before the
    /// null/File.Exists guards added alongside this lock) surfaced as the text tiles randomly
    /// going blank — worst right around another edit, exactly when a repaint's background thread
    /// and a marquee tick were most likely to land at the same moment (user report 2026-09-01).
    /// A plain lock is fine: none of the guarded sections block on I/O other than local disk/HID
    /// writes already on their own thread.</summary>
    private static readonly object _renderLock = new();
    private static readonly Dictionary<int, DeviceCtx> _devices = new();
    private static readonly Dictionary<int, DeviceRt> _rt = new();
    private static bool _subscribed;
    private static int _suspend;
    private static int _tick;

    private static Timer? _webPoll;   // WebApi source has no push events — poll GET /me/player
    private static Timer? _marquee;   // scrolls the Single-layout text tiles

    private const int WebPollMs = 4000;
    // ~8 fps. Each tick re-uploads one 102x102 tile per OVERFLOWING field, and the pad shares
    // that pipe with every other upload K2 does; the original 11 fps left no room for them and
    // showed as tearing on the scrolling line (user report 2026-09-01). The step grows to keep
    // the same scroll speed.
    private const int MarqueeMs = 125;
    private const int MarqueeStepPx = 4;
    private const int MarqueeGapPx = 34;

    public static void Start(IDisplayPadClient client, Action<string> log, int deviceId, int rotation,
        SpotifyCoverConfig cfg, KeyIconSpec?[]? tileSpecs = null)
    {
        bool firstSubscriber;
        lock (_gate)
        {
            _devices[deviceId] = new DeviceCtx(client, log, rotation, cfg, tileSpecs ?? new KeyIconSpec?[12]);
            if (!_rt.ContainsKey(deviceId)) _rt[deviceId] = new DeviceRt();
            firstSubscriber = !_subscribed;
            _subscribed = true;
            SyncTimersLocked();
        }
        if (firstSubscriber)
            SpotifyMediaService.Instance.TrackChanged += OnTrackChanged;

        _ = SpotifyMediaService.Instance.EnsureStartedAsync();
        _ = RefreshAsync(deviceId);
    }

    public static void Stop(int deviceId)
    {
        lock (_gate)
        {
            _devices.Remove(deviceId);
            _rt.Remove(deviceId);
            SyncTimersLocked();
        }
    }

    /// <summary>True while this overlay owns <paramref name="btn"/> on <paramref name="deviceId"/>
    /// — one of the 4 block keys while the Spotify profile is active. Repaint paths use it to
    /// SKIP blanking / re-uploading those keys.</summary>
    public static bool Owns(int deviceId, int btn)
    {
        lock (_gate)
            return _devices.TryGetValue(deviceId, out var ctx)
                   && Array.IndexOf(BlockKeysFor(ctx.Cfg.Position), btn) >= 0;
    }

    /// <summary>Synchronous hardware re-push of the last resolved track — called at the tail of
    /// a profile/page repaint batch so the batch's blanks can't sit on top of the block.</summary>
    public static void Repaint(int deviceId)
    {
        DeviceCtx ctx;
        TrackData track;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out ctx)) return;
            track = _rt.TryGetValue(deviceId, out var rt) ? rt.Track : default;
        }
        PushDevice(deviceId, ctx, track, notify: false);
    }

    // ─────────────────────────── refresh ───────────────────────────

    /// <summary>SMTC raises its change event as the session is being swapped, so reading it
    /// right then can return a half-populated (or momentarily empty) session — which painted
    /// three blank tiles that nothing came back to fix, because the Local source has no poll
    /// behind it (user report 2026-09-01: "quando scorro brano spariscono i testi"). One
    /// follow-up read shortly after settles it.</summary>
    private const int TrackChangeSettleMs = 900;

    private static void OnTrackChanged()
    {
        _ = RefreshAsync(null);
        _ = Task.Delay(TrackChangeSettleMs).ContinueWith(_ => RefreshAsync(null));
    }

    /// <summary>Re-resolves the current track for <paramref name="onlyDeviceId"/> (just
    /// activated) or every active device, repaints the block and raises
    /// <see cref="PreviewChanged"/>.</summary>
    private static async Task RefreshAsync(int? onlyDeviceId)
    {
        var targets = new List<(int Id, DeviceCtx Ctx)>();
        lock (_gate)
        {
            if (onlyDeviceId is int only)
            {
                if (_devices.TryGetValue(only, out var c)) targets.Add((only, c));
            }
            else
            {
                foreach (var kv in _devices) targets.Add((kv.Key, kv.Value));
            }
        }

        foreach (var (id, ctx) in targets)
        {
            TrackData track = await ResolveTrackAsync(ctx).ConfigureAwait(false);
            bool skip = false;
            bool stateChanged = false;
            lock (_gate)
            {
                if (!_rt.TryGetValue(id, out var rt)) { rt = new DeviceRt(); _rt[id] = rt; }

                // Blank read: keep what is on the pad rather than wiping it, ONCE. A real stop
                // (Spotify closed) reads blank again on the follow-up above and goes through on
                // the second try, so nothing gets stuck showing a track that ended.
                bool blank = string.IsNullOrWhiteSpace(track.Title)
                             && string.IsNullOrWhiteSpace(track.Artist)
                             && string.IsNullOrWhiteSpace(track.Album)
                             && !track.HasArt;
                bool hadSomething = !string.IsNullOrWhiteSpace(rt.Track.Title)
                                    || !string.IsNullOrWhiteSpace(rt.Track.Artist)
                                    || !string.IsNullOrWhiteSpace(rt.Track.Album);
                rt.BlankStreak = blank ? rt.BlankStreak + 1 : 0;
                if (blank && hadSomething && rt.BlankStreak < 2) skip = true;
                // Rewind the marquee only when the TEXT actually changed. The WebApi source
                // re-resolves the same track every 4s; resetting unconditionally made every
                // scrolling line snap back to its start on each poll (user report 2026-09-01).
                bool sameText = rt.Track.Title == track.Title
                                && rt.Track.Artist == track.Artist
                                && rt.Track.Album == track.Album;
                bool sameState = rt.Track.IsPlaying == track.IsPlaying
                                 && rt.Track.ShuffleOn == track.ShuffleOn
                                 && rt.Track.RepeatMode == track.RepeatMode;
                stateChanged = !skip && !sameState;
                if (!skip) rt.Track = track;
                if (!skip && !sameText)
                    for (int i = 0; i < 3; i++) rt.ScrollPx[i] = 0;
            }
            if (skip) continue;
            PushDevice(id, ctx, track, notify: true);
            if (stateChanged)
                try { PlaybackStateChanged?.Invoke(id, new PlaybackState(track.IsPlaying, track.ShuffleOn, track.RepeatMode)); }
                catch (Exception ex) { ctx.Log($"[Spotify] PlaybackStateChanged handler threw: {ex.Message}"); }
        }

        lock (_gate) SyncTimersLocked();
    }

    /// <summary>Resolves the current track honouring the device's source, with the documented
    /// WebApi→Local fallback.</summary>
    private static async Task<TrackData> ResolveTrackAsync(DeviceCtx ctx)
    {
        if (ctx.Cfg.Source == SpotifyCoverSource.WebApi)
        {
            try
            {
                var np = await SpotifyWebPlayback.GetNowPlayingAsync(ctx.Log).ConfigureAwait(false);
                if (np is { } n)
                {
                    byte[]? art = null;
                    if (!string.IsNullOrEmpty(n.ArtUrl))
                    {
                        try { art = await _http.GetByteArrayAsync(n.ArtUrl).ConfigureAwait(false); }
                        catch (Exception ex) { ctx.Log($"[Spotify] cover download failed: {ex.Message}"); }
                    }
                    return new TrackData(art, n.Title, n.Artist, n.Album, n.IsPlaying, n.ShuffleOn, n.RepeatMode);
                }
            }
            catch (Exception ex) { ctx.Log($"[Spotify] web now-playing threw: {ex.Message}"); }
            // fall through to Local
        }

        byte[]? smtcArt = null;
        try
        {
            var stream = await SpotifyMediaService.Instance.GetThumbnailStreamAsync().ConfigureAwait(false);
            if (stream is not null) smtcArt = await ReadAllAsync(stream).ConfigureAwait(false);
        }
        catch { /* no art */ }

        var (title, artist, album) = await SpotifyMediaService.Instance.GetNowPlayingFullAsync().ConfigureAwait(false);
        var (isPlaying, shuffleOn, repeatMode) = await SpotifyMediaService.Instance.GetPlaybackStateAsync().ConfigureAwait(false);
        return new TrackData(smtcArt, title, artist, album, isPlaying, shuffleOn, repeatMode);
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStreamWithContentType stream)
    {
        using var reader = new DataReader(stream);
        uint size = (uint)stream.Size;
        await reader.LoadAsync(size);
        byte[] raw = new byte[size];
        reader.ReadBytes(raw);
        return raw;
    }

    // ─────────────────────────── paint (hardware) ───────────────────────────

    private static void PushDevice(int deviceId, DeviceCtx ctx, TrackData track, bool notify)
    {
        try
        {
            lock (_renderLock) PushDeviceLocked(deviceId, ctx, track);
        }
        catch (Exception ex)
        {
            ctx.Log($"[Spotify] push failed for device {deviceId}: {ex.Message}");
        }

        if (notify)
        {
            try { PreviewChanged?.Invoke(deviceId); } catch { }
        }
    }

    private static void PushDeviceLocked(int deviceId, DeviceCtx ctx, TrackData track)
    {
            Directory.CreateDirectory(CacheDir);
            int[] qb = QuadrantButtonsFor(ctx.Rotation, ctx.Cfg.Position);

            if (ctx.Cfg.Layout == SpotifyCoverLayout.Quad)
            {
                var quads = track.HasArt ? DecodeAndSlice(track.Art!) : null;
                for (int i = 0; i < 4; i++)
                    PushHwTile(deviceId, ctx, qb[i], $"q{i}", TileFromQuadrant(quads, i));
            }
            else
            {
                PushHwTile(deviceId, ctx, qb[0], "cover", TileFromSquare(track.Art));
                string?[] fields = { track.Title, track.Artist, track.Album };
                string[] labels = FieldLabels();
                int[] scroll = ScrollOf(deviceId);
                for (int i = 0; i < 3; i++)
                {
                    // ALWAYS repainted, including in marquee mode: this method is also what
                    // re-asserts the block after a page repaint has blanked the pad, and a text
                    // tile that is not currently scrolling (short field) would otherwise stay
                    // blank until the track changed (user report 2026-09-01).
                    //
                    // What it draws in marquee mode is the frame at the CURRENT offset, not the
                    // static tile: pushing the static one made the line jump back to its start
                    // for a frame on every refresh, which is the flicker reported alongside it.
                    if (ctx.Cfg.TextMode == SpotifyTextMode.Marquee)
                    {
                        Bitmap? frame;
                        using (PushTextStyle(ctx, qb[i + 1]))
                            frame = IconImageGenerator.RenderMarqueeFrame(
                                fields[i] ?? "", DpHidNative.IconSize, scroll[i], MarqueeGapPx, out _, labels[i]);
                        // A render failure (GDI+ is not thread-safe across concurrent calls —
                        // see the _renderLock note below — occasionally throws here) must NOT
                        // push a blank tile over whatever the hardware already shows; skipping
                        // leaves it as-is for the next tick/refresh to fix (user report
                        // 2026-09-01: text tiles going blank, worst around other profile edits,
                        // which is exactly when a repaint here can overlap a marquee tick).
                        if (frame is null) continue;
                        PushHwTile(deviceId, ctx, qb[i + 1], $"txt{i}", frame);
                        continue;
                    }
                    string path = Path.Combine(CacheDir, $"txt{i}_r{ctx.Rotation}.png");
                    bool rendered;
                    // This branch only runs for Static (Marquee returned above) — no field
                    // label here, it only shows up alongside scrolling text (user request
                    // 2026-09-01).
                    using (PushTextStyle(ctx, qb[i + 1]))
                        rendered = IconImageGenerator.TryGenerateTrackTextTile(fields[i] ?? "", DpHidNative.IconSize, path, out _, null);
                    // Same guard as the marquee branch above: a failed render with no earlier
                    // file to fall back to must not push (and cache) a blank tile.
                    if (!rendered && !File.Exists(path)) continue;
                    PushHwTile(deviceId, ctx, qb[i + 1], $"txt{i}", LoadDetached(path));
                }
            }
            ctx.Log($"[Spotify] block pushed to device {deviceId} ({ctx.Cfg.LayoutToken}/{ctx.Cfg.SourceToken})");
    }

    /// <summary>The device's current marquee offsets (zeros when it has no runtime yet).</summary>
    private static int[] ScrollOf(int deviceId)
    {
        lock (_gate)
            return _rt.TryGetValue(deviceId, out var rt) ? (int[])rt.ScrollPx.Clone() : new int[3];
    }

    private static void PushHwTile(int deviceId, DeviceCtx ctx, int btn, string cacheKey, Bitmap? content)
    {
        using var final = RenderTile(ctx, content, backBadge: ctx.Cfg.BackArrow && btn == BackKeyOf(ctx));
        string cachePath = Path.Combine(CacheDir,
            $"{cacheKey}_r{ctx.Rotation}_{ctx.Cfg.LayoutToken}{ctx.Cfg.BackArrowToken}.png");
        try { final.Save(cachePath, ImageFormat.Png); } catch { }

        byte[] bgr = ExtractBgr24(final);
        if (!ctx.Client.TryUploadRawBgr(deviceId, bgr, btn))
            ctx.Client.UploadImage(deviceId, cachePath, btn, 0);
    }

    /// <summary>Composites <paramref name="content"/> onto a black 102×102 tile and applies the
    /// device rotation — the exact bytes both the hardware push and the UI preview use.</summary>
    private static Bitmap RenderTile(DeviceCtx ctx, Bitmap? content, bool rotate = true, bool backBadge = false)
    {
        var final = new Bitmap(DpHidNative.IconSize, DpHidNative.IconSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(final))
        {
            g.Clear(Color.Black);
            if (content is not null)
                g.DrawImage(content, 0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        }
        content?.Dispose();
        // Same arrow the Discord voice page stamps on its server tile, and the same meaning:
        // "this key hands the panel back". Drawn BEFORE the rotation so it lands in the corner
        // the user sees, not in the corner of the raw bitmap.
        if (backBadge) DiscordTileRenderer.StampBackBadge(final);
        if (rotate) RotateForDevice(final, ctx.Rotation);
        return final;
    }

    // ─────────────────────────── paint (app grid preview) ───────────────────────────

    /// <summary>Still per-key PNG the app's own DisplayPad grid binds to for a block key — a
    /// separate file from the hardware path's cache so the two never race on one bitmap
    /// (mirrors <see cref="DpLiveTileService"/>.UiPreviewPath).</summary>
    public static string UiPreviewPath(int deviceId, int btn) =>
        Path.Combine(UiCacheDir, $"ui_{deviceId}_{btn}.png");

    /// <summary>(Re)renders the 4 block tiles for <paramref name="deviceId"/> into their
    /// <see cref="UiPreviewPath"/> files from the last resolved track — static even in marquee
    /// mode (the grid shows a snapshot, the scroll is a hardware-only effect). Safe to call on
    /// the UI thread; no hardware I/O.</summary>
    public static void RenderUiPreview(int deviceId)
    {
        DeviceCtx ctx;
        TrackData track;
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out ctx)) return;
            track = _rt.TryGetValue(deviceId, out var rt) ? rt.Track : default;
        }

        try
        {
            lock (_renderLock)
            RenderUiPreviewLocked(ctx, deviceId, track);
        }
        catch { /* preview is best-effort */ }
    }

    private static void RenderUiPreviewLocked(DeviceCtx ctx, int deviceId, TrackData track)
    {
            Directory.CreateDirectory(UiCacheDir);
            int[] qb = QuadrantButtonsFor(ctx.Rotation, ctx.Cfg.Position);

            if (ctx.Cfg.Layout == SpotifyCoverLayout.Quad)
            {
                var quads = track.HasArt ? DecodeAndSlice(track.Art!) : null;
                for (int i = 0; i < 4; i++)
                    SaveUiTile(ctx, deviceId, qb[i], TileFromQuadrant(quads, i));
            }
            else
            {
                SaveUiTile(ctx, deviceId, qb[0], TileFromSquare(track.Art));
                string?[] fields = { track.Title, track.Artist, track.Album };
                // This still-frame preview never scrolls itself, but the label follows the
                // CONFIGURED mode: Marquee is what the hardware is actually doing, Static gets
                // no label (user request 2026-09-01 — the label is scroll-only).
                string?[] labels = ctx.Cfg.TextMode == SpotifyTextMode.Marquee ? FieldLabels() : new string?[3];
                for (int i = 0; i < 3; i++)
                {
                    string tmp = Path.Combine(UiCacheDir, $"src_{deviceId}_{i}.png");
                    bool ok;
                    using (PushTextStyle(ctx, qb[i + 1]))
                        ok = IconImageGenerator.TryGenerateTrackTextTile(fields[i] ?? "", DpHidNative.IconSize, tmp, out _, labels[i]);
                    if (ok) SaveUiTile(ctx, deviceId, qb[i + 1], LoadDetached(tmp));
                }
            }
    }

    /// <summary>Writes one block tile's app-grid preview. Deliberately NOT rotated, unlike the
    /// hardware push: the grid rotates the whole canvas by the device rotation and counter-rotates
    /// each key's picture (see <c>DpRebuildKeyGrid</c>), so it expects the same upright PNG every
    /// stored icon is. Baking the device counter-rotation in here left the 4 block tiles lying on
    /// their side among upright neighbours (user report 2026-09-01).</summary>
    private static void SaveUiTile(DeviceCtx ctx, int deviceId, int btn, Bitmap? content)
    {
        using var final = RenderTile(ctx, content, rotate: false,
            backBadge: ctx.Cfg.BackArrow && btn == BackKeyOf(ctx));
        try { final.Save(UiPreviewPath(deviceId, btn), ImageFormat.Png); } catch { }
    }

    // ─────────────────────────── image helpers ───────────────────────────

    private static Bitmap? TileFromQuadrant(byte[]?[]? quads, int i)
    {
        if (quads?[i] is not byte[] png) return null;
        using var ms = new MemoryStream(png);
        using var src = new Bitmap(ms);
        return new Bitmap(src);
    }

    /// <summary>Center-crops <paramref name="raw"/> to a square and scales it to one 102×102
    /// tile (the Single-layout cover). Null when there is no art.</summary>
    private static Bitmap? TileFromSquare(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        try
        {
            using var ms = new MemoryStream(raw);
            using var src = new Bitmap(ms);
            int side = Math.Min(src.Width, src.Height);
            var crop = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);

            int n = DpHidNative.IconSize;
            var tile = new Bitmap(n, n, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(tile);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, n, n), crop, GraphicsUnit.Pixel);
            return tile;
        }
        catch { return null; }
    }

    /// <summary>Center-crops <paramref name="raw"/> to a square, scales to 204×204 and slices
    /// it into 4 unrotated 102×102 PNGs (TL,TR,BL,BR).</summary>
    private static byte[]?[] DecodeAndSlice(byte[] raw)
    {
        using var ms = new MemoryStream(raw);
        using var src = new Bitmap(ms);

        const int full = 2 * DpHidNative.IconSize; // 204
        int side = Math.Min(src.Width, src.Height);
        var cropRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);

        using var square = new Bitmap(full, full, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(square))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, full, full), cropRect, GraphicsUnit.Pixel);
        }

        var result = new byte[4][];
        var quadRects = new[]
        {
            new Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize),
            new Rectangle(DpHidNative.IconSize, 0, DpHidNative.IconSize, DpHidNative.IconSize),
            new Rectangle(0, DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize),
            new Rectangle(DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize, DpHidNative.IconSize),
        };
        for (int i = 0; i < 4; i++)
        {
            using var tile = square.Clone(quadRects[i], PixelFormat.Format24bppRgb);
            using var msOut = new MemoryStream();
            tile.Save(msOut, ImageFormat.Png);
            result[i] = msOut.ToArray();
        }
        return result;
    }

    private static void RotateForDevice(Bitmap bmp, int rotation)
    {
        switch (rotation)
        {
            case 90:  bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 180: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 270: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }
    }

    private static byte[] ExtractBgr24(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, DpHidNative.IconSize, DpHidNative.IconSize);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = DpHidNative.IconSize * 3;
            var bgr = new byte[DpHidNative.IconBytes];
            for (int y = 0; y < DpHidNative.IconSize; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, bgr, y * rowBytes, rowBytes);
            return bgr;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static Bitmap? LoadDetached(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var tmp = new Bitmap(fs);
            return new Bitmap(tmp);
        }
        catch { return null; }
    }

    // ─────────────────────────── timers ───────────────────────────

    /// <summary>Starts/stops the WebApi poll and the marquee tick to match the current device
    /// set. Call with <see cref="_gate"/> held.</summary>
    private static void SyncTimersLocked()
    {
        bool wantPoll = _devices.Values.Any(d => d.Cfg.Source == SpotifyCoverSource.WebApi);
        if (wantPoll && _webPoll is null)
            _webPoll = new Timer(_ => { _ = RefreshAsync(null); }, null, WebPollMs, WebPollMs);
        else if (!wantPoll && _webPoll is not null) { _webPoll.Dispose(); _webPoll = null; }

        bool wantMarquee = _devices.Values.Any(d =>
            d.Cfg.Layout == SpotifyCoverLayout.Single && d.Cfg.TextMode == SpotifyTextMode.Marquee);
        if (wantMarquee && _marquee is null)
        {
            _tick = 0; // so MarqueeTick's one-shot "ticking" confirmation log fires again this run
            _marquee = new Timer(_ => MarqueeTick(), null, MarqueeMs, MarqueeMs);
            // User report 2026-09-01: "marquee looks static" — this line proves whether the
            // timer is even being CREATED; if it's missing from the log the bug is upstream
            // (Layout/TextMode not actually saved as Single/Marquee), not in the tick itself.
            AnyLog()?.Invoke("[Spotify] marquee timer started");
        }
        else if (!wantMarquee && _marquee is not null)
        {
            _marquee.Dispose();
            _marquee = null;
            AnyLog()?.Invoke("[Spotify] marquee timer stopped");
        }
    }

    /// <summary>Any one registered device's log delegate — <see cref="SyncTimersLocked"/> has no
    /// device of its own to log through, and any of them writes to the same shared log file.</summary>
    private static Action<string>? AnyLog() => _devices.Values.FirstOrDefault().Log;

    /// <summary>Holds the marquee off the device until the returned scope is disposed. The
    /// scroll uploads ~8 tiles/s down the same pipe every other upload uses, so a synchronous
    /// upload started from the UI thread (saving a key, repainting a page) can end up queued
    /// behind a long backlog of scroll frames — which is a frozen window, not a slow one
    /// (user report 2026-09-01). The scroll position is kept: it resumes where it left off.</summary>
    public static IDisposable Suspend() => new SuspendScope();

    private sealed class SuspendScope : IDisposable
    {
        public SuspendScope() => Interlocked.Increment(ref _suspend);
        public void Dispose() => Interlocked.Decrement(ref _suspend);
    }

    /// <summary>How often (in ticks) a non-scrolling text tile is re-pushed anyway — see the
    /// use site. 8 ticks × 125 ms ≈ 1 s.</summary>
    private const int StillFieldEveryTicks = 8;

    private static void MarqueeTick()
    {
        if (Volatile.Read(ref _suspend) > 0) return;
        int tick = Interlocked.Increment(ref _tick);
        // First tick after (re)starting: confirms the timer is actually FIRING, as opposed to
        // "started" (SyncTimersLocked's own log) but never ticking — user report 2026-09-01.
        if (tick == 1) AnyLog()?.Invoke("[Spotify] marquee ticking");
        var work = new List<(int Id, DeviceCtx Ctx, string?[] Fields, int[] Scroll)>();
        lock (_gate)
        {
            foreach (var (id, ctx) in _devices)
            {
                if (ctx.Cfg.Layout != SpotifyCoverLayout.Single || ctx.Cfg.TextMode != SpotifyTextMode.Marquee) continue;
                if (!_rt.TryGetValue(id, out var rt)) continue;
                for (int i = 0; i < 3; i++) rt.ScrollPx[i] += MarqueeStepPx;
                var t = rt.Track;
                work.Add((id, ctx, new[] { t.Title, t.Artist, t.Album }, (int[])rt.ScrollPx.Clone()));
            }
        }
        if (tick == 1) AnyLog()?.Invoke($"[Spotify] marquee tick #1: {work.Count} device(s) eligible");

        // Same GDI+ serialization as PushDeviceLocked/RenderUiPreviewLocked — see _renderLock's
        // remarks. Held across the whole tile (render + rotate + BGR extraction), not just the
        // RenderMarqueeFrame call, since those touch the same Bitmap and must not overlap with a
        // concurrent Repaint()/RefreshAsync() call for this or another device.
        lock (_renderLock)
        foreach (var (id, ctx, fields, scroll) in work)
        {
            int[] qb = QuadrantButtonsFor(ctx.Rotation, ctx.Cfg.Position);
            string[] labels = FieldLabels();
            for (int i = 0; i < 3; i++)
            {
                Bitmap? frame;
                int cycle;
                using (PushTextStyle(ctx, qb[i + 1]))
                    frame = IconImageGenerator.RenderMarqueeFrame(
                        fields[i] ?? "", DpHidNative.IconSize, scroll[i], MarqueeGapPx, out cycle, labels[i]);
                if (frame is null) continue;
                // Nothing to scroll on this field (short text): no point re-uploading an
                // identical tile 8 times a second — but do refresh it about once a second, so a
                // blank pass from some other repaint path can never leave it empty for good.
                if (cycle == 0 && scroll[i] > MarqueeStepPx && tick % StillFieldEveryTicks != 0)
                {
                    frame.Dispose();
                    continue;
                }
                try
                {
                    RotateForDevice(frame, ctx.Rotation);
                    byte[] bgr = ExtractBgr24(frame);
                    if (!ctx.Client.TryUploadRawBgr(id, bgr, qb[i + 1]))
                    {
                        string p = Path.Combine(CacheDir, $"mq{i}_r{ctx.Rotation}.png");
                        try { frame.Save(p, ImageFormat.Png); ctx.Client.UploadImage(id, p, qb[i + 1], 0); } catch { }
                    }
                }
                finally { frame.Dispose(); }

                // Keep the offset bounded once it has scrolled a full cycle.
                if (cycle > 0 && scroll[i] >= cycle)
                    lock (_gate) { if (_rt.TryGetValue(id, out var rt)) rt.ScrollPx[i] %= cycle; }
            }
        }
    }
}
