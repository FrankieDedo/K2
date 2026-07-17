using System.Collections.Generic;
using System.Linq;
using System.Windows;
using K2.Core;

namespace K2.App;

/// <summary>
/// Lets the user pick which Base Camp device (by its BC-internal DeviceId) to import
/// from, when the DB contains profiles for more than one device of the same type.
/// Shown by every device's BaseCamp-import flow (see MainWindow.{Device}.cs's
/// BtnXxImportBc_Click) whenever <c>bcDevices.Count &gt; 1</c> — skipped entirely when
/// there's only one candidate, since there's nothing to choose.
/// </summary>
public partial class BcDevicePickerDialog : Window
{
    public sealed class Item(int bcDeviceId, string label)
    {
        public int BcDeviceId { get; } = bcDeviceId;
        public string Label { get; } = label;
    }

    public int? SelectedBcDeviceId { get; private set; }

    public BcDevicePickerDialog(string k2DeviceLabel, IReadOnlyList<(int BcDeviceId, string Label)> devices)
    {
        InitializeComponent();
        TxtPrompt.Text = Loc.Get("bc_pick_device_text", k2DeviceLabel);
        LstDevices.ItemsSource = devices.Select(d => new Item(d.BcDeviceId, d.Label)).ToList();
        LstDevices.SelectedIndex = 0;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (LstDevices.SelectedItem is not Item item)
        {
            MessageBox.Show(this, Loc.Get("bc_pick_device_none"), Loc.Get("bc_pick_device_title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedBcDeviceId = item.BcDeviceId;
        DialogResult = true;
    }
}
