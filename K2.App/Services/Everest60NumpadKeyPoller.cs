using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace K2.App.Services;

/// <summary>
/// Periodic polling for numpad accessory key press events, via
/// <see cref="Everest60Service.TryQueryNumpadKeyEvent"/> (same raw-HID
/// feature-report channel as <see cref="Everest60LedColorPoller"/> — see
/// <see cref="Everest60Protocol.NumpadKeyBinding"/> for the protocol).
///
/// <para>
/// Unlike <see cref="Everest60LedColorPoller"/> (started/stopped with the
/// Lighting section's visibility), this poller is "always on" once started —
/// mirrors MainWindow.Everest60.cs's own <c>_ev60PollTimer</c> lifecycle
/// (started once in InitEverest60Module, never stopped by section changes),
/// since a numpad key press should fire its action regardless of which
/// section/tab is currently showing.
/// </para>
///
/// <para>
/// 100ms interval — tighter than the LED poller's 300ms since this is
/// direct user input (perceived latency matters more than for a color
/// preview), and a single cmd 0x08 round-trip is one Feature Report pair,
/// not a multi-page sweep.
/// </para>
///
/// <para>
/// <b>Edge detection</b> (reworked 2026-07-27 after a real-hardware report that a
/// bound numpad key "only opens sometimes"): the device bumps its event counter
/// TWICE per tap — once for the down, once for the up. Sampling every 100ms, a tap
/// shorter than the interval is only ever observed in its released state, and the
/// original "fire when the counter changed AND the sample says pressed" rule dropped
/// it silently. A tap is now also recognised from its up edge, by the counter having
/// advanced by more than one since the last sample (the skipped event can only be
/// this key's own down). Still fails toward "nothing happens" rather than firing the
/// wrong action: the DllKeyId always comes from the sample in hand.
/// </para>
/// </summary>
internal sealed class Everest60NumpadKeyPoller : IDisposable
{
    private readonly Everest60Service _ev60;
    private readonly DispatcherTimer _timer;
    private volatile bool _inFlight;
    private int? _lastCounter;

    /// <summary>Raised on the UI thread when a numpad key's press edge is
    /// detected, with its DLLKeyId (see <see cref="Everest60RemapData.NumpadDllKeyId"/>
    /// to translate to a <c>KeyDef.NumpadIndex</c>).</summary>
    public event Action<int>? KeyPressed;

    public Everest60NumpadKeyPoller(Everest60Service ev60)
    {
        _ev60 = ev60;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        App.WriteLog("[Ev60-NumpadPoll] started");
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        App.WriteLog("[Ev60-NumpadPoll] stopped");
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_inFlight) return;
        _inFlight = true;
        var dispatcher = _timer.Dispatcher;
        Task.Run(() =>
        {
            var result = _ev60.TryQueryNumpadKeyEvent();
            _inFlight = false;
            if (result is not { } ev) return;

            // First sample only primes the baseline: the device reports whatever event
            // was last in its buffer, which may long predate K2 starting.
            if (_lastCounter is not int last) { _lastCounter = ev.Counter; return; }

            int advanced = (ev.Counter - last) & 0xFF;   // counter is one byte, it wraps
            if (advanced == 0) return;
            _lastCounter = ev.Counter;

            // Down edge caught directly, or caught late on the up edge of a tap whose
            // down fell between two polls (see the class doc's edge-detection note).
            if (ev.Pressed || advanced > 1)
                dispatcher.BeginInvoke(() => KeyPressed?.Invoke(ev.DllKeyId));
        });
    }

    public void Dispose() => _timer.Stop();
}
