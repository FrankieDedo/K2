using System.Windows;

namespace K2.Core.Themes;

/// <summary>
/// Attached property letting a K2SegmentedButton report its own corner
/// rounding, bound by the button's ControlTemplate into its own inner
/// Border. Border.CornerRadius rounds that Border's own Background/stroke
/// natively — round the button's OWN Border on its outer side instead of
/// clipping it from a parent container: simpler and avoids relying on the
/// parent's rounded corners "showing through" any child chrome painted
/// on top of them (fragile — Border.ClipToBounds only clips to the plain
/// rectangular layout bounds, not to CornerRadius). Set by whichever
/// caller knows the button's position in its group: SegmentedButtonGroup
/// for dynamic direction pickers, static XAML for fixed 2-way toggles
/// (Angle Snapping, Lift-off, Makalu Direction).
/// </summary>
public static class K2Segment
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(K2Segment),
            new FrameworkPropertyMetadata(new CornerRadius(0)));

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);
    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
}
