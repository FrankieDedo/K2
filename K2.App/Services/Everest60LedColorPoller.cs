using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace K2.App.Services;

/// <summary>
/// Periodic polling of the Everest 60's current LED colors via
/// <see cref="Everest60Service.TryGetColorData"/> (raw HID, NOT the vendor
/// SDK — see its doc comment for why: <c>Everest60SdkNative.GetColorData2</c>
/// was found 2026-07-13, via a real Base Camp USB capture, to reliably fail
/// whenever a Makalu mouse is also connected, while raw-HID lighting on the
/// same interface never failed in any test). Mirrors <see cref="LedColorPoller"/>'s
/// role for Everest Max/MacroPad.
///
/// <para>
/// 300ms interval (Base Camp's own verified cadence for this device,
/// decompiled 2026-07-11 from <c>BaseCamp.UI.dll</c>'s
/// <c>EverestMiniController.GetColorData</c> websocket handler). Was sped up
/// to 60ms while backed by the SDK session (a single fast P/Invoke call);
/// reverted to 300ms 2026-07-13 switching to raw HID, since a full readback
/// is now <see cref="Everest60Protocol.ReadColorData"/>'s 10 sequential
/// Feature Report round-trips (~15ms apart each, see its doc comment) — a
/// tighter interval would overlap ticks. The read itself runs on a
/// background thread (<see cref="Task.Run(Action)"/>) with an in-flight
/// guard, NOT on the DispatcherTimer's own UI-thread callback, since ~150ms
/// of blocking HID I/O on the UI thread would stutter the window every tick.
/// </para>
/// </summary>
internal sealed class Everest60LedColorPoller : IDisposable
{
    private readonly Everest60Service _ev60;
    private readonly DispatcherTimer _timer;
    private readonly EverestSdkNative.FWColor[] _buf = new EverestSdkNative.FWColor[Everest60Protocol.ColorEntryCount];
    private volatile bool _inFlight;

    /// <summary>Updated colors, 192 elements indexed by firmware LED hardware
    /// address (see Everest60Protocol.LedIndex/SideLedIndex). Raised on the UI thread.</summary>
    public event Action<EverestSdkNative.FWColor[]>? ColorsUpdated;

    public Everest60LedColorPoller(Everest60Service ev60)
    {
        _ev60 = ev60;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        App.WriteLog("[Ev60-POLL] started (raw HID)");
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        App.WriteLog("[Ev60-POLL] stopped");
        _timer.Stop();
    }

    /// <summary>Skips a tick if the previous read hasn't finished yet (10
    /// sequential HID round-trips can occasionally run past 300ms) rather
    /// than piling up overlapping background reads. Only failures are
    /// logged, same as a plain error path.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_inFlight) return;
        _inFlight = true;
        var dispatcher = _timer.Dispatcher;
        Task.Run(() =>
        {
            bool ok = _ev60.TryGetColorData(_buf);
            _inFlight = false;
            if (ok)
                dispatcher.BeginInvoke(() => ColorsUpdated?.Invoke(_buf));
        });
    }

    public void Dispose() => _timer.Stop();
}
