using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// The full screen-reading library, reachable from the studio's "Browse" button.
///
/// <para>
/// It exists because the studio's own probe combo is FILTERED: it lists the readings taken from
/// the profile's process, which is what you want ninety-nine times out of a hundred and is exactly
/// wrong the one time you are reusing a reading across games, or picking one up again after the
/// profile's process name changed. So this dialog starts UNFILTERED — the checkbox narrows it to
/// the profile's process rather than widening it — and hands the chosen id back to the combo,
/// which then keeps showing it even though it does not match the filter.
/// </para>
/// </summary>
public partial class ScreenProbePickerDialog : Window
{
    /// <summary>The process the filter checkbox narrows to; empty when the profile has none, in
    /// which case the checkbox is not offered at all (it could only ever empty the list).</summary>
    private readonly string _process;

    /// <summary>The probe the list should come up on: the one the caller was already using, then
    /// whatever was last made or edited here. Not readonly — the list is refilled every time the
    /// library changes, and it must not jump back to the caller's choice each time.</summary>
    private string? _pick;

    /// <summary>Id of the probe the user picked. Only meaningful when ShowDialog returned true.</summary>
    public string? SelectedProbeId { get; private set; }

    /// <param name="process">Process name of the profile the action belongs to, without extension.
    /// Empty/null disables the filter.</param>
    /// <param name="currentProbeId">Probe currently assigned to the action, so the list opens on it.</param>
    public ScreenProbePickerDialog(string? process, string? currentProbeId)
    {
        InitializeComponent();
        _process = (process ?? "").Trim();
        _pick = currentProbeId;

        if (_process.Length == 0) ChkFilter.Visibility = Visibility.Collapsed;
        Fill();
    }

    private void Fill()
    {
        bool filter = _process.Length > 0 && ChkFilter.IsChecked == true;

        List<ScreenProbe> items = ScreenProbeStore.All()
            .Where(p => !filter || string.Equals(p.Process, _process, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        LstProbes.ItemsSource = items;
        LstProbes.SelectedItem = items.FirstOrDefault(p => p.Id == _pick) ?? items.FirstOrDefault();
        LblEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnEdit.IsEnabled = BtnDelete.IsEnabled = LstProbes.SelectedItem is ScreenProbe;
    }

    private void ChkFilter_Click(object sender, RoutedEventArgs e) => Fill();

    private void LstProbes_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BtnEdit.IsEnabled = BtnDelete.IsEnabled = LstProbes.SelectedItem is ScreenProbe;

    private void BtnNew_Click(object sender, RoutedEventArgs e) => OpenProbe(null);

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (LstProbes.SelectedItem is ScreenProbe p) OpenProbe(p.Id);
    }

    /// <summary>Calibration on the reading the list is on — or a brand new one. Whatever comes back
    /// is what the list re-opens on, so a reading made here is already picked when the dialog is
    /// closed with OK.</summary>
    private void OpenProbe(string? probeId)
    {
        var dlg = new ScreenProbeDialog(probeId) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // "" comes back from a delete inside the calibration window: nothing to re-select then.
        _pick = string.IsNullOrEmpty(dlg.SavedProbeId) ? null : dlg.SavedProbeId;
        Fill();
    }

    /// <summary>Removes a reading from the shared library, saying what it costs: probes are shared,
    /// so this can leave a tile in ANOTHER profile without a source (that tile then draws its
    /// labels and no value — the same state a new reading is in, not a crash).</summary>
    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LstProbes.SelectedItem is not ScreenProbe p) return;
        if (MessageBox.Show(this, Loc.Get("studio_probe_delete_confirm", p.Name),
                            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        ScreenProbeStore.Delete(p.Id);
        if (_pick == p.Id) _pick = null;
        Fill();
    }

    private void LstProbes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstProbes.SelectedItem is ScreenProbe) BtnOk_Click(sender, e);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (LstProbes.SelectedItem is not ScreenProbe p) return;
        SelectedProbeId = p.Id;
        DialogResult = true;
    }
}
