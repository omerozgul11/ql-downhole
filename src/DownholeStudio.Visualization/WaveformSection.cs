using SkiaSharp;

namespace DownholeStudio.Visualization;

public enum WaveDisplayMode
{
    Wiggle,
    VariableArea,
    WiggleAndArea,
}

public enum PickMarkerKind
{
    P,
    S,
    Reference,
    /// <summary>Automatic suggestion not (yet) accepted.</summary>
    Suggestion,
    /// <summary>Time predicted from neighbouring picks.</summary>
    Predicted,
}

/// <summary>A pick drawn on a trace. Manual picks are solid, automatic picks dashed, suggestions hollow.</summary>
public sealed record PickMarker(double Time, PickMarkerKind Kind, SKColor Color)
{
    public bool Selected { get; init; }
    public bool Automatic { get; init; }
    public bool Excluded { get; init; }
    public string? Label { get; init; }
}

/// <summary>Quality status shown next to a trace (colour + glyph, never colour alone).</summary>
public enum TraceQc
{
    None,
    Good,
    Review,
    Problem,
}

public sealed class WaveTrace
{
    public required object Key { get; init; }
    public required double[] Samples { get; init; }
    public required double SampleInterval { get; init; }
    public double Delay { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Position along the trace axis (depth in metres when <see cref="WaveformSectionModel.SpaceByPosition"/>).</summary>
    public double Position { get; init; }
    public bool Selected { get; init; }
    public bool Excluded { get; init; }
    /// <summary>Per-trace display gain multiplier.</summary>
    public double Gain { get; init; } = 1;
    public SKColor? Color { get; init; }
    /// <summary>Optional second waveform drawn behind (e.g. raw data for comparison).</summary>
    public double[]? Overlay { get; init; }
    public SKColor? OverlayColor { get; init; }
    public IReadOnlyList<PickMarker> Picks { get; init; } = [];
    public TraceQc Qc { get; init; }

    public double TimeAt(int i) => Delay + i * SampleInterval;
}

public sealed class WaveformSectionModel
{
    public List<WaveTrace> Traces { get; } = [];
    public double TimeMin { get; set; }
    public double TimeMax { get; set; } = 0.1;
    public WaveDisplayMode Mode { get; set; } = WaveDisplayMode.WiggleAndArea;
    /// <summary>Each trace scaled to its own peak (true) or all traces to the common peak (false).</summary>
    public bool NormalizeEach { get; set; } = true;
    public double GlobalGain { get; set; } = 1;
    /// <summary>All traces drawn on one baseline (overlay) instead of stacked.</summary>
    public bool OverlayMode { get; set; }
    /// <summary>Space traces by their <see cref="WaveTrace.Position"/> (depth scale) instead of evenly.</summary>
    public bool SpaceByPosition { get; set; }
    public double? PositionMin { get; set; }
    public double? PositionMax { get; set; }
    /// <summary>First visible trace index and visible count when evenly spaced (vertical zoom/pan).</summary>
    public double TraceOffset { get; set; }
    public double? VisibleTraceCount { get; set; }
    public string TimeTitle { get; set; } = "Time (ms)";
    public string PositionTitle { get; set; } = "Depth (m)";
    public double TimeLabelScale { get; set; } = 1000;
    public double? CursorTime { get; set; }
    public string? EmptyMessage { get; set; }
    /// <summary>Clip deflections to the trace spacing (true for stacked displays).</summary>
    public bool Clip { get; set; } = true;

    /// <summary>An independent copy for rendering on another thread; traces are immutable and shared.</summary>
    public WaveformSectionModel Snapshot()
    {
        var copy = new WaveformSectionModel
        {
            TimeMin = TimeMin, TimeMax = TimeMax, Mode = Mode, NormalizeEach = NormalizeEach, GlobalGain = GlobalGain, OverlayMode = OverlayMode,
            SpaceByPosition = SpaceByPosition, PositionMin = PositionMin, PositionMax = PositionMax, TraceOffset = TraceOffset, VisibleTraceCount = VisibleTraceCount,
            TimeTitle = TimeTitle, PositionTitle = PositionTitle, TimeLabelScale = TimeLabelScale, CursorTime = CursorTime, EmptyMessage = EmptyMessage, Clip = Clip,
        };
        copy.Traces.AddRange(Traces);
        return copy;
    }
}

public sealed class WaveLayout(SKRect area, WaveformSectionModel model, IReadOnlyList<float> baselines, float spacing)
{
    public SKRect PlotArea { get; } = area;
    public IReadOnlyList<float> Baselines { get; } = baselines;
    public float Spacing { get; } = spacing;

    public float ToPixelX(double t) => (float)(PlotArea.Left + (t - model.TimeMin) / (model.TimeMax - model.TimeMin) * PlotArea.Width);

    public double ToTime(float x) => model.TimeMin + (x - PlotArea.Left) / PlotArea.Width * (model.TimeMax - model.TimeMin);

    /// <summary>Index of the trace whose baseline is nearest to <paramref name="y"/>, or -1.</summary>
    public int TraceAt(float y)
    {
        var best = -1;
        var bestD = float.MaxValue;
        for (var i = 0; i < Baselines.Count; i++)
        {
            var d = Math.Abs(Baselines[i] - y);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best >= 0 && bestD <= Math.Max(Spacing * 0.6f, 6) ? best : -1;
    }
}

/// <summary>
/// Seismic trace section renderer. Time runs left to right; traces are stacked top to bottom.
/// For long traces each pixel column is drawn from the minimum and maximum of the samples it
/// covers, so drawing cost depends on the plot width, not on the number of samples.
/// </summary>
public static class WaveformRenderer
{
    public static WaveLayout Render(SKCanvas canvas, SKRect bounds, WaveformSectionModel model, PlotTheme theme, float scale = 1f)
    {
        using var bg = Draw.Fill(theme.Background);
        canvas.DrawRect(bounds, bg);
        using var tickFont = PlotFonts.Font(11 * scale);
        using var labelFont = PlotFonts.Font(10.5f * scale, PlotFonts.Medium);
        using var axisFont = PlotFonts.Font(11.5f * scale, PlotFonts.Medium);

        var labelWidth = model.Traces.Count == 0 ? 40 * scale : Math.Max(40 * scale, model.Traces.Max(t => Draw.Width(t.Label, labelFont)) + 26 * scale);
        var area = new SKRect(bounds.Left + labelWidth + 8 * scale, bounds.Top + 10 * scale, bounds.Right - 12 * scale, bounds.Bottom - 40 * scale);
        if (area.Width < 20 || area.Height < 20 || model.TimeMax <= model.TimeMin)
        {
            return new WaveLayout(area, model, [], 0);
        }

        // Baselines.
        var n = model.Traces.Count;
        var baselines = new float[n];
        float spacing;
        if (model.OverlayMode)
        {
            spacing = area.Height * 0.9f;
            for (var i = 0; i < n; i++)
            {
                baselines[i] = area.MidY;
            }
        }
        else if (model.SpaceByPosition && n > 0)
        {
            var pmin = model.PositionMin ?? model.Traces.Min(t => t.Position);
            var pmax = model.PositionMax ?? model.Traces.Max(t => t.Position);
            if (pmax <= pmin)
            {
                pmax = pmin + 1;
            }

            var pad = area.Height * 0.04f;
            for (var i = 0; i < n; i++)
            {
                baselines[i] = (float)(area.Top + pad + (model.Traces[i].Position - pmin) / (pmax - pmin) * (area.Height - 2 * pad));
            }

            var gaps = baselines.Order().Zip(baselines.Order().Skip(1), (a, b) => b - a).Where(g => g > 0.5f).ToList();
            spacing = gaps.Count > 0 ? gaps.Min() : area.Height / 2;
        }
        else
        {
            var visible = Math.Max(1, model.VisibleTraceCount ?? n);
            spacing = (float)(area.Height / visible);
            for (var i = 0; i < n; i++)
            {
                baselines[i] = (float)(area.Top + (i - model.TraceOffset + 0.5) * spacing);
            }
        }

        var layout = new WaveLayout(area, model, baselines, spacing);
        using var gridPen = Draw.Stroke(theme.Grid, 1 * scale, antialias: false);
        using var axisPen = Draw.Stroke(theme.Axis, 1 * scale, antialias: false);

        // Time grid.
        var timeAxis = new Axis { Min = model.TimeMin * model.TimeLabelScale, Max = model.TimeMax * model.TimeLabelScale };
        var (ticks, step) = timeAxis.Ticks(Math.Max(2, (int)(area.Width / (90 * scale))));
        foreach (var t in ticks)
        {
            var x = layout.ToPixelX(t / model.TimeLabelScale);
            canvas.DrawLine(x, area.Top, x, area.Bottom, gridPen);
            Draw.Text(canvas, Draw.Number(t, step), x, area.Bottom + 15 * scale, tickFont, theme.MutedText, SKTextAlign.Center);
        }

        Draw.Text(canvas, model.TimeTitle, area.MidX, area.Bottom + 32 * scale, axisFont, theme.Text, SKTextAlign.Center);

        // Global peak for common normalisation, within the visible window.
        double globalPeak = 0;
        if (!model.NormalizeEach)
        {
            foreach (var tr in model.Traces)
            {
                globalPeak = Math.Max(globalPeak, VisiblePeak(tr, model));
            }
        }

        // The selected trace is always labelled; regular labels that would overlap it are skipped.
        var selectedIndex = -1;
        for (var i = 0; i < n; i++)
        {
            if (model.Traces[i].Selected)
            {
                selectedIndex = i;
                break;
            }
        }

        for (var i = 0; i < n; i++)
        {
            var tr = model.Traces[i];
            var y0 = baselines[i];
            if (y0 < area.Top - spacing || y0 > area.Bottom + spacing)
            {
                continue;
            }

            if (tr.Selected && !model.OverlayMode)
            {
                using var sel = Draw.Fill(theme.Selection.WithAlpha(28));
                canvas.DrawRect(new SKRect(bounds.Left, y0 - spacing / 2, area.Right, y0 + spacing / 2), sel);
            }

            var peak = model.NormalizeEach ? VisiblePeak(tr, model) : globalPeak;
            var amp = peak > 0 ? spacing * 0.48 * model.GlobalGain * tr.Gain / peak : 0;
            canvas.Save();
            var clip = model.Clip && !model.OverlayMode ? new SKRect(area.Left, y0 - spacing, area.Right, y0 + spacing) : area;
            clip.Intersect(area);
            canvas.ClipRect(clip);
            if (tr.Overlay is not null)
            {
                DrawTrace(canvas, layout, model, tr.Overlay, tr, y0, amp, (tr.OverlayColor ?? theme.Raw), WaveDisplayMode.Wiggle, scale, 1.1f);
            }

            var color = tr.Excluded ? theme.Excluded : tr.Color ?? theme.Wiggle;
            DrawTrace(canvas, layout, model, tr.Samples, tr, y0, amp, color, model.OverlayMode ? WaveDisplayMode.Wiggle : model.Mode, scale, tr.Selected ? 1.4f : 1.0f);
            canvas.Restore();

            // Picks.
            foreach (var p in tr.Picks)
            {
                var x = layout.ToPixelX(p.Time);
                if (x < area.Left || x > area.Right)
                {
                    continue;
                }

                var half = Math.Max(spacing * 0.45f, 7 * scale);
                var w = (p.Selected ? 3.2f : 2.2f) * scale;
                using var pp = Draw.Stroke(p.Excluded ? theme.Excluded : p.Color, w,
                    dash: p.Kind == PickMarkerKind.Suggestion || p.Automatic ? [4 * scale, 3 * scale] : p.Kind == PickMarkerKind.Predicted ? [1.5f * scale, 3 * scale] : null);
                canvas.DrawLine(x, y0 - half, x, y0 + half, pp);
                if (p.Kind != PickMarkerKind.Predicted)
                {
                    // Small cap triangle makes the pick visible even without colour.
                    using var cap = new SKPath();
                    cap.MoveTo(x - 4 * scale, y0 - half);
                    cap.LineTo(x + 4 * scale, y0 - half);
                    cap.LineTo(x, y0 - half + 5 * scale);
                    cap.Close();
                    using var cf = Draw.Fill(p.Kind == PickMarkerKind.Suggestion ? theme.Background : p.Color);
                    canvas.DrawPath(cap, cf);
                    canvas.DrawPath(cap, pp);
                }

                if (p.Label is not null)
                {
                    Draw.Text(canvas, p.Label, x + 4 * scale, y0 - half + 10 * scale, tickFont, p.Color);
                }
            }

            // Trace label with QC glyph; labels are thinned when traces are closer than the text height,
            // but the selected trace and traces with problems are always labelled.
            if (!model.OverlayMode && y0 >= area.Top && y0 <= area.Bottom)
            {
                var every = Math.Max(1, (int)Math.Ceiling(13f * scale / Math.Max(1f, spacing)));
                var crowdsSelected = selectedIndex >= 0 && i != selectedIndex && Math.Abs(i - selectedIndex) < every;
                if ((i % every == 0 && !crowdsSelected) || tr.Selected)
                {
                    Draw.Text(canvas, tr.Label, area.Left - 22 * scale, y0 + 4 * scale, labelFont, tr.Excluded ? theme.Excluded : theme.Text, SKTextAlign.Right);
                }

                if (spacing >= 7 * scale || tr.Qc is TraceQc.Problem or TraceQc.Review || tr.Selected)
                {
                    QcGlyph(canvas, new SKPoint(area.Left - 12 * scale, y0), tr.Qc, theme, Math.Min(scale, Math.Max(0.6f * scale, spacing / 11f)));
                }
            }
        }

        canvas.DrawRect(area, axisPen);
        if (model.CursorTime is { } ct && ct >= model.TimeMin && ct <= model.TimeMax)
        {
            using var cp = Draw.Stroke(theme.Accent, 1 * scale, dash: [4 * scale, 3 * scale]);
            var x = layout.ToPixelX(ct);
            canvas.DrawLine(x, area.Top, x, area.Bottom, cp);
            Draw.Text(canvas, Draw.Number(ct * model.TimeLabelScale, 0.01), x + 4 * scale, area.Top + 12 * scale, tickFont, theme.Accent);
        }

        if (n == 0 && model.EmptyMessage is { } msg)
        {
            Draw.Text(canvas, msg, area.MidX, area.MidY, axisFont, theme.MutedText, SKTextAlign.Center);
        }

        return layout;
    }

    /// <summary>A small status glyph: ✓ circle (good), ! triangle (review), × square (problem).</summary>
    public static void QcGlyph(SKCanvas canvas, SKPoint c, TraceQc qc, PlotTheme theme, float scale)
    {
        if (qc == TraceQc.None)
        {
            return;
        }

        var r = 4.5f * scale;
        var (color, shape) = qc switch
        {
            TraceQc.Good => (theme.Good, MarkerShape.Circle),
            TraceQc.Review => (theme.Warning, MarkerShape.Triangle),
            _ => (theme.Problem, MarkerShape.Square),
        };
        XyPlotRenderer.Marker(canvas, c, r, shape, color, false, theme.Background, scale);
        using var mark = Draw.Stroke(SKColors.White, 1.3f * scale);
        switch (qc)
        {
            case TraceQc.Good:
                canvas.DrawLine(c.X - r * 0.45f, c.Y, c.X - r * 0.1f, c.Y + r * 0.4f, mark);
                canvas.DrawLine(c.X - r * 0.1f, c.Y + r * 0.4f, c.X + r * 0.5f, c.Y - r * 0.4f, mark);
                break;
            case TraceQc.Review:
                canvas.DrawLine(c.X, c.Y - r * 0.35f, c.X, c.Y + r * 0.2f, mark);
                break;
            default:
                canvas.DrawLine(c.X - r * 0.4f, c.Y - r * 0.4f, c.X + r * 0.4f, c.Y + r * 0.4f, mark);
                canvas.DrawLine(c.X - r * 0.4f, c.Y + r * 0.4f, c.X + r * 0.4f, c.Y - r * 0.4f, mark);
                break;
        }
    }

    private static double VisiblePeak(WaveTrace tr, WaveformSectionModel model)
    {
        var a = Math.Clamp((int)Math.Floor((model.TimeMin - tr.Delay) / tr.SampleInterval), 0, tr.Samples.Length);
        var b = Math.Clamp((int)Math.Ceiling((model.TimeMax - tr.Delay) / tr.SampleInterval) + 1, a, tr.Samples.Length);
        double peak = 0;
        for (var i = a; i < b; i++)
        {
            peak = Math.Max(peak, Math.Abs(tr.Samples[i]));
        }

        return peak;
    }

    private static void DrawTrace(SKCanvas canvas, WaveLayout layout, WaveformSectionModel model, double[] samples, WaveTrace tr, float y0, double amp,
        SKColor color, WaveDisplayMode mode, float scale, float widthFactor)
    {
        if (samples.Length == 0 || amp == 0)
        {
            using var flat = Draw.Stroke(color, 1 * scale);
            canvas.DrawLine(layout.PlotArea.Left, y0, layout.PlotArea.Right, y0, flat);
            return;
        }

        var area = layout.PlotArea;
        var dt = tr.SampleInterval;
        var first = Math.Clamp((int)Math.Floor((model.TimeMin - tr.Delay) / dt) - 1, 0, samples.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((model.TimeMax - tr.Delay) / dt) + 1, 0, samples.Length - 1);
        var count = last - first + 1;
        var samplesPerPixel = count / Math.Max(1.0, area.Width);
        using var pen = Draw.Stroke(color, 1.1f * scale * widthFactor);
        using var line = new SKPath();
        using var fillPath = new SKPath();
        var fill = mode is WaveDisplayMode.VariableArea or WaveDisplayMode.WiggleAndArea;

        if (samplesPerPixel <= 1.5)
        {
            for (var i = first; i <= last; i++)
            {
                var x = layout.ToPixelX(tr.Delay + i * dt);
                var y = (float)(y0 - samples[i] * amp);
                if (i == first)
                {
                    line.MoveTo(x, y);
                }
                else
                {
                    line.LineTo(x, y);
                }
            }

            if (fill)
            {
                // Positive lobes, closed back along the baseline with exact zero crossings.
                var open = false;
                for (var i = first; i <= last; i++)
                {
                    var v = samples[i];
                    var x = layout.ToPixelX(tr.Delay + i * dt);
                    if (v > 0)
                    {
                        if (!open)
                        {
                            var x0 = i > first && samples[i - 1] <= 0 ? layout.ToPixelX(tr.Delay + (i - 1 + samples[i - 1] / (samples[i - 1] - v)) * dt) : x;
                            fillPath.MoveTo(x0, y0);
                            open = true;
                        }

                        fillPath.LineTo(x, (float)(y0 - v * amp));
                    }
                    else if (open)
                    {
                        var prev = samples[i - 1];
                        var xc = layout.ToPixelX(tr.Delay + (i - 1 + prev / (prev - v)) * dt);
                        fillPath.LineTo(xc, y0);
                        fillPath.Close();
                        open = false;
                    }
                }

                if (open)
                {
                    fillPath.LineTo(layout.ToPixelX(tr.Delay + last * dt), y0);
                    fillPath.Close();
                }
            }
        }
        else
        {
            // Min/max decimation per pixel column.
            var columns = (int)Math.Ceiling(area.Width);
            for (var c = 0; c < columns; c++)
            {
                var t0 = layout.ToTime(area.Left + c);
                var t1 = layout.ToTime(area.Left + c + 1);
                var a = Math.Clamp((int)Math.Floor((t0 - tr.Delay) / dt), 0, samples.Length - 1);
                var b = Math.Clamp((int)Math.Ceiling((t1 - tr.Delay) / dt), a, samples.Length - 1);
                double lo = double.MaxValue, hi = double.MinValue;
                for (var i = a; i <= b; i++)
                {
                    lo = Math.Min(lo, samples[i]);
                    hi = Math.Max(hi, samples[i]);
                }

                var x = area.Left + c + 0.5f;
                var yLo = (float)(y0 - lo * amp);
                var yHi = (float)(y0 - hi * amp);
                if (c == 0)
                {
                    line.MoveTo(x, yHi);
                }
                else
                {
                    line.LineTo(x, yHi);
                }

                line.LineTo(x, yLo);
                if (fill && hi > 0)
                {
                    fillPath.AddRect(new SKRect(x - 0.5f, yHi, x + 0.5f, y0));
                }
            }
        }

        if (fill)
        {
            using var fp = Draw.Fill(color.WithAlpha(Math.Min(color.Alpha, (byte)170)));
            canvas.DrawPath(fillPath, fp);
        }

        if (mode != WaveDisplayMode.VariableArea || samplesPerPixel > 1.5)
        {
            canvas.DrawPath(line, pen);
        }
        else
        {
            using var thin = Draw.Stroke(color.WithAlpha(120), 0.8f * scale);
            canvas.DrawPath(line, thin);
        }
    }
}
