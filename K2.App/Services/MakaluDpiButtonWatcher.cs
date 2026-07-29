using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K2.App.Services;

/// <summary>
/// Watches the Makalu's DPI-button HID collection for its physical press — user request
/// 2026-07-27 ("fai la cosa del tasto che si illumina anche su makalu max"), confirmed
/// feasible via a real USB capture (_reference/usb_dumps/makalu_tasti.pcapng).
/// <para>
/// Unlike the mouse's 5 standard buttons (left/right/middle/back/forward — real OS mouse
/// clicks, see <see cref="RawMouseActivityWatcher"/>), the DPI button lives on its own HID
/// top-level collection under interface 1 (mi_01) — the same USB interface as the vendor
/// Feature-Report config channel <see cref="MakaluHidNative"/> already talks to, but a
/// DIFFERENT collection/device path (col01 vs col02). The capture shows pressing DPI sends
/// exactly ONE 8-byte input report, <c>03 02 01 00 00 00 00 00</c> — no distinct release, a
/// one-shot pulse rather than a press/hold/release state. This collection isn't a Generic
/// Desktop/Mouse usage, so Windows' mouse Raw Input decoding never sees it either; a plain
/// blocking HID ReadFile on its own device path is the only way in.
/// </para>
/// <para>
/// Since there's no release edge, the UI side (MainWindow.Makalu.cs's MkHighlightHotspot)
/// treats <see cref="DpiPressed"/> as a timed flash rather than a press/release pair.
/// </para>
/// <para>
/// 2026-07-28: the SAME collection also carries a "software-action button pressed"
/// notification — any button assigned a function that needs host execution (Run Program/
/// Open Folder confirmed so far, see <see cref="MakaluProtocol.ParseButtonEventReport"/>'s
/// doc) fires an 8-byte report here too, distinguished from the DPI pulse by carrying a real
/// 1-based button index at byte[4] (the DPI pulse always has byte[4]=0). Exposed as
/// <see cref="ButtonEvent"/>, kept on this same reader thread rather than a second one —
/// two background threads both blocking on ReadFile against the same HID collection is not
/// a pattern to introduce without a specific reason to.
/// </para>
/// </summary>
internal sealed class MakaluDpiButtonWatcher : IDisposable
{
    public const int InputReportSize = 8;

    private Thread? _thread;
    private SafeFileHandle? _handle;
    private volatile bool _stopping;
    private readonly Action<string>? _log;

    /// <summary>Raised off the UI thread (marshal via Dispatcher yourself) whenever the DPI
    /// button's one-shot report arrives.</summary>
    public event Action? DpiPressed;

    /// <summary>Raised off the UI thread (marshal via Dispatcher yourself) whenever a
    /// software-action button fires — see this class's doc. Args: (category, 1-based physical
    /// button index).</summary>
    public event Action<byte, int>? ButtonEvent;

    public MakaluDpiButtonWatcher(Action<string>? log = null) => _log = log;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Starts the background read loop, if not already running and the DPI-button
    /// collection can be found/opened right now. Safe to call repeatedly (e.g. every
    /// reconnect poll) — no-ops if already running, cheap to retry if the device wasn't
    /// found last time (e.g. not yet enumerated after a fresh plug-in).</summary>
    public bool Start()
    {
        if (IsRunning) return true;

        var found = MakaluHidNative.FindDpiButtonDevice(_log);
        if (found is not { } dev) return false;

        var h = MakaluHidNative.OpenForRead(dev.Path, _log);
        if (h is null) return false;

        _handle = h;
        _stopping = false;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "MakaluDpiRead" };
        _thread.Start();
        _log?.Invoke("[MakaluDpi] read thread started");
        return true;
    }

    /// <summary>Stops the read loop. Disposing the handle is what unblocks the background
    /// thread's pending (synchronous, non-overlapped) ReadFile call.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        _stopping = true;
        try { _handle?.Dispose(); } catch { /* best-effort — unblocks ReadLoop's ReadFile */ }
        _handle = null;
        _thread = null;
    }

    private void ReadLoop()
    {
        var buf = new byte[InputReportSize];
        var h = _handle;
        while (!_stopping && h is { IsClosed: false, IsInvalid: false })
        {
            bool ok;
            int read;
            try { ok = MakaluHidNative.ReadReport(h, buf, out read); }
            catch { break; } // handle disposed from Stop() mid-call
            if (_stopping) break;
            if (!ok) break; // device unplugged or handle closed
            if (read < 2) continue;

            // DPI native pulse: buttonIndex byte (buf[4]) is always 0 — checked explicitly so
            // a software-action category that happens to equal 0x02 (Keyboard Shortcut, per
            // MakaluProtocol's category table) on a REAL button never gets misread as a DPI
            // press, since a real button event always carries buf[4] in 1..8.
            if (buf[0] == 0x03 && buf[1] == 0x02 && (read < 5 || buf[4] == 0x00))
            {
                DpiPressed?.Invoke();
                continue;
            }

            var evt = MakaluProtocol.ParseButtonEventReport(buf);
            if (evt is { } e)
                ButtonEvent?.Invoke(e.Category, e.ButtonIndex1Based);
        }
        _log?.Invoke("[MakaluDpi] read thread exiting");
    }

    public void Dispose() => Stop();
}
