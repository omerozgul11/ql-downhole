using SkiaSharp;

namespace DownholeStudio.Visualization;

/// <summary>A linear (or log10) axis with "nice" tick spacing.</summary>
public sealed class Axis
{
    public string Title { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    /// <summary>Values increase downward (Y) or leftward (X) — used for depth axes.</summary>
    public bool Inverted { get; set; }
    public bool Logarithmic { get; set; }
    public Func<double, string>? Format { get; set; }
    public bool ShowGrid { get; set; } = true;

    public double Span => Max - Min;

    /// <summary>Copies every setting from another axis.</summary>
    public void CopyFrom(Axis other)
    {
        Title = other.Title;
        Min = other.Min;
        Max = other.Max;
        Inverted = other.Inverted;
        Logarithmic = other.Logarithmic;
        Format = other.Format;
        ShowGrid = other.ShowGrid;
    }

    public void Fit(IEnumerable<double> values, double padFraction = 0.05, bool includeZero = false)
    {
        var finite = values.Where(double.IsFinite).ToList();
        if (includeZero)
        {
            finite.Add(0);
        }

        if (finite.Count == 0)
        {
            return;
        }

        var lo = finite.Min();
        var hi = finite.Max();
        if (Logarithmic)
        {
            lo = Math.Max(lo, 1e-12);
            Min = Math.Pow(10, Math.Floor(Math.Log10(lo)));
            Max = Math.Pow(10, Math.Ceiling(Math.Log10(Math.Max(hi, lo * 10))));
            return;
        }

        if (hi - lo < 1e-12 * Math.Max(1, Math.Abs(hi)))
        {
            var d = Math.Abs(hi) > 0 ? Math.Abs(hi) * 0.1 : 1;
            lo -= d;
            hi += d;
        }

        var pad = (hi - lo) * padFraction;
        Min = includeZero && lo >= 0 ? Math.Max(0, lo - pad) : lo - pad;
        Max = hi + pad;
    }

    /// <summary>Tick positions using 1-2-5 steps (Heckbert's nice numbers) for roughly <paramref name="target"/> ticks.</summary>
    public (IReadOnlyList<double> Ticks, double Step) Ticks(int target)
    {
        if (Logarithmic)
        {
            var list = new List<double>();
            for (var e = Math.Floor(Math.Log10(Min)); e <= Math.Ceiling(Math.Log10(Max)); e++)
            {
                var v = Math.Pow(10, e);
                if (v >= Min * 0.999 && v <= Max * 1.001)
                {
                    list.Add(v);
                }
            }

            return (list, 1);
        }

        var step = NiceStep(Span / Math.Max(1, target));
        var first = Math.Ceiling(Min / step) * step;
        var ticks = new List<double>();
        for (var v = first; v <= Max + step * 1e-9; v += step)
        {
            ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        }

        return (ticks, step);
    }

    public static double NiceStep(double raw)
    {
        if (!(raw > 0) || !double.IsFinite(raw))
        {
            return 1;
        }

        var exp = Math.Floor(Math.Log10(raw));
        var f = raw / Math.Pow(10, exp);
        var nice = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
        return nice * Math.Pow(10, exp);
    }

    public string Label(double v, double step) => Format?.Invoke(v) ?? Draw.Number(v, step);

    internal double Normalise(double v) => Logarithmic
        ? (Math.Log10(Math.Max(v, 1e-300)) - Math.Log10(Min)) / (Math.Log10(Max) - Math.Log10(Min))
        : (v - Min) / Span;

    internal double Denormalise(double f) => Logarithmic
        ? Math.Pow(10, Math.Log10(Min) + f * (Math.Log10(Max) - Math.Log10(Min)))
        : Min + f * Span;
}

public enum MarkerShape
{
    Circle,
    Square,
    Triangle,
    Diamond,
    Cross,
}

/// <summary>A data point with optional individual styling and a tag for hit testing.</summary>
public sealed record PlotPoint(double X, double Y)
{
    public SKColor? Color { get; init; }
    public MarkerShape? Shape { get; init; }
    /// <summary>Hollow markers show excluded or automatic values.</summary>
    public bool Hollow { get; init; }
    public bool Selected { get; init; }
    public object? Tag { get; init; }
    public string? Label { get; init; }
}

public enum SeriesKind
{
    Line,
    Scatter,
    LineAndMarkers,
    /// <summary>Vertical step profile: points are (value, depth) pairs given as consecutive top/bottom pairs.</summary>
    StepProfile,
    /// <summary>Filled area between the curve and Y = 0 (spectra).</summary>
    Area,
}

public sealed class PlotSeries
{
    public string Title { get; set; } = "";
    public SeriesKind Kind { get; set; } = SeriesKind.Line;
    public SKColor Color { get; set; } = SKColors.Black;
    public float LineWidth { get; set; } = 1.6f;
    public float MarkerSize { get; set; } = 5f;
    public MarkerShape Marker { get; set; } = MarkerShape.Circle;
    public float[]? Dash { get; set; }
    public bool ShowInLegend { get; set; } = true;
    public bool HitTestable { get; set; } = true;
    public IReadOnlyList<PlotPoint> Points { get; set; } = [];

    public PlotSeries Clone() => (PlotSeries)MemberwiseClone();
}

public enum LineOrientation
{
    Horizontal,
    Vertical,
}

/// <summary>A reference line (layer boundary, cursor, threshold). Draggable lines carry an id.</summary>
public sealed record ReferenceLine(LineOrientation Orientation, double Value, SKColor Color)
{
    public string? Label { get; init; }
    public float Width { get; init; } = 1.2f;
    public float[]? Dash { get; init; }
    public object? DragId { get; init; }
    public bool Emphasised { get; init; }
}

public sealed record TextNote(double X, double Y, string Text, SKColor Color)
{
    public SKTextAlign Align { get; init; } = SKTextAlign.Left;
    public bool Bold { get; init; }
}

public sealed record ShadedBand(LineOrientation Orientation, double From, double To, SKColor Color);

public sealed class XyPlotModel
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public Axis X { get; } = new();
    public Axis Y { get; } = new();
    public List<PlotSeries> Series { get; } = [];
    public List<ReferenceLine> Lines { get; } = [];
    public List<TextNote> Notes { get; } = [];
    public List<ShadedBand> Bands { get; } = [];
    public bool ShowLegend { get; set; } = true;
    /// <summary>X axis drawn at the top (common for depth profiles).</summary>
    public bool XAxisOnTop { get; set; }
    public (double X, double Y)? Crosshair { get; set; }
    public string? EmptyMessage { get; set; }

    /// <summary>
    /// An independent copy for rendering on another thread: settings, axes, series and lists are copied;
    /// point lists are shared because they are never modified after a series is built.
    /// </summary>
    public XyPlotModel Snapshot()
    {
        var copy = new XyPlotModel { Title = Title, Subtitle = Subtitle, ShowLegend = ShowLegend, XAxisOnTop = XAxisOnTop, Crosshair = Crosshair, EmptyMessage = EmptyMessage };
        copy.X.CopyFrom(X);
        copy.Y.CopyFrom(Y);
        copy.Series.AddRange(Series.Select(s => s.Clone()));
        copy.Lines.AddRange(Lines);
        copy.Notes.AddRange(Notes);
        copy.Bands.AddRange(Bands);
        return copy;
    }
}

/// <summary>Pixel layout of a rendered plot, for hit testing and coordinate conversion.</summary>
public sealed class PlotLayout(SKRect plotArea, Axis x, Axis y)
{
    public SKRect PlotArea { get; } = plotArea;

    public float ToPixelX(double v)
    {
        var f = x.Normalise(v);
        return (float)(x.Inverted ? PlotArea.Right - f * PlotArea.Width : PlotArea.Left + f * PlotArea.Width);
    }

    public float ToPixelY(double v)
    {
        var f = y.Normalise(v);
        return (float)(y.Inverted ? PlotArea.Top + f * PlotArea.Height : PlotArea.Bottom - f * PlotArea.Height);
    }

    public double ToDataX(float px)
    {
        var f = (px - PlotArea.Left) / PlotArea.Width;
        return x.Denormalise(x.Inverted ? 1 - f : f);
    }

    public double ToDataY(float py)
    {
        var f = (py - PlotArea.Top) / PlotArea.Height;
        return y.Denormalise(y.Inverted ? f : 1 - f);
    }

    public SKPoint ToPixel(double vx, double vy) => new(ToPixelX(vx), ToPixelY(vy));
}

public sealed record PlotHit(PlotSeries Series, int Index, PlotPoint Point, float Distance);

public static class XyPlotRenderer
{
    /// <param name="scale">Multiplier for fonts and strokes (1 = screen; larger for print).</param>
    public static PlotLayout Render(SKCanvas canvas, SKRect bounds, XyPlotModel model, PlotTheme theme, float scale = 1f)
    {
        using var bg = Draw.Fill(theme.Background);
        canvas.DrawRect(bounds, bg);
        using var tickFont = PlotFonts.Font(11 * scale);
        using var titleFont = PlotFonts.Font(13 * scale, PlotFonts.SemiBold);
        using var axisFont = PlotFonts.Font(11.5f * scale, PlotFonts.Medium);
        using var subFont = PlotFonts.Font(11 * scale);

        var top = bounds.Top + 8 * scale;
        if (!string.IsNullOrEmpty(model.Title))
        {
            Draw.Text(canvas, model.Title, bounds.Left + 10 * scale, top + 13 * scale, titleFont, theme.Text);
            top += 20 * scale;
            if (!string.IsNullOrEmpty(model.Subtitle))
            {
                Draw.Text(canvas, model.Subtitle, bounds.Left + 10 * scale, top + 10 * scale, subFont, theme.MutedText);
                top += 16 * scale;
            }
        }

        var (yTicks, yStep) = model.Y.Ticks(Math.Max(3, (int)(bounds.Height / (48 * scale))));
        var yLabelWidth = yTicks.Count == 0 ? 0 : yTicks.Max(t => Draw.Width(model.Y.Label(t, yStep), tickFont));
        var left = bounds.Left + 10 * scale + (string.IsNullOrEmpty(model.Y.Title) ? 0 : 16 * scale) + yLabelWidth + 6 * scale;
        var right = bounds.Right - 14 * scale;
        var xAxisSpace = 18 * scale + (string.IsNullOrEmpty(model.X.Title) ? 0 : 16 * scale);
        var plotTop = top + 6 * scale + (model.XAxisOnTop ? xAxisSpace : 0);
        var plotBottom = bounds.Bottom - 8 * scale - (model.XAxisOnTop ? 0 : xAxisSpace);
        if (plotBottom - plotTop < 20 || right - left < 20)
        {
            return new PlotLayout(new SKRect(left, plotTop, Math.Max(left + 1, right), Math.Max(plotTop + 1, plotBottom)), model.X, model.Y);
        }

        var area = new SKRect(left, plotTop, right, plotBottom);
        var layout = new PlotLayout(area, model.X, model.Y);
        using (var pb = Draw.Fill(theme.PlotBackground))
        {
            canvas.DrawRect(area, pb);
        }

        var (xTicks, xStep) = model.X.Ticks(Math.Max(2, (int)(area.Width / (90 * scale))));
        using var grid = Draw.Stroke(theme.Grid, 1 * scale, antialias: false);
        using var axisPen = Draw.Stroke(theme.Axis, 1 * scale, antialias: false);

        canvas.Save();
        canvas.ClipRect(area);
        foreach (var band in model.Bands)
        {
            using var bp = Draw.Fill(band.Color);
            var r = band.Orientation == LineOrientation.Horizontal
                ? new SKRect(area.Left, Math.Min(layout.ToPixelY(band.From), layout.ToPixelY(band.To)), area.Right, Math.Max(layout.ToPixelY(band.From), layout.ToPixelY(band.To)))
                : new SKRect(Math.Min(layout.ToPixelX(band.From), layout.ToPixelX(band.To)), area.Top, Math.Max(layout.ToPixelX(band.From), layout.ToPixelX(band.To)), area.Bottom);
            canvas.DrawRect(r, bp);
        }

        if (model.X.ShowGrid)
        {
            foreach (var t in xTicks)
            {
                var px = layout.ToPixelX(t);
                canvas.DrawLine(px, area.Top, px, area.Bottom, grid);
            }
        }

        if (model.Y.ShowGrid)
        {
            foreach (var t in yTicks)
            {
                var py = layout.ToPixelY(t);
                canvas.DrawLine(area.Left, py, area.Right, py, grid);
            }
        }

        foreach (var line in model.Lines)
        {
            using var lp = Draw.Stroke(line.Color, line.Width * scale * (line.Emphasised ? 1.8f : 1f), dash: line.Dash?.Select(d => d * scale).ToArray());
            if (line.Orientation == LineOrientation.Horizontal)
            {
                var py = layout.ToPixelY(line.Value);
                canvas.DrawLine(area.Left, py, area.Right, py, lp);
                if (line.Label is not null)
                {
                    Draw.Text(canvas, line.Label, area.Right - 4 * scale, py - 4 * scale, tickFont, line.Color, SKTextAlign.Right);
                }
            }
            else
            {
                var px = layout.ToPixelX(line.Value);
                canvas.DrawLine(px, area.Top, px, area.Bottom, lp);
                if (line.Label is not null)
                {
                    Draw.Text(canvas, line.Label, px + 4 * scale, area.Top + 12 * scale, tickFont, line.Color);
                }
            }
        }

        foreach (var s in model.Series)
        {
            DrawSeries(canvas, layout, s, theme, scale);
        }

        foreach (var note in model.Notes)
        {
            using var nf = PlotFonts.Font(11 * scale, note.Bold ? PlotFonts.SemiBold : PlotFonts.Regular);
            var p = layout.ToPixel(note.X, note.Y);
            var align = note.Align;
            var w = Draw.Width(note.Text, nf);
            // Keep labels inside the plot: flip the alignment when the text would run off an edge.
            if (align == SKTextAlign.Right && p.X - w < layout.PlotArea.Left)
            {
                align = SKTextAlign.Left;
            }
            else if (align == SKTextAlign.Left && p.X + w > layout.PlotArea.Right)
            {
                align = SKTextAlign.Right;
            }

            Draw.Text(canvas, note.Text, p.X, p.Y, nf, note.Color, align);
        }

        if (model.Crosshair is { } ch)
        {
            using var cp = Draw.Stroke(theme.MutedText.WithAlpha(160), 1 * scale, dash: [4 * scale, 3 * scale]);
            var p = layout.ToPixel(ch.X, ch.Y);
            canvas.DrawLine(area.Left, p.Y, area.Right, p.Y, cp);
            canvas.DrawLine(p.X, area.Top, p.X, area.Bottom, cp);
        }

        canvas.Restore();
        canvas.DrawRect(area, axisPen);

        // Tick labels.
        var xLabelY = model.XAxisOnTop ? area.Top - 6 * scale : area.Bottom + 14 * scale;
        // Skip labels that would collide with the previous one (narrow plots).
        var lastRight = float.NegativeInfinity;
        foreach (var t in xTicks.OrderBy(t => layout.ToPixelX(t)))
        {
            var label = model.X.Label(t, xStep);
            var w = Draw.Width(label, tickFont);
            var x = layout.ToPixelX(t);
            if (x - w / 2 < lastRight + 4 * scale)
            {
                continue;
            }

            Draw.Text(canvas, label, x, xLabelY, tickFont, theme.MutedText, SKTextAlign.Center);
            lastRight = x + w / 2;
        }

        foreach (var t in yTicks)
        {
            Draw.Text(canvas, model.Y.Label(t, yStep), area.Left - 6 * scale, layout.ToPixelY(t) + 4 * scale, tickFont, theme.MutedText, SKTextAlign.Right);
        }

        if (!string.IsNullOrEmpty(model.X.Title))
        {
            var ty = model.XAxisOnTop ? area.Top - 22 * scale : area.Bottom + 30 * scale;
            Draw.Text(canvas, model.X.Title, area.MidX, ty, axisFont, theme.Text, SKTextAlign.Center);
        }

        if (!string.IsNullOrEmpty(model.Y.Title))
        {
            canvas.Save();
            canvas.RotateDegrees(-90, bounds.Left + 14 * scale, area.MidY);
            Draw.Text(canvas, model.Y.Title, bounds.Left + 14 * scale, area.MidY + 4 * scale, axisFont, theme.Text, SKTextAlign.Center);
            canvas.Restore();
        }

        if (model.ShowLegend)
        {
            DrawLegend(canvas, area, model, theme, scale);
        }

        if (model.EmptyMessage is { } empty && model.Series.All(s => s.Points.Count == 0))
        {
            Draw.Text(canvas, empty, area.MidX, area.MidY, axisFont, theme.MutedText, SKTextAlign.Center);
        }

        return layout;
    }

    private static void DrawSeries(SKCanvas canvas, PlotLayout layout, PlotSeries s, PlotTheme theme, float scale)
    {
        if (s.Points.Count == 0)
        {
            return;
        }

        using var pen = Draw.Stroke(s.Color, s.LineWidth * scale, dash: s.Dash?.Select(d => d * scale).ToArray());
        switch (s.Kind)
        {
            case SeriesKind.Line or SeriesKind.LineAndMarkers:
            {
                using var path = new SKPath();
                var started = false;
                foreach (var p in s.Points)
                {
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                    {
                        started = false;
                        continue;
                    }

                    var px = layout.ToPixel(p.X, p.Y);
                    if (!started)
                    {
                        path.MoveTo(px);
                        started = true;
                    }
                    else
                    {
                        path.LineTo(px);
                    }
                }

                canvas.DrawPath(path, pen);
                if (s.Kind == SeriesKind.LineAndMarkers)
                {
                    DrawMarkers(canvas, layout, s, theme, scale);
                }

                break;
            }

            case SeriesKind.Area:
            {
                using var path = new SKPath();
                var baseY = layout.ToPixelY(Math.Max(0, layout.ToDataY(layout.PlotArea.Bottom)));
                path.MoveTo(layout.ToPixelX(s.Points[0].X), baseY);
                foreach (var p in s.Points)
                {
                    path.LineTo(layout.ToPixel(p.X, p.Y));
                }

                path.LineTo(layout.ToPixelX(s.Points[^1].X), baseY);
                path.Close();
                using var fill = Draw.Fill(s.Color.WithAlpha(60));
                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, pen);
                break;
            }

            case SeriesKind.StepProfile:
            {
                // Points come in pairs: (value, top) and (value, bottom) for each layer.
                using var path = new SKPath();
                for (var i = 0; i + 1 < s.Points.Count; i += 2)
                {
                    var a = s.Points[i];
                    var b = s.Points[i + 1];
                    if (!double.IsFinite(a.X))
                    {
                        continue;
                    }

                    var pa = layout.ToPixel(a.X, a.Y);
                    var pbb = layout.ToPixel(b.X, b.Y);
                    if (i == 0 || !double.IsFinite(s.Points[i - 1].X))
                    {
                        path.MoveTo(pa);
                    }
                    else
                    {
                        path.LineTo(pa);
                    }

                    path.LineTo(pbb);
                }

                canvas.DrawPath(path, pen);
                break;
            }

            default:
                DrawMarkers(canvas, layout, s, theme, scale);
                break;
        }
    }

    private static void DrawMarkers(SKCanvas canvas, PlotLayout layout, PlotSeries s, PlotTheme theme, float scale)
    {
        foreach (var p in s.Points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
            {
                continue;
            }

            var c = layout.ToPixel(p.X, p.Y);
            var color = p.Color ?? s.Color;
            var size = s.MarkerSize * scale * (p.Selected ? 1.6f : 1f);
            Marker(canvas, c, size, p.Shape ?? s.Marker, color, p.Hollow, theme.PlotBackground, scale);
            if (p.Selected)
            {
                using var ring = Draw.Stroke(theme.Selection, 2 * scale);
                canvas.DrawCircle(c, size + 3 * scale, ring);
            }

            if (p.Label is not null)
            {
                using var f = PlotFonts.Font(10 * scale);
                Draw.Text(canvas, p.Label, c.X + size + 3 * scale, c.Y + 3.5f * scale, f, theme.MutedText);
            }
        }
    }

    public static void Marker(SKCanvas canvas, SKPoint c, float size, MarkerShape shape, SKColor color, bool hollow, SKColor background, float scale = 1f)
    {
        using var fill = Draw.Fill(hollow ? background : color);
        using var edge = Draw.Stroke(color, 1.4f * scale);
        using var path = new SKPath();
        switch (shape)
        {
            case MarkerShape.Square:
                path.AddRect(new SKRect(c.X - size * 0.85f, c.Y - size * 0.85f, c.X + size * 0.85f, c.Y + size * 0.85f));
                break;
            case MarkerShape.Triangle:
                path.MoveTo(c.X, c.Y - size);
                path.LineTo(c.X + size, c.Y + size * 0.8f);
                path.LineTo(c.X - size, c.Y + size * 0.8f);
                path.Close();
                break;
            case MarkerShape.Diamond:
                path.MoveTo(c.X, c.Y - size);
                path.LineTo(c.X + size, c.Y);
                path.LineTo(c.X, c.Y + size);
                path.LineTo(c.X - size, c.Y);
                path.Close();
                break;
            case MarkerShape.Cross:
                using (var x = Draw.Stroke(color, 2 * scale))
                {
                    canvas.DrawLine(c.X - size, c.Y - size, c.X + size, c.Y + size, x);
                    canvas.DrawLine(c.X - size, c.Y + size, c.X + size, c.Y - size, x);
                }

                return;
            default:
                path.AddCircle(c.X, c.Y, size);
                break;
        }

        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, edge);
    }

    private static void DrawLegend(SKCanvas canvas, SKRect area, XyPlotModel model, PlotTheme theme, float scale)
    {
        var items = model.Series.Where(s => s.ShowInLegend && !string.IsNullOrEmpty(s.Title)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        using var font = PlotFonts.Font(11 * scale);
        var rowH = 16 * scale;
        var w = items.Max(s => Draw.Width(s.Title, font)) + 34 * scale;
        var h = items.Count * rowH + 8 * scale;
        var box = new SKRect(area.Right - w - 8 * scale, area.Top + 8 * scale, area.Right - 8 * scale, area.Top + 8 * scale + h);
        using (var bg = Draw.Fill(theme.Background.WithAlpha(theme.Background.Alpha == 0 ? (byte)0 : (byte)225)))
        using (var border = Draw.Stroke(theme.Grid, 1 * scale))
        {
            canvas.DrawRoundRect(box, 4 * scale, 4 * scale, bg);
            canvas.DrawRoundRect(box, 4 * scale, 4 * scale, border);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i];
            var y = box.Top + 4 * scale + rowH * i + rowH / 2;
            if (s.Kind is SeriesKind.Scatter)
            {
                Marker(canvas, new SKPoint(box.Left + 14 * scale, y), 4 * scale, s.Marker, s.Color, false, theme.PlotBackground, scale);
            }
            else
            {
                using var p = Draw.Stroke(s.Color, 2 * scale, dash: s.Dash?.Select(d => d * scale).ToArray());
                canvas.DrawLine(box.Left + 6 * scale, y, box.Left + 22 * scale, y, p);
            }

            Draw.Text(canvas, s.Title, box.Left + 28 * scale, y + 4 * scale, font, theme.Text);
        }
    }

    /// <summary>Nearest hit-testable point within <paramref name="radius"/> pixels.</summary>
    public static PlotHit? HitTest(XyPlotModel model, PlotLayout layout, SKPoint pixel, float radius = 8)
    {
        PlotHit? best = null;
        foreach (var s in model.Series.Where(s => s.HitTestable && s.Kind is SeriesKind.Scatter or SeriesKind.LineAndMarkers))
        {
            for (var i = 0; i < s.Points.Count; i++)
            {
                var p = s.Points[i];
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                {
                    continue;
                }

                var d = SKPoint.Distance(layout.ToPixel(p.X, p.Y), pixel);
                if (d <= radius && (best is null || d < best.Distance))
                {
                    best = new PlotHit(s, i, p, d);
                }
            }
        }

        return best;
    }

    /// <summary>Draggable reference line within <paramref name="radius"/> pixels.</summary>
    public static ReferenceLine? HitLine(XyPlotModel model, PlotLayout layout, SKPoint pixel, float radius = 6)
    {
        return model.Lines.Where(l => l.DragId is not null).Select(l => (Line: l, D: l.Orientation == LineOrientation.Horizontal
                ? Math.Abs(layout.ToPixelY(l.Value) - pixel.Y)
                : Math.Abs(layout.ToPixelX(l.Value) - pixel.X)))
            .Where(x => x.D <= radius && layout.PlotArea.Contains(pixel)).OrderBy(x => x.D).Select(x => x.Line).FirstOrDefault();
    }
}
