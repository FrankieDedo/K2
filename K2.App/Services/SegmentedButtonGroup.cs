using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using K2.Core.Themes;

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
    /// <summary>Wave's 4-way labels (see Everest60Protocol.WaveDirections and
    /// the mirrored arrays in MainWindow.Everest.cs/MainWindow.MacroLed.cs —
    /// all three devices use these exact strings) get an arrow glyph instead
    /// of text, per user request 2026-07-21 (assets recolored white from the
    /// user-supplied PNGs in Grafiche/arrow-*.png). Tornado's "Clockwise"/
    /// "Counter-CW" have no matching icon and stay text.</summary>
    private static readonly Dictionary<string, string> DirectionIcons = new()
    {
        ["Right"] = "Assets/arrow_right.png",
        ["Down"]  = "Assets/arrow_down.png",
        ["Left"]  = "Assets/arrow_left.png",
        ["Up"]    = "Assets/arrow_up.png",
    };

    /// <summary>Clears <paramref name="grid"/> and adds one RadioButton per
    /// label, all sharing <paramref name="groupName"/> (must be unique to this
    /// grid — WPF's RadioButton grouping isn't scoped to a container). Each
    /// button's <c>Tag</c> is its 0-based index, read back by the caller's
    /// <paramref name="checkedHandler"/> via <c>(int)((RadioButton)sender).Tag</c>.
    /// Labels with a matching entry in <see cref="DirectionIcons"/> render as
    /// an arrow icon (with the label as ToolTip) instead of text.</summary>
    /// <summary>Must match K2SegmentedGroupBorder's own CornerRadius (K2Theme.xaml)
    /// so the edge buttons' self-rounded corners line up with the group border.</summary>
    private const double GroupCornerRadius = 6;

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
                GroupName = groupName,
                Style = style,
                Tag = i,
            };
            bool isFirst = i == 0;
            bool isLast = i == labels.Length - 1;
            K2Segment.SetCornerRadius(rb, new CornerRadius(
                isFirst ? GroupCornerRadius : 0,
                isLast ? GroupCornerRadius : 0,
                isLast ? GroupCornerRadius : 0,
                isFirst ? GroupCornerRadius : 0));
            if (DirectionIcons.TryGetValue(labels[i], out var iconPath))
            {
                rb.Content = new Image
                {
                    Source = new BitmapImage(new Uri(iconPath, UriKind.Relative)),
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                };
                rb.ToolTip = labels[i];
            }
            else
            {
                rb.Content = labels[i];
            }
            rb.Checked += checkedHandler;
            grid.Children.Add(rb);
        }
        int idx = Math.Clamp(selectedIndex, 0, labels.Length - 1);
        ((RadioButton)grid.Children[idx]).IsChecked = true;
    }
}
