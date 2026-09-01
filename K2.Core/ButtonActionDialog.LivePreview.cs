using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: a 1 Hz "this is what the key would show right now" readout for
/// the "PC monitor" (<c>dp_sysmon</c>) action — its presets and its "pick any sensor" mode. The
/// value comes from the host (<see cref="IActionHost.PreviewLiveTile"/>) so K2.Core stays free
/// of any sensor backend; it is non-blocking and simply reads "—" until the backend warms up.
/// </summary>
public partial class ButtonActionDialog
{
    private DispatcherTimer? _livePreviewTimer;

    /// <summary>Runs the preview while the dialog is on "PC monitor"; a no-op otherwise.
    /// Called from <c>UpdatePanels</c>.</summary>
    private void UpdateLivePreview(string tag)
    {
        if (tag == "dp_sysmon")
        {
            if (_livePreviewTimer is null)
            {
                _livePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _livePreviewTimer.Tick += (_, _) => RefreshLivePreview();
                _livePreviewTimer.Start();
            }
            RefreshLivePreview();
        }
        else
        {
            _livePreviewTimer?.Stop();
            _livePreviewTimer = null;
            LblSysMonPreview.Text = "";
        }
    }

    private void RefreshLivePreview()
    {
        if (CurrentTag() != "dp_sysmon") return;
        LblSysMonPreview.Text = Compose(_host?.PreviewLiveTile("dp_sysmon", SaveSysMonSpec()));
    }

    private static string Compose(string? reading) =>
        string.IsNullOrWhiteSpace(reading) ? "" : string.Format(Loc.Get("sensor_preview_fmt"), reading);
}
