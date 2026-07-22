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
    private readonly Action _onTimeout;
    private readonly Action _onWake;
    private bool _enabled;
    private bool _forcedOff;

    public BacklightIdleTimer(Dispatcher dispatcher, Action onTimeout, Action onWake)
    {
        _onTimeout = onTimeout;
        _onWake = onWake;
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
                _onWake();
            }
            return;
        }
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    /// <summary>Call on every physical key/button event from the device.
    /// Restarts the countdown and, if the backlight was forced off, wakes it.</summary>
    public void RegisterActivity()
    {
        if (_forcedOff)
        {
            _forcedOff = false;
            _onWake();
        }
        if (!_enabled) return;
        _timer.Stop();
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_forcedOff) return;
        _forcedOff = true;
        _onTimeout();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
