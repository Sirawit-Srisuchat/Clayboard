using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clayboard.UI;

/// <summary>
/// Drag-to-reorder for the bindings list, done in-app instead of with Windows drag and drop:
/// a snapshot of the row follows the pointer while the other rows glide apart to show where it
/// will land. Only transforms and one small bitmap are involved, so it stays smooth on any PC.
/// </summary>
internal sealed class RowDragController
{
    private const double EdgeZone = 44;    // Pointer this close to the list's top or bottom scrolls it.
    private const double MaxScrollStep = 16;
    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(140));

    private readonly ItemsControl _list;
    private readonly ScrollViewer _scroller;
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _scrollTimer;

    private readonly List<Slot> _slots = new();
    private object? _item;
    private int _from;
    private double _grab;       // Pointer offset from the dragged row's top.
    private double _height;
    private int _target;        // Drop position among the other rows (headers mode: index into _slots).
    private bool _targetAfter;
    private GhostAdorner? _ghost;
    private InsertionAdorner? _line;

    private sealed class Slot(object item, FrameworkElement row, double top, double height)
    {
        public object Item { get; } = item;
        public FrameworkElement Row { get; } = row;
        public double Top { get; } = top;
        public double Height { get; } = height;
        public double Middle => Top + Height / 2;
        public double Shift { get; set; }
        public TranslateTransform Transform { get; } = new();
    }

    public RowDragController(ItemsControl list, ScrollViewer scroller, MainViewModel viewModel)
    {
        _list = list;
        _scroller = scroller;
        _viewModel = viewModel;
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += (_, _) => ScrollNearEdges();
    }

    public bool IsDragging => _item is not null;

    private bool IsHeaderDrag => _item is GroupHeader;

    /// <summary>Picks up <paramref name="item"/>; the list keeps the mouse until <see cref="Drop"/> or <see cref="Cancel"/>.</summary>
    public void Begin(object item)
    {
        _slots.Clear();
        for (int i = 0; i < _viewModel.Rows.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row)
                continue;
            var top = row.TranslatePoint(new Point(0, 0), _list).Y;
            _slots.Add(new Slot(_viewModel.Rows[i], row, top, row.ActualHeight));
        }
        _from = _slots.FindIndex(s => ReferenceEquals(s.Item, item));
        if (_from < 0)
            return;

        var dragged = _slots[_from];
        _item = item;
        _height = dragged.Height;
        _grab = Mouse.GetPosition(_list).Y - dragged.Top;
        _target = _from;

        foreach (var slot in _slots)
            slot.Row.RenderTransform = slot.Transform;

        var layer = AdornerLayer.GetAdornerLayer(_list);
        _ghost = new GhostAdorner(_list, Snapshot(dragged.Row), dragged.Top,
            (Brush)_list.FindResource("CardBrush"), (Brush)_list.FindResource("LineBrush"));
        layer?.Add(_ghost);
        if (IsHeaderDrag)
        {
            _line = new InsertionAdorner(_list, (Brush)_list.FindResource("ClayBrush"));
            layer?.Add(_line);
        }
        dragged.Row.Opacity = 0; // Its place stays open, as the gap the other rows close and reopen.

        _list.CaptureMouse();
        _scrollTimer.Start();
        Move();
    }

    /// <summary>Follows the pointer: moves the snapshot and opens the gap where the row would land.</summary>
    public void Move()
    {
        if (_item is null || _ghost is null)
            return;
        double pointer = Mouse.GetPosition(_list).Y;
        double first = _slots[0].Top;
        double last = _slots[^1].Top + _slots[^1].Height;
        _ghost.Top = Math.Clamp(pointer - _grab, first - _height / 2, last - _height / 2);

        if (IsHeaderDrag)
            TrackGroupTarget(pointer);
        else
            TrackRowTarget(_ghost.Top + _height / 2);
    }

    public void Drop()
    {
        if (_item is null)
            return;
        var item = _item;
        object? target = null;
        bool after = false;

        if (IsHeaderDrag)
        {
            if (_target >= 0 && !ReferenceEquals(_slots[_target].Item, item))
                (target, after) = (_slots[_target].Item, _targetAfter);
        }
        else if (_target != _from)
        {
            // Place it after the row that ends up above it, or before the first row at the very top.
            var others = _slots.Where((_, i) => i != _from).ToList();
            (target, after) = _target > 0 ? (others[_target - 1].Item, true) : (others[0].Item, false);
        }

        Finish(animateBack: false);
        if (target is not null)
            _viewModel.MoveRow(item, target, after); // Rows reorder under the reset transforms in the same frame.
    }

    public void Cancel() => Finish(animateBack: true);

    private void TrackRowTarget(double center)
    {
        int target = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i != _from && _slots[i].Middle < center)
                target++;
        }
        if (target == _target)
            return;
        _target = target;

        // Rows between the old and new spot slide by the dragged row's height to open the gap.
        for (int i = 0; i < _slots.Count; i++)
        {
            if (i == _from)
                continue;
            int position = i < _from ? i : i - 1; // Index among the rows left behind.
            double shift = i < _from && position >= target ? _height
                : i > _from && position < target ? -_height
                : 0;
            GlideTo(_slots[i], shift);
        }
    }

    // Groups move as whole sections, so instead of opening gaps the line marks the boundary they'll drop at.
    private void TrackGroupTarget(double pointer)
    {
        var headers = Enumerable.Range(0, _slots.Count).Where(i => _slots[i].Item is GroupHeader).ToList();
        int target = headers.Count > 0 ? headers[^1] : _from;
        bool after = true;
        double lineY = SectionBottom(target);
        foreach (int i in headers)
        {
            double bottom = SectionBottom(i);
            if (pointer < bottom)
            {
                after = pointer > (_slots[i].Top + bottom) / 2;
                (target, lineY) = (i, after ? bottom : _slots[i].Top);
                break;
            }
        }
        (_target, _targetAfter) = (target, after);
        if (_line is not null)
            _line.Y = lineY;
    }

    private double SectionBottom(int headerIndex)
    {
        double bottom = _slots[headerIndex].Top + _slots[headerIndex].Height;
        for (int i = headerIndex + 1; i < _slots.Count && _slots[i].Item is not GroupHeader; i++)
            bottom = _slots[i].Top + _slots[i].Height;
        return bottom;
    }

    private static void GlideTo(Slot slot, double shift)
    {
        if (slot.Shift == shift)
            return;
        slot.Shift = shift;
        if (!Motion.Enabled)
        {
            slot.Transform.BeginAnimation(TranslateTransform.YProperty, null);
            slot.Transform.Y = shift;
            return;
        }
        slot.Transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(shift, Glide) { EasingFunction = Motion.EaseOut });
    }

    private void Finish(bool animateBack)
    {
        if (_item is null)
            return;
        _scrollTimer.Stop();
        foreach (var slot in _slots)
        {
            if (animateBack)
            {
                GlideTo(slot, 0);
            }
            else
            {
                slot.Transform.BeginAnimation(TranslateTransform.YProperty, null);
                slot.Transform.Y = 0;
            }
        }
        if (_from >= 0 && _from < _slots.Count)
            _slots[_from].Row.Opacity = 1;

        var layer = AdornerLayer.GetAdornerLayer(_list);
        if (_ghost is not null)
            layer?.Remove(_ghost);
        if (_line is not null)
            layer?.Remove(_line);
        _ghost = null;
        _line = null;
        _item = null;
        if (_list.IsMouseCaptured)
            _list.ReleaseMouseCapture();
    }

    private void ScrollNearEdges()
    {
        double y = Mouse.GetPosition(_scroller).Y;
        double step = y < EdgeZone ? -(EdgeZone - y) / EdgeZone * MaxScrollStep
            : y > _scroller.ActualHeight - EdgeZone ? (y - (_scroller.ActualHeight - EdgeZone)) / EdgeZone * MaxScrollStep
            : 0;
        if (step == 0)
            return;
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + Math.Clamp(step, -MaxScrollStep, MaxScrollStep));
        Move(); // The content moved under a still pointer.
    }

    // One small bitmap of the row, taken once; drawing the live row instead would re-render it every frame.
    // Rendering the row directly would draw it at its offset inside the list, outside the bitmap, so it's
    // painted through a VisualBrush into a rectangle that starts at the bitmap's corner.
    private static ImageSource Snapshot(FrameworkElement row)
    {
        var dpi = VisualTreeHelper.GetDpi(row);
        var size = new Size(row.ActualWidth, row.ActualHeight);
        var painter = new DrawingVisual();
        using (var drawing = painter.RenderOpen())
        {
            // The view box pins the brush to the row's own box (which a VisualBrush measures from the row's
            // offset in its panel); by default it would fit just the content, shifting text off its place.
            var brush = new VisualBrush(row)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(row), size),
                Stretch = Stretch.Fill,
            };
            drawing.DrawRectangle(brush, null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(painter);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The picked-up row, drawn as a lifted card that follows the pointer.</summary>
    private sealed class GhostAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly Brush _background;
        private readonly Pen _border;
        private double _top;

        public GhostAdorner(UIElement adorned, ImageSource image, double top, Brush background, Brush border)
            : base(adorned)
        {
            IsHitTestVisible = false;
            _image = image;
            _top = top;
            _background = background;
            _border = new Pen(border, 1);
            _border.Freeze();
        }

        public double Top
        {
            get => _top;
            set
            {
                if (_top == value)
                    return;
                _top = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var size = new Size(((FrameworkElement)AdornedElement).ActualWidth, _image.Height);
            var card = new Rect(new Point(4, _top), new Size(size.Width - 8, size.Height));
            drawing.PushOpacity(0.96);
            drawing.DrawRoundedRectangle(_background, _border, card, 10, 10);
            drawing.DrawImage(_image, new Rect(new Point(0, _top), new Size(size.Width, _image.Height)));
            drawing.Pop();
        }
    }
}
