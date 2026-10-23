using Avalonia;
using Avalonia.Input;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.App.Controls;

public sealed class PlotPointEventArgs(PlotHit hit, KeyModifiers modifiers) : EventArgs
{
    public PlotHit Hit { get; } = hit;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class PlotLineDragEventArgs(object dragId, double value, bool completed) : EventArgs
{
    public object DragId { get; } = dragId;
    public double Value { get; } = value;
    public bool Completed { get; } = completed;
}

public sealed class PlotCursorEventArgs(double x, double y) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>Interactive XY chart: wheel zoom, drag pan, double-click reset, point picking and draggable reference lines.</summary>
public sealed class PlotView : SkiaView
{
    public static readonly StyledProperty<XyPlotModel?> ModelProperty = AvaloniaProperty.Register<PlotView, XyPlotModel?>(nameof(Model));

    private volatile PlotLayout? _layout;
    private (double X0, double X1, double Y0, double Y1)? _zoom;
    private Point? _panStart;
    private (double X0, double X1, double Y0, double Y1)? _panOrigin;
    private ReferenceLine? _dragging;

    static PlotView()
    {
        AffectsRender<PlotView>(ModelProperty);
    }

    public XyPlotModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Allow zooming the X axis.</summary>
    public bool ZoomX { get; set; } = true;

    /// <summary>Allow zooming the Y axis.</summary>
    public bool ZoomY { get; set; } = true;

    public event EventHandler<PlotPointEventArgs>? PointClicked;

    public event EventHandler<PlotLineDragEventArgs>? LineDragged;

    public event EventHandler<PlotCursorEventArgs>? CursorMoved;

    public event EventHandler<PlotCursorEventArgs>? BackgroundClicked;

    public PlotLayout? Layout => _layout;

    public void ResetZoom()
    {
        _zoom = null;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            ApplyZoom();
        }
    }

    private void ApplyZoom()
    {
        if (Model is { } m && _zoom is { } z)
        {
            if (ZoomX)
            {
                (m.X.Min, m.X.Max) = (z.X0, z.X1);
            }

            if (ZoomY)
            {
                (m.Y.Min, m.Y.Max) = (z.Y0, z.Y1);
            }
        }
    }

    protected override Action<SKCanvas, SKRect, PlotTheme> CreatePainter()
    {
        var model = Model?.Snapshot();
        return (canvas, bounds, theme) =>
        {
            if (model is null)
            {
                using var bg = Draw.Fill(theme.Background);
                canvas.DrawRect(bounds, bg);
                return;
            }

            // The layout describes exactly what was drawn and is read by pointer handling on the UI thread.
            _layout = XyPlotRenderer.Render(canvas, bounds, model, theme);
        };
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Model is not { } m || _layout is not { } l)
        {
            return;
        }

        var p = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 0.8 : 1.25;
        var cx = l.ToDataX((float)p.X);
        var cy = l.ToDataY((float)p.Y);
        var z = _zoom ?? (m.X.Min, m.X.Max, m.Y.Min, m.Y.Max);
        var zoomX = ZoomX && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var zoomY = ZoomY && !e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (m.X.Logarithmic)
        {
            zoomX = false;
        }

        _zoom = (zoomX ? cx - (cx - z.X0) * factor : z.X0, zoomX ? cx + (z.X1 - cx) * factor : z.X1,
            zoomY ? cy - (cy - z.Y0) * factor : z.Y0, zoomY ? cy + (z.Y1 - cy) * factor : z.Y1);
        ApplyZoom();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Model is not { } m || _layout is not { } l)
        {
            return;
        }

        var p = e.GetPosition(this);
        var sp = new SKPoint((float)p.X, (float)p.Y);
        var props = e.GetCurrentPoint(this).Properties;
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (XyPlotRenderer.HitTest(m, l, sp) is { } hit)
            {
                PointClicked?.Invoke(this, new PlotPointEventArgs(hit, e.KeyModifiers));
                e.Handled = true;
                return;
            }

            if (XyPlotRenderer.HitLine(m, l, sp) is { } line)
            {
                _dragging = line;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (l.PlotArea.Contains(sp))
            {
                BackgroundClicked?.Invoke(this, new PlotCursorEventArgs(l.ToDataX(sp.X), l.ToDataY(sp.Y)));
            }
        }

        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed || (props.IsLeftButtonPressed && l.PlotArea.Contains(sp)))
        {
            _panStart = p;
            _panOrigin = _zoom ?? (m.X.Min, m.X.Max, m.Y.Min, m.Y.Max);
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Model is not { } m || _layout is not { } l)
        {
            return;
        }

        var p = e.GetPosition(this);
        var sp = new SKPoint((float)p.X, (float)p.Y);
        if (_dragging is { } line)
        {
            var v = line.Orientation == LineOrientation.Horizontal ? l.ToDataY(sp.Y) : l.ToDataX(sp.X);
            LineDragged?.Invoke(this, new PlotLineDragEventArgs(line.DragId!, v, false));
            return;
        }

        if (_panStart is { } start && _panOrigin is { } o)
        {
            var dx = l.ToDataX((float)p.X) - l.ToDataX((float)start.X);
            var dy = l.ToDataY((float)p.Y) - l.ToDataY((float)start.Y);
            _zoom = (ZoomX && !m.X.Logarithmic ? o.X0 - dx : o.X0, ZoomX && !m.X.Logarithmic ? o.X1 - dx : o.X1, ZoomY ? o.Y0 - dy : o.Y0, ZoomY ? o.Y1 - dy : o.Y1);
            ApplyZoom();
            InvalidateVisual();
            return;
        }

        Cursor = XyPlotRenderer.HitLine(m, l, sp) is { } hover
            ? new Cursor(hover.Orientation == LineOrientation.Horizontal ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
            : XyPlotRenderer.HitTest(m, l, sp) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (l.PlotArea.Contains(sp))
        {
            CursorMoved?.Invoke(this, new PlotCursorEventArgs(l.ToDataX(sp.X), l.ToDataY(sp.Y)));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is { } line && _layout is { } l)
        {
            var p = e.GetPosition(this);
            var v = line.Orientation == LineOrientation.Horizontal ? l.ToDataY((float)p.Y) : l.ToDataX((float)p.X);
            LineDragged?.Invoke(this, new PlotLineDragEventArgs(line.DragId!, v, true));
        }

        _dragging = null;
        _panStart = null;
        _panOrigin = null;
        e.Pointer.Capture(null);
    }
}

/// <summary>Draws the borehole geometry diagram.</summary>
public sealed class BoreholeView : SkiaView
{
    public static readonly StyledProperty<BoreholeDiagramModel?> ModelProperty = AvaloniaProperty.Register<BoreholeView, BoreholeDiagramModel?>(nameof(Model));

    static BoreholeView()
    {
        AffectsRender<BoreholeView>(ModelProperty);
    }

    public BoreholeDiagramModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override Action<SKCanvas, SKRect, PlotTheme> CreatePainter()
    {
        var model = Model?.Snapshot();
        return (canvas, bounds, theme) =>
        {
            if (model is not null)
            {
                BoreholeDiagramRenderer.Render(canvas, bounds, model, theme);
            }
        };
    }
}
