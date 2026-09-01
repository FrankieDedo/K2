using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: the "PC monitor" (<c>dp_sysmon</c>) panel — CPU/GPU
/// usage-vs-temperature and "which disk" for Disk. The metric is picked from the breadcrumb's
/// sub-action grid, which also carries a 7th card, "Sensor selection", that opens the
/// HWiNFO-style hardware-sensor picker (<see cref="SensorPickTag"/> / <c>SubActionCard_Click</c>).
///
/// Wire format: the bare metric token (unchanged, fully back-compatible), optionally suffixed
///   <c>cpu:temp</c> / <c>gpu:temp</c> / <c>disk:&lt;lhm-hw-id&gt;|&lt;name&gt;</c>, OR a full
///   arbitrary-sensor wire string <c>/&lt;lhm-id&gt;|&lt;stat&gt;|&lt;label&gt;</c>.
/// </summary>
public partial class ButtonActionDialog
{
    /// <summary>Tag of the synthetic "Sensor selection" sub-action card — never a real metric
    /// token or LHM identifier, so it can't collide with a stored value.</summary>
    private const string SensorPickTag = "__sysmon_sensor_pick__";

    /// <summary>The hardware-sensor wire string (<c>/&lt;lhm-id&gt;|&lt;stat&gt;|&lt;label&gt;</c>)
    /// last chosen for this key, "" until the picker is used. Kept out of the card's Tag (which
    /// stays the sentinel) so the card can always re-open the picker.</summary>
    private string _sysmonSensorWire = "";

    /// <summary>One entry in the "which disk" combo. <see cref="Id"/> is "" for the
    /// "All disks" sentinel (→ the metric stays the bare <c>disk</c> token).</summary>
    private sealed record DiskItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Suffix ("temp", "&lt;diskid&gt;|&lt;name&gt;", or "") parsed from the value on
    /// dialog-open, applied once <see cref="CbSysMonDisk"/> has been populated.</summary>
    private string _pendingSysMonArg = "";
    /// <summary>The disk id the dialog opened on ("" = All disks), re-applied when the async
    /// disk list finishes loading.</summary>
    private string _openedDiskId = "";
    private bool _sysMonDiskLoaded;
    private bool _sysMonLoading;

    private string CurrentSysMonMetric() =>
        CbComboValue.SelectedItem is ComboBoxItem ci ? (string?)ci.Tag ?? "" : "";

    /// <summary>Label for the "Sensor selection" card / breadcrumb crumb: the plain action name,
    /// plus the chosen sensor and statistic once one has been picked.</summary>
    private string SensorPickCardLabel()
    {
        string baseLabel = Loc.Get("sysmon_pick_sensor");
        return ActionTypeHelper.ParseSensorValue(_sysmonSensorWire) is { } s
            ? $"{baseLabel} · {s.Label} · {ActionTypeHelper.SensorStatLabel(s.Stat)}"
            : baseLabel;
    }

    /// <summary>Shows the usage/temperature radios for CPU/GPU and the disk combo for Disk;
    /// nothing extra for a sensor pick (the breadcrumb crumb + live preview carry it). Applies
    /// <see cref="_pendingSysMonArg"/> the first time it runs for a freshly-loaded value.</summary>
    private void RefreshSysMonPanel()
    {
        string metric = CurrentSysMonMetric();
        bool isSensor = metric == SensorPickTag;
        bool isCpuGpu = !isSensor && metric is "cpu" or "gpu";
        bool isDisk   = !isSensor && metric == "disk";

        PnlSysMonMode.Visibility   = isCpuGpu ? Visibility.Visible : Visibility.Collapsed;
        PnlSysMonDisk.Visibility   = isDisk   ? Visibility.Visible : Visibility.Collapsed;
        PnlSysMonSensor.Visibility = isSensor ? Visibility.Visible : Visibility.Collapsed;
        LblSensorName.Text = isSensor && ActionTypeHelper.ParseSensorValue(_sysmonSensorWire) is { } s
            ? $"{s.Label} · {ActionTypeHelper.SensorStatLabel(s.Stat)}"
            : "";

        if (isDisk) EnsureSysMonDiskList();

        if (isCpuGpu)
        {
            _sysMonLoading = true;
            try
            {
                bool temp = _pendingSysMonArg == "temp";
                RbSysTemp.IsChecked  = temp;
                RbSysUsage.IsChecked = !temp;
            }
            finally { _sysMonLoading = false; }
        }
        else if (isDisk)
        {
            string id = _pendingSysMonArg;
            int bar = id.IndexOf('|');
            _openedDiskId = bar >= 0 ? id[..bar] : id;
            ApplyDiskSelection(_openedDiskId);   // the async list append re-applies it later
        }

        // Consumed — a later metric switch by the user must not re-apply the opened value.
        _pendingSysMonArg = "";
    }

    private void EnsureSysMonDiskList()
    {
        if (_sysMonDiskLoaded) return;
        _sysMonDiskLoaded = true;

        CbSysMonDisk.Items.Clear();
        CbSysMonDisk.Items.Add(new DiskItem("", Loc.Get("sysmon_disk_all")));
        CbSysMonDisk.SelectedIndex = 0;

        var host = _host;
        if (host is null) return;

        // The host's disk list can block while LHM opens — fetch off-thread, append on return
        // and re-apply the opened value's disk selection once the real entries are in.
        System.Threading.Tasks.Task.Run(() => host.ListStorageDisks())
            .ContinueWith(t =>
            {
                foreach (var (id, name) in t.Result)
                    CbSysMonDisk.Items.Add(new DiskItem(id, name));
                if (CurrentSysMonMetric() == "disk") ApplyDiskSelection(_openedDiskId);
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyDiskSelection(string diskId)
    {
        _sysMonLoading = true;
        try
        {
            CbSysMonDisk.SelectedItem =
                CbSysMonDisk.Items.OfType<DiskItem>().FirstOrDefault(d => d.Id == diskId)
                ?? CbSysMonDisk.Items.OfType<DiskItem>().FirstOrDefault();
        }
        finally { _sysMonLoading = false; }
    }

    private void SysMonMode_Checked(object sender, RoutedEventArgs e)
    {
        // XAML-load-time Checked (RbSysUsage IsChecked="True") + programmatic sets during
        // RefreshSysMonPanel must not do anything except refresh the preview — the value
        // itself is read at save time.
        if (_sysMonLoading || !IsLoaded) return;
        RefreshLivePreview();
    }

    private void CbSysMonDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sysMonLoading || !IsLoaded) return;
        RefreshLivePreview();
    }

    /// <summary>Builds the <c>dp_sysmon</c> value: the chosen sensor's wire string when the
    /// "Sensor selection" card is active, otherwise a bare metric token plus a refinement suffix
    /// when the metric carries one and a non-default choice is made.</summary>
    private string SaveSysMonSpec()
    {
        string metric = CurrentSysMonMetric();
        if (metric.Length == 0) return "cpu";

        if (metric == SensorPickTag)
            return ActionTypeHelper.ParseSensorValue(_sysmonSensorWire) is not null ? _sysmonSensorWire : "cpu";

        if (metric is "cpu" or "gpu" && RbSysTemp.IsChecked == true)
            return metric + ":temp";

        if (metric == "disk" && CbSysMonDisk.SelectedItem is DiskItem { Id.Length: > 0 } disk)
            return $"disk:{disk.Id}|{disk.Name}";

        return metric;
    }
}
