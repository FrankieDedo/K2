using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace K2.Core.Converters;

/// <summary>Given a <see cref="TreeViewItem"/> (bind with <c>RelativeSource Self</c> from
/// inside its template), reports where it sits so the connecting-line rectangles can be
/// drawn: <c>"root"</c> = a top-level item (no incoming rail/elbow), <c>"last"</c> = the
/// last child of its parent (rail stops at the elbow), <c>"mid"</c> = anything else (rail
/// runs the full height). Evaluated once when the template loads, which is fine for a tree
/// that is rebuilt wholesale on every refresh.</summary>
public sealed class TreeLinePositionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DependencyObject container)
        {
            var owner = ItemsControl.ItemsControlFromItemContainer(container);
            if (owner is TreeView) return "root";
            if (owner is not null)
            {
                int index = owner.ItemContainerGenerator.IndexFromContainer(container);
                if (index >= 0 && index == owner.Items.Count - 1) return "last";
            }
        }
        return "mid";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
