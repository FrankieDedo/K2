using System;
using System.Globalization;

namespace K2.App.Services;

/// <summary>How the DisplayPad's <c>dp_speedtest</c> keys reach the network.
/// <see cref="Cloudflare"/> is the built-in default (<c>speed.cloudflare.com</c>, no config);
/// <see cref="Custom"/> points ping/download/upload at URLs the user supplied, so an endpoint
/// that works on a network where Cloudflare is blocked can be pasted in from the config popup.</summary>
public enum SpeedTestMode { Cloudflare, Custom }

/// <summary>
/// The knobs of the speed-test configuration popup
/// (<c>K2.App.SpeedTestConfigWindow</c>), persisted GLOBALLY (not per pad — there is one
/// <see cref="SpeedTestService"/> for the whole app) in <c>DisplayPadStore</c> under the
/// <c>speedtest.*</c> keys, and consumed by <see cref="SpeedTestService"/>.
///
/// <para>In <see cref="SpeedTestMode.Custom"/> the download URL is a template: the literal
/// <c>{bytes}</c> is replaced with the byte count to fetch (Cloudflare/Ookla style) and
/// <c>{mb}</c> with the same rounded up to whole megabytes (LibreSpeed <c>ckSize</c> style), so
/// an endpoint found online drops in without new code. An empty upload URL means "skip the
/// upload leg" — the tile then reads <c>n/d</c> instead of a number.</para>
/// </summary>
/// <param name="Preset">Which dropdown row the popup last showed — cosmetic only, so reopening
/// lands on the same item; the behaviour is entirely decided by <paramref name="Mode"/> and the
/// URLs. 0 = Cloudflare, 1 = LibreSpeed, 2 = Ookla, 3 = test file, 4 = fully custom.</param>
public readonly record struct SpeedTestConfig(
    SpeedTestMode Mode = SpeedTestMode.Cloudflare,
    string DownUrl = "",
    string UpUrl = "",
    int DownBytes = 25_000_000,
    int UpBytes = 8_000_000,
    int TimeoutSeconds = 60,
    int Preset = 0)
{
    // A record struct's primary-ctor parameter defaults can't reference the type's own
    // consts (they aren't in scope for the parameter list), so the three literals above are
    // kept in sync with these by hand.
    public const int DefaultDownBytes = 25_000_000;
    public const int DefaultUpBytes = 8_000_000;
    public const int DefaultTimeoutSeconds = 60;

    private const string CloudflareDown = "https://speed.cloudflare.com/__down?bytes={bytes}";
    private const string CloudflareUp = "https://speed.cloudflare.com/__up";

    // NB: a record struct's parameterless new() zero-inits and SKIPS the primary ctor's default
    // values, so spell them out — otherwise Default.DownBytes/TimeoutSeconds would be 0.
    public static readonly SpeedTestConfig Default =
        new(SpeedTestMode.Cloudflare, "", "", DefaultDownBytes, DefaultUpBytes, DefaultTimeoutSeconds, 0);

    public static int ClampBytes(int b) => Math.Clamp(b, 100_000, 500_000_000);
    public static int ClampTimeout(int s) => Math.Clamp(s, 5, 600);

    // ─────────────────── effective values (Cloudflare mode ignores the fields) ───────────────────

    private string DownTemplate => Mode == SpeedTestMode.Cloudflare ? CloudflareDown : DownUrl.Trim();

    /// <summary>The upload endpoint, or null when the upload leg should be skipped.</summary>
    public string? EffectiveUpUrl => Mode == SpeedTestMode.Cloudflare
        ? CloudflareUp
        : string.IsNullOrWhiteSpace(UpUrl) ? null : UpUrl.Trim();

    public int EffectiveDownBytes => Mode == SpeedTestMode.Cloudflare ? DefaultDownBytes : ClampBytes(DownBytes);
    public int EffectiveUpBytes => Mode == SpeedTestMode.Cloudflare ? DefaultUpBytes : ClampBytes(UpBytes);
    public TimeSpan EffectiveTimeout => TimeSpan.FromSeconds(
        Mode == SpeedTestMode.Cloudflare ? DefaultTimeoutSeconds : ClampTimeout(TimeoutSeconds));

    /// <summary>True when the upload leg won't run (custom mode, no upload URL) — the tile reads
    /// <c>n/d</c> rather than "—" so it doesn't look like the test never ran.</summary>
    public bool UploadDisabled => EffectiveUpUrl is null;

    public bool IsUsable => Mode == SpeedTestMode.Cloudflare
                            || Uri.IsWellFormedUriString(Subst(DownTemplate, 0), UriKind.Absolute);

    private static string Subst(string template, int bytes)
    {
        int mb = (int)Math.Max(1, Math.Ceiling(bytes / 1_000_000d));
        return (template ?? "")
            .Replace("{bytes}", bytes.ToString(CultureInfo.InvariantCulture))
            .Replace("{mb}", mb.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Download URL for <paramref name="bytes"/> bytes.</summary>
    public string BuildDownloadUrl(int bytes) => Subst(DownTemplate, bytes);

    /// <summary>URL for the latency probe — the download template asked for 0 bytes, which every
    /// supported endpoint answers with an empty (or tiny) body.</summary>
    public string BuildPingUrl() => Subst(DownTemplate, 0);

    public string Summary => Mode == SpeedTestMode.Cloudflare
        ? "Cloudflare"
        : $"custom down={DownUrl} up={(UploadDisabled ? "(off)" : UpUrl)} " +
          $"{EffectiveDownBytes / 1_000_000}/{EffectiveUpBytes / 1_000_000}MB {EffectiveTimeout.TotalSeconds:F0}s";

    // ─────────────────────────── persistence (DisplayPadStore k/v) ───────────────────────────

    private const string K = "speedtest.";

    public static SpeedTestConfig Load(DisplayPadStore store)
    {
        string? mode = store.GetSetting(K + "mode");
        int ParseInt(string key, int fallback) =>
            int.TryParse(store.GetSetting(K + key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;

        return new SpeedTestConfig(
            Mode: string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase)
                ? SpeedTestMode.Custom : SpeedTestMode.Cloudflare,
            DownUrl: store.GetSetting(K + "downUrl") ?? "",
            UpUrl: store.GetSetting(K + "upUrl") ?? "",
            DownBytes: ParseInt("downBytes", DefaultDownBytes),
            UpBytes: ParseInt("upBytes", DefaultUpBytes),
            TimeoutSeconds: ParseInt("timeoutSec", DefaultTimeoutSeconds),
            Preset: ParseInt("preset", 0));
    }

    public void Save(DisplayPadStore store)
    {
        store.SetSetting(K + "mode", Mode == SpeedTestMode.Custom ? "custom" : "cloudflare");
        store.SetSetting(K + "downUrl", DownUrl ?? "");
        store.SetSetting(K + "upUrl", UpUrl ?? "");
        store.SetSetting(K + "downBytes", EffectiveDownBytes.ToString(CultureInfo.InvariantCulture));
        store.SetSetting(K + "upBytes", EffectiveUpBytes.ToString(CultureInfo.InvariantCulture));
        store.SetSetting(K + "timeoutSec",
            ((int)EffectiveTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        store.SetSetting(K + "preset", Preset.ToString(CultureInfo.InvariantCulture));
    }
}
