using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Analysis.SWave;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.SignalProcessing;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.Reporting.Charts;

public enum ProfileKind
{
    Interval,
    Layer,
    Average,
}

public enum EngineeringProperty
{
    Vp,
    Vs,
    VpVs,
    Poisson,
    Density,
    Shear,
    Young,
    Bulk,
}

/// <summary>Builds chart models from analysis results. Used identically by the application and the report.</summary>
public static class ChartFactory
{
    private static string L(UnitPreferences u) => u.Symbol(Quantity.Length);

    /// <summary>Travel time (ms) versus depth for P and/or S with layer boundaries and regression lines.</summary>
    public static XyPlotModel TravelTime(AnalysisResult r, UnitPreferences units, PlotTheme theme, bool showP = true, bool showS = true,
        bool vertical = true, Guid? selected = null, QcReport? qc = null)
    {
        var m = new XyPlotModel
        {
            Title = "Travel time",
            Subtitle = vertical ? "Vertical-equivalent (corrected) travel time t·z/R" : "Travel time after trigger correction",
            XAxisOnTop = true, EmptyMessage = "No picks yet",
        };
        m.X.Title = "Time (ms)";
        m.Y.Title = $"Depth ({L(units)})";
        m.Y.Inverted = true;
        m.Y.Format = v => Draw.Number(units.ToDisplay(v, Quantity.Length), Axis.NiceStep(Math.Max(1e-9, units.ToDisplay(m.Y.Span, Quantity.Length)) / 8));
        var times = new List<double>();
        var depths = new List<double> { 0 };
        foreach (var (w, show, color) in new[] { (r.P, showP, theme.P), (r.S, showS, theme.S) })
        {
            if (!show)
            {
                continue;
            }

            var points = new List<PlotPoint>();
            foreach (var o in w.Observations.Where(o => o.Pick?.Time is not null))
            {
                var t = vertical ? o.VerticalTime : o.Time;
                if (t is null)
                {
                    t = o.RawTime;
                }

                var severity = qc?.WorstFor(o.StationId, w.Wave);
                var flagged = severity == Severity.Error ? theme.Problem : severity == Severity.Warning ? theme.Warning : (SKColor?)null;
                points.Add(new PlotPoint(t!.Value * 1000, o.Depth)
                {
                    Hollow = !o.Included || o.Pick?.IsManual == false,
                    Shape = !o.Included ? MarkerShape.Cross : severity == Severity.Error ? MarkerShape.Square : severity == Severity.Warning ? MarkerShape.Triangle : null,
                    Color = !o.Included ? theme.Excluded : flagged, Selected = selected == o.StationId, Tag = (o.StationId, w.Wave),
                });
                times.Add(t.Value * 1000);
                depths.Add(o.Depth);
            }

            m.Series.Add(new PlotSeries
            {
                Title = w.Wave == WaveType.P ? "P arrivals" : "S arrivals", Kind = SeriesKind.Scatter, Color = color, Points = points,
                Marker = w.Wave == WaveType.P ? MarkerShape.Circle : MarkerShape.Diamond, MarkerSize = 4.5f,
            });

            // Model lines: regression per layer (vertical time) or the ray model.
            if (vertical)
            {
                foreach (var l in w.Layers.Where(l => l.Fit is { } f && l.Method == VelocityMethod.LayerRegression && double.IsFinite(f.Slope)))
                {
                    var f = l.Fit!;
                    var a = l.Layer.Top;
                    var b = l.Layer.Bottom;
                    m.Series.Add(new PlotSeries
                    {
                        Kind = SeriesKind.Line, Color = color.WithAlpha(200), LineWidth = 1.4f, ShowInLegend = false, HitTestable = false,
                        Points = [new PlotPoint((f.Intercept + f.Slope * a) * 1000, a), new PlotPoint((f.Intercept + f.Slope * b) * 1000, b)],
                    });
                }
            }
        }

        foreach (var layer in r.Layers.Skip(1))
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, layer.Top, theme.Layer) { Dash = [6, 4], DragId = layer.Id, Label = $"{units.Format(layer.Top, Quantity.Length)}" });
        }

        m.X.Fit(times, 0.05, includeZero: true);
        m.Y.Fit(depths.Append(r.Layers.LastOrDefault()?.Bottom ?? 0), 0.03, includeZero: true);
        m.Y.Min = 0;
        return m;
    }

    /// <summary>Residuals (ms) versus depth.</summary>
    public static XyPlotModel Residuals(AnalysisResult r, UnitPreferences units, PlotTheme theme)
    {
        var m = new XyPlotModel { Title = "Residuals", Subtitle = "Observed − model", XAxisOnTop = true, EmptyMessage = "No model yet" };
        m.X.Title = "Residual (ms)";
        m.Y.Title = $"Depth ({L(units)})";
        m.Y.Inverted = true;
        m.Y.Format = v => Draw.Number(units.ToDisplay(v, Quantity.Length), 1);
        var xs = new List<double> { -0.5, 0.5 };
        var ys = new List<double> { 0 };
        foreach (var (w, color) in new[] { (r.P, theme.P), (r.S, theme.S) })
        {
            var pts = w.Residuals.Select(kv => (Obs: w.Observations.FirstOrDefault(o => o.StationId == kv.Key), Res: kv.Value))
                .Where(x => x.Obs is not null).Select(x => new PlotPoint(x.Res * 1000, x.Obs!.Depth) { Tag = (x.Obs.StationId, w.Wave) }).ToList();
            xs.AddRange(pts.Select(p => p.X));
            ys.AddRange(pts.Select(p => p.Y));
            m.Series.Add(new PlotSeries { Title = w.Wave == WaveType.P ? "P" : "S", Kind = SeriesKind.LineAndMarkers, Color = color, Points = pts.OrderBy(p => p.Y).ToList(), LineWidth = 1, MarkerSize = 3.5f });
        }

        m.Lines.Add(new ReferenceLine(LineOrientation.Vertical, 0, theme.Axis));
        var lim = xs.Max(Math.Abs) * 1.15;
        m.X.Min = -lim;
        m.X.Max = lim;
        m.Y.Fit(ys, 0.03, includeZero: true);
        m.Y.Min = 0;
        return m;
    }

    /// <summary>Vp and Vs versus depth: layer steps, interval steps or average velocity.</summary>
    public static XyPlotModel VelocityProfile(AnalysisResult r, UnitPreferences units, PlotTheme theme, ProfileKind kind, bool showP = true, bool showS = true, bool labels = true)
    {
        var m = new XyPlotModel
        {
            Title = "Velocity profile",
            Subtitle = kind switch { ProfileKind.Layer => $"Layer velocities — {r.MethodDescription}", ProfileKind.Interval => "Pseudo-interval velocities", _ => "Average velocity R/t" },
            XAxisOnTop = true, EmptyMessage = "No velocities yet",
        };
        m.X.Title = $"Velocity ({units.Symbol(Quantity.Velocity)})";
        m.X.Format = v => Draw.Number(units.ToDisplay(v, Quantity.Velocity), Axis.NiceStep(units.ToDisplay(m.X.Span, Quantity.Velocity) / 5));
        m.Y.Title = $"Depth ({L(units)})";
        m.Y.Inverted = true;
        m.Y.Format = v => Draw.Number(units.ToDisplay(v, Quantity.Length), Axis.NiceStep(Math.Max(1e-9, units.ToDisplay(m.Y.Span, Quantity.Length)) / 8));
        var values = new List<double>();
        var depths = new List<double> { 0 };
        foreach (var (w, show, color) in new[] { (r.P, showP, theme.P), (r.S, showS, theme.S) })
        {
            if (!show)
            {
                continue;
            }

            var name = w.Wave == WaveType.P ? "Vp" : "Vs";
            var pts = new List<PlotPoint>();
            switch (kind)
            {
                case ProfileKind.Layer:
                    foreach (var l in w.Layers)
                    {
                        var v = l.Velocity ?? double.NaN;
                        pts.Add(new PlotPoint(v, l.Layer.Top));
                        pts.Add(new PlotPoint(v, l.Layer.Bottom));
                        if (double.IsFinite(v))
                        {
                            values.Add(v);
                            if (labels)
                            {
                                // Vs labels sit left of the Vs line and Vp labels right of the Vp line, so they never collide.
                                m.Notes.Add(w.Wave == WaveType.S
                                    ? new TextNote(v, l.Layer.Mid, units.Format(v, Quantity.Velocity) + " ", color) { Bold = true, Align = SKTextAlign.Right }
                                    : new TextNote(v, l.Layer.Mid, " " + units.Format(v, Quantity.Velocity), color) { Bold = true });
                            }
                        }

                        depths.Add(l.Layer.Bottom);
                    }

                    m.Series.Add(new PlotSeries { Title = name, Kind = SeriesKind.StepProfile, Color = color, LineWidth = 2.2f, Points = pts });
                    break;
                case ProfileKind.Interval:
                    foreach (var iv in w.PseudoIntervals)
                    {
                        var v = iv.Velocity ?? double.NaN;
                        pts.Add(new PlotPoint(v, iv.Top));
                        pts.Add(new PlotPoint(v, iv.Bottom));
                        if (double.IsFinite(v))
                        {
                            values.Add(v);
                        }

                        depths.Add(iv.Bottom);
                    }

                    m.Series.Add(new PlotSeries { Title = $"{name} (interval)", Kind = SeriesKind.StepProfile, Color = color, LineWidth = 1.6f, Points = pts });
                    break;
                default:
                    foreach (var o in w.Observations.Where(o => w.AverageVelocity.ContainsKey(o.StationId)))
                    {
                        var v = w.AverageVelocity[o.StationId];
                        pts.Add(new PlotPoint(v, o.Depth) { Tag = (o.StationId, w.Wave) });
                        values.Add(v);
                        depths.Add(o.Depth);
                    }

                    m.Series.Add(new PlotSeries { Title = $"{name} (average)", Kind = SeriesKind.LineAndMarkers, Color = color, Points = pts, MarkerSize = 3.5f });
                    break;
            }
        }

        foreach (var layer in r.Layers.Skip(1))
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, layer.Top, theme.Layer) { Dash = [6, 4], DragId = layer.Id });
        }

        if (r.Vs30.Value is not null && showS)
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, 30, theme.MutedText) { Dash = [2, 3], Label = r.Vs30.Extrapolated ? "30 m (Vs30 EXTRAPOLATED)" : "30 m" });
        }

        m.X.Fit(values, 0.1, includeZero: true);
        m.X.Min = 0;
        m.Y.Fit(depths, 0.02, includeZero: true);
        m.Y.Min = 0;
        return m;
    }

    /// <summary>One engineering property versus depth as a layer step profile.</summary>
    public static XyPlotModel Property(AnalysisResult r, EngineeringProperty property, UnitPreferences units, PlotTheme theme, double? crosshairDepth = null)
    {
        var (title, quantity, color, get) = Describe(property, theme);
        var m = new XyPlotModel { XAxisOnTop = true, ShowLegend = false, EmptyMessage = "Not available" };
        var unit = quantity is { } q ? units.Symbol(q) : "";
        m.X.Title = unit.Length > 0 ? $"{title} ({unit})" : title;
        if (quantity is { } qq)
        {
            m.X.Format = v => Draw.Number(units.ToDisplay(v, qq), Axis.NiceStep(Math.Max(1e-12, units.ToDisplay(m.X.Span, qq)) / 4));
        }

        m.Y.Title = $"Depth ({L(units)})";
        m.Y.Inverted = true;
        m.Y.Format = v => Draw.Number(units.ToDisplay(v, Quantity.Length), Axis.NiceStep(Math.Max(1e-9, units.ToDisplay(m.Y.Span, Quantity.Length)) / 8));
        var pts = new List<PlotPoint>();
        var vals = new List<double>();
        foreach (var l in r.LayerResults)
        {
            var v = get(l) ?? double.NaN;
            var estimated = l.Density.Estimated && property is EngineeringProperty.Density or EngineeringProperty.Shear or EngineeringProperty.Young or EngineeringProperty.Bulk;
            pts.Add(new PlotPoint(v, l.Layer.Top));
            pts.Add(new PlotPoint(v, l.Layer.Bottom));
            if (double.IsFinite(v))
            {
                vals.Add(v);
                if (estimated)
                {
                    m.Notes.Add(new TextNote(v, l.Layer.Mid, " est.", theme.MutedText));
                }
            }
        }

        m.Series.Add(new PlotSeries { Title = title, Kind = SeriesKind.StepProfile, Color = color, LineWidth = 2, Points = pts });
        foreach (var layer in r.Layers.Skip(1))
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, layer.Top, theme.Grid) { Dash = [4, 4] });
        }

        if (crosshairDepth is { } cd)
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, cd, theme.Accent) { Dash = [3, 3] });
        }

        m.X.Fit(vals, 0.15, includeZero: property is not EngineeringProperty.Poisson and not EngineeringProperty.VpVs);
        if (property == EngineeringProperty.Poisson)
        {
            m.X.Min = Math.Min(0, m.X.Min);
            m.X.Max = Math.Max(0.5, m.X.Max);
        }

        m.Y.Fit(r.Layers.Select(l => l.Bottom).Append(0), 0.02, includeZero: true);
        m.Y.Min = 0;
        return m;
    }

    public static (string Title, Quantity? Quantity, SKColor Color, Func<LayerResult, double?> Get) Describe(EngineeringProperty p, PlotTheme theme) => p switch
    {
        EngineeringProperty.Vp => ("Vp", Quantity.Velocity, theme.P, l => l.Properties.Vp),
        EngineeringProperty.Vs => ("Vs", Quantity.Velocity, theme.S, l => l.Properties.Vs),
        EngineeringProperty.VpVs => ("Vp/Vs", null, theme.Text, l => l.Properties.VpVsRatio),
        EngineeringProperty.Poisson => ("ν", null, SKColor.Parse("#0F766E"), l => l.Properties.PoissonRatio),
        EngineeringProperty.Density => ("ρ", Quantity.Density, SKColor.Parse("#6B4F2A"), l => l.Properties.Density),
        EngineeringProperty.Shear => ("G", Quantity.Modulus, SKColor.Parse("#7C3AED"), l => l.Properties.ShearModulus),
        EngineeringProperty.Young => ("E", Quantity.Modulus, SKColor.Parse("#B45309"), l => l.Properties.YoungModulus),
        _ => ("K", Quantity.Modulus, SKColor.Parse("#1D4ED8"), l => l.Properties.BulkModulus),
    };

    public static XyPlotModel Spectrum(AmplitudeSpectrum spectrum, PlotTheme theme, AmplitudeSpectrum? comparison = null, string title = "Amplitude spectrum", bool logFrequency = false)
    {
        var m = new XyPlotModel { Title = title, EmptyMessage = "No trace selected" };
        m.X.Title = "Frequency (Hz)";
        m.X.Logarithmic = logFrequency;
        m.Y.Title = "Amplitude (normalised)";
        var peak = spectrum.Amplitudes.Length == 0 ? 1 : Math.Max(1e-300, spectrum.Amplitudes.Max());
        if (comparison is not null)
        {
            var cpeak = Math.Max(1e-300, comparison.Amplitudes.DefaultIfEmpty(1).Max());
            m.Series.Add(new PlotSeries
            {
                Title = "Raw", Kind = SeriesKind.Line, Color = theme.Raw, LineWidth = 1.2f, HitTestable = false,
                Points = comparison.Frequencies.Zip(comparison.Amplitudes, (f, a) => new PlotPoint(f, a / cpeak)).Where(p => !logFrequency || p.X > 0).ToList(),
            });
        }

        m.Series.Add(new PlotSeries
        {
            Title = comparison is null ? "Spectrum" : "Processed", Kind = SeriesKind.Area, Color = theme.Accent, LineWidth = 1.4f,
            Points = spectrum.Frequencies.Zip(spectrum.Amplitudes, (f, a) => new PlotPoint(f, a / peak)).Where(p => !logFrequency || p.X > 0).ToList(),
        });
        if (spectrum.Frequencies.Length > 0)
        {
            var dom = spectrum.DominantFrequency(1);
            var (lo, hi) = spectrum.Bandwidth(0.70710678118654757, 1);
            m.Bands.Add(new ShadedBand(LineOrientation.Vertical, lo, hi, theme.Accent.WithAlpha(28)));
            m.Lines.Add(new ReferenceLine(LineOrientation.Vertical, dom, theme.Accent) { Label = $"{dom:0} Hz", Dash = [4, 3] });
            m.X.Min = logFrequency ? Math.Max(1, spectrum.Frequencies.Skip(1).FirstOrDefault()) : 0;
            m.X.Max = Math.Min(spectrum.Frequencies[^1], Math.Max(hi * 4, dom * 6));
        }

        m.Y.Min = 0;
        m.Y.Max = 1.05;
        return m;
    }

    public static XyPlotModel Correlation(CorrelationAnalysis c, PlotTheme theme)
    {
        var m = new XyPlotModel { Title = "Cross-correlation", Subtitle = $"Shift {c.TimeShift * 1000:+0.000;-0.000} ms, r = {c.Coefficient:0.000}", ShowLegend = false };
        m.X.Title = "Lag (ms)";
        m.Y.Title = "Correlation";
        m.Series.Add(new PlotSeries { Kind = SeriesKind.Line, Color = theme.Accent, Points = c.Correlation.Lags.Zip(c.Correlation.Values, (l, v) => new PlotPoint(l * 1000, v)).ToList() });
        m.Lines.Add(new ReferenceLine(LineOrientation.Vertical, c.TimeShift * 1000, theme.Text) { Dash = [4, 3] });
        m.Lines.Add(new ReferenceLine(LineOrientation.Horizontal, 0, theme.Axis));
        m.X.Fit(c.Correlation.Lags.Select(l => l * 1000), 0);
        m.Y.Min = -1.05;
        m.Y.Max = 1.05;
        return m;
    }

    public static XyPlotModel Hodogram(PolarizationResult p, PlotTheme theme)
    {
        var m = new XyPlotModel { Title = "Particle motion (hodogram)", Subtitle = $"Max energy at {p.AngleDegrees:0.0}° — {p.RadialEnergyFraction:P0} of horizontal energy", ShowLegend = false };
        m.X.Title = "X (horizontal)";
        m.Y.Title = "Y (horizontal)";
        var peak = Math.Max(1e-300, p.HodogramX.Concat(p.HodogramY).Select(Math.Abs).DefaultIfEmpty(1).Max());
        m.Series.Add(new PlotSeries { Kind = SeriesKind.Line, Color = theme.S, LineWidth = 1.2f, Points = p.HodogramX.Zip(p.HodogramY, (x, y) => new PlotPoint(x / peak, y / peak)).ToList() });
        var rad = p.AngleDegrees * Math.PI / 180;
        m.Series.Add(new PlotSeries
        {
            Kind = SeriesKind.Line, Color = theme.Accent, LineWidth = 2, Dash = [6, 3],
            Points = [new PlotPoint(-Math.Cos(rad), -Math.Sin(rad)), new PlotPoint(Math.Cos(rad), Math.Sin(rad))],
        });
        m.X.Min = m.Y.Min = -1.1;
        m.X.Max = m.Y.Max = 1.1;
        m.X.Format = m.Y.Format = v => v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
        return m;
    }

    public static XyPlotModel EnergyScan(PolarizationResult p, PlotTheme theme, double? applied)
    {
        var m = new XyPlotModel { Title = "Rotation energy", Subtitle = "Share of horizontal energy on the rotated component", ShowLegend = false };
        m.X.Title = "Rotation angle (°)";
        m.Y.Title = "Energy fraction";
        m.Series.Add(new PlotSeries { Kind = SeriesKind.Line, Color = theme.S, Points = p.ScanAngles.Zip(p.ScanEnergy, (a, e) => new PlotPoint(a, e)).ToList() });
        m.Lines.Add(new ReferenceLine(LineOrientation.Vertical, p.AngleDegrees, theme.Accent) { Label = $"best {p.AngleDegrees:0.0}°", Dash = [4, 3] });
        if (applied is { } a)
        {
            m.Lines.Add(new ReferenceLine(LineOrientation.Vertical, a, theme.Text) { Label = $"applied {a:0.0}°" });
        }

        m.X.Min = 0;
        m.X.Max = 180;
        m.Y.Min = 0;
        m.Y.Max = 1.02;
        return m;
    }

    public static XyPlotModel FilterResponse(SosFilter filter, double sampleRate, PlotTheme theme, bool zeroPhase, string title)
    {
        var m = new XyPlotModel { Title = title, Subtitle = zeroPhase ? "Zero-phase: |H|² (forward + backward)" : "Causal: |H|", ShowLegend = false };
        m.X.Title = "Frequency (Hz)";
        m.X.Logarithmic = true;
        m.Y.Title = "Gain";
        var pts = new List<PlotPoint>();
        var nyq = sampleRate / 2;
        for (var i = 0; i <= 400; i++)
        {
            var f = Math.Pow(10, Math.Log10(1) + i / 400.0 * (Math.Log10(nyq * 0.999) - Math.Log10(1)));
            var g = filter.Magnitude(f, sampleRate);
            pts.Add(new PlotPoint(f, zeroPhase ? g * g : g));
        }

        m.Series.Add(new PlotSeries { Kind = SeriesKind.Line, Color = theme.Accent, Points = pts });
        m.X.Min = 1;
        m.X.Max = nyq;
        m.Y.Min = 0;
        m.Y.Max = 1.1;
        return m;
    }
}
