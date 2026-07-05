using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Periodic polling of the current LED colors from the devices (Everest + MacroPad).
/// Uses a DispatcherTimer so events are raised on the UI thread,
/// allowing direct updates of WPF brushes without Dispatcher.Invoke.
///
/// Usage pattern:
///   1. Create an instance passing the device services
///   2. Subscribe to <see cref="EverestColorsUpdated"/> and/or <see cref="MacroPadColorsUpdated"/>
///   3. Call <see cref="Start"/>
///   4. In the handler, update the key brushes in the overlay
/// </summary>
internal sealed class LedColorPoller : IDisposable
{
    private readonly EverestService _everest;
    private readonly MacroPadService _macroPad;
    private readonly DispatcherTimer _timer;

    private bool _everestEnabled;
    private bool _macroPadEnabled;
    private uint _macroPadSlot = 1;

    // Buffers reused to avoid allocations on every tick
    private EverestSdkNative.KEYBOARD_COLOR _evColorBuf;
    private MacroPadSdkNative.MACROPAD_COLOR _mpColorBuf;

    /// <summary>
    /// Updated Everest colors. The array has 171 elements indexed by matrixId.
    /// Raised on the UI thread.
    /// </summary>
    public event Action<EverestSdkNative.FWColor[]>? EverestColorsUpdated;

    /// <summary>
    /// Updated MacroPad colors. The array has 126 elements indexed by matrixId.
    /// Raised on the UI thread.
    /// </summary>
    public event Action<MacroPadSdkNative.FWColor[]>? MacroPadColorsUpdated;

    public LedColorPoller(EverestService everest, MacroPadService macroPad)
    {
        _everest  = everest;
        _macroPad = macroPad;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>Polling interval (default 120ms ~ 8fps, a good compromise).</summary>
    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    /// <summary>Enables polling of Everest colors.</summary>
    public bool EverestEnabled
    {
        get => _everestEnabled;
        set => _everestEnabled = value;
    }

    /// <summary>Enables polling of MacroPad colors.</summary>
    public bool MacroPadEnabled
    {
        get => _macroPadEnabled;
        set => _macroPadEnabled = value;
    }

    /// <summary>MacroPad device slot to poll (default 1).</summary>
    public uint MacroPadSlot
    {
        get => _macroPadSlot;
        set => _macroPadSlot = value;
    }

    /// <summary>
    /// Emitted (on the UI thread) when the poller detects that the SDK has crashed.
    /// MainWindow can use it to show a message to the user.
    /// </summary>
    public event Action? SdkCrashDetected;

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private int _diagCount;

    private void OnTick(object? sender, EventArgs e)
    {
        // If the SDK has crashed, stop polling — calling the DLL
        // after its internal thread is dead would cause more crashes.
        if (App.SdkCrashRecoveryNeeded)
        {
            Stop();
            App.WriteLog("[LED-POLL] stopped: SdkCrashRecoveryNeeded");
            SdkCrashDetected?.Invoke();
            return;
        }

        // LED-poll diagnostic logging: noisy (called every 120ms), so it only
        // fires when the user has selected "Verbose" in General Settings.
        bool diag = AppSettings.LogLevel == K2LogLevel.Verbose && _diagCount < 30;

        if (_everestEnabled && _everest.IsOpen)
        {
            // DIAG tick#0: also test the raw (IntPtr) variant to rule out
            // marshalling issues with the ref KEYBOARD_COLOR struct.
            if (diag && _diagCount == 0)
            {
                const int bufSize = 171 * 3; // 513 byte = 171 FWColor
                IntPtr rawBuf = Marshal.AllocHGlobal(bufSize);
                try
                {
                    for (int i = 0; i < bufSize; i++)
                        Marshal.WriteByte(rawBuf, i, 0);
                    bool rawOk = _everest.TryGetColorDataRaw(rawBuf);
                    byte b0 = Marshal.ReadByte(rawBuf, 0);
                    byte b1 = Marshal.ReadByte(rawBuf, 1);
                    byte b2 = Marshal.ReadByte(rawBuf, 2);
                    byte b3 = Marshal.ReadByte(rawBuf, 3);
                    byte b4 = Marshal.ReadByte(rawBuf, 4);
                    byte b5 = Marshal.ReadByte(rawBuf, 5);
                    App.WriteLog($"[LED-POLL] DIAG GetColorDataRaw={rawOk} first6={b0:X2}{b1:X2}{b2:X2}{b3:X2}{b4:X2}{b5:X2}");
                }
                finally { Marshal.FreeHGlobal(rawBuf); }
            }

            // TryGetColorData uses Monitor.TryEnter: if the SDK lock is busy
            // (upload, SaveFlash, etc.) the tick is skipped — this avoids concurrent
            // access to the DLL that causes crashes at SDKDLL.dll+0x5133.
            bool ok = _everest.TryGetColorData(ref _evColorBuf);
            if (diag)
                App.WriteLog($"[LED-POLL] tick#{_diagCount} GetColorData={ok} colorNull={_evColorBuf.color == null}");
            if (ok && _evColorBuf.color != null)
            {
                EverestColorsUpdated?.Invoke(_evColorBuf.color);
            }
        }
        else if (diag)
        {
            App.WriteLog($"[LED-POLL] tick#{_diagCount} SKIP ev: enabled={_everestEnabled} open={_everest.IsOpen}");
        }

        if (_macroPadEnabled && _macroPad.IsOpen)
        {
            bool mpOk = MacroPadSdkNative.GetColorData(ref _mpColorBuf, _macroPadSlot);
            if (diag)
                App.WriteLog($"[LED-POLL] tick#{_diagCount} MP GetColorData={mpOk} slot={_macroPadSlot} colorNull={_mpColorBuf.color == null}");
            if (mpOk && _mpColorBuf.color != null)
            {
                MacroPadColorsUpdated?.Invoke(_mpColorBuf.color);
            }
        }
        else if (diag && _macroPadEnabled)
        {
            App.WriteLog($"[LED-POLL] tick#{_diagCount} SKIP mp: open={_macroPad.IsOpen}");
        }

        if (diag) _diagCount++;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
