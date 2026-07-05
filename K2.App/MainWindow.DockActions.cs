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
using System.Windows.Shapes;
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
    /// Hotspot geometry on the 200×64 CvsEvDock canvas (Assets/keytop.png at its
    /// rendered size, scale = 200/749). Knob centers/radii were measured directly
    /// on the source PNG (pixel scan for the dark rim of each knob / the display
    /// bezel), not eyeballed:
    ///   knob 1-4 centers (orig. px): (119.5,120) (203,120) (287,120) (370,120), r≈32
    ///   crown/display circle (orig. px): center (630,122), r≈114 — the "corona"
    ///   is this big round display, not the small 5th knob next to it.
    /// Media buttons sit right on top of the knob artwork; the crown buttons sit
    /// above the display circle (negative Y — the circle's top edge is basically
    /// at the artwork's own top edge, so there is no room for them underneath it).
    /// </summary>
    private static readonly (double X, double Y, double W, double H)[] DockHotspots =
    {
        (31.9, 32.0, 20, 20),  // Dock Btn 1 — Prev track knob
        (54.2, 32.0, 20, 20),  // Dock Btn 2 — Next track knob
        (76.6, 32.0, 20, 20),  // Dock Btn 3 — Play/Pause knob
        (98.8, 32.0, 20, 20),  // Dock Btn 4 — Mute knob
    };

    private static readonly (double X, double Y, double W, double H)[] CrownHotspots =
    {
        (156.2, -14, 22, 14), // Crown ← (counter-clockwise)
        (180.2, -14, 22, 14), // Crown → (clockwise)
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

    /// <summary>Round-button template shared by every dock/crown hotspot: an
    /// <see cref="Ellipse"/> (so it renders as a circle even though the default
    /// app-wide Button template — see K2Theme.xaml — uses a rounded rectangle)
    /// plus a centered glyph. Fill/Stroke are TemplateBindings so each button
    /// instance can still set its own Background/BorderBrush.</summary>
    private static ControlTemplate BuildRoundHotspotTemplate()
    {
        var ellipseFactory = new FrameworkElementFactory(typeof(Ellipse), "PART_Circle");
        ellipseFactory.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Background")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        ellipseFactory.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("BorderBrush")
        { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        ellipseFactory.SetValue(Shape.StrokeThicknessProperty, 2.0);

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

        var gridFactory = new FrameworkElementFactory(typeof(Grid));
        gridFactory.AppendChild(ellipseFactory);
        gridFactory.AppendChild(contentFactory);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = gridFactory };
        var hoverBrush = (Brush)Application.Current.FindResource("K2HoverBrush");
        template.Triggers.Add(new Trigger
        {
            Property = Button.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Shape.FillProperty, hoverBrush, "PART_Circle") },
        });
        return template;
    }

    private static readonly ControlTemplate RoundHotspotTemplate = BuildRoundHotspotTemplate();

    /// <summary>Creates a round, mostly-transparent hotspot button for a slot and
    /// places it on the given canvas at the given geometry. <paramref name="glyph"/>,
    /// if set, is shown as small centered text (used where the artwork has no icon
    /// of its own, e.g. the crown rotation buttons).</summary>
    private void PlaceHwOverlayButton(
        HwActionSlot slot, Canvas canvas, (double X, double Y, double W, double H) geo, string? glyph)
    {
        var btn = new Button
        {
            Width = geo.W,
            Height = geo.H,
            Template = RoundHotspotTemplate,
            Content = glyph,
            FontSize = 10,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
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
            ? (Brush)Application.Current.FindResource("K2AccentBrush") // action bound
            : Brushes.Transparent;

    private static string SlotTooltip(HwActionSlot slot) =>
        $"{slot.Name}\nmatrixId=0x{slot.MatrixId:X4}\nAction: {slot.ActionType ?? "none"}";

    private void RefreshSlotButton(HwActionSlot slot)
    {
        if (slot.UiButton is null) return;
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
