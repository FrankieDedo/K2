using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: a 1 Hz "this is what the key would show right now" readout for
/// the "PC monitor" (<c>dp_sysmon</c>) action — its presets and its "pick any sensor" mode — and
/// for a "Screen reading" (<c>dp_screen</c>) key, where it is the only way to tell a probe that
/// is aimed correctly from one that is measuring empty screen. The
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
        if (tag is "dp_sysmon" or "dp_screen" or "dp_custom")
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
            LblScreenProbePreview.Text = "";
        }
    }

    private void RefreshLivePreview()
    {
        string tag = CurrentTag();
        if (tag == "dp_sysmon")
            LblSysMonPreview.Text = Compose(_host?.PreviewLiveTile("dp_sysmon", SaveSysMonSpec()));
        else if (tag is "dp_screen" or "dp_custom")
            // Both draw into the same readout line: only one of the two can be the current type,
            // and a second label would be an empty row under every screen key.
            LblScreenProbePreview.Text = Compose(_host?.PreviewLiveTile(tag, SaveComboSpec()));
    }

    private static string Compose(string? reading) =>
        string.IsNullOrWhiteSpace(reading) ? "" : string.Format(Loc.Get("sensor_preview_fmt"), reading);
}
