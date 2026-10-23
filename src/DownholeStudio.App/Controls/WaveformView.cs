using Avalonia;
using Avalonia.Input;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.App.Controls;

public sealed class TracePointerEventArgs(int traceIndex, object? key, double time, KeyModifiers modifiers, bool completed = false) : EventArgs
{
    public int TraceIndex { get; } = traceIndex;
    public object? Key { get; } = key;
    public double Time { get; } = time;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool Completed { get; } = completed;
}

/// <summary>
/// Interactive seismic section. Mouse wheel zooms time around the cursor, Ctrl+wheel changes
/// amplitude gain, Shift+wheel scrolls traces, right/middle drag pans, double-click fits all.
/// In picking mode a click places a pick, dragging a pick moves it, Alt+click snaps to the
/// automatic suggestion.
/// </summary>
public sealed class WaveformView : SkiaView
{
    public static readonly StyledProperty<WaveformSectionModel?> ModelProperty = AvaloniaProperty.Register<WaveformView, WaveformSectionModel?>(nameof(Model));
    public static readonly StyledProperty<bool> PickingEnabledProperty = AvaloniaProperty.Register<WaveformView, bool>(nameof(PickingEnabled));
    public static readonly StyledProperty<bool> MagnifierProperty = AvaloniaProperty.Register<WaveformView, bool>(nameof(Magnifier), true);

    private volatile WaveLayout? _layout;
    private (double T0, double T1)? _timeView;
    private double _traceOffset;
    private double? _visibleTraces;
    private Point? _panStart;
    private (double T0, double T1, double Offset)? _panOrigin;
    private int _dragTrace = -1;
    private (int Trace, double Time, SKPoint Pixel)? _hover;

    static WaveformView()
    {
        AffectsRender<WaveformView>(ModelProperty, PickingEnabledProperty, MagnifierProperty);
    }

    public WaveformSectionModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public bool PickingEnabled
    {
        get => GetValue(PickingEnabledProperty);
        set => SetValue(PickingEnabledProperty, value);
    }

    public bool Magnifier
    {
        get => GetValue(MagnifierProperty);
        set => SetValue(MagnifierProperty, value);
    }

    /// <summary>Provides hover information lines (depth, channel, amplitude, pick, SNR, QC…).</summary>
    public Func<int, double, IReadOnlyList<string>>? HoverInfo { get; set; }

    public event EventHandler<TracePointerEventArgs>? TraceClicked;

    public event EventHandler<TracePointerEventArgs>? PickDragged;

    public event EventHandler<TracePointerEventArgs>? Hovered;

    /// <summary>Raised on Ctrl+wheel with a multiplicative gain factor.</summary>
    public event EventHandler<double>? GainWheel;

    public (double T0, double T1)? TimeView => _timeView;

    public void FitAll()
    {
        _timeView = null;
        _traceOffset = 0;
        _visibleTraces = null;
        ApplyView();
        InvalidateVisual();
    }

    /// <summary>Zooms the time axis by a factor around the centre (toolbar buttons).</summary>
    public void ZoomTime(double factor)
    {
        if (Model is not { } m)
        {
            return;
        }

        var (t0, t1) = _timeView ?? (m.TimeMin, m.TimeMax);
        var c = 0.5 * (t0 + t1);
        _timeView = (c - (c - t0) * factor, c + (t1 - c) * factor);
        ApplyView();
        InvalidateVisual();
    }

    /// <summary>Changes how many traces are visible (vertical zoom of stacked sections).</summary>
    public void ZoomTraces(double factor)
    {
        if (Model is not { } m || m.Traces.Count == 0)
        {
            return;
        }

        _visibleTraces = Math.Clamp((_visibleTraces ?? m.Traces.Count) * factor, 1, m.Traces.Count);
        ApplyView();
        InvalidateVisual();
    }

    /// <summary>Scrolls so that a trace is visible.</summary>
    public void EnsureVisible(int index)
    {
        if (Model is not { } m || _visibleTraces is not { } visible)
        {
            return;
        }

        if (index < _traceOffset || index > _traceOffset + visible - 1)
        {
            _traceOffset = Math.Clamp(index - visible / 2, 0, Math.Max(0, m.Traces.Count - visible));
            ApplyView();
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            ApplyView();
        }
    }

    private void ApplyView()
    {
        if (Model is not { } m)
        {
            return;
        }

        if (_timeView is { } v)
        {
            m.TimeMin = v.T0;
            m.TimeMax = v.T1;
        }

        if (!m.SpaceByPosition && !m.OverlayMode)
        {
            m.TraceOffset = _traceOffset;
            m.VisibleTraceCount = _visibleTraces;
        }
    }

    protected override Action<SKCanvas, SKRect, PlotTheme> CreatePainter()
    {
        // Everything is captured here on the UI thread; the painter below may run on the render thread.
        var m = Model?.Snapshot();
        var hover = m is not null && _hover is { } h && h.Trace >= 0 && h.Trace < m.Traces.Count ? h : ((int Trace, double Time, SKPoint Pixel)?)null;
        var magnifier = PickingEnabled && Magnifier && m is { OverlayMode: false };
        var lines = hover is { } hv ? HoverInfo?.Invoke(hv.Trace, hv.Time) : null;
        return (canvas, bounds, theme) =>
        {
            if (m is null)
            {
                using var bg = Draw.Fill(theme.Background);
                canvas.DrawRect(bounds, bg);
                return;
            }

            var layout = WaveformRenderer.Render(canvas, bounds, m, theme);
            _layout = layout;
            if (hover is not { } h)
            {
                return;
            }

            if (magnifier)
            {
                DrawMagnifier(canvas, m, layout, h.Trace, h.Time, h.Pixel, theme);
            }

            if (lines is { Count: > 0 })
            {
                DrawInfoCard(canvas, lines, h.Pixel, bounds, theme);
            }
        };
    }

    private static void DrawInfoCard(SKCanvas canvas, IReadOnlyList<string> lines, SKPoint at, SKRect bounds, PlotTheme theme)
    {
        using var font = PlotFonts.Font(11.5f);
        using var bold = PlotFonts.Font(11.5f, PlotFonts.SemiBold);
        var w = lines.Max(l => Draw.Width(l, font)) + 20;
        var h = lines.Count * 16 + 12;
        var x = at.X + 18;
        var y = at.Y + 18;
        if (x + w > bounds.Right - 4)
        {
            x = at.X - w - 12;
        }

        if (y + h > bounds.Bottom - 4)
        {
            y = at.Y - h - 12;
        }

        var rect = new SKRect(x, y, x + w, y + h);
        using (var shadow = Draw.Fill(SKColors.Black.WithAlpha(40)))
        {
            canvas.DrawRoundRect(new SKRect(rect.Left + 1, rect.Top + 2, rect.Right + 1, rect.Bottom + 2), 6, 6, shadow);
        }

        using (var bg = Draw.Fill(theme.Background.WithAlpha(245)))
        using (var border = Draw.Stroke(theme.Grid, 1))
        {
            canvas.DrawRoundRect(rect, 6, 6, bg);
            canvas.DrawRoundRect(rect, 6, 6, border);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            Draw.Text(canvas, lines[i], x + 10, y + 18 + 16 * i, i == 0 ? bold : font, i == 0 ? theme.Text : theme.MutedText);
        }
    }

    private static void DrawMagnifier(SKCanvas canvas, WaveformSectionModel m, WaveLayout layout, int index, double time, SKPoint at, PlotTheme theme)
    {
        var tr = m.Traces[index];
        var span = (m.TimeMax - m.TimeMin) * 0.06;
        var t0 = time - span;
        var t1 = time + span;
        var w = 260f;
        var h = 120f;
        var area = layout.PlotArea;
        var x = Math.Min(area.Right - w - 6, Math.Max(area.Left + 6, at.X - w / 2));
        var y = at.Y - h - 36 < area.Top ? at.Y + 36 : at.Y - h - 36;
        var rect = new SKRect(x, y, x + w, y + h);
        using (var bg = Draw.Fill(theme.Background))
        using (var border = Draw.Stroke(theme.Accent, 1.2f))
        {
            canvas.DrawRoundRect(rect, 6, 6, bg);
            canvas.DrawRoundRect(rect, 6, 6, border);
        }

        var sub = new WaveformSectionModel { TimeMin = t0, TimeMax = t1, Mode = WaveDisplayMode.WiggleAndArea, OverlayMode = true, TimeTitle = "", NormalizeEach = true };
        sub.Traces.Add(new WaveTrace
        {
            Key = tr.Key, Samples = tr.Samples, SampleInterval = tr.SampleInterval, Delay = tr.Delay, Picks = tr.Picks, Color = tr.Color, Gain = 1.6,
        });
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, 6, 6));
        var inner = new SKRect(rect.Left - 8, rect.Top - 8, rect.Right + 8, rect.Bottom + 34);
        var subLayout = WaveformRenderer.Render(canvas, inner, sub, theme with { Background = SKColors.Transparent }, 0.8f);
        using (var cursor = Draw.Stroke(theme.Accent, 1f, dash: [3, 3]))
        {
            var cx = subLayout.ToPixelX(time);
            canvas.DrawLine(cx, rect.Top, cx, rect.Bottom, cursor);
        }

        canvas.Restore();
        using var label = PlotFonts.Font(10, PlotFonts.Medium);
        Draw.Text(canvas, $"×{(m.TimeMax - m.TimeMin) / (t1 - t0):0} · {time * 1000:0.000} ms", rect.Left + 8, rect.Top + 14, label, theme.Accent);
    }

    private (int Index, double Time)? Locate(Point p)
    {
        if (_layout is not { } l || Model is null)
        {
            return null;
        }

        var sp = new SKPoint((float)p.X, (float)p.Y);
        if (!l.PlotArea.Contains(sp))
        {
            return null;
        }

        return (l.TraceAt(sp.Y), l.ToTime(sp.X));
    }

    private bool NearPick(int index, double time, out double pickTime)
    {
        pickTime = 0;
        if (Model is not { } m || _layout is not { } l || index < 0)
        {
            return false;
        }

        foreach (var pk in m.Traces[index].Picks.Where(p => p.Kind is PickMarkerKind.P or PickMarkerKind.S or PickMarkerKind.Reference))
        {
            if (Math.Abs(l.ToPixelX(pk.Time) - l.ToPixelX(time)) <= 6)
            {
                pickTime = pk.Time;
                return true;
            }
        }

        return false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Model is not { } m || _layout is not { } l)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            GainWheel?.Invoke(this, e.Delta.Y > 0 ? 1.25 : 0.8);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (!m.SpaceByPosition && !m.OverlayMode)
            {
                var visible = _visibleTraces ?? m.Traces.Count;
                _traceOffset = Math.Clamp(_traceOffset - Math.Sign(e.Delta.Y) * Math.Max(1, visible / 6), 0, Math.Max(0, m.Traces.Count - visible));
            }
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            ZoomTraces(e.Delta.Y > 0 ? 0.8 : 1.25);
            e.Handled = true;
            return;
        }
        else
        {
            var p = e.GetPosition(this);
            var c = l.ToTime((float)p.X);
            var (t0, t1) = _timeView ?? (m.TimeMin, m.TimeMax);
            var f = e.Delta.Y > 0 ? 0.8 : 1.25;
            _timeView = (c - (c - t0) * f, c + (t1 - c) * f);
        }

        ApplyView();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Model is not { } m)
        {
            return;
        }

        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (e.ClickCount == 2 && props.IsLeftButtonPressed && !PickingEnabled)
        {
            FitAll();
            e.Handled = true;
            return;
        }

        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            _panStart = p;
            _panOrigin = (_timeView?.T0 ?? m.TimeMin, _timeView?.T1 ?? m.TimeMax, _traceOffset);
            e.Pointer.Capture(this);
            return;
        }

        if (!props.IsLeftButtonPressed || Locate(p) is not { } loc || loc.Index < 0)
        {
            return;
        }

        if (PickingEnabled && NearPick(loc.Index, loc.Time, out _))
        {
            _dragTrace = loc.Index;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var time = loc.Time;
        if (PickingEnabled && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && m.Traces[loc.Index].Picks.FirstOrDefault(pk => pk.Kind == PickMarkerKind.Suggestion) is { } suggestion)
        {
            time = suggestion.Time;
        }

        TraceClicked?.Invoke(this, new TracePointerEventArgs(loc.Index, m.Traces[loc.Index].Key, time, e.KeyModifiers));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Model is not { } m || _layout is not { } l)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (_panStart is { } start && _panOrigin is { } o)
        {
            var dt = (p.X - start.X) / l.PlotArea.Width * (o.T1 - o.T0);
            _timeView = (o.T0 - dt, o.T1 - dt);
            if (!m.SpaceByPosition && !m.OverlayMode && l.Spacing > 0)
            {
                var visible = _visibleTraces ?? m.Traces.Count;
                _traceOffset = Math.Clamp(o.Offset - (p.Y - start.Y) / l.Spacing, 0, Math.Max(0, m.Traces.Count - visible));
            }

            ApplyView();
            InvalidateVisual();
            return;
        }

        var loc = Locate(p);
        if (_dragTrace >= 0)
        {
            var t = l.ToTime((float)p.X);
            PickDragged?.Invoke(this, new TracePointerEventArgs(_dragTrace, m.Traces[_dragTrace].Key, t, e.KeyModifiers));
            _hover = (_dragTrace, t, new SKPoint((float)p.X, (float)p.Y));
            InvalidateVisual();
            return;
        }

        if (loc is { Index: >= 0 } hit)
        {
            _hover = (hit.Index, hit.Time, new SKPoint((float)p.X, (float)p.Y));
            Cursor = PickingEnabled && NearPick(hit.Index, hit.Time, out _) ? new Cursor(StandardCursorType.SizeWestEast)
                : PickingEnabled ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            Hovered?.Invoke(this, new TracePointerEventArgs(hit.Index, m.Traces[hit.Index].Key, hit.Time, e.KeyModifiers));
        }
        else
        {
            _hover = null;
            Cursor = Cursor.Default;
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragTrace >= 0 && Model is { } m && _layout is { } l)
        {
            var t = l.ToTime((float)e.GetPosition(this).X);
            PickDragged?.Invoke(this, new TracePointerEventArgs(_dragTrace, m.Traces[_dragTrace].Key, t, e.KeyModifiers, completed: true));
        }

        _dragTrace = -1;
        _panStart = null;
        _panOrigin = null;
        e.Pointer.Capture(null);
    }
}
