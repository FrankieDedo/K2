using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace K2.App.Services;

/// <summary>
/// Rebuilds a UniformGrid's children as a fresh row of mutually-exclusive
/// K2SegmentedButton RadioButtons — for direction pickers whose option count
/// varies with the selected effect (Wave = 4 directions, Tornado = 2), so a
/// fixed XAML-declared RadioButton pair/trio (fine for the static ≤3-option
/// settings elsewhere, see Makalu Angle Snapping/Lift-off) doesn't fit.
/// </summary>
internal static class SegmentedButtonGroup
{
    /// <summary>Clears <paramref name="grid"/> and adds one RadioButton per
    /// label, all sharing <paramref name="groupName"/> (must be unique to this
    /// grid — WPF's RadioButton grouping isn't scoped to a container). Each
    /// button's <c>Tag</c> is its 0-based index, read back by the caller's
    /// <paramref name="checkedHandler"/> via <c>(int)((RadioButton)sender).Tag</c>.</summary>
    internal static void Rebuild(UniformGrid grid, string groupName, string[] labels,
        RoutedEventHandler checkedHandler, int selectedIndex = 0)
    {
        grid.Children.Clear();
        if (labels.Length == 0) return;

        var style = (Style)grid.FindResource("K2SegmentedButton");
        for (int i = 0; i < labels.Length; i++)
        {
            var rb = new RadioButton
            {
                Content = labels[i],
                GroupName = groupName,
                Style = style,
                Tag = i,
            };
            rb.Checked += checkedHandler;
            grid.Children.Add(rb);
        }
        int idx = Math.Clamp(selectedIndex, 0, labels.Length - 1);
        ((RadioButton)grid.Children[idx]).IsChecked = true;
    }
}
