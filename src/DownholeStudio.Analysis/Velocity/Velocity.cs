using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Velocity;

/// <summary>A layer with resolved top and bottom (metres below ground).</summary>
public sealed record ResolvedLayer(Guid Id, int Index, double Top, double Bottom, LayerDefinition Definition)
{
    public double Thickness => Bottom - Top;

    public double Mid => 0.5 * (Top + Bottom);

    /// <summary>Points on a boundary belong to both adjacent layers for line fitting.</summary>
    public bool ContainsForFit(double depth) => depth >= Top - 1e-9 && depth <= Bottom + 1e-9;

    /// <summary>Unique membership: (top, bottom], except the first layer which includes its top.</summary>
    public bool Owns(double depth) => (Index == 0 ? depth >= Top - 1e-9 : depth > Top + 1e-9) && depth <= Bottom + 1e-9;
}

/// <summary>Velocity between two successive receivers.</summary>
public sealed record IntervalVelocity(double Top, double Bottom, Guid UpperStation, Guid LowerStation, double? Velocity, string? Reason)
{
    public double Mid => 0.5 * (Top + Bottom);
}

/// <summary>Velocity of one interpreted layer for one wave type.</summary>
public sealed record LayerVelocity
{
    public required ResolvedLayer Layer { get; init; }
    public double? Velocity { get; init; }
    /// <summary>One-sigma velocity uncertainty from the regression slope error, when defined.</summary>
    public double? VelocityStandardError { get; init; }
    public LineFit? Fit { get; init; }
    public int PointCount { get; init; }
    public IReadOnlyList<Guid> Stations { get; init; } = [];
    public string? Reason { get; init; }
    public VelocityMethod Method { get; init; }
}

/// <summary>Velocity analysis for one wave type.</summary>
public sealed record WaveVelocityResult
{
    public required WaveType Wave { get; init; }
    public required VelocityMethod Method { get; init; }
    public IReadOnlyList<StationObservation> Observations { get; init; } = [];
    public IReadOnlyList<IntervalVelocity> PseudoIntervals { get; init; } = [];
    public IReadOnlyList<IntervalVelocity> TrueIntervals { get; init; } = [];
    /// <summary>Average (equivalent) velocity from the source to each receiver: R / t.</summary>
    public IReadOnlyDictionary<Guid, double> AverageVelocity { get; init; } = new Dictionary<Guid, double>();
    public IReadOnlyList<LayerVelocity> Layers { get; init; } = [];
    /// <summary>Observed minus modelled time per station (vertical time for line fits, travel time for refracted rays).</summary>
    public IReadOnlyDictionary<Guid, double> Residuals { get; init; } = new Dictionary<Guid, double>();
    /// <summary>Root-mean-square of the residuals of included observations, seconds.</summary>
    public double? RmsResidual { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public LayerVelocity? LayerAt(double depth) => Layers.FirstOrDefault(l => l.Layer.Owns(depth));
}

public interface IVelocityCalculator
{
    VelocityMethod Method { get; }

    /// <summary>Layer velocities and residuals for the interpreted layers.</summary>
    WaveVelocityResult Calculate(IReadOnlyList<StationObservation> observations, IReadOnlyList<ResolvedLayer> layers, SourceGeometry source, AnalysisSettings settings, WaveType wave);
}

/// <summary>Velocity computations shared by all methods.</summary>
public static class VelocityCommon
{
    /// <summary>Δz/Δt_v between successive included receivers (pseudo-interval velocity).</summary>
    public static IReadOnlyList<IntervalVelocity> PseudoIntervals(IReadOnlyList<StationObservation> obs)
    {
        var list = new List<IntervalVelocity>();
        var used = obs.Where(o => o.Included && o.VerticalTime is not null).OrderBy(o => o.Depth).ToList();
        for (var i = 0; i + 1 < used.Count; i++)
        {
            var a = used[i];
            var b = used[i + 1];
            var dz = b.Depth - a.Depth;
            var dt = b.VerticalTime!.Value - a.VerticalTime!.Value;
            list.Add(dz <= 0
                ? new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, null, "Two receivers share the same depth.")
                : dt <= 0
                    ? new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, null, "Corrected arrival time does not increase with depth; review the picks or geometry.")
                    : new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, dz / dt, null));
        }

        return list;
    }

    /// <summary>(R₂ − R₁)/(t₂ − t₁) between successive included receivers (straight slant rays).</summary>
    public static IReadOnlyList<IntervalVelocity> TrueIntervals(IReadOnlyList<StationObservation> obs)
    {
        var list = new List<IntervalVelocity>();
        var used = obs.Where(o => o.Included && o.Time is not null).OrderBy(o => o.Depth).ToList();
        for (var i = 0; i + 1 < used.Count; i++)
        {
            var a = used[i];
            var b = used[i + 1];
            var dr = b.SlantDistance - a.SlantDistance;
            var dt = b.Time!.Value - a.Time!.Value;
            list.Add(dr <= 0
                ? new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, null, "Slant distance does not increase between these receivers.")
                : dt <= 0
                    ? new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, null, "Arrival time does not increase with depth; review the picks or trigger correction.")
                    : new IntervalVelocity(a.Depth, b.Depth, a.StationId, b.StationId, StraightRay.IntervalVelocity(a.SlantDistance, a.Time.Value, b.SlantDistance, b.Time.Value), null));
        }

        return list;
    }

    public static IReadOnlyDictionary<Guid, double> Average(IReadOnlyList<StationObservation> obs) =>
        obs.Where(o => o.Included && o.Time is > 0).ToDictionary(o => o.StationId, o => o.SlantDistance / o.Time!.Value);

    /// <summary>Line fits of vertical time versus depth inside each layer (used directly by LayerRegression and for QC residuals).</summary>
    public static (List<LayerVelocity> Layers, Dictionary<Guid, double> Residuals) LineFits(IReadOnlyList<StationObservation> obs,
        IReadOnlyList<ResolvedLayer> layers, AnalysisSettings settings)
    {
        var used = obs.Where(o => o.Included && o.VerticalTime is not null).ToList();
        var results = new List<LayerVelocity>();
        var residuals = new Dictionary<Guid, double>();
        foreach (var layer in layers)
        {
            var pts = used.Where(o => layer.ContainsForFit(o.Depth)).OrderBy(o => o.Depth).ToList();
            if (pts.Count < 2)
            {
                results.Add(new LayerVelocity
                {
                    Layer = layer, PointCount = pts.Count, Stations = pts.Select(p => p.StationId).ToList(), Method = VelocityMethod.LayerRegression,
                    Reason = pts.Count == 0 ? "No usable measurements in this layer." : "Only one usable measurement in this layer; at least two are needed for a velocity.",
                });
                continue;
            }

            var fit = LinearRegression.Fit(pts.Select(p => p.Depth).ToArray(), pts.Select(p => p.VerticalTime!.Value).ToArray(), settings.Regression, settings.HuberThreshold);
            if (fit is null)
            {
                results.Add(new LayerVelocity { Layer = layer, PointCount = pts.Count, Method = VelocityMethod.LayerRegression, Reason = "The regression is undefined (all points at one depth)." });
                continue;
            }

            for (var i = 0; i < pts.Count; i++)
            {
                if (layer.Owns(pts[i].Depth))
                {
                    residuals[pts[i].StationId] = fit.Residuals[i];
                }
            }

            if (!(fit.Slope > 0))
            {
                results.Add(new LayerVelocity
                {
                    Layer = layer, Fit = fit, PointCount = pts.Count, Stations = pts.Select(p => p.StationId).ToList(), Method = VelocityMethod.LayerRegression,
                    Reason = "Corrected travel time decreases with depth in this layer, so no physical velocity can be fitted.",
                });
                continue;
            }

            var v = 1.0 / fit.Slope;
            double? se = double.IsFinite(fit.SlopeStandardError) ? fit.SlopeStandardError / (fit.Slope * fit.Slope) : null;
            results.Add(new LayerVelocity
            {
                Layer = layer, Velocity = v, VelocityStandardError = se, Fit = fit, PointCount = pts.Count,
                Stations = pts.Select(p => p.StationId).ToList(), Method = VelocityMethod.LayerRegression,
            });
        }

        return (results, residuals);
    }

    public static double? Rms(IEnumerable<double> values)
    {
        var a = values.ToArray();
        return a.Length == 0 ? null : Math.Sqrt(a.Sum(v => v * v) / a.Length);
    }
}

/// <summary>Straight lines fitted to vertical-equivalent travel time versus depth in each layer; V = 1/slope.</summary>
public sealed class LayerRegressionCalculator : IVelocityCalculator
{
    public VelocityMethod Method => VelocityMethod.LayerRegression;

    public WaveVelocityResult Calculate(IReadOnlyList<StationObservation> observations, IReadOnlyList<ResolvedLayer> layers, SourceGeometry source, AnalysisSettings settings, WaveType wave)
    {
        var (fits, residuals) = VelocityCommon.LineFits(observations, layers, settings);
        return new WaveVelocityResult
        {
            Wave = wave, Method = Method, Observations = observations, PseudoIntervals = VelocityCommon.PseudoIntervals(observations),
            TrueIntervals = VelocityCommon.TrueIntervals(observations), AverageVelocity = VelocityCommon.Average(observations),
            Layers = fits, Residuals = residuals, RmsResidual = VelocityCommon.Rms(residuals.Values),
        };
    }
}

/// <summary>
/// Interval methods: the layer velocity spans the first and last usable receivers inside the layer,
/// V = Δz/Δt_v (pseudo-interval) or V = ΔR/Δt (true interval).
/// </summary>
public sealed class IntervalCalculator(bool trueInterval) : IVelocityCalculator
{
    public VelocityMethod Method => trueInterval ? VelocityMethod.TrueInterval : VelocityMethod.PseudoInterval;

    public WaveVelocityResult Calculate(IReadOnlyList<StationObservation> observations, IReadOnlyList<ResolvedLayer> layers, SourceGeometry source, AnalysisSettings settings, WaveType wave)
    {
        var used = observations.Where(o => o.Included && o.Time is not null && o.VerticalTime is not null).OrderBy(o => o.Depth).ToList();
        var layerResults = new List<LayerVelocity>();
        foreach (var layer in layers)
        {
            var pts = used.Where(o => layer.ContainsForFit(o.Depth)).ToList();
            if (pts.Count < 2)
            {
                layerResults.Add(new LayerVelocity { Layer = layer, PointCount = pts.Count, Method = Method, Stations = pts.Select(p => p.StationId).ToList(),
                    Reason = pts.Count == 0 ? "No usable measurements in this layer." : "Only one usable measurement in this layer; at least two are needed for a velocity." });
                continue;
            }

            var a = pts[0];
            var b = pts[^1];
            var num = trueInterval ? b.SlantDistance - a.SlantDistance : b.Depth - a.Depth;
            var den = trueInterval ? b.Time!.Value - a.Time!.Value : b.VerticalTime!.Value - a.VerticalTime!.Value;
            layerResults.Add(den > 0 && num > 0
                ? new LayerVelocity { Layer = layer, Velocity = num / den, PointCount = pts.Count, Method = Method, Stations = pts.Select(p => p.StationId).ToList() }
                : new LayerVelocity { Layer = layer, PointCount = pts.Count, Method = Method, Stations = pts.Select(p => p.StationId).ToList(),
                    Reason = "Arrival time does not increase across this layer; review the picks." });
        }

        var (_, residuals) = VelocityCommon.LineFits(observations, layers, settings);
        return new WaveVelocityResult
        {
            Wave = wave, Method = Method, Observations = observations, PseudoIntervals = VelocityCommon.PseudoIntervals(observations),
            TrueIntervals = VelocityCommon.TrueIntervals(observations), AverageVelocity = VelocityCommon.Average(observations), Layers = layerResults,
            Residuals = residuals, RmsResidual = VelocityCommon.Rms(residuals.Values),
            Notes = ["Residuals are measured from a straight line fitted within each layer."],
        };
    }
}

public static class VelocityCalculatorFactory
{
    public static IVelocityCalculator Create(VelocityMethod method) => method switch
    {
        VelocityMethod.PseudoInterval => new IntervalCalculator(trueInterval: false),
        VelocityMethod.TrueInterval => new IntervalCalculator(trueInterval: true),
        VelocityMethod.RefractedRay => new RefractedRayCalculator(),
        _ => new LayerRegressionCalculator(),
    };

    public static string Describe(VelocityMethod method) => method switch
    {
        VelocityMethod.LayerRegression => "Layer regression of corrected travel time versus depth",
        VelocityMethod.PseudoInterval => "Pseudo-interval (Δz / Δt corrected)",
        VelocityMethod.TrueInterval => "True interval, straight rays ((R₂ − R₁) / (t₂ − t₁))",
        VelocityMethod.RefractedRay => "Refracted-ray layer stripping (Snell's law)",
        _ => method.ToString(),
    };
}
