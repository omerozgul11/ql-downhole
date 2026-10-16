using System.Collections.Immutable;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Velocity;

/// <summary>A suggested layer boundary with the evidence for it.</summary>
public sealed record BoundarySuggestion(double Depth, string Reason);

public interface ILayerModeler
{
    /// <summary>Resolves the stored layer definitions into contiguous layers from the surface to the model bottom.</summary>
    IReadOnlyList<ResolvedLayer> Resolve(LayerModel model, double dataBottom);

    /// <summary>Suggests boundaries by piecewise-linear segmentation of corrected travel time versus depth.</summary>
    IReadOnlyList<BoundarySuggestion> SuggestBoundaries(IReadOnlyList<StationObservation> observations, int minPointsPerLayer = 3, int maxLayers = 8);
}

public sealed class LayerModeler : ILayerModeler
{
    public IReadOnlyList<ResolvedLayer> Resolve(LayerModel model, double dataBottom)
    {
        var bottom = model.BottomDepth ?? dataBottom;
        var defs = model.Layers.OrderBy(l => l.TopDepth).ToList();
        if (defs.Count == 0)
        {
            defs = [new LayerDefinition { Id = Guid.Empty, TopDepth = 0 }];
        }

        if (defs[0].TopDepth > 0)
        {
            defs[0] = defs[0] with { TopDepth = 0 };
        }

        var result = new List<ResolvedLayer>();
        for (var i = 0; i < defs.Count; i++)
        {
            var top = defs[i].TopDepth;
            var bot = i + 1 < defs.Count ? defs[i + 1].TopDepth : Math.Max(bottom, top);
            if (bot <= top && i + 1 < defs.Count)
            {
                continue;
            }

            result.Add(new ResolvedLayer(defs[i].Id, result.Count, top, bot, defs[i]));
        }

        return result;
    }

    public IReadOnlyList<BoundarySuggestion> SuggestBoundaries(IReadOnlyList<StationObservation> observations, int minPointsPerLayer = 3, int maxLayers = 8)
        => SuggestBoundaries(observations, minPointsPerLayer, maxLayers, 0.15);

    /// <param name="minimumContrast">Smallest relative velocity change between adjacent suggested layers.</param>
    public IReadOnlyList<BoundarySuggestion> SuggestBoundaries(IReadOnlyList<StationObservation> observations, int minPointsPerLayer, int maxLayers, double minimumContrast)
    {
        var pts = observations.Where(o => o.Included && o.VerticalTime is not null).OrderBy(o => o.Depth).ToList();
        var n = pts.Count;
        if (n < 2 * minPointsPerLayer)
        {
            return [];
        }

        var z = pts.Select(p => p.Depth).ToArray();
        var t = pts.Select(p => p.VerticalTime!.Value).ToArray();

        // Sum of squared residuals of a line through points i..j (inclusive).
        var sse = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = i + minPointsPerLayer - 1; j < n; j++)
            {
                var fit = LinearRegression.Fit(z[i..(j + 1)], t[i..(j + 1)]);
                sse[i, j] = fit is null ? double.PositiveInfinity : fit.Residuals.Sum(r => r * r);
            }
        }

        // Optimal segmentation for each segment count k by dynamic programming.
        var kMax = Math.Min(maxLayers, n / minPointsPerLayer);
        var cost = new double[kMax + 1, n + 1];
        var prev = new int[kMax + 1, n + 1];
        for (var k = 0; k <= kMax; k++)
        {
            for (var j = 0; j <= n; j++)
            {
                cost[k, j] = double.PositiveInfinity;
            }
        }

        cost[0, 0] = 0;
        for (var k = 1; k <= kMax; k++)
        {
            for (var j = k * minPointsPerLayer; j <= n; j++)
            {
                for (var i = (k - 1) * minPointsPerLayer; i <= j - minPointsPerLayer; i++)
                {
                    var c = cost[k - 1, i] + sse[i, j - 1];
                    if (c < cost[k, j])
                    {
                        cost[k, j] = c;
                        prev[k, j] = i;
                    }
                }
            }
        }

        // Model selection by the Bayesian information criterion (3 parameters per segment: slope, intercept, break).
        var noiseFloor = 1e-14;
        var bestK = 1;
        var bestBic = double.PositiveInfinity;
        for (var k = 1; k <= kMax; k++)
        {
            if (!double.IsFinite(cost[k, n]))
            {
                continue;
            }

            var bic = n * Math.Log(Math.Max(cost[k, n] / n, noiseFloor)) + (3 * k - 1) * Math.Log(n);
            if (bic < bestBic - 1e-9)
            {
                bestBic = bic;
                bestK = k;
            }
        }

        // Keep the largest segmentation (not above the BIC choice) in which every pair of adjacent segments
        // differs in velocity by at least the minimum contrast; smaller changes are not resolvable layers.
        List<int> starts = [];
        for (var k = bestK; k >= 1; k--)
        {
            if (!double.IsFinite(cost[k, n]))
            {
                continue;
            }

            starts = Backtrack(prev, k, n);
            if (k == 1 || MinimumContrast(z, t, starts, n) >= minimumContrast)
            {
                break;
            }
        }

        var suggestions = new List<BoundarySuggestion>();
        for (var s = 1; s < starts.Count; s++)
        {
            var i0 = starts[s - 1];
            var i1 = starts[s];
            var upper = LinearRegression.Fit(z[i0..i1], t[i0..i1]);
            var lowerEnd = s + 1 < starts.Count ? starts[s + 1] : n;
            var lower = LinearRegression.Fit(z[i1..lowerEnd], t[i1..lowerEnd]);
            var a = z[i1 - 1];
            var b = z[i1];
            var depth = 0.5 * (a + b);
            if (upper is not null && lower is not null && Math.Abs(upper.Slope - lower.Slope) > 1e-15)
            {
                // Intersection of the two lines, kept between the bracketing receivers.
                var zi = (lower.Intercept - upper.Intercept) / (upper.Slope - lower.Slope);
                if (zi > a && zi < b)
                {
                    depth = zi;
                }
            }

            var vUp = upper is { Slope: > 0 } ? 1 / upper.Slope : double.NaN;
            var vLo = lower is { Slope: > 0 } ? 1 / lower.Slope : double.NaN;
            suggestions.Add(new BoundarySuggestion(Math.Round(depth, 2),
                $"Slope change between {a:0.##} m and {b:0.##} m: about {vUp:0} m/s above, {vLo:0} m/s below."));
        }

        return suggestions;
    }

    private static List<int> Backtrack(int[,] prev, int k, int n)
    {
        var starts = new List<int>();
        var end = n;
        for (var j = k; j >= 1; j--)
        {
            var s = prev[j, end];
            starts.Add(s);
            end = s;
        }

        starts.Reverse();
        return starts;
    }

    /// <summary>Smallest relative velocity contrast between adjacent segments (∞ for one segment).</summary>
    private static double MinimumContrast(double[] z, double[] t, List<int> starts, int n)
    {
        var velocities = new List<double>();
        for (var s = 0; s < starts.Count; s++)
        {
            var a = starts[s];
            var b = s + 1 < starts.Count ? starts[s + 1] : n;
            var fit = LinearRegression.Fit(z[a..b], t[a..b]);
            velocities.Add(fit is { Slope: > 0 } ? 1 / fit.Slope : double.NaN);
        }

        var min = double.PositiveInfinity;
        for (var i = 1; i < velocities.Count; i++)
        {
            var (v1, v2) = (velocities[i - 1], velocities[i]);
            if (!double.IsFinite(v1) || !double.IsFinite(v2))
            {
                return 0;
            }

            min = Math.Min(min, Math.Abs(v1 - v2) / Math.Min(v1, v2));
        }

        return min;
    }
}

/// <summary>Pure edit operations on a layer model (used by undoable commands).</summary>
public static class LayerEdits
{
    public static LayerModel AddBoundary(LayerModel model, double depth)
    {
        var layers = model.Layers.Count == 0 ? [new LayerDefinition { TopDepth = 0 }] : model.Layers;
        if (layers.Any(l => Math.Abs(l.TopDepth - depth) < 1e-6) || depth <= 0)
        {
            return model with { Layers = layers };
        }

        var parent = layers.Where(l => l.TopDepth < depth).OrderByDescending(l => l.TopDepth).FirstOrDefault();
        var added = (parent ?? new LayerDefinition()) with { Id = Guid.NewGuid(), TopDepth = depth, Label = null };
        return model with { Layers = layers.Add(added).Sort((a, b) => a.TopDepth.CompareTo(b.TopDepth)) };
    }

    /// <summary>Removes the boundary at the top of the layer with <paramref name="layerId"/> (merging it into the layer above).</summary>
    public static LayerModel RemoveBoundary(LayerModel model, Guid layerId)
    {
        var sorted = model.Layers.OrderBy(l => l.TopDepth).ToList();
        var index = sorted.FindIndex(l => l.Id == layerId);
        return index <= 0 ? model : model with { Layers = [.. sorted.Where(l => l.Id != layerId)] };
    }

    public static LayerModel MoveBoundary(LayerModel model, Guid layerId, double newTop)
    {
        var sorted = model.Layers.OrderBy(l => l.TopDepth).ToList();
        var index = sorted.FindIndex(l => l.Id == layerId);
        if (index <= 0)
        {
            return model;
        }

        var min = sorted[index - 1].TopDepth + 0.01;
        var max = index + 1 < sorted.Count ? sorted[index + 1].TopDepth - 0.01 : model.BottomDepth ?? double.MaxValue;
        sorted[index] = sorted[index] with { TopDepth = Math.Clamp(newTop, min, Math.Max(min, max)) };
        return model with { Layers = [.. sorted] };
    }

    /// <summary>Merges a layer with the one below it.</summary>
    public static LayerModel MergeWithBelow(LayerModel model, Guid layerId)
    {
        var sorted = model.Layers.OrderBy(l => l.TopDepth).ToList();
        var index = sorted.FindIndex(l => l.Id == layerId);
        return index < 0 || index + 1 >= sorted.Count ? model : RemoveBoundary(model, sorted[index + 1].Id);
    }

    public static LayerModel Split(LayerModel model, Guid layerId, double bottom)
    {
        var sorted = model.Layers.OrderBy(l => l.TopDepth).ToList();
        var index = sorted.FindIndex(l => l.Id == layerId);
        if (index < 0)
        {
            return model;
        }

        var top = sorted[index].TopDepth;
        var bot = index + 1 < sorted.Count ? sorted[index + 1].TopDepth : bottom;
        return AddBoundary(model, Math.Round(0.5 * (top + bot), 2));
    }

    public static LayerModel FromBoundaries(IEnumerable<double> boundaries, LayerModel? existing = null)
    {
        var model = new LayerModel { Layers = [new LayerDefinition { TopDepth = 0 }], BottomDepth = existing?.BottomDepth };
        foreach (var b in boundaries.OrderBy(b => b))
        {
            model = AddBoundary(model, b);
        }

        return model;
    }

    public static LayerModel SetDensity(LayerModel model, Guid layerId, double? density) =>
        model with { Layers = model.Layers.Select(l => l.Id == layerId ? l with { MeasuredDensity = density } : l).ToImmutableList() };
}
