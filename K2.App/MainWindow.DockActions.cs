// MainWindow.DockActions.cs — partial class: actions for display keys, dock buttons, and crown.
//
// This panel manages action assignment for non-standard keys:
//   - 4 numpad display keys (matrixId captured via SDK callback)
//   - 4 physical media dock buttons (matrixId captured)
//   - 2 directions of the rotating crown (matrixId captured)
//
// MatrixIds are not known in advance: the user uses "Capture HW key" to
// press the physical key and associate it with a slot.
//
// Persistence in EverestStore: keys "dockact.{slot}.matrixId/actionType/actionValue".

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using K2.Core;

namespace K2.App;

public partial class MainWindow
{
    /// <summary>Action slot: readable name + captured matrixId + action.</summary>
    private sealed class HwActionSlot
    {
        public string Name { get; init; } = "";
        public string StoreKey { get; init; } = "";
        public int MatrixId { get; set; }
        public string? ActionType { get; set; }
        public string? ActionValue { get; set; }
        public Button? UiButton { get; set; }
    }

    private readonly List<HwActionSlot> _hwSlots = new();
    private bool _hwCapturing;
    private HwActionSlot? _hwCaptureTarget;

    // ─────────────────────── Init ───────────────────────

    private void InitDockActionsPanel()
    {
        // Define slots
        string[] ndkNames = { "Display 1", "Display 2", "Display 3", "Display 4" };
        string[] dockNames = { "Dock Btn 1", "Dock Btn 2", "Dock Btn 3", "Dock Btn 4" };
        string[] dialNames = { "Crown ←", "Crown →" };

        foreach (var (names, prefix, panel) in new[]
        {
            (ndkNames,  "ndk",  WpNdkActions),
            (dockNames, "dock", WpDockActions),
            (dialNames, "dial", WpDialActions),
        })
        {
            for (int i = 0; i < names.Length; i++)
            {
                var slot = new HwActionSlot
                {
                    Name = names[i],
                    StoreKey = $"dockact.{prefix}{i}",
                };

                // Load from store
                var mid = _evStore.GetSetting($"{slot.StoreKey}.matrixId");
                if (int.TryParse(mid, out int m)) slot.MatrixId = m;
                slot.ActionType = _evStore.GetSetting($"{slot.StoreKey}.actionType");
                slot.ActionValue = _evStore.GetSetting($"{slot.StoreKey}.actionValue");

                // Create UI button
                var btn = new Button
                {
                    Content = FormatSlotLabel(slot),
                    MinWidth = 100,
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 4, 4),
                    Background = new SolidColorBrush(
                        slot.MatrixId != 0 ? Color.FromRgb(0x2A, 0x4A, 0x4C) : Color.FromRgb(0x3A, 0x3A, 0x40)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3)),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = slot,
                    ToolTip = $"{slot.Name}\nmatrixId=0x{slot.MatrixId:X4}\nAction: {slot.ActionType ?? "none"}",
                    ContextMenu = BuildHwSlotContextMenu(),
                };
                btn.Click += HwSlotButton_Click;
                slot.UiButton = btn;

                panel.Children.Add(btn);
                _hwSlots.Add(slot);
            }
        }
    }

    private static string FormatSlotLabel(HwActionSlot slot)
    {
        string action = slot.ActionType ?? "—";
        string mapped = slot.MatrixId != 0 ? $"0x{slot.MatrixId:X2}" : "?";
        return $"{slot.Name}\n[{mapped}] {action}";
    }

    private void RefreshSlotButton(HwActionSlot slot)
    {
        if (slot.UiButton is null) return;
        slot.UiButton.Content = FormatSlotLabel(slot);
        slot.UiButton.Background = new SolidColorBrush(
            slot.MatrixId != 0 ? Color.FromRgb(0x2A, 0x4A, 0x4C) : Color.FromRgb(0x3A, 0x3A, 0x40));
        slot.UiButton.ToolTip = $"{slot.Name}\nmatrixId=0x{slot.MatrixId:X4}\nAction: {slot.ActionType ?? "none"}";
    }

    // ─────────────────────── Persistence ───────────────────────

    private void SaveHwSlot(HwActionSlot slot)
    {
        _evStore.SetSetting($"{slot.StoreKey}.matrixId", slot.MatrixId.ToString());
        _evStore.SetSetting($"{slot.StoreKey}.actionType", slot.ActionType ?? "");
        _evStore.SetSetting($"{slot.StoreKey}.actionValue", slot.ActionValue ?? "");
    }

    // ─────────────────────── Click: assign action ───────────────────────

    private void HwSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HwActionSlot slot }) return;

        var dlg = new ButtonActionDialog(
            _hwSlots.IndexOf(slot), slot.ActionType, slot.ActionValue)
        { Owner = this };

        if (dlg.ShowDialog() == true)
        {
            slot.ActionType = string.IsNullOrEmpty(dlg.ActionType) || dlg.ActionType == "none"
                ? null : dlg.ActionType;
            slot.ActionValue = slot.ActionType is null ? null : dlg.ActionValue;
            SaveHwSlot(slot);
            RefreshSlotButton(slot);
            LogEverest($"[DOCK-ACT] {slot.Name} <- {slot.ActionType ?? "none"}");
        }
    }

    // ─────────────────────── Context menu ───────────────────────

    private ContextMenu BuildHwSlotContextMenu()
    {
        var menu = new ContextMenu();

        var miMap = new MenuItem { Header = Loc.Get("dock_capture_matrix") };
        miMap.Click += HwMnuCapture_Click;

        var miCfg = new MenuItem { Header = Loc.Get("dock_configure_action") };
        miCfg.Click += HwMnuConfigure_Click;

        var miRm = new MenuItem { Header = Loc.Get("dock_remove_action") };
        miRm.Click += HwMnuRemoveAction_Click;

        var miReset = new MenuItem { Header = "Reset slot" };
        miReset.Click += HwMnuReset_Click;

        menu.Items.Add(miMap);
        menu.Items.Add(miCfg);
        menu.Items.Add(new Separator());
        menu.Items.Add(miRm);
        menu.Items.Add(miReset);
        return menu;
    }

    private static HwActionSlot? HwSlotFromMenu(object sender) =>
        sender is MenuItem mi && mi.Parent is ContextMenu cm
            && cm.PlacementTarget is Button { Tag: HwActionSlot s } ? s : null;

    private void HwMnuCapture_Click(object sender, RoutedEventArgs e)
    {
        if (HwSlotFromMenu(sender) is not HwActionSlot slot) return;
        StartHwCapture(slot);
    }

    private void HwMnuConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (HwSlotFromMenu(sender) is not HwActionSlot slot) return;
        HwSlotButton_Click(slot.UiButton!, e);
    }

    private void HwMnuRemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (HwSlotFromMenu(sender) is not HwActionSlot slot) return;
        slot.ActionType = null;
        slot.ActionValue = null;
        SaveHwSlot(slot);
        RefreshSlotButton(slot);
        LogEverest($"[DOCK-ACT] {slot.Name} action removed");
    }

    private void HwMnuReset_Click(object sender, RoutedEventArgs e)
    {
        if (HwSlotFromMenu(sender) is not HwActionSlot slot) return;
        slot.MatrixId = 0;
        slot.ActionType = null;
        slot.ActionValue = null;
        SaveHwSlot(slot);
        RefreshSlotButton(slot);
        LogEverest($"[DOCK-ACT] {slot.Name} fully reset");
    }

    // ─────────────────────── Capture matrixId ───────────────────────

    private void BtnCaptureHwKey_Click(object sender, RoutedEventArgs e)
    {
        if (_hwCapturing)
        {
            StopHwCapture();
            return;
        }
        // No specific target: generic capture (shows the matrixId in the label)
        _hwCapturing = true;
        _hwCaptureTarget = null;
        BtnCaptureHwKey.Content = Loc.Get("dock_cancel_capture");
        LblCapturedKey.Text = Loc.Get("dock_press_hw");
    }

    private void StartHwCapture(HwActionSlot target)
    {
        _hwCapturing = true;
        _hwCaptureTarget = target;
        BtnCaptureHwKey.Content = Loc.Get("dock_cancel_capture");
        LblCapturedKey.Text = Loc.Get("dock_press_for", target.Name);
    }

    private void StopHwCapture()
    {
        _hwCapturing = false;
        _hwCaptureTarget = null;
        BtnCaptureHwKey.Content = Loc.Get("dock_capture_hw");
    }

    /// <summary>
    /// Called by the SDK callback handler when a key is pressed while HW capture
    /// is active. Returns true if the key was consumed.
    /// </summary>
    internal bool TryHwCapture(int wMatrix)
    {
        if (!_hwCapturing) return false;

        if (_hwCaptureTarget is HwActionSlot slot)
        {
            slot.MatrixId = wMatrix;
            SaveHwSlot(slot);
            RefreshSlotButton(slot);
            LogEverest($"[DOCK-ACT] {slot.Name} <- matrixId=0x{wMatrix:X4}");
            LblCapturedKey.Text = $"{slot.Name} → 0x{wMatrix:X4}";
        }
        else
        {
            // Generic capture: show value only
            LblCapturedKey.Text = $"matrixId=0x{wMatrix:X4} (wMatrix={wMatrix})";
            LogEverest($"[DOCK-ACT] captured matrixId=0x{wMatrix:X4}");
        }

        StopHwCapture();
        return true;
    }

    // ─────────────────────── Action execution ───────────────────────

    /// <summary>
    /// Called from the SDK callback: finds a slot with the given matrixId and
    /// executes the assigned action.
    /// </summary>
    internal bool TryExecuteHwAction(int wMatrix)
    {
        foreach (var slot in _hwSlots)
        {
            if (slot.MatrixId != 0 && slot.MatrixId == wMatrix && slot.ActionType is not null)
            {
                _evEngine?.Execute(slot.ActionType, slot.ActionValue, buttonIndex: -1);
                LogEverest($"[DOCK-ACT] executing {slot.Name}: {slot.ActionType}");
                return true;
            }
        }
        return false;
    }
}
