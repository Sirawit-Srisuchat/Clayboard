using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Clayboard.UI;

/// <summary>The clay line that shows where a dragged row will land.</summary>
internal sealed class InsertionAdorner : Adorner
{
    private readonly Pen _pen;
    private readonly Brush _brush;
    private double _y;

    public InsertionAdorner(UIElement adorned, Brush brush) : base(adorned)
    {
        IsHitTestVisible = false;
        _brush = brush;
        _pen = new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pen.Freeze();
    }

    /// <summary>Vertical position of the line, in the adorned element's coordinates.</summary>
    public double Y
    {
        get => _y;
        set
        {
            if (_y == value)
                return;
            _y = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double right = ((FrameworkElement)AdornedElement).ActualWidth - 20;
        drawing.DrawEllipse(_brush, null, new Point(14, _y), 3.5, 3.5);
        drawing.DrawLine(_pen, new Point(18, _y), new Point(right, _y));
    }
}
