using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// "DisplayPad device mapping" popup (General Settings tab). Lets the user reassign
/// which STABLE logical id each currently-connected pad's raw SDK id maps to — the fix
/// for a pad's USB port changing and the SDK renumbering it (e.g. what used to report id
/// 2 now reports id 3, and vice versa), which otherwise makes K2 swap which physical
/// panel shows which stored profiles. See <see cref="RemappingDisplayPadClient"/> for how
/// the mapping saved here is actually applied, and <see cref="DisplayPadDeviceMap"/> for
/// persistence.
/// </summary>
public partial class DpDeviceMapWindow : Window
{
    public sealed class Row
    {
        public int RawId { get; set; }
        public string Firmware { get; set; } = "";
        public string CurrentLabel { get; set; } = "";
        public int BoardNumber { get; set; }
    }

    private readonly IDisplayPadClient _raw;
    private readonly Action _onApplied;
    private readonly ObservableCollection<Row> _rows = new();

    /// <param name="dpClient">MainWindow's <c>_dpClient</c> field — unwrapped to its raw,
    /// untranslated backend via <see cref="RemappingDisplayPadClient.RawInner"/> so this
    /// window can see the SDK's real ids, not the already-remapped logical ones.</param>
    /// <param name="currentLabels">SDK-id (already logical) -&gt; current tab label, from
    /// MainWindow's <c>_dpDeviceLabels</c> — used only to show a human-friendly "this is
    /// currently shown as..." hint per row.</param>
    /// <param name="onApplied">Called after Save persists the new mapping, so the caller
    /// can refresh its device list/tabs immediately.</param>
    public DpDeviceMapWindow(IDisplayPadClient dpClient, IReadOnlyDictionary<int, string> currentLabels, Action onApplied)
    {
        InitializeComponent();
        _raw = dpClient is RemappingDisplayPadClient rc ? rc.RawInner : dpClient;
        _onApplied = onApplied;
        LvDpDeviceMap.ItemsSource = _rows;
        LoadRows(currentLabels);
    }

    private void LoadRows(IReadOnlyDictionary<int, string> currentLabels)
    {
        _rows.Clear();
        var map = DisplayPadDeviceMap.GetAll();
        var ids = _raw.DeviceIds().Where(_raw.IsPlugged).ToList();

        TxtDpDeviceMapEmpty.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var rawId in ids)
        {
            int logical = map.TryGetValue(rawId, out var l) ? l : rawId;
            string label = currentLabels.TryGetValue(logical, out var lbl) ? lbl : $"DisplayPad {logical}";
            _rows.Add(new Row
            {
                RawId = rawId,
                Firmware = _raw.FirmwareVersion(rawId),
                CurrentLabel = label,
                BoardNumber = logical,
            });
        }
    }

    /// <summary>Blinks the physical panel's backlight (off/on ×3) so the user can tell
    /// which raw SDK id in the list corresponds to which physical pad — there is no
    /// hardware serial number exposed, so this is the only reliable way to identify one
    /// among several connected pads.</summary>
    private void BtnDpDeviceMapIdentify_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int rawId) return;
        _ = BlinkAsync(rawId);
    }

    private async Task BlinkAsync(int rawId)
    {
        int original = _raw.GetBrightness(rawId);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                _raw.SetBrightness(rawId, 0);
                await Task.Delay(220);
                _raw.SetBrightness(rawId, 100);
                await Task.Delay(220);
            }
        }
        finally
        {
            _raw.SetBrightness(rawId, original >= 0 ? original : 100);
        }
    }

    private void BtnDpDeviceMapSave_Click(object sender, RoutedEventArgs e)
    {
        var dupes = _rows.GroupBy(r => r.BoardNumber).Any(g => g.Count() > 1);
        if (dupes)
        {
            MessageBox.Show(this, Loc.Get("dp_devicemap_duplicate_error"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_rows.Any(r => r.BoardNumber <= 0))
        {
            MessageBox.Show(this, Loc.Get("dp_devicemap_invalid_number"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newMap = new Dictionary<int, int>(DisplayPadDeviceMap.GetAll());
        foreach (var row in _rows)
            newMap[row.RawId] = row.BoardNumber;
        DisplayPadDeviceMap.SetAll(newMap);

        _onApplied();
        DialogResult = true;
    }
}
