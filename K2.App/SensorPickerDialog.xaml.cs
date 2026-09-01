using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// HWiNFO-style hardware-sensor picker for a DisplayPad "PC monitor" (<c>dp_sysmon</c>) tile:
/// a searchable, grouped list of every sensor <see cref="HardwareSensors"/> has seen — icon,
/// kind, name, live value — plus a Current / Minimum / Maximum / Average selector. The result
/// is the tile's wire value <c>"&lt;lhm-id&gt;|&lt;stat&gt;|&lt;label&gt;"</c>
/// (see <see cref="ActionTypeHelper.ParseSensorValue"/>).
///
/// Opened from <see cref="ButtonActionDialog"/> via <see cref="IActionHost.PickSensorTileValue"/>
/// (only the K2.App DisplayPad host implements it). The list refreshes at 1 Hz while open;
/// selection and scroll survive the refresh because rows are updated in place, keyed by id.
/// </summary>
public partial class SensorPickerDialog : Window
{
    /// <summary>One row: identity + kind are fixed, <see cref="DisplayValue"/> tracks the
    /// chosen statistic and the 1 Hz refresh.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public string Id = "";
        public string Icon { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string HardwareName { get; set; } = "";
        public HardwareSensors.Group Group { get; set; }
        public string GroupName { get; set; } = "";

        private string _displayValue = "—";
        public string DisplayValue
        {
            get => _displayValue;
            set { if (_displayValue != value) { _displayValue = value; PropertyChanged?.Invoke(this, _dvArgs); } }
        }

        private static readonly PropertyChangedEventArgs _dvArgs = new(nameof(DisplayValue));
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Chosen wire value — valid only when <see cref="Window.ShowDialog"/> returned true.</summary>
    public string? ResultValue { get; private set; }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly Dictionary<string, Row> _byId = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer;
    private readonly string? _seedId;

    public SensorPickerDialog(string? currentValue)
    {
        InitializeComponent();

        (_seedId, string? seedStat) = ParseSeed(currentValue);
        (seedStat switch
        {
            "min" => RbStatMin,
            "max" => RbStatMax,
            "avg" => RbStatAvg,
            _     => RbStatCur,
        }).IsChecked = true;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.GroupName)));
        _view.SortDescriptions.Add(new SortDescription(nameof(Row.Group),        ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(Row.HardwareName), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(Row.Kind),         ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(Row.Name),         ListSortDirection.Ascending));
        _view.Filter = FilterRow;
        LvSensors.ItemsSource = _view;

        // LHM's first open loads a kernel driver and walks the whole hardware tree — never
        // inline on the UI thread. Kick it off in the background; the 1 Hz timer (and the
        // ContinueWith below) fill the list in once sensors start reporting.
        LblStatus.Text = Loc.Get("sensor_starting");
        System.Threading.Tasks.Task.Run(HardwareSensors.Start)
            .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { Refresh(); TrySelectSeed(); })),
                          System.Threading.Tasks.TaskScheduler.Default);

        Refresh();
        TrySelectSeed();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private bool _seedSelected;

    /// <summary>Selects the row for the value the dialog opened on, once it has appeared in the
    /// list (sensors trickle in as LHM warms up). No-op after it has been done once, so a later
    /// refresh can't yank the selection back from wherever the user moved it.</summary>
    private void TrySelectSeed()
    {
        if (_seedSelected || _seedId is null) return;
        if (!_byId.TryGetValue(_seedId, out var seedRow)) return;
        _seedSelected = true;
        LvSensors.SelectedItem = seedRow;
        LvSensors.ScrollIntoView(seedRow);
    }

    private static (string? Id, string? Stat) ParseSeed(string? value)
    {
        if (ActionTypeHelper.ParseSensorValue(value) is { } s) return (s.Id, s.Stat);
        return (null, null);
    }

    private HardwareSensors.Stat CurrentStat() =>
        RbStatMin.IsChecked == true ? HardwareSensors.Stat.Minimum :
        RbStatMax.IsChecked == true ? HardwareSensors.Stat.Maximum :
        RbStatAvg.IsChecked == true ? HardwareSensors.Stat.Average :
                                      HardwareSensors.Stat.Current;

    private void Refresh()
    {
        var stat = CurrentStat();
        var seen = HardwareSensors.Snapshot();

        foreach (var r in seen)
        {
            if (_byId.TryGetValue(r.Id, out var row))
            {
                row.DisplayValue = r.Display(stat);
            }
            else
            {
                row = new Row
                {
                    Id           = r.Id,
                    Icon         = r.Icon,
                    Kind         = r.Kind,
                    Name         = r.Name,
                    HardwareName = r.HardwareName,
                    Group        = r.Group,
                    GroupName    = GroupLabel(r.Group) + " · " + r.HardwareName,
                    DisplayValue = r.Display(stat),
                };
                _byId[r.Id] = row;
                _rows.Add(row);
            }
        }

        TrySelectSeed();

        bool ready = HardwareSensors.Available;
        LblListOverlay.Visibility =
            _rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        LblListOverlay.Text = ready ? Loc.Get("sensor_unavailable") : Loc.Get("sensor_starting");

        LblStatus.Text =
            !ready            ? Loc.Get("sensor_starting")
            : _rows.Count == 0 ? Loc.Get("sensor_unavailable")
            :                    string.Format(Loc.Get("sensor_count_fmt"), _rows.Count);
    }

    private bool FilterRow(object o)
    {
        string q = TxtSearch.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        if (o is not Row r) return false;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.HardwareName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => _view.Refresh();

    private void Stat_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh();
    }

    private void BtnResetStats_Click(object sender, RoutedEventArgs e)
    {
        HardwareSensors.ResetStats();
        Refresh();
    }

    private void LvSensors_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LvSensors.SelectedItem is Row) BtnOk_Click(sender, e);
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (LvSensors.SelectedItem is not Row row)
        {
            LblStatus.Text = Loc.Get("sensor_pick_none");
            return;
        }

        string label = row.Name.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
        ResultValue = $"{row.Id}|{HardwareSensors.StatWire(CurrentStat())}|{label}";
        DialogResult = true;
    }

    private static string GroupLabel(HardwareSensors.Group g) => g switch
    {
        HardwareSensors.Group.Cpu         => Loc.Get("sensor_grp_cpu"),
        HardwareSensors.Group.Gpu         => Loc.Get("sensor_grp_gpu"),
        HardwareSensors.Group.Memory      => Loc.Get("sensor_grp_memory"),
        HardwareSensors.Group.Storage     => Loc.Get("sensor_grp_storage"),
        HardwareSensors.Group.Motherboard => Loc.Get("sensor_grp_motherboard"),
        HardwareSensors.Group.Network     => Loc.Get("sensor_grp_network"),
        HardwareSensors.Group.Battery     => Loc.Get("sensor_grp_battery"),
        HardwareSensors.Group.Cooler      => Loc.Get("sensor_grp_cooler"),
        _                                 => Loc.Get("sensor_grp_other"),
    };
}
