using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using K2.Core;

namespace K2.App;

/// <summary>
/// "Device order" popup (General Settings tab). Lets the user reorder the CURRENTLY
/// CONNECTED devices; the saved order drives BOTH the top-level tab strip and the
/// Home cards (they are kept in sync by <c>MainWindow.ApplyDeviceOrder</c>, which
/// sorts the tabs and then rebuilds the Home tiles straight from the tab order).
///
/// Ordering is per physical unit, not per device kind: with three DisplayPads plugged
/// in there are three rows (each under its own tab label), freely interleavable with
/// the other devices. A disconnected device is never listed here; if it had been
/// ordered before, its position is kept in <see cref="AppSettings.DeviceOrder"/> and
/// restored silently on Save (see <see cref="DeviceOrderCatalog.Compose"/>) so it
/// reappears where the user left it once reconnected. A device that has never been
/// connected is never remembered or shown at all.
/// </summary>
public partial class DeviceOrderWindow : Window
{
    public sealed class Row
    {
        /// <summary>Order key: a static device kind, or "dp_&lt;id&gt;" for one DisplayPad.</summary>
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private readonly Action _onApplied;
    private readonly List<string> _loadedOrder;
    private readonly ObservableCollection<Row> _rows = new();

    /// <param name="rows">One row per CONNECTED device, already in the current order —
    /// see <c>MainWindow.BuildDeviceOrderRows</c>.</param>
    /// <param name="onApplied">Called after Save persists the new order, so the caller
    /// can re-sort its tabs/Home cards immediately.</param>
    public DeviceOrderWindow(IEnumerable<Row> rows, Action onApplied)
    {
        InitializeComponent();
        _onApplied = onApplied;
        // The RAW saved list (never default-filled — see DeviceOrderCatalog.Resolve's
        // doc comment): only devices the user has actually connected and ordered before
        // ever end up in here, which is exactly what Compose is allowed to restore below.
        _loadedOrder = new List<string>(AppSettings.DeviceOrder);
        LvDeviceOrder.ItemsSource = _rows;
        foreach (var r in rows) _rows.Add(r);
        TxtDeviceOrderEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Move(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        int i = _rows.ToList().FindIndex(r => r.Key == key);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        _rows.Move(i, j);
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)   => Move(sender, -1);
    private void BtnMoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    /// <summary>Back to the built-in order. A stable sort, so several DisplayPads — which
    /// all rank at the same built-in "displaypad" slot — keep their current relative
    /// order rather than being shuffled.</summary>
    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = DeviceOrderCatalog.DefaultOrder.ToList();
        var sorted = _rows.OrderBy(r => DeviceOrderCatalog.Rank(r.Key, defaults)).ToList();
        _rows.Clear();
        foreach (var r in sorted) _rows.Add(r);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.SetDeviceOrder(DeviceOrderCatalog.Compose(_rows.Select(r => r.Key), _loadedOrder));
        _onApplied();
        DialogResult = true;
    }
}
