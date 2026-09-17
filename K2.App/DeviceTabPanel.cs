using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace K2.App;

/// <summary>
/// Items host for the top device tab strip (<c>TcDevices</c>).
/// <para>
/// The stock <see cref="TabPanel"/> WRAPS onto a second row when the tabs no longer fit
/// and stretches them to fill it — with many devices connected the strip ate half the
/// window. This panel never wraps: it lays the tabs out on a single row starting at
/// <see cref="FirstIndex"/> and simply leaves out whatever doesn't fit (hidden tabs are
/// arranged off-screen, the panel clips). The two narrow arrow cards in the TabControl
/// template shift <see cref="FirstIndex"/>; <see cref="ScrollStateChanged"/> tells the
/// template which of them still has something to reveal, so an arrow with nothing hidden
/// on its side is collapsed.
/// </para>
/// <para>
/// The Home tab (<c>Tag="home"</c>, the K2 logo) is PINNED: it stays glued to the left
/// edge and never scrolls away — scrolling only ever moves the device tabs after it.
/// The LEFT arrow belongs between Home and the first scrolled tab, which is inside this
/// panel, not next to it: the panel therefore reserves an <see cref="ArrowSlot"/>-wide
/// hole there while it has something hidden on the left (<see cref="PinnedWidth"/> tells
/// the template where to put the arrow). The right arrow needs no such trick — it simply
/// follows the panel in the template's StackPanel.
/// </para>
/// <para>
/// Width: <see cref="MeasureOverride"/> reports only the width the tabs it decided to
/// SHOW actually occupy, never the whole constraint — otherwise the right arrow, which
/// follows the panel in the template's StackPanel, would float away from the last tab by
/// however much slack the tab that didn't fit left behind. A tab is shown only if it fits
/// whole, so a tight window may well swallow two tabs instead of one; that is preferred
/// over a gap or a clipped card.
/// </para>
/// <para>
/// The constraint itself comes from MainWindow: the panel sits in a horizontal StackPanel
/// (infinite width), so <c>MaxWidth</c> is set to the TabControl width minus the space
/// reserved for the arrows. WPF folds MaxWidth into the constraint before
/// <see cref="MeasureOverride"/>, which is what makes the strip responsive to resizing.
/// </para>
/// </summary>
public sealed class DeviceTabPanel : Panel
{
    /// <summary>Width the left arrow card occupies (Width 28 + Margin 4), reserved between
    /// the pinned Home tab and the first scrolled tab while that arrow is showing.</summary>
    private const double ArrowSlot = 32;

    private readonly List<UIElement> _others = new();
    private UIElement? _pinned;

    private int _firstIndex;      // index into _others of the leftmost scrollable tab shown
    private int _shown;           // how many of _others are currently shown
    private double _room;         // width available to _others (constraint minus pinned tab + left-arrow slot)
    private double _leftSlot;     // ArrowSlot while the left arrow shows, else 0
    private bool _canLeft, _canRight;

    public DeviceTabPanel() => ClipToBounds = true;

    /// <summary>Raised (deferred to the dispatcher) when the "something is hidden to the
    /// left / to the right" state changes, so the arrows can show/hide.</summary>
    public event EventHandler? ScrollStateChanged;

    /// <summary>Left offset at which the template must place the left arrow: the width of
    /// the pinned Home tab, i.e. the start of the hole reserved for it.</summary>
    public double PinnedWidth => _pinned?.DesiredSize.Width ?? 0;

    public bool CanScrollLeft  => _canLeft;
    public bool CanScrollRight => _canRight;

    /// <summary>Index of the leftmost scrollable tab shown. Clamped during measure so the
    /// strip is never left with unused space on the right while tabs are hidden on the left.</summary>
    public int FirstIndex
    {
        get => _firstIndex;
        set
        {
            int v = Math.Max(0, value);
            if (v == _firstIndex) return;
            _firstIndex = v;
            InvalidateMeasure();
        }
    }

    public void ScrollLeft()  => FirstIndex = _firstIndex - 1;
    public void ScrollRight() => FirstIndex = _firstIndex + 1;

    /// <summary>Brings <paramref name="child"/> into the visible window — used when a tab
    /// is selected programmatically (device connected, profile applied, tab reorder) and
    /// happens to be scrolled out of view. The pinned Home tab is always visible.</summary>
    public void EnsureVisible(UIElement? child)
    {
        if (child is null) return;
        int idx = _others.IndexOf(child);
        if (idx < 0) return;
        if (idx < _firstIndex) { FirstIndex = idx; return; }
        if (idx < _firstIndex + _shown) return;

        int first = _firstIndex;
        while (first < idx)
        {
            double w = 0;
            for (int i = first; i <= idx; i++) w += _others[i].DesiredSize.Width;
            if (w <= _room + 0.5) break;
            first++;
        }
        FirstIndex = first;
    }

    /// <summary>Splits the live children into the pinned Home tab and the scrollable rest.
    /// Collapsed tabs (a device that isn't connected) take part in neither.</summary>
    private void Split()
    {
        _pinned = null;
        _others.Clear();
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            if (_pinned is null && c is FrameworkElement fe && fe.Tag as string == "home") _pinned = c;
            else _others.Add(c);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Split();

        double height = 0;
        var probe = new Size(double.PositiveInfinity, availableSize.Height);
        if (_pinned is not null) { _pinned.Measure(probe); height = _pinned.DesiredSize.Height; }
        foreach (var c in _others)
        {
            c.Measure(probe);
            height = Math.Max(height, c.DesiredSize.Height);
        }

        double pinnedW = _pinned?.DesiredSize.Width ?? 0;
        double avail = availableSize.Width;

        // The left-arrow slot only exists while that arrow is showing, and whether it shows
        // depends on the layout the slot takes part in. Two passes settle it (a third only
        // ever confirms): start with no slot, add it if tabs turn out to be hidden on the
        // left, and drop it again if adding it pulled the strip back to the first tab.
        double slot = 0;
        int first = 0, shown = 0;
        double used = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            _room = double.IsInfinity(avail) ? double.PositiveInfinity : Math.Max(0, avail - pinnedW - slot);

            first = Math.Min(_firstIndex, Math.Max(0, _others.Count - 1));
            // Pull back: if everything from first-1 to the end already fits, there is no
            // reason to keep that tab hidden (happens after enlarging the window).
            while (first > 0)
            {
                double w = 0;
                for (int i = first - 1; i < _others.Count; i++) w += _others[i].DesiredSize.Width;
                if (w > _room + 0.5) break;
                first--;
            }

            used = 0;
            shown = 0;
            for (int i = first; i < _others.Count; i++)
            {
                double w = _others[i].DesiredSize.Width;
                if (used + w > _room + 0.5) break;   // whole tabs only: no half-drawn card, no slack
                used += w;
                shown++;
            }

            double want = first > 0 ? ArrowSlot : 0;
            if (Math.Abs(want - slot) < 0.5) break;
            slot = want;
        }

        _firstIndex = first;
        _shown = shown;
        _leftSlot = slot;

        UpdateScrollState(first > 0, first + shown < _others.Count);
        return new Size(pinnedW + slot + used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        if (_pinned is not null)
        {
            double w = _pinned.DesiredSize.Width;
            _pinned.Arrange(new Rect(0, 0, w, finalSize.Height));
            x = w;
        }
        x += _leftSlot;   // hole the template's left arrow sits in
        for (int i = 0; i < _others.Count; i++)
        {
            var c = _others[i];
            double w = c.DesiredSize.Width;
            if (i >= _firstIndex && i < _firstIndex + _shown)
            {
                c.Arrange(new Rect(x, 0, w, finalSize.Height));
                x += w;
            }
            else
            {
                // Off-screen instead of zero-sized: a zero-width arrange makes the tab's
                // Border render degenerate content; ClipToBounds hides it either way.
                c.Arrange(new Rect(-20000, 0, w, finalSize.Height));
            }
        }
        return finalSize;
    }

    private void UpdateScrollState(bool left, bool right)
    {
        if (left == _canLeft && right == _canRight) return;
        _canLeft = left;
        _canRight = right;
        // Deferred: the handler flips arrow Visibility, which would re-enter layout.
        Dispatcher.BeginInvoke(new Action(() => ScrollStateChanged?.Invoke(this, EventArgs.Empty)),
                               System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
