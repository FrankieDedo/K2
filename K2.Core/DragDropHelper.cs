using System;
using System.Windows;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// Shared plumbing for the "drag one button's action (and icon, where the
/// device has one) onto another to swap them" gesture, used identically by
/// MacroPad, Everest (keyboard + numpad display keys) and DisplayPad. Each
/// device still owns its own DragEnter/Drop handlers — the data being
/// swapped differs per device — this only factors out the mouse-move
/// drag-threshold check and the drag-over visual feedback so the call sites
/// don't each reimplement them slightly differently.
/// </summary>
public static class DragDropHelper
{
    public static bool ExceedsDragThreshold(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    public static void SetDropTargetHighlight(Button button, bool active) =>
        button.Opacity = active ? 0.55 : 1.0;
}
