using System;
using System.Globalization;
using System.Text.Json;
using K2.Core;

namespace K2.App.Services;

// Kerbal Space Program tiles (dp_ksp) — values and commands through the Telemachus mod.
// Kept in its own part of the class because nothing in here is shared with the other games:
// the catalogue is KspTelemachus (K2.Core), the transport is TelemachusClient.
internal static partial class DpLiveTileService
{
    /// <summary>What one KSP tile shows right now. <paramref name="Fraction"/> non-null draws a
    /// dial; <paramref name="Lamp"/> is the tile's light (Unknown when the value is not known).</summary>
    private readonly record struct KspReading(string Text, string Unit, double? Fraction,
                                              LiveTileRenderer.EdState Lamp);

    private static readonly KspReading KspUnknown = new("—", "", null, LiveTileRenderer.EdState.Unknown);

    /// <summary>Renders one Kerbal Space Program tile with the current telemetry. Shared by the
    /// hardware repaint and the still-image paths, like <see cref="RenderZcTile"/>.</summary>
    internal static bool RenderKspTile(string? value, string caption, int size, string outputPngPath)
    {
        var accent = GameProfileTheme.AccentFor(GameProfileSpecs.KspId);
        LiveTileRenderer.EdAccentOverride = accent;

        var item = KspTelemachus.Find(value);
        var r = item is null ? KspUnknown : KspRead(item);
        ApplyKspStyle(r.Lamp);

        if (item is null || item.Format == KspFormat.Lamp)
            return LiveTileRenderer.TryRenderEdStatus(r.Lamp, caption, size, outputPngPath);

        if (item.Format is KspFormat.Fraction or KspFormat.Resource)
            return LiveTileRenderer.TryRenderSegmentedGauge(r.Text, r.Fraction, caption, size, outputPngPath,
                                                            accent, KspDialSegments,
                                                            edgeInset: KspEdgeInset, dialScale: KspDialScale);

        return LiveTileRenderer.TryRenderSpeedTile(r.Text, null, caption, r.Unit, size, outputPngPath,
                                                   ownValueSize: item.Format is KspFormat.Text or KspFormat.SasMode,
                                                   edgeInset: KspEdgeInset,
                                                   // Words (body, SAS mode) share one size: the width of a long one.
                                                   valueSizeReference: item.Format is KspFormat.Text or KspFormat.SasMode
                                                       ? "Retrograde" : null);
    }

    private const int KspDialSegments = 12;

    /// <summary>Clearance kept between the KSP frame and the lettering. The frame runs close to the
    /// key's edge, and the caption/unit strips drawn edge-to-edge sat right on it on the pad.</summary>
    private const float KspEdgeInset = 0.08f;

    /// <summary>KSP dials are half as big again as the stock segmented gauge: a fuel or throttle key
    /// is read at a glance, and the ring is the reading.</summary>
    private const float KspDialScale = 1.5f;

    /// <summary>Change stamp: exactly what the tile draws, so an altitude that moves by a metre
    /// the display rounds away does not cost an upload.</summary>
    private static string KspStamp(string? value)
    {
        var item = KspTelemachus.Find(value);
        var r = item is null ? KspUnknown : KspRead(item);
        return $"{value}:{r.Text}:{r.Unit}:{r.Fraction?.ToString("0.00", CultureInfo.InvariantCulture)}:{r.Lamp}";
    }

    /// <summary>Tile caption: the entry's own name, upper-cased to read as a panel legend. A name
    /// from the mod's listing loses its parenthetical ("Next Apsis Type (-1=Pe, 1=Ap, 0=N/A)"): that
    /// is documentation, and on a 102 px strip it pushes out the words that say what the key is.</summary>
    private static string KspCaption(string? value)
    {
        if (KspTelemachus.Find(value) is not { } item) return "";
        string name = Loc.Get(item.LocKey);
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (item.LocKey.StartsWith('!') && paren > 0) name = name[..paren];
        return name.ToUpper(CultureInfo.CurrentCulture);
    }

    private static void KspPress(string value, Action<string> log)
    {
        if (KspTelemachus.Find(value) is { Press: { Length: > 0 } press })
            TelemachusClient.Send(press, log);
    }

    private static void ApplyKspStyle(LiveTileRenderer.EdState state)
    {
        if (IconStyleScope.Current is not { } spec) return;
        bool lit = state is LiveTileRenderer.EdState.On or LiveTileRenderer.EdState.Alarm;
        if (GameProfileTheme.BgImagePathFor(GameProfileSpecs.KspId, lit) is { } bg)
            spec.BgImagePath = bg;
        spec.TextColor = GameProfileTheme.TextHexFor(GameProfileSpecs.KspId, lit);
    }

    private static KspReading KspRead(KspItem item)
    {
        var s = TelemachusClient.Want(item.ReadKeys());

        // A command with nothing to report is "ready" whenever the mod answers.
        if (item.Read is null)
            return s.Alive ? new("", "", null, LiveTileRenderer.EdState.On) : KspUnknown;

        if (s.Get(item.Read) is not { } v) return KspUnknown;
        var on = LiveTileRenderer.EdState.On;

        switch (item.Format)
        {
            case KspFormat.Lamp:
            {
                bool lit = item.LitWhen is { } word
                    ? string.Equals(KspText(v), word, StringComparison.OrdinalIgnoreCase)
                    : KspBool(v);
                return new("", "", null, lit ? on : LiveTileRenderer.EdState.Off);
            }
            case KspFormat.Text:
            {
                string t = KspText(v);
                // Telemachus answers "" / "No Target Selected." for things that are not there.
                return t.Length == 0 || t.StartsWith("No ", StringComparison.Ordinal)
                    ? KspUnknown with { Lamp = LiveTileRenderer.EdState.Off }
                    : new(t, "", null, on);
            }
            case KspFormat.SasMode:
            {
                string mode = KspText(v);
                return mode.Length == 0 ? KspUnknown : new(Loc.Get(KspTelemachus.SasModeLocKey(mode)), "", null, on);
            }
        }

        if (KspNumber(v) is not { } n) return KspUnknown;

        switch (item.Format)
        {
            case KspFormat.Resource:
            {
                // -1 is the mod's "this craft has none"; a zero capacity is the same thing.
                if (s.Get(item.ReadMax) is not { } mv || KspNumber(mv) is not { } max || max <= 0 || n < 0)
                    return KspUnknown with { Lamp = LiveTileRenderer.EdState.Off };
                double f = Math.Clamp(n / max, 0, 1);
                return new(Pct(f), "", f, on);
            }
            case KspFormat.Fraction:
            {
                double f = Math.Clamp(n, 0, 1);
                return new(Pct(f), "", f, on);
            }
            case KspFormat.Distance:
            {
                var (t, u) = KspDistance(n);
                return new(t, u, null, on);
            }
            case KspFormat.Speed:
                return Math.Abs(n) >= 10_000
                    ? new(Num(n / 1000, 1), "km/s", null, on)
                    : new(Num(n, Math.Abs(n) >= 1000 ? 0 : 1), "m/s", null, on);
            case KspFormat.Duration:
            {
                // Negative durations are the mod's "no such event" (no impact, no encounter).
                if (n < 0) return KspUnknown with { Lamp = LiveTileRenderer.EdState.Off };
                var (t, u) = KspDuration(n);
                return new(t, u, null, on);
            }
            // The degree sign rides on the number: alone in the unit strip it is drawn at the
            // strip's size and comes out a speck.
            case KspFormat.Angle:       return new(Num(n, 1) + "°", "", null, on);
            case KspFormat.GForce:      return new(Num(n, 2), "g", null, on);
            case KspFormat.Pressure:    return new(Num(n, n >= 100 ? 0 : 1), "kPa", null, on);
            case KspFormat.Temperature: return new(Num(n, 0), "K", null, on);
            case KspFormat.Mach:        return new(Num(n, 2), "Mach", null, on);
            case KspFormat.Mass:        return new(Num(n, n >= 100 ? 0 : 1), "t", null, on);
            case KspFormat.Integer:     return new(Math.Round(n).ToString("0", CultureInfo.CurrentCulture), "", null, on);
            case KspFormat.Warp:        return new("×" + Num(n, n < 10 && n % 1 != 0 ? 1 : 0), "", null,
                                                   n > 1 ? LiveTileRenderer.EdState.Alarm : on);
            case KspFormat.Funds:
                return Math.Abs(n) >= 1_000_000 ? new(Num(n / 1_000_000, 2), "M √", null, on)
                     : Math.Abs(n) >= 10_000    ? new(Num(n / 1000, 1), "k √", null, on)
                     : new(Num(n, 0), "√", null, on);
            default:
                return new(Num(n, Math.Abs(n) >= 100 ? 0 : 2), "", null, on);
        }
    }

    private static string Num(double v, int decimals) =>
        v.ToString("F" + decimals, CultureInfo.CurrentCulture);

    private static string Pct(double f) => Math.Round(f * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Metres as the game's own altimeter scales them, kept to ~5 characters so the value
    /// fits the tile at the size every other number on the page uses.</summary>
    private static (string Text, string Unit) KspDistance(double m)
    {
        double a = Math.Abs(m);
        if (a < 10_000) return (Num(m, 0), "m");
        if (a < 10_000_000) return (Num(m / 1e3, a < 100_000 ? 1 : 0), "km");
        if (a < 10_000_000_000) return (Num(m / 1e6, a < 100_000_000 ? 1 : 0), "Mm");
        return (Num(m / 1e9, 1), "Gm");
    }

    /// <summary>Seconds as a countdown: m:ss under an hour, h:mm under a Kerbin day, then days —
    /// KERBIN days of 6 hours, the unit every clock in the game uses.</summary>
    private static (string Text, string Unit) KspDuration(double seconds)
    {
        const double KerbinDay = 6 * 3600;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        if (seconds < 3600) return ($"{(int)t.TotalMinutes}:{t.Seconds:00}", "min");
        if (seconds < KerbinDay) return ($"{(int)t.TotalHours}:{t.Minutes:00}", "h");
        double days = seconds / KerbinDay;
        return (Num(days, days < 100 ? 1 : 0), Loc.Get("ksp_unit_days"));
    }

    private static double? KspNumber(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDouble(out double d) && double.IsFinite(d) => d,
        JsonValueKind.True  => 1,
        JsonValueKind.False => 0,
        _ => null,
    };

    private static bool KspBool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0,
        JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) && b,
        _ => false,
    };

    private static string KspText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        _ => "",
    };
}
