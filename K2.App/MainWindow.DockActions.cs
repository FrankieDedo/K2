// MainWindow.DockActions.cs — partial class: actions for the 4 physical media
// dock buttons and the 2 rotation directions of the crown (dial).
//
// These hotspots are drawn directly on the device graphic (CvsEvDock, overlaid
// on Assets/keytop.png — see MainWindow.xaml, GrdEvDock/CvsEvDock) instead of a
// separate list panel: left-click opens the action dialog, right-click opens a
// context menu (capture matrixId / configure / remove / reset).
//
// MatrixIds are not known in advance: the user uses "Capture matrixId…" (or the
// generic "Capture HW key" button in the Key Binding section) to press the
// physical key and associate it with a slot.
//
// Persistence in EverestStore: keys "dockact.{slot}.matrixId/actionType/actionValue".
//
// Numpad display keys (also physically on the dock) have their own dedicated
// image+action interface — see MainWindow.NumpadDisplayKeys.cs — and are not
// part of this file.

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

    /// <summary>
    /// Hotspot geometry on the 200×64 CvsEvDock canvas (matches the knob centers
    /// in Assets/keytop.png at its rendered size). Media buttons 1-4 sit on the
    /// artwork; the crown buttons sit above it (negative Y) since the 5th knob
    /// (the dial) has no push-button of its own — only its rotation is bindable.
    /// </summary>
    private static readonly (double X, double Y, double W, double H)[] DockHotspots =
    {
        (30.7, 29.9, 22, 22),   // Dock Btn 1 — Prev track knob
        (56.1, 29.9, 22, 22),   // Dock Btn 2 — Next track knob
        (81.4, 29.9, 22, 22),   // Dock Btn 3 — Play/Pause knob
        (105.5, 29.9, 22, 22),  // Dock Btn 4 — Mute knob
    };

    private static readonly (double X, double Y, double W, double H)[] CrownHotspots =
    {
        (96.5, -16, 24, 14),  // Crown ← (counter-clockwise)
        (122.5, -16, 24, 14), // Crown → (clockwise)
    };

    // ─────────────────────── Init ───────────────────────

    private void InitDockActionsPanel()
    {
        string[] dockNames = { "Dock Btn 1", "Dock Btn 2", "Dock Btn 3", "Dock Btn 4" };
        string[] dialNames = { "Crown ←", "Crown →" };
        string[] dialGlyphs = { "↺", "↻" }; // ↺ ↻

        for (int i = 0; i < dockNames.Length; i++)
            _hwSlots.Add(CreateHwSlot(dockNames[i], "dock", i));
        for (int i = 0; i < dialNames.Length; i++)
            _hwSlots.Add(CreateHwSlot(dialNames[i], "dial", i));

        // Media dock buttons: transparent hotspots over the knobs already drawn
        // in the artwork (no extra glyph needed, the icon is baked in the image).
        for (int i = 0; i < 4; i++)
            PlaceHwOverlayButton(_hwSlots[i], CvsEvDock, DockHotspots[i], glyph: null);

        // Crown rotation: no artwork above the dial, so show a small ↺ / ↻ glyph.
        for (int i = 0; i < 2; i++)
            PlaceHwOverlayButton(_hwSlots[4 + i], CvsEvDock, CrownHotspots[i], glyph: dialGlyphs[i]);
    }

    private HwActionSlot CreateHwSlot(string name, string prefix, int index)
    {
        var slot = new HwActionSlot
        {
            Name = name,
            StoreKey = $"dockact.{prefix}{index}",
        };

        var mid = _evStore.GetSetting($"{slot.StoreKey}.matrixId");
        if (int.TryParse(mid, out int m)) slot.MatrixId = m;
        slot.ActionType = _evStore.GetSetting($"{slot.StoreKey}.actionType");
        slot.ActionValue = _evStore.GetSetting($"{slot.StoreKey}.actionValue");
        return slot;
    }

    /// <summary>Creates a transparent hotspot button for a slot and places it on
    /// the given canvas at the given geometry. <paramref name="glyph"/>, if set,
    /// is shown as small centered text (used where the artwork has no icon).</summary>
    private void PlaceHwOverlayButton(
        HwActionSlot slot, Canvas canvas, (double X, double Y, double W, double H) geo, string? glyph)
    {
        var btn = new Button
        {
            Width = geo.W,
            Height = geo.H,
            Content = glyph,
            FontSize = 10,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(slot.ActionType is null ? 1 : 2),
            BorderBrush = SlotBorderBrush(slot),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = slot,
            ToolTip = SlotTooltip(slot),
            ContextMenu = BuildHwSlotContextMenu(),
        };
        btn.Click += HwSlotButton_Click;
        slot.UiButton = btn;

        Canvas.SetLeft(btn, geo.X - geo.W / 2);
        Canvas.SetTop(btn, geo.Y - geo.H / 2);
        canvas.Children.Add(btn);
    }

    private static Brush SlotBorderBrush(HwActionSlot slot) =>
        slot.ActionType is not null
            ? new SolidColorBrush(Color.FromRgb(0x5B, 0xBE, 0xC3)) // teal accent: action bound
            : Brushes.Transparent;

    private static string SlotTooltip(HwActionSlot slot) =>
        $"{slot.Name}\nmatrixId=0x{slot.MatrixId:X4}\nAction: {slot.ActionType ?? "none"}";

    private void RefreshSlotButton(HwActionSlot slot)
    {
        if (slot.UiButton is null) return;
        slot.UiButton.BorderThickness = new Thickness(slot.ActionType is null ? 1 : 2);
        slot.UiButton.BorderBrush = SlotBorderBrush(slot);
        slot.UiButton.ToolTip = SlotTooltip(slot);
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
