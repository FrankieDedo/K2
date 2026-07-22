// MainWindow.HwBusy.cs — partial class: blocking hardware-write overlay.
//
// StartPicUpdate (the Mountain SDK's picture-upload export) is synchronous and takes ~2s per
// image (see K2/_reference/usb_dumps analysis, 2026-07-16): the whole header+chunked-transfer
// USB dance runs on the calling thread before it returns. Base Camp shows a blocking "please
// wait" message for the duration instead of leaving the app looking frozen — this mirrors that.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace K2.App;

public partial class MainWindow
{
    private Cursor? _hwBusyPrevCursor;

    private void ShowHwBusy(string message)
    {
        TxtHwBusyMessage.Text = message;
        PnlHwBusy.Visibility = Visibility.Visible;
        _hwBusyPrevCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
    }

    private void HideHwBusy()
    {
        PnlHwBusy.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = _hwBusyPrevCursor;
    }

    /// <summary>
    /// Shows the blocking "please wait" overlay, runs <paramref name="work"/> on a background
    /// thread, and blocks the calling (UI) method until it finishes — same call-site contract
    /// as calling <paramref name="work"/> inline, but the UI thread is actually free to paint
    /// while it waits, via a nested <see cref="DispatcherFrame"/> pump (the same technique
    /// <c>ShowDialog</c> uses internally).
    /// </summary>
    /// <remarks>
    /// Replaces the previous approach (a single <c>Dispatcher.Invoke(…, Background)</c> pump
    /// to force one render frame, then calling the ~2s synchronous SDK export inline) — that
    /// relied on painting one frame and hoping the OS compositor kept presenting it while the
    /// SAME thread then blocked solid inside the P/Invoke call. Confirmed on real hardware
    /// (2026-07-18) that this does not reliably show the overlay at all. Genuinely freeing the
    /// UI thread for the duration is the only approach that's guaranteed to paint.
    /// <para/>
    /// <paramref name="work"/> runs off the UI thread, so it must not touch WPF elements
    /// directly — use <see cref="LogEverestSafe"/> (or <c>Dispatcher.BeginInvoke</c>) for
    /// logging from inside it. It's safe to call Everest SDK methods from here even while the
    /// LED-preview poller keeps ticking on the UI thread: <c>EverestService</c> serializes all
    /// SDK access through <c>_sdkLock</c>, and the poller's reads use a non-blocking
    /// <c>Monitor.TryEnter</c> that just skips a tick if the lock is held — no concurrent
    /// native call, no risk of the SDKDLL crashes this project has hit before under real
    /// contention.
    /// </remarks>
    private T RunHwBusy<T>(string message, Func<T> work)
    {
        ShowHwBusy(message);
        try
        {
            T result = default!;
            Exception? error = null;
            var frame = new DispatcherFrame();
            Task.Run(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)); }
            });
            Dispatcher.PushFrame(frame);
            if (error is not null) throw error;
            return result;
        }
        finally { HideHwBusy(); }
    }

    private void RunHwBusy(string message, Action work) =>
        RunHwBusy<object?>(message, () => { work(); return null; });
}
