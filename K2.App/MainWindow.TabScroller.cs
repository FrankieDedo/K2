using System;
using System.Windows;
using System.Windows.Controls;

namespace K2.App;

/// <summary>
/// Overflow handling for the top device tab strip: keeps it on ONE row and reveals the
/// tabs that don't fit through the two narrow arrow cards, instead of letting the strip
/// wrap onto a second row (see DeviceTabPanel and the TabControl template in
/// MainWindow.xaml).
/// </summary>
public partial class MainWindow
{
    private DeviceTabPanel? _tabStrip;
    private Button? _tabScrollLeft;
    private Button? _tabScrollRight;

    /// <summary>Width reserved inside the TabControl for the RIGHT arrow card (Width 28 +
    /// Margin 4) — the left arrow is accounted for by the strip itself, which reserves a
    /// hole for it after the pinned Home tab. Reserved unconditionally: making it depend on
    /// whether the arrow is currently visible would feed the layout its own output.</summary>
    private const double TabArrowReserve = 32;

    private void WireDeviceTabScroller()
    {
        TcDevices.ApplyTemplate();
        _tabStrip        = TcDevices.Template.FindName("DeviceTabStrip",     TcDevices) as DeviceTabPanel;
        _tabScrollLeft   = TcDevices.Template.FindName("BtnTabScrollLeft",   TcDevices) as Button;
        _tabScrollRight  = TcDevices.Template.FindName("BtnTabScrollRight",  TcDevices) as Button;
        if (_tabStrip is null) return;

        _tabStrip.ScrollStateChanged += (_, _) => UpdateTabScrollArrows();
        TcDevices.SizeChanged += (_, _) => UpdateTabStripWidth();
        UpdateTabStripWidth();
    }

    /// <summary>Feeds the strip the width it may actually use. The strip lives in a
    /// horizontal StackPanel (infinite width), so without this it would never know it has
    /// to hide anything; WPF folds MaxWidth into the measure constraint, which is what
    /// makes the strip shrink tab-by-tab as the window narrows.</summary>
    private void UpdateTabStripWidth()
    {
        // Before the first layout pass ActualWidth is 0 — clamping to 0 there would hide
        // every tab; leave the strip unconstrained until a real width is known.
        if (_tabStrip is null || TcDevices.ActualWidth <= TabArrowReserve) return;
        _tabStrip.MaxWidth = TcDevices.ActualWidth - TabArrowReserve;
    }

    private void UpdateTabScrollArrows()
    {
        if (_tabStrip is null) return;
        if (_tabScrollLeft is not null)
        {
            // Slide it into the hole the strip left after the pinned Home tab.
            _tabScrollLeft.Margin = new Thickness(_tabStrip.PinnedWidth, 0, 0, 0);
            _tabScrollLeft.Visibility = _tabStrip.CanScrollLeft ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_tabScrollRight is not null)
            _tabScrollRight.Visibility = _tabStrip.CanScrollRight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnTabScrollLeft_Click(object sender, RoutedEventArgs e)  => _tabStrip?.ScrollLeft();
    private void BtnTabScrollRight_Click(object sender, RoutedEventArgs e) => _tabStrip?.ScrollRight();

    /// <summary>Scrolls a tab into view when it gets selected without being clicked
    /// (device connected, profile applied, tab reorder) and is currently hidden.</summary>
    private void EnsureDeviceTabVisible(TabItem? tab) => _tabStrip?.EnsureVisible(tab);
}
