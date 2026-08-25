using System;
using System.Diagnostics;
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
/// <b>Endpoint.</b> Cloudflare's public measurement endpoints (<c>speed.cloudflare.com</c>) —
/// the same ones its own speed test uses: <c>__down?bytes=N</c> streams N bytes, <c>__up</c>
/// accepts a body and discards it. No account, no API key and no third-party library, which is
/// what makes it usable from an app that must stay a single self-contained x86 build. The
/// figures are reported in Mbit/s, the unit every speed test quotes (note that
/// <see cref="SystemMonitor"/>'s live network tiles are in BYTES/s — they measure a different
/// thing: what the PC is transferring right now, not what the line can do).
/// </para>
/// </summary>
internal static class SpeedTestService
{
    /// <summary>Bytes pulled/pushed per run. Big enough for a broadband line to reach its
    /// steady rate, small enough that a slow line still finishes inside
    /// <see cref="Timeout"/> (a 10 Mbit/s connection downloads 25 MB in ~20 s).</summary>
    private const int DownloadBytes = 25_000_000;
    private const int UploadBytes   =  8_000_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // A compressed transfer would measure the compressor, not the line.
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    { Timeout = Timeout };

    private static int _running;   // 0/1, Interlocked — see IsRunning

    /// <summary>True while a test is in flight; the tiles show a "working" marker instead of a
    /// stale number.</summary>
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

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

    /// <summary>Raised whenever a figure changes or the running state flips, so the live tiles
    /// repaint immediately instead of waiting for their next tick.</summary>
    public static event Action? Changed;

    /// <summary>Runs ping → download → upload in the background. Returns immediately; the
    /// results arrive through <see cref="Changed"/>. A second call while a run is in flight is
    /// ignored (see class remarks).</summary>
    public static void Start(Action<string>? log = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        Notify();

        _ = Task.Run(async () =>
        {
            try
            {
                LastError = null;
                LastPingMs = await MeasurePingAsync().ConfigureAwait(false);
                Notify();
                LastDownMbps = await MeasureDownloadAsync().ConfigureAwait(false);
                Notify();
                LastUpMbps = await MeasureUploadAsync().ConfigureAwait(false);
                LastRunAt = DateTime.Now;
                log?.Invoke($"[SPEEDTEST] ping={LastPingMs:F0}ms down={LastDownMbps:F1}Mbps up={LastUpMbps:F1}Mbps");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log?.Invoke($"[SPEEDTEST] failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                Notify();
            }
        });
    }

    private static void Notify()
    {
        try { Changed?.Invoke(); } catch { /* a subscriber's repaint must never kill the run */ }
    }

    /// <summary>Round-trip time to the endpoint: the best of four zero-byte requests, which
    /// discards the first one's TLS handshake and any single slow sample.</summary>
    private static async Task<double> MeasurePingAsync()
    {
        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=0",
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            sw.Stop();
            if (i > 0) best = Math.Min(best, sw.Elapsed.TotalMilliseconds);   // skip the handshake sample
        }
        return best == double.MaxValue ? 0 : best;
    }

    private static async Task<double> MeasureDownloadAsync()
    {
        using var resp = await Http.GetAsync($"https://speed.cloudflare.com/__down?bytes={DownloadBytes}",
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
            total += read;
        sw.Stop();

        return Mbps(total, sw.Elapsed);
    }

    private static async Task<double> MeasureUploadAsync()
    {
        var payload = new byte[UploadBytes];
        using var content = new ByteArrayContent(payload);
        var sw = Stopwatch.StartNew();
        using var resp = await Http.PostAsync("https://speed.cloudflare.com/__up", content).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        sw.Stop();
        return Mbps(payload.Length, sw.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;
}
