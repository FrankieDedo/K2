using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "Screen reading" (<c>dp_screen</c>) action — a key that shows
/// a value K2 measures on another program's window.
///
/// <para>
/// There is no fixed list of values for this type, because the values are the ones the USER
/// defined: the picker's cards are the probes on this machine, plus one card that creates a new
/// one. Everything about a probe — which window, which rectangle, which colour — lives in K2.App
/// (<c>Services.ScreenProbeStore</c>) and is reached through <see cref="IActionHost"/>, the same
/// seam the macro library and the hardware-sensor picker already use, so K2.Core neither knows
/// how a screen is captured nor needs to.
/// </para>
///
/// <para>
/// Wire format: <c>"&lt;probe-id&gt;|&lt;name&gt;"</c>. The name rides along so a key can be
/// listed, captioned and summarised without asking the host — see
/// <see cref="ActionTypeHelper.ParseScreenValue"/>.
/// </para>
/// </summary>
public partial class ButtonActionDialog
{
    /// <summary>Tag of the synthetic "New reading…" card. Contains a character no probe id can
    /// (ids are <c>p</c> + hex), so it can never collide with a real wire value.</summary>
    private const string ScreenProbeNewTag = "__screen_probe_new__";

    /// <summary>"Edit reading…" — opens the calibration window on the probe the key currently
    /// points at, or creates one when the key has none yet.</summary>
    private void BtnScreenProbeEdit_Click(object sender, RoutedEventArgs e) =>
        OpenScreenProbeEditor(SelectedProbeId());

    /// <summary>Opens the host's calibration window and, on a save, re-lists the probes and
    /// selects the one that came back. Cancel changes nothing — including the current selection,
    /// which matters because the "New reading…" card is a card like the others and clicking it
    /// must not leave the key bound to a sentinel.</summary>
    private void OpenScreenProbeEditor(string? probeId)
    {
        string? saved = _host?.EditScreenProbe(probeId);
        if (saved is null)
        {
            // Cancelled: nothing changes — but make sure the sentinel card isn't left selected,
            // which is what clicking "New reading…" and backing out would otherwise leave behind.
            if (SelectedRawTag() == ScreenProbeNewTag) SelectProbe(probeId);
            return;
        }

        // "" comes back from a DELETE: re-list (the probe is gone) and select nothing, so the key
        // is honestly unconfigured instead of pointing at something that no longer exists.
        string name = saved.Length == 0
            ? ""
            : _host?.ListScreenProbes().FirstOrDefault(p => p.Id == saved).Name ?? "";
        PopulateCombo("dp_screen", saved.Length == 0 ? "" : $"{saved}|{name}");
        UpdateSubActionCrumb();
        RefreshLivePreview();
    }

    /// <summary>The combo's raw tag, sentinel included — only <see cref="OpenScreenProbeEditor"/>
    /// wants to see the sentinel; everything else goes through <see cref="SelectedProbeId"/>.</summary>
    private string SelectedRawTag() =>
        CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";

    /// <summary>Id of the probe the dialog is currently on, or null when it is on the "new" card
    /// or on nothing at all.</summary>
    private string? SelectedProbeId()
    {
        string raw = SelectedRawTag();
        if (raw.Length == 0 || raw == ScreenProbeNewTag) return null;
        return ActionTypeHelper.ParseScreenValue(raw)?.Id;
    }

    private void SelectProbe(string? probeId)
    {
        var match = CbComboValue.Items.OfType<ComboBoxItem>().FirstOrDefault(
            i => (string?)i.Tag is { } t && t != ScreenProbeNewTag &&
                 ActionTypeHelper.ParseScreenValue(t)?.Id == probeId);
        CbComboValue.SelectedItem = match;   // null clears it, which is the honest state
    }

    /// <summary>The saved value: the selected probe's wire string, or "" when the key is on the
    /// "new" card or on nothing — an unconfigured screen key stores nothing rather than a
    /// sentinel that would later read as a missing probe.</summary>
    private string SaveScreenProbeSpec()
    {
        string raw = SelectedRawTag();
        return raw == ScreenProbeNewTag ? "" : raw;
    }
}
