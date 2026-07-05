using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using K2.Core;

namespace K2.App.Services;

/// <summary>
/// Polling periodico dei colori LED correnti dai dispositivi (Everest + MacroPad).
/// Usa un DispatcherTimer in modo che gli eventi vengano emessi sul thread UI,
/// permettendo l'aggiornamento diretto dei brush WPF senza Dispatcher.Invoke.
///
/// Pattern d'uso:
///   1. Creare istanza passando i servizi device
///   2. Sottoscrivere <see cref="EverestColorsUpdated"/> e/o <see cref="MacroPadColorsUpdated"/>
///   3. Chiamare <see cref="Start"/>
///   4. Nel handler aggiornare i brush dei tasti nell'overlay
/// </summary>
internal sealed class LedColorPoller : IDisposable
{
    private readonly EverestService _everest;
    private readonly MacroPadService _macroPad;
    private readonly DispatcherTimer _timer;

    private bool _everestEnabled;
    private bool _macroPadEnabled;
    private uint _macroPadSlot = 1;

    // Buffer riusati per evitare allocazioni a ogni tick
    private EverestSdkNative.KEYBOARD_COLOR _evColorBuf;
    private MacroPadSdkNative.MACROPAD_COLOR _mpColorBuf;

    /// <summary>
    /// Colori Everest aggiornati. L'array ha 171 elementi indicizzati per matrixId.
    /// Emesso sul thread UI.
    /// </summary>
    public event Action<EverestSdkNative.FWColor[]>? EverestColorsUpdated;

    /// <summary>
    /// Colori MacroPad aggiornati. L'array ha 126 elementi indicizzati per matrixId.
    /// Emesso sul thread UI.
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

    /// <summary>Intervallo di polling (default 120ms ≈ 8fps, buon compromesso).</summary>
    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    /// <summary>Abilita il polling dei colori Everest.</summary>
    public bool EverestEnabled
    {
        get => _everestEnabled;
        set => _everestEnabled = value;
    }

    /// <summary>Abilita il polling dei colori MacroPad.</summary>
    public bool MacroPadEnabled
    {
        get => _macroPadEnabled;
        set => _macroPadEnabled = value;
    }

    /// <summary>Slot device MacroPad da interrogare (default 1).</summary>
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
            App.WriteLog("[LED-POLL] fermato: SdkCrashRecoveryNeeded");
            SdkCrashDetected?.Invoke();
            return;
        }

        // LED-poll diagnostic logging: noisy (called every 120ms), so it only
        // fires when the user has selected "Verbose" in General Settings.
        bool diag = AppSettings.LogLevel == K2LogLevel.Verbose && _diagCount < 30;

        if (_everestEnabled && _everest.IsOpen)
        {
            // DIAG tick#0: testa anche la variante raw (IntPtr) per escludere
            // problemi di marshalling della struct ref KEYBOARD_COLOR.
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
            // (upload, SaveFlash, ecc.) salta il tick — evita accesso concorrente
            // alla DLL che causa crash a SDKDLL.dll+0x5133.
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
