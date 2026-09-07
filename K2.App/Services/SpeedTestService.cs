using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace K2.App.Services;

/// <summary>
/// The internet speed test behind the DisplayPad's <c>dp_speedtest</c> keys: latency, download
/// and upload, measured on demand when one of those keys is pressed and then shown on every
/// speed-test key of the profile until the next run.
///
/// <para>
/// <b>Why on demand only.</b> Unlike <see cref="SystemMonitor"/>'s counters (free to read, so
/// the monitor tiles refresh once a second), a throughput measurement costs real bandwidth —
/// tens of megabytes each way. Running it on a timer would saturate the line at random and make
/// every other reading on the pad wrong, so it runs ONLY on a keypress, one test at a time
/// (<see cref="IsRunning"/> gates re-entry: pressing a second speed-test key mid-run joins the
/// run in progress instead of starting another).
/// </para>
///
/// <para>
/// <b>Endpoint.</b> By default Cloudflare's public measurement endpoints (<c>speed.cloudflare.com</c>) —
/// the same ones its own speed test uses: <c>__down?bytes=N</c> streams N bytes, <c>__up</c>
/// accepts a body and discards it. No account, no API key and no third-party library, which is
/// what makes it usable from an app that must stay a single self-contained x86 build. The user
/// can point <see cref="Config"/> at any equivalent endpoint (LibreSpeed, an Ookla test server, a
/// plain test file for download-only) from the config popup — for networks where Cloudflare is
/// blocked, which is the usual reason a run reaches nothing. The
/// figures are reported in Mbit/s, the unit every speed test quotes (note that
/// <see cref="SystemMonitor"/>'s live network tiles are in BYTES/s — they measure a different
/// thing: what the PC is transferring right now, not what the line can do).
/// </para>
/// </summary>
internal static class SpeedTestService
{
    // Default bytes pulled/pushed per run (Cloudflare mode) live in SpeedTestConfig now — big
    // enough for a broadband line to reach its steady rate, small enough that a slow line still
    // finishes inside the 60 s timeout (a 10 Mbit/s connection downloads 25 MB in ~20 s). Custom
    // mode overrides both.

    /// <summary>Endpoint / payload / timeout in force, set at startup from <c>DisplayPadStore</c>
    /// and whenever the config popup saves (<see cref="ApplyConfig"/>). Cloudflare by default —
    /// identical to the values this class shipped with before it was configurable.</summary>
    public static SpeedTestConfig Config { get; private set; } = SpeedTestConfig.Default;

    private static HttpClient Http = BuildHttp(SpeedTestConfig.Default);

    private static HttpClient BuildHttp(SpeedTestConfig cfg) => new(new HttpClientHandler
    {
        // A compressed transfer would measure the compressor, not the line.
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        // Follow the system proxy AND hand it the logged-in user's credentials — an authenticated
        // corporate proxy is a common reason a run silently reaches nothing.
        UseProxy = true,
        DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
    })
    { Timeout = cfg.EffectiveTimeout };

    /// <summary>Swaps in a new configuration (and a fresh <see cref="HttpClient"/> for its
    /// timeout/proxy). A run already in flight keeps the old client; the next run uses this one.</summary>
    public static void ApplyConfig(SpeedTestConfig cfg)
    {
        Config = cfg;
        Http = BuildHttp(cfg);
        Notify();
    }

    private static int _running;   // 0/1, Interlocked — see IsRunning

    /// <summary>True while a test is in flight; the tiles show a "working" marker instead of a
    /// stale number.</summary>
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Which leg of the run is currently in flight — lets each <c>dp_speedtest</c> key
    /// (ping/download/upload are separate keys, each showing its own metric) draw a progress
    /// ring for ITS OWN leg only, instead of all three ticking together.</summary>
    public enum Phase { Idle, Ping, Download, Upload }

    public static Phase CurrentPhase { get; private set; } = Phase.Idle;

    /// <summary>0..1 progress of each leg within the CURRENT run: 0 before it starts, 1 once it
    /// finishes, holds at 1 for legs already done while a later leg is still running. Reset to 0
    /// at the start of every run.</summary>
    public static double PingProgress { get; private set; }
    public static double DownloadProgress { get; private set; }
    public static double UploadProgress { get; private set; }

    // Progress notifications are throttled: a download/upload leg fires them once per read
    // chunk (hundreds of times over 25 MB), and each one triggers a DisplayPad tile re-render +
    // USB write — see DpLiveTileService. Redrawing the ring 60+ times a run would be wasted work
    // (and USB traffic) the eye can't tell apart from redrawing it 5 times a second.
    private static readonly TimeSpan ProgressNotifyInterval = TimeSpan.FromMilliseconds(200);
    private static readonly Stopwatch ProgressClock = new();

    /// <summary>Last results, in Mbit/s (down/up) and milliseconds (ping). Null until the first
    /// successful run — the tile then reads "—" rather than a made-up zero. A run that fails
    /// (no connectivity, endpoint unreachable) leaves the previous values alone and sets
    /// <see cref="LastError"/>.</summary>
    public static double? LastDownMbps { get; private set; }

    /// <inheritdoc cref="LastDownMbps"/>
    public static double? LastUpMbps { get; private set; }

    /// <inheritdoc cref="LastDownMbps"/>
    public static double? LastPingMs { get; private set; }

    /// <summary>When the last successful run finished (local time), or null if there hasn't
    /// been one this session.</summary>
    public static DateTime? LastRunAt { get; private set; }

    public static string? LastError { get; private set; }

    /// <summary>True when the most recent run threw before finishing — lets the tile show "ERR"
    /// instead of a stale "—" that looks identical to "nothing happened" (the whole point of the
    /// user-visible failure signal). Cleared when a run completes.</summary>
    public static bool LastRunFailed { get; private set; }

    /// <summary>Mirrors <see cref="SpeedTestConfig.UploadDisabled"/> — the "up" tile reads "n/d"
    /// rather than "—" when the current config has no upload endpoint.</summary>
    public static bool UploadDisabled => Config.UploadDisabled;

    /// <summary>Raised whenever a figure changes or the running state flips, so the live tiles
    /// repaint immediately instead of waiting for their next tick.</summary>
    public static event Action? Changed;

    /// <summary>Runs ping → download → upload in the background. Returns immediately; the
    /// results arrive through <see cref="Changed"/>. A second call while a run is in flight is
    /// ignored (see class remarks).</summary>
    public static void Start(Action<string>? log = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        CurrentPhase = Phase.Ping;
        PingProgress = DownloadProgress = UploadProgress = 0;
        ProgressClock.Restart();
        Notify();

        _ = Task.Run(async () =>
        {
            try
            {
                LastError = null;
                LastPingMs = await MeasurePingAsync().ConfigureAwait(false);
                PingProgress = 1;
                CurrentPhase = Phase.Download;
                Notify();
                LastDownMbps = await MeasureDownloadAsync().ConfigureAwait(false);
                DownloadProgress = 1;
                CurrentPhase = Phase.Upload;
                Notify();
                // Custom mode with no upload URL: skip the leg outright rather than measure
                // against nothing. LastUpMbps stays null; the tile reads "n/d" (UploadDisabled).
                if (Config.UploadDisabled)
                    LastUpMbps = null;
                else
                    LastUpMbps = await MeasureUploadAsync().ConfigureAwait(false);
                UploadProgress = 1;
                LastRunAt = DateTime.Now;
                LastRunFailed = false;
                log?.Invoke($"[SPEEDTEST] ping={LastPingMs:F0}ms down={LastDownMbps:F1}Mbps " +
                            $"up={(Config.UploadDisabled ? "(off)" : LastUpMbps?.ToString("F1"))}Mbps");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LastRunFailed = true;
                log?.Invoke($"[SPEEDTEST] failed: {ex.Message}");
                // Must-have diagnostic: the runtime log level is often Off in user reports, so a
                // silent throw here is exactly the "nothing happens" complaint. See K2 runtime log.
                App.WriteLog($"[SPEEDTEST] run failed ({Config.Summary}): {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                CurrentPhase = Phase.Idle;
                Notify();
            }
        });
    }

    private static void Notify()
    {
        try { Changed?.Invoke(); } catch { /* a subscriber's repaint must never kill the run */ }
    }

    /// <summary>Same as <see cref="Notify"/> but rate-limited to <see cref="ProgressNotifyInterval"/>
    /// — for the many small progress updates within a download/upload leg, not the few
    /// phase-boundary events (those always call <see cref="Notify"/> directly).</summary>
    private static void NotifyProgress()
    {
        if (ProgressClock.Elapsed < ProgressNotifyInterval) return;
        ProgressClock.Restart();
        Notify();
    }

    /// <summary>Round-trip time to the endpoint: the best of four zero-byte requests, which
    /// discards the first one's TLS handshake and any single slow sample.</summary>
    private static async Task<double> MeasurePingAsync()
    {
        string url = Config.BuildPingUrl();
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await Http.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            sw.Stop();
            if (i > 0) best = Math.Min(best, sw.Elapsed.TotalMilliseconds);   // skip the handshake sample
            PingProgress = (i + 1) / 4d;
            Notify();
        }
        return best == double.MaxValue ? 0 : best;
    }

    private static async Task<double> MeasureDownloadAsync()
    {
        int want = Config.EffectiveDownBytes;
        using var resp = await Http.GetAsync(Config.BuildDownloadUrl(want),
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // The clock starts once the headers are in, so the connection setup isn't billed to
        // the transfer; bytes are counted as they are read and thrown away.
        var sw = Stopwatch.StartNew();
        long total = 0;
        var buffer = new byte[64 * 1024];
        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            total += read;
            DownloadProgress = Math.Clamp((double)total / want, 0, 1);
            NotifyProgress();
        }
        sw.Stop();

        return Mbps(total, sw.Elapsed);
    }

    private static async Task<double> MeasureUploadAsync()
    {
        int size = Config.EffectiveUpBytes;
        var payload = new byte[size];
        using var content = new ProgressStreamContent(payload, sent =>
        {
            UploadProgress = Math.Clamp((double)sent / size, 0, 1);
            NotifyProgress();
        });
        var sw = Stopwatch.StartNew();
        using var resp = await Http.PostAsync(Config.EffectiveUpUrl, content).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        sw.Stop();
        return Mbps(payload.Length, sw.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;

    /// <summary>A <see cref="ByteArrayContent"/> that reports cumulative bytes written as it
    /// serializes — <see cref="HttpClient"/> gives no built-in upload-progress hook, so the only
    /// way to see it is to own the write loop ourselves.</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly Action<long> _onProgress;

        public ProgressStreamContent(byte[] payload, Action<long> onProgress)
        {
            _payload = payload;
            _onProgress = onProgress;
            Headers.ContentLength = payload.Length;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            const int chunk = 64 * 1024;
            long sent = 0;
            while (sent < _payload.Length)
            {
                int n = (int)Math.Min(chunk, _payload.Length - sent);
                await stream.WriteAsync(_payload.AsMemory((int)sent, n)).ConfigureAwait(false);
                sent += n;
                _onProgress(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }
}
