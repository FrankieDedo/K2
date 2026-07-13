using System;
using System.Windows.Threading;

namespace K2.App.Services;

/// <summary>
/// Periodic polling of the Everest 60's current LED colors via
/// <see cref="Everest60SdkService.TryGetColorData"/>, mirroring
/// <see cref="LedColorPoller"/>'s role for Everest Max/MacroPad.
///
/// <para>
/// 60ms interval, same cadence as Everest Max/MacroPad (see LedColorPoller).
/// Originally 300ms (Base Camp's own verified cadence for this device,
/// decompiled 2026-07-11 from <c>BaseCamp.UI.dll</c>'s
/// <c>EverestMiniController.GetColorData</c> websocket handler — a plain
/// <c>Thread.Sleep(300)</c> loop), kept gentle out of caution because the
/// SDK-session/raw-HID coexistence on this device was unverified. 2026-07-12:
/// verified stable on real hardware across a full debugging session (numpad
/// detection + full-keyboard LED preview, no contention observed) — sped up
/// to 120ms, then to 60ms on user request once that held up too.
/// </para>
/// </summary>
internal sealed class Everest60LedColorPoller : IDisposable
{
    private readonly Everest60SdkService _sdk;
    private readonly DispatcherTimer _timer;
    private readonly EverestSdkNative.FWColor[] _buf = new EverestSdkNative.FWColor[Everest60SdkService.ColorEntryCount];
    private int _tick;

    /// <summary>Updated colors, 192 elements indexed by firmware LED hardware
    /// address (see Everest60Protocol.LedIndex/SideLedIndex). Raised on the UI thread.</summary>
    public event Action<EverestSdkNative.FWColor[]>? ColorsUpdated;

    public Everest60LedColorPoller(Everest60SdkService sdk)
    {
        _sdk = sdk;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _tick = 0;
        App.WriteLog($"[Ev60-POLL] started (sdk.IsOpen={_sdk.IsOpen})");
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        App.WriteLog("[Ev60-POLL] stopped");
        _timer.Stop();
    }

    /// <summary>Per-tick diagnostic logging (added 2026-07-12 while chasing the
    /// numpad/LED-preview hardware bugs, since confirmed fixed and verified on
    /// real hardware) is intentionally NOT here on the happy path anymore — at
    /// 120ms it would flood the log for no ongoing benefit. Only failures are
    /// logged, same as a plain error path.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        _tick++;
        if (!_sdk.IsOpen) return;
        if (_sdk.TryGetColorData(_buf))
            ColorsUpdated?.Invoke(_buf);
    }

    public void Dispose() => _timer.Stop();
}
