// MainWindow.HwBusy.cs — partial class: blocking hardware-write overlay.
//
// StartPicUpdate (the Mountain SDK's picture-upload export) is synchronous and takes ~2s per
// image (see K2/_reference/usb_dumps analysis, 2026-07-16): the whole header+chunked-transfer
// USB dance runs on the calling thread before it returns. Base Camp shows a blocking "please
// wait" message for the duration instead of leaving the app looking frozen — this mirrors that.

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace K2.App;

public partial class MainWindow
{
    private Cursor? _hwBusyPrevCursor;

    /// <summary>Shows the blocking overlay with <paramref name="message"/> and forces an
    /// immediate render pass — otherwise WPF wouldn't paint it until the UI thread yields,
    /// which never happens since the caller blocks it right after this call returns.</summary>
    private void ShowHwBusy(string message)
    {
        TxtHwBusyMessage.Text = message;
        PnlHwBusy.Visibility = Visibility.Visible;
        _hwBusyPrevCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private void HideHwBusy()
    {
        PnlHwBusy.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = _hwBusyPrevCursor;
    }
}
