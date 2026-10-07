using System;

namespace K2.App.Services;

/// <summary>
/// Battery level of the Makalu Max, ported from Base Camp's own Makalu.dll
/// (<c>Makalu.Battery()</c> in <c>_reference/BaseCamp_Decompiled/Makalu/Makalu.cs</c>).
/// <para>
/// Feature Report on interface 0, report ID 0, 9 bytes: set <c>00 C4 11 00…</c>, wait
/// 40 ms, get. A good answer is <c>00 B0 C4 pp v0 v1 v2</c> — <c>pp</c> the percentage the
/// mouse reports, <c>v</c> a 24-bit little-endian cell reading. Through the receiver Base
/// Camp shows <c>pp</c>; on the cable (charging) it ignores <c>pp</c> and derives the
/// level from <c>v</c> with the table below.
/// </para>
/// <para>
/// <b>Never run against a real Makalu Max</b> — decompile only, no capture. The raw answer
/// is logged whenever its outcome changes so a hardware report can confirm or correct it.
/// </para>
/// </summary>
internal sealed class MakaluMaxBattery
{
    public const int ReportSize = 9;

    public readonly record struct Reading(int Percent, bool Wired);

    private const int Samples = 3, MaxAttempts = 6, ReplyDelayMs = 40;

    /// <summary>Cell reading -> percentage while on the cable, as (above this, percent).</summary>
    private static readonly (int Above, int Percent)[] WiredTable =
    {
        (2872000, 100), (2860000, 95), (2848000, 90), (2836000, 85), (2824000, 80),
        (2812000, 75),  (2800000, 70), (2788000, 65), (2777000, 60), (2766000, 55),
        (2755000, 50),  (2744000, 45), (2730000, 40), (2724000, 35), (2710000, 30),
        (2695000, 25),  (2680000, 20), (2660000, 15), (2632000, 10), (2558000, 5),
    };

    private int _shown = -1;
    private bool _shownWired;
    private string _lastOutcome = "";

    /// <summary>One battery read. Blocking (~150 ms) — call off the UI thread. Null when no
    /// Makalu Max answers; the caller keeps whatever it was showing.</summary>
    public Reading? Read(Action<string> log)
    {
        var dev = MakaluHidNative.FindMaxBatteryDevice(log);
        if (dev is null) return Fail(log, "collection not found");

        using var h = MakaluHidNative.OpenQueryOnly(dev.Value.Path);
        if (h is null || h.IsInvalid) return Fail(log, "open failed");

        int good = 0, pctSum = 0;
        long rawSum = 0;
        byte[]? last = null, lastGood = null;
        for (int i = 0; i < MaxAttempts && good < Samples; i++)
        {
            var cmd = new byte[ReportSize];
            cmd[1] = 0xC4;
            cmd[2] = 0x11;
            last = MakaluHidNative.FeatureRoundTrip(h, cmd, ReplyDelayMs);
            if (last is null || last[1] != 0xB0 || last[2] != 0xC4 || last[3] > 100) continue;

            lastGood = last;
            good++;
            pctSum += last[3];
            rawSum += last[4] | (last[5] << 8) | (last[6] << 16);
        }
        if (good == 0)
            return Fail(log, "no valid answer, last=" + (last is null ? "transfer failed" : BitConverter.ToString(last)));

        bool wired = !dev.Value.Wireless;
        int raw = (int)(rawSum / good);
        int measured = wired ? FromCell(raw) : pctSum / good;
        int shown = Smooth(measured, wired);

        Note(log, $"ok {(wired ? "wired" : "wireless")}", $"pct={pctSum / good} cell={raw} -> {shown}% " +
                  $"(last={BitConverter.ToString(lastGood!)})");
        return new Reading(shown, wired);
    }

    private static int FromCell(int raw)
    {
        foreach (var (above, percent) in WiredTable)
            if (raw > above) return percent;
        return 0;
    }

    /// <summary>The level is noisy from one read to the next: like Base Camp, only follow
    /// it in the direction it can really go (down on battery, up on the cable) unless it
    /// jumps far enough to be a different state altogether.</summary>
    private int Smooth(int measured, bool wired)
    {
        bool jump = _shown < 0 || wired != _shownWired || Math.Abs(measured - _shown) >= (wired ? 20 : 35);
        if (jump || (wired ? measured > _shown : measured < _shown))
            _shown = measured;
        _shownWired = wired;
        return _shown;
    }

    private Reading? Fail(Action<string> log, string why)
    {
        Note(log, "fail", why);
        return null;
    }

    /// <summary>Logs only when the outcome changes — this runs once a minute forever.</summary>
    private void Note(Action<string> log, string outcome, string detail)
    {
        if (outcome == _lastOutcome) return;
        _lastOutcome = outcome;
        log($"[MakaluMaxBattery] {outcome}: {detail}");
    }
}
