using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using K2.App.Models;
using K2.App.Services;
using K2.Core;

namespace K2.App;

/// <summary>
/// "Key Binding" (button remap + sniper) section content for the Makalu tab —
/// see MakaluDpiRemapPanel.xaml for why this is kept as its own small
/// UserControl, and for why DPI no longer lives here despite the class name.
/// Owns no MakaluService of its own; the parent (MainWindow) passes one in
/// via <see cref="Init"/> and keeps it in sync via
/// <see cref="UpdateDeviceInfo"/> whenever the detected model/connection
/// state changes.
/// </summary>
public partial class MakaluDpiRemapPanel : UserControl
{
    private MakaluService _makalu = null!;
    private Action<string> _log = _ => { };
    private MakaluService.DeviceInfo _mkInfo =
        new(MakaluService.Model.Makalu67, "Makalu 67", 6, MakaluProtocol.DpiMin67);
    /// <summary>Profile persistence — set once from Init, same pattern as
    /// MakaluRgbSettingsPanel._mkStore/_mkSlot.</summary>
    private MakaluStore? _mkStore;
    private Func<int>? _mkSlot;
    private int CurrentSlot => _mkSlot?.Invoke() ?? 1;

    public MakaluDpiRemapPanel()
    {
        InitializeComponent();
    }

    internal void Init(MakaluService service, Action<string> log, MakaluStore store, Func<int> currentSlot)
    {
        _makalu = service;
        _log = log;
        _mkStore = store;
        _mkSlot = currentSlot;
        LvMkButtons.ItemsSource = _items;
        BuildMkButtonList();
    }

    /// <summary>Called by the parent whenever the detected model/connection
    /// state changes — rebuilds the button list for the new model.</summary>
    internal void UpdateDeviceInfo(MakaluService.DeviceInfo info)
    {
        _mkInfo = info;
        BuildMkButtonList();
    }

    /// <summary>Selects the given physical button in the list and opens its
    /// Configure dialog directly — called from MainWindow.Makalu.cs's
    /// MkHotspotClicked when a hotspot on the device image is clicked, same
    /// flow as Everest 60's SelectKey (click image -> select + configure).</summary>
    internal void SelectRemapButton(int btnIdx)
    {
        if (!_byIdx.TryGetValue(btnIdx, out var item)) return;
        LvMkButtons.SelectedItem = item;
        LvMkButtons.ScrollIntoView(item);
        OpenConfigureDialog(item);
    }

    // ------------------------------------------------------------
    // Button list + Configure/Remove — same shape as every other device's
    // Key Binding section (see MakaluDpiRemapPanel.xaml's doc comment).
    // ------------------------------------------------------------

    private readonly ObservableCollection<MakaluButtonItem> _items = new();
    private readonly Dictionary<int, MakaluButtonItem> _byIdx = new();

    /// <summary>Merges the current profile's saved remap rows (if any) over
    /// this model's defaults — a button with no saved row yet (never applied
    /// in this profile) falls back to the model default, same as a brand new
    /// installation before any Apply has ever been pressed.</summary>
    private Dictionary<int, string> MkLoadAssignments()
    {
        var result = new Dictionary<int, string>(MakaluRemapData.RemapDefaults(_mkInfo.Model));
        if (_mkStore is not null)
            foreach (var kv in _mkStore.LoadRemap(CurrentSlot))
                result[kv.Key] = kv.Value;
        return result;
    }

    private void BuildMkButtonList()
    {
        _items.Clear();
        _byIdx.Clear();
        var names = MakaluRemapData.BtnNames(_mkInfo.Model);
        var assignments = MkLoadAssignments();

        foreach (var kv in names.OrderBy(k => k.Key))
        {
            int btnIdx = kv.Key;
            var item = new MakaluButtonItem(btnIdx, kv.Value) { Assignment = assignments[btnIdx] };
            _byIdx[btnIdx] = item;
            RefreshMkItemVisibility(item);
        }

        UpdateListButtons();
    }

    /// <summary>Only buttons whose current assignment differs from this model's default
    /// show up in the visible list (user request 2026-07-27: every physical button used
    /// to always be listed — reasonable in principle, "a mouse button always does
    /// something", but in practice every still-default button just read as clutter/an
    /// empty row with nothing customized to show). <see cref="_byIdx"/> stays unfiltered
    /// regardless, since MainWindow.Makalu.cs's hotspot-click-to-configure
    /// (<see cref="SelectRemapButton"/>) needs to reach EVERY button, customized or not.
    /// Keeps <see cref="_items"/> sorted by button index when inserting.</summary>
    private void RefreshMkItemVisibility(MakaluButtonItem item)
    {
        bool customized = item.Assignment != MakaluRemapData.RemapDefaults(_mkInfo.Model).GetValueOrDefault(item.Index);
        bool inList = _items.Contains(item);
        if (customized && !inList)
        {
            int insertAt = 0;
            while (insertAt < _items.Count && _items[insertAt].Index < item.Index) insertAt++;
            _items.Insert(insertAt, item);
        }
        else if (!customized && inList)
        {
            _items.Remove(item);
        }
    }

    private void UpdateListButtons()
    {
        bool hasSelection = LvMkButtons.SelectedItem is not null;
        BtnMkConfigure.IsEnabled = hasSelection;
        BtnMkRemove.IsEnabled = hasSelection;
    }

    private void LvMkButtons_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateListButtons();

    private void BtnMkConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (LvMkButtons.SelectedItem is MakaluButtonItem item)
            OpenConfigureDialog(item);
    }

    private void OpenConfigureDialog(MakaluButtonItem item)
    {
        var dlg = new MakaluRemapConfigDialog(item.BaseLabel, item.Assignment, _mkInfo.DpiMin)
                  { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        ApplyAssignment(item, dlg.ResultAssignment);
    }

    /// <summary>Resets the selected button back to this model's default
    /// function — Makalu has no "unassigned" state (a mouse button always
    /// does something), so "Remove" here means "restore the native
    /// function" rather than clearing the row entirely.</summary>
    private void BtnMkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LvMkButtons.SelectedItem is not MakaluButtonItem item) return;
        string defaultFn = MakaluRemapData.RemapDefaults(_mkInfo.Model).GetValueOrDefault(item.Index, "left");
        ApplyAssignment(item, defaultFn);
    }

    /// <summary>Pushes a new assignment to the mouse, and — if it succeeds —
    /// updates the list row and persists it. Button #1 (left click) going to
    /// anything else risks locking the user out of clicking, so that case
    /// shows a countdown confirm/auto-revert overlay instead of applying
    /// silently, same as controller.py's UI reference.</summary>
    private void ApplyAssignment(MakaluButtonItem item, string newAssignment)
    {
        string oldRaw = item.Assignment;
        bool ok;
        if (newAssignment.StartsWith("sniper:") && int.TryParse(newAssignment.Split(':')[1], out int dpi))
            ok = _makalu.SetButtonSniper(item.Index, dpi, _mkInfo.DpiMin);
        else
            ok = _makalu.SetButtonRemap(item.Index, newAssignment);
        _log($"[REMAP] button={item.Index} fn={newAssignment} -> {ok}");

        if (!ok)
        {
            MessageBox.Show(Window.GetWindow(this), Loc.Get("makalu_failed"));
            return;
        }

        item.Assignment = newAssignment;
        _mkStore?.SaveRemapButton(CurrentSlot, item.Index, newAssignment);
        RefreshMkItemVisibility(item);

        if (item.Index == 1 && newAssignment != "left")
            MkShowRemapConfirm(item, oldRaw);
    }

    // ------------------------------------------------------------
    // Left-button remap safety confirm/auto-revert overlay
    // ------------------------------------------------------------

    private DispatcherTimer? _mkConfirmTimer;
    private int _mkConfirmSeconds;
    private MakaluButtonItem? _mkConfirmItem;
    private string _mkConfirmOldFn = "left";

    private void MkShowRemapConfirm(MakaluButtonItem item, string oldRaw)
    {
        _mkConfirmItem = item;
        _mkConfirmOldFn = oldRaw;
        _mkConfirmSeconds = 10;
        LblMkRemapConfirmText.Text = Loc.Get("makalu_remap_keep_text", _mkConfirmSeconds);
        PnlMkRemapConfirm.Visibility = Visibility.Visible;

        _mkConfirmTimer?.Stop();
        _mkConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mkConfirmTimer.Tick += (_, _) =>
        {
            _mkConfirmSeconds--;
            if (_mkConfirmSeconds <= 0) { MkRemapRevert(); return; }
            LblMkRemapConfirmText.Text = Loc.Get("makalu_remap_keep_text", _mkConfirmSeconds);
        };
        _mkConfirmTimer.Start();
    }

    private void BtnMkRemapKeep_Click(object sender, RoutedEventArgs e)
    {
        _mkConfirmTimer?.Stop();
        PnlMkRemapConfirm.Visibility = Visibility.Collapsed;
    }

    private void BtnMkRemapRevert_Click(object sender, RoutedEventArgs e) => MkRemapRevert();

    private void MkRemapRevert()
    {
        _mkConfirmTimer?.Stop();
        PnlMkRemapConfirm.Visibility = Visibility.Collapsed;
        if (_mkConfirmItem is not MakaluButtonItem item) return;
        string oldFn = _mkConfirmOldFn;

        bool ok = oldFn.StartsWith("sniper:") && int.TryParse(oldFn.Split(':')[1], out int dpi)
            ? _makalu.SetButtonSniper(item.Index, dpi, _mkInfo.DpiMin)
            : _makalu.SetButtonRemap(item.Index, oldFn);
        _log($"[REMAP] revert button={item.Index} -> {oldFn} ok={ok}");

        if (ok)
        {
            item.Assignment = oldFn;
            _mkStore?.SaveRemapButton(CurrentSlot, item.Index, oldFn);
            RefreshMkItemVisibility(item);
        }
    }

    // ------------------------------------------------------------
    // Profile switch: push the stored slot's button assignments into this
    // panel and re-send every one of them to firmware (if connected). Called
    // by MainWindow.Makalu.cs alongside MakaluRgbSettingsPanel.MkReloadProfile.
    // ------------------------------------------------------------

    internal void MkReloadRemap(int slot)
    {
        var assignments = MkLoadAssignments();

        // Not connected: UI reflects the profile, hardware catches up on reconnect
        // (MainWindow.Makalu.cs calls this again on the disconnected->connected
        // poll transition).
        bool anyConnected = false;
        foreach (var kv in assignments)
        {
            if (_byIdx.TryGetValue(kv.Key, out var item))
            {
                item.Assignment = kv.Value;
                RefreshMkItemVisibility(item);
            }

            bool ok = kv.Value.StartsWith("sniper:") && int.TryParse(kv.Value.Split(':')[1], out int dpi)
                ? _makalu.SetButtonSniper(kv.Key, dpi, _mkInfo.DpiMin)
                : _makalu.SetButtonRemap(kv.Key, kv.Value);
            anyConnected |= ok;
        }
        _log($"[PROFILE] reload remap slot={slot}: {assignments.Count} button(s), hw ok={anyConnected}");
    }
}
