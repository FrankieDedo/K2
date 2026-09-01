using System;
using System.Windows.Threading;

namespace K2.Core.Services;

/// <summary>
/// Software-only "turn off backlight after N seconds of inactivity" timer,
/// one instance per physical device. No K2-supported device firmware exposes
/// a native key-backlight auto-off (only Everest Max's Media Dock LCD has a
/// firmware timeout, via FW_EXTEND_INFO, unrelated to key lighting) — so this
/// tracks idle time in software and lets the caller wire it to that device's
/// existing brightness-set primitives via <paramref name="onTimeout"/>/
/// <paramref name="onWake"/>.
/// </summary>
public sealed class BacklightIdleTimer : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _wakeTimer;
    private readonly Action _onTimeout;
    private readonly Action _onWake;
    private readonly TimeSpan _wakeDelay;
    private bool _enabled;
    private bool _forcedOff;

    /// <param name="wakeDelayMs">
    /// When &gt; 0, the wake callback triggered by <see cref="RegisterActivity"/>
    /// is deferred by this many ms instead of running inline. Everest Max/60
    /// need this: the wake resends a full lighting effect (+ SaveFlash) over the
    /// SAME firmware that drives the HID keyboard endpoint, and doing it inline
    /// with the very keypress that woke the backlight stalls that endpoint long
    /// enough for Windows key-repeat to fire — the first key after idle then
    /// registers several times ("AAAAAAAA"). Letting the keystroke's HID report
    /// drain first avoids the collision. Manual toggles / feature-disable still
    /// wake immediately (not in the keypress path).
    /// </param>
    public BacklightIdleTimer(Dispatcher dispatcher, Action onTimeout, Action onWake, int wakeDelayMs = 0)
    {
        _onTimeout = onTimeout;
        _onWake = onWake;
        _wakeDelay = TimeSpan.FromMilliseconds(Math.Max(0, wakeDelayMs));
        // DispatcherPriority.Normal, NOT Background: on real Everest Max hardware,
        // an SDKDLL.dll call (SetEffect/SetBacklight) issued from a Background-priority
        // Tick permanently kills the SDK's KeyEvent callback delivery afterward (no
        // more physical key events ever again — confirmed 2026-07-20 on hardware; the
        // exact same call issued from a normal UI event handler, e.g. the brightness
        // slider, does NOT break it). Matching the dispatcher priority used by ordinary
        // UI-driven SDK calls avoids whatever timing/reentrancy issue this triggers in
        // the vendor SDK's internal thread.
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _timer.Tick += Timer_Tick;
        _wakeTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _wakeTimer.Tick += WakeTimer_Tick;
    }

    /// <summary>Call when the setting is loaded/changed. Disabling stops the
    /// timer outright and, if the backlight was already forced off, wakes it
    /// immediately (user request 2026-07-21: turning the feature off shouldn't
    /// leave the backlight stuck off with no key/activity ever coming to
    /// revive it — DisplayPad in particular has no manual backlight switch to
    /// fall back on).</summary>
    public void Configure(bool enabled, int seconds)
    {
        _enabled = enabled && seconds > 0;
        _timer.Stop();
        if (!_enabled)
        {
            if (_forcedOff)
            {
                _forcedOff = false;
                _wakeTimer.Stop();
                _onWake();
            }
            return;
        }
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    /// <summary>Call on every physical key/button event from the device.
    /// Restarts the countdown and, if the backlight was forced off, wakes it
    /// (deferred by <c>wakeDelayMs</c> when that was set — see the ctor).</summary>
    public void RegisterActivity()
    {
        if (_forcedOff)
        {
            _forcedOff = false;
            if (_wakeDelay > TimeSpan.Zero)
            {
                // Coalesced one-shot: _forcedOff is already cleared, so further
                // activity before it fires won't schedule a second wake.
                _wakeTimer.Stop();
                _wakeTimer.Interval = _wakeDelay;
                _wakeTimer.Start();
            }
            else
            {
                _onWake();
            }
        }
        if (!_enabled) return;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Call when the backlight got turned back on by a path OTHER than
    /// this timer — the user picked a new effect / moved the brightness slider /
    /// applied Custom while the backlight was idle-off. Clears the forced-off
    /// state and restarts the idle countdown WITHOUT invoking <c>onWake</c> (the
    /// caller already re-lit the device). Returns whether it had actually been
    /// forced off, so the caller can re-sync its own UI (e.g. the manual
    /// backlight checkbox).</summary>
    public bool NotifyWokenExternally()
    {
        bool wasForcedOff = _forcedOff;
        _wakeTimer.Stop();
        _forcedOff = false;
        if (_enabled)
        {
            _timer.Stop();
            _timer.Start();
        }
        return wasForcedOff;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_forcedOff) return;
        _forcedOff = true;
        _onTimeout();
    }

    private void WakeTimer_Tick(object? sender, EventArgs e)
    {
        _wakeTimer.Stop();
        if (_forcedOff) return; // re-armed in the meantime
        _onWake();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _wakeTimer.Stop();
        _wakeTimer.Tick -= WakeTimer_Tick;
    }
}
