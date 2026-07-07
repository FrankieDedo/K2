using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// ButtonActionDialog partial: "Switch profile" panel — one or more target rows
/// (device + "Next"/"Previous"/"Profile N"), with a "+" button to add rows. The first
/// device choice is always the synthetic "this device" self-target (empty key), which
/// preserves the exact pre-existing single-target behavior; entries beyond that come
/// from the calling <see cref="IActionHost.ListProfileTargets"/> (cross-device targets —
/// only available when the dialog was opened with a host, see <see cref="_host"/>).
/// </summary>
public partial class ButtonActionDialog
{
    private sealed class ProfileRow
    {
        public required Grid Container { get; init; }
        public required ComboBox CbDevice { get; init; }
        public required ComboBox CbWhat { get; init; }
    }

    private List<ProfileTargetOption>? _profileDeviceChoices;

    private List<ProfileTargetOption> ProfileDeviceChoices => _profileDeviceChoices ??= BuildDeviceChoices();

    private List<ProfileTargetOption> BuildDeviceChoices()
    {
        var selfProfiles = _host is not null
            ? Enumerable.Range(1, System.Math.Max(1, _host.ProfileCount)).ToList()
            : new List<int> { 1 };
        var list = new List<ProfileTargetOption>
        {
            new("", Loc.Get("profile_this_device"), selfProfiles),
        };
        if (_host is not null) list.AddRange(_host.ListProfileTargets());
        return list;
    }

    private void EnsureProfileRows()
    {
        if (PnlProfileRows.Children.Count == 0)
            AddProfileRow("", "Next");
    }

    private void LoadProfileSpec(ProfileTargetPayload spec)
    {
        PnlProfileRows.Children.Clear();
        if (spec.Targets.Count == 0) { AddProfileRow("", "Next"); return; }
        foreach (var t in spec.Targets)
            AddProfileRow(t.Key, t.Target);
    }

    private static ProfileTargetPayload LegacyProfileSpec(string? currentValue)
    {
        var payload = new ProfileTargetPayload();
        payload.Targets.Add(new ProfileTarget { Key = "", Target = string.IsNullOrWhiteSpace(currentValue) ? "Next" : currentValue! });
        return payload;
    }

    private ProfileTargetPayload SaveProfileSpec()
    {
        var payload = new ProfileTargetPayload();
        foreach (var child in PnlProfileRows.Children.OfType<Grid>())
        {
            if (child.Tag is not ProfileRow row) continue;
            var device = row.CbDevice.SelectedItem as ProfileTargetOption;
            var what = row.CbWhat.SelectedItem as ComboBoxItem;
            payload.Targets.Add(new ProfileTarget
            {
                Key = device?.Key ?? "",
                Target = (string?)what?.Tag ?? "Next",
            });
        }
        if (payload.Targets.Count == 0)
            payload.Targets.Add(new ProfileTarget { Key = "", Target = "Next" });
        return payload;
    }

    private void BtnProfileAddRow_Click(object sender, RoutedEventArgs e) => AddProfileRow("", "Next");

    private void AddProfileRow(string deviceKey, string whatTarget)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cbDevice = new ComboBox
        {
            ItemsSource = ProfileDeviceChoices,
            DisplayMemberPath = "Label",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var cbWhat = new ComboBox
        {
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var btnRemove = new Button
        {
            Content = "✕",
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(cbDevice, 0);
        Grid.SetColumn(cbWhat, 1);
        Grid.SetColumn(btnRemove, 2);
        grid.Children.Add(cbDevice);
        grid.Children.Add(cbWhat);
        grid.Children.Add(btnRemove);

        var row = new ProfileRow { Container = grid, CbDevice = cbDevice, CbWhat = cbWhat };
        grid.Tag = row;

        cbDevice.SelectionChanged += (_, _) =>
        {
            if (cbDevice.SelectedItem is ProfileTargetOption dev)
                PopulateWhatCombo(cbWhat, dev, null);
        };
        btnRemove.Click += (_, _) =>
        {
            PnlProfileRows.Children.Remove(grid);
            if (PnlProfileRows.Children.Count == 0) AddProfileRow("", "Next");
        };

        var selectedDevice = ProfileDeviceChoices.FirstOrDefault(d => d.Key == deviceKey) ?? ProfileDeviceChoices[0];
        cbDevice.SelectedItem = selectedDevice;
        PopulateWhatCombo(cbWhat, selectedDevice, whatTarget);

        PnlProfileRows.Children.Add(grid);
    }

    private void PopulateWhatCombo(ComboBox cbWhat, ProfileTargetOption device, string? selectTag)
    {
        cbWhat.Items.Clear();
        cbWhat.Items.Add(new ComboBoxItem { Content = Loc.Get("profile_next"), Tag = "Next" });
        cbWhat.Items.Add(new ComboBoxItem { Content = Loc.Get("profile_previous"), Tag = "Previous" });
        foreach (var p in device.Profiles)
            cbWhat.Items.Add(new ComboBoxItem { Content = Loc.Get("profile_n", p), Tag = p.ToString() });

        var match = cbWhat.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == selectTag);
        cbWhat.SelectedItem = match ?? cbWhat.Items[0];
    }
}
