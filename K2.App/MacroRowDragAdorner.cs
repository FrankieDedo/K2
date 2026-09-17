// MacroRowDragAdorner.cs — the "ghost" that follows the cursor while a macro
// INPUTS row is being dragged to a new position.
//
// WPF's DragDrop gives no visual of the thing being dragged, only a cursor, so
// the row appeared to stay put until the drop landed. This adorner paints a
// semi-transparent copy of the dragged row under the cursor plus a thin
// insertion line at the place the drop would land. It lives on the ListView's
// adorner layer and is hit-test invisible, so it never interferes with the drop
// target underneath it.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace K2.App;

internal sealed class MacroRowDragAdorner : Adorner
{
    private readonly VisualCollection _children;
    private readonly Image _ghost;

    /// <summary>Where inside the dragged row the grab happened, so the ghost
    /// keeps the same grip point under the cursor instead of snapping its
    /// corner to it.</summary>
    private readonly Point _grabOffset;

    /// <summary>Cursor position, in the adorned element's coordinates.</summary>
    private Point _pos;

    /// <summary>Y of the insertion line (adorned-element coordinates), or null
    /// while the cursor is not over a valid drop position.</summary>
    private double? _insertY;

    private static readonly Pen InsertPen = CreateInsertPen();

    public MacroRowDragAdorner(UIElement adornedElement, FrameworkElement source, Point grabOffset)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        _grabOffset = grabOffset;
        _children = new VisualCollection(this);
        var size = source.RenderSize;
        _ghost = new Image
        {
            Width = size.Width,
            Height = size.Height,
            IsHitTestVisible = false,
            Opacity = 0.85,
            // A SNAPSHOT, not a live VisualBrush: the caller dims the original
            // row while it is being dragged, and a live brush would faithfully
            // dim the ghost along with it.
            Source = Snapshot(source),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 3,
                Opacity = 0.55
            }
        };
        _children.Add(_ghost);
    }

    /// <summary>Renders <paramref name="source"/> to a bitmap at the current DPI.
    /// Goes through a DrawingVisual + VisualBrush rather than rendering the
    /// element directly: <see cref="RenderTargetBitmap.Render"/> honours the
    /// visual's offset inside its parent, which would push a row well down the
    /// bitmap (or clean off it) depending on its position in the list.</summary>
    private static ImageSource Snapshot(FrameworkElement source)
    {
        var size = source.RenderSize;
        var dpi = VisualTreeHelper.GetDpi(source);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(source), null, new Rect(size));

        var bmp = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Moves the ghost and (re)places the insertion line. Both are in
    /// the adorned element's coordinate space.</summary>
    public void Update(Point cursor, double? insertY)
    {
        _pos = cursor;
        _insertY = insertY;
        InvalidateArrange();
        InvalidateVisual();
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _ghost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ghost.Arrange(new Rect(
            new Point(_pos.X - _grabOffset.X, _pos.Y - _grabOffset.Y),
            new Size(_ghost.Width, _ghost.Height)));
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_insertY is not double y) return;
        double w = AdornedElement.RenderSize.Width;
        dc.DrawLine(InsertPen, new Point(6, y), new Point(w - 6, y));
    }

    private static Pen CreateInsertPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x52, 0x52)), 2);
        pen.Freeze();
        return pen;
    }
}
