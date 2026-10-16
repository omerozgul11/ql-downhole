using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Velocity;

/// <summary>
/// Direct transmitted ray from a surface source to a buried receiver through horizontal layers,
/// obeying Snell's law (sin θ_j / v_j = p, constant ray parameter). Head waves are not modelled.
/// </summary>
public static class LayeredRayTracer
{
    /// <summary>A velocity segment of the ray path: thickness h (m) and velocity v (m/s).</summary>
    public readonly record struct Segment(double Thickness, double Velocity);

    /// <summary>
    /// Travel time of the transmitted ray with horizontal offset <paramref name="offset"/>. Solves
    /// X(p) = Σ h_j·p·v_j/√(1 − p²v_j²) = offset for p by bisection (X is monotonic in p), then
    /// t = Σ h_j/(v_j·√(1 − p²v_j²)).
    /// </summary>
    public static double TravelTime(IReadOnlyList<Segment> path, double offset)
    {
        if (path.Count == 0)
        {
            return double.NaN;
        }

        if (offset <= 0)
        {
            return path.Sum(s => s.Thickness / s.Velocity);
        }

        var vmax = path.Max(s => s.Velocity);
        double lo = 0, hi = (1.0 / vmax) * (1 - 1e-15);
        if (Distance(path, hi) < offset)
        {
            // Only possible for vanishing thickness: the ray is horizontal in the fastest layer.
            return offset / vmax + path.Sum(s => s.Thickness / s.Velocity);
        }

        for (var i = 0; i < 200; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Distance(path, mid) < offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }

            if (hi - lo <= 1e-16 / vmax)
            {
                break;
            }
        }

        var p = 0.5 * (lo + hi);
        double t = 0;
        foreach (var s in path)
        {
            var c = Math.Sqrt(Math.Max(1e-300, 1 - p * p * s.Velocity * s.Velocity));
            t += s.Thickness / (s.Velocity * c);
        }

        return t;
    }

    private static double Distance(IReadOnlyList<Segment> path, double p)
    {
        double x = 0;
        foreach (var s in path)
        {
            var pv = p * s.Velocity;
            x += s.Thickness * pv / Math.Sqrt(Math.Max(1e-300, 1 - pv * pv));
        }

        return x;
    }

    /// <summary>Ray path segments between source depth and receiver depth through layers with known velocities.</summary>
    public static List<Segment> Path(IReadOnlyList<(double Top, double Bottom, double Velocity)> layers, double sourceDepth, double receiverDepth)
    {
        var path = new List<Segment>();
        var top = Math.Max(0, sourceDepth);
        foreach (var (lt, lb, v) in layers)
        {
            var a = Math.Max(lt, top);
            var b = Math.Min(lb, receiverDepth);
            if (b > a)
            {
                path.Add(new Segment(b - a, v));
            }
        }

        return path;
    }
}

/// <summary>
/// Layer stripping with refracted rays: proceeding downwards, each layer's velocity is the value
/// that minimises the squared misfit between observed travel times (trigger-corrected, not
/// geometry-corrected) and Snell's-law ray times for receivers inside that layer, holding the
/// velocities of the layers above fixed. The search is a log-spaced grid from 10 to 10 000 m/s
/// refined by golden-section search.
/// </summary>
public sealed class RefractedRayCalculator : IVelocityCalculator
{
    public VelocityMethod Method => VelocityMethod.RefractedRay;

    public WaveVelocityResult Calculate(IReadOnlyList<StationObservation> observations, IReadOnlyList<ResolvedLayer> layers, SourceGeometry source, AnalysisSettings settings, WaveType wave)
    {
        var used = observations.Where(o => o.Included && o.Time is not null).OrderBy(o => o.Depth).ToList();
        var notes = new List<string>();
        if (source.SourceDepth < 0)
        {
            notes.Add("The source is above ground; ray paths start at the ground surface.");
        }

        var known = new List<(double Top, double Bottom, double Velocity)>();
        var results = new List<LayerVelocity>();
        var residuals = new Dictionary<Guid, double>();
        var blocked = false;
        foreach (var layer in layers)
        {
            var pts = used.Where(o => layer.Owns(o.Depth)).ToList();
            if (blocked)
            {
                results.Add(new LayerVelocity { Layer = layer, PointCount = pts.Count, Method = Method, Reason = "A layer above has no velocity, so rays through this layer cannot be traced." });
                continue;
            }

            if (pts.Count == 0)
            {
                results.Add(new LayerVelocity { Layer = layer, PointCount = 0, Method = Method, Reason = "No usable measurements in this layer." });
                blocked = true;
                continue;
            }

            double Misfit(double v)
            {
                var model = known.Append((layer.Top, layer.Bottom, v)).ToList();
                double s = 0;
                foreach (var o in pts)
                {
                    var t = LayeredRayTracer.TravelTime(LayeredRayTracer.Path(model, source.SourceDepth, o.Depth), source.HorizontalOffset);
                    var r = o.Time!.Value - t;
                    s += r * r;
                }

                return s;
            }

            var best = Minimise(Misfit);
            known.Add((layer.Top, layer.Bottom, best));
            foreach (var o in pts)
            {
                var t = LayeredRayTracer.TravelTime(LayeredRayTracer.Path(known, source.SourceDepth, o.Depth), source.HorizontalOffset);
                residuals[o.StationId] = o.Time!.Value - t;
            }

            var rms = VelocityCommon.Rms(pts.Select(o => residuals[o.StationId]));
            results.Add(new LayerVelocity
            {
                Layer = layer, Velocity = best, PointCount = pts.Count, Stations = pts.Select(p => p.StationId).ToList(), Method = Method,
                Fit = new LineFit { Count = pts.Count, RmsResidual = rms ?? double.NaN, RSquared = double.NaN, SlopeStandardError = double.NaN, Method = RegressionMethod.OrdinaryLeastSquares },
            });
        }

        return new WaveVelocityResult
        {
            Wave = wave, Method = Method, Observations = observations, PseudoIntervals = VelocityCommon.PseudoIntervals(observations),
            TrueIntervals = VelocityCommon.TrueIntervals(observations), AverageVelocity = VelocityCommon.Average(observations),
            Layers = results, Residuals = residuals, RmsResidual = VelocityCommon.Rms(residuals.Values),
            Notes = [.. notes, "Residuals are observed minus ray-traced travel times. Head waves are not modelled."],
        };
    }

    private static double Minimise(Func<double, double> f)
    {
        const double lo = 10, hi = 10000;
        const int n = 240;
        var bestI = 0;
        var bestF = double.PositiveInfinity;
        var grid = new double[n];
        for (var i = 0; i < n; i++)
        {
            grid[i] = lo * Math.Pow(hi / lo, i / (n - 1.0));
            var v = f(grid[i]);
            if (v < bestF)
            {
                bestF = v;
                bestI = i;
            }
        }

        var a = Math.Log(grid[Math.Max(0, bestI - 1)]);
        var b = Math.Log(grid[Math.Min(n - 1, bestI + 1)]);
        var g = (Math.Sqrt(5) - 1) / 2;
        var c = b - g * (b - a);
        var d = a + g * (b - a);
        double fc = f(Math.Exp(c)), fd = f(Math.Exp(d));
        for (var i = 0; i < 100 && b - a > 1e-12; i++)
        {
            if (fc < fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - g * (b - a);
                fc = f(Math.Exp(c));
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + g * (b - a);
                fd = f(Math.Exp(d));
            }
        }

        return Math.Exp(0.5 * (a + b));
    }
}
