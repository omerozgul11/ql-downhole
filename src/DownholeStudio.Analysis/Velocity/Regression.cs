using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Velocity;

/// <summary>Straight-line fit y = Intercept + Slope·x with goodness-of-fit statistics.</summary>
public sealed record LineFit
{
    public double Slope { get; init; }
    public double Intercept { get; init; }
    /// <summary>Coefficient of determination (weighted when weights are used).</summary>
    public double RSquared { get; init; }
    public int Count { get; init; }
    /// <summary>Root-mean-square of the (unweighted) residuals.</summary>
    public double RmsResidual { get; init; }
    /// <summary>Standard error of the slope; NaN with fewer than three points.</summary>
    public double SlopeStandardError { get; init; }
    public double[] Residuals { get; init; } = [];
    /// <summary>Final IRLS weights (all 1 for ordinary least squares).</summary>
    public double[] Weights { get; init; } = [];
    public RegressionMethod Method { get; init; }
    public int Iterations { get; init; }
}

public static class LinearRegression
{
    /// <summary>Weighted least squares; returns null for fewer than two distinct x values.</summary>
    public static LineFit? Fit(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double>? weights = null)
    {
        var n = x.Count;
        if (n < 2 || y.Count != n)
        {
            return null;
        }

        double sw = 0, sx = 0, sy = 0;
        for (var i = 0; i < n; i++)
        {
            var w = weights?[i] ?? 1.0;
            sw += w;
            sx += w * x[i];
            sy += w * y[i];
        }

        if (!(sw > 0))
        {
            return null;
        }

        var mx = sx / sw;
        var my = sy / sw;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var w = weights?[i] ?? 1.0;
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxx += w * dx * dx;
            sxy += w * dx * dy;
            syy += w * dy * dy;
        }

        if (!(sxx > 0))
        {
            return null;
        }

        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var residuals = new double[n];
        double ssr = 0, sswr = 0;
        for (var i = 0; i < n; i++)
        {
            residuals[i] = y[i] - (intercept + slope * x[i]);
            ssr += residuals[i] * residuals[i];
            sswr += (weights?[i] ?? 1.0) * residuals[i] * residuals[i];
        }

        var r2 = syy > 0 ? 1.0 - sswr / syy : 1.0;
        var se = n > 2 ? Math.Sqrt(sswr / (n - 2) / sxx) : double.NaN;
        return new LineFit
        {
            Slope = slope, Intercept = intercept, RSquared = r2, Count = n, RmsResidual = Math.Sqrt(ssr / n),
            SlopeStandardError = se, Residuals = residuals, Weights = weights?.ToArray() ?? Enumerable.Repeat(1.0, n).ToArray(),
            Method = RegressionMethod.OrdinaryLeastSquares,
        };
    }

    /// <summary>
    /// Huber M-estimate by iteratively reweighted least squares. Residual scale s is re-estimated at
    /// every iteration as the normalised median absolute deviation of the current residuals; weights are
    /// w = 1 for |r| ≤ k·s and w = k·s/|r| otherwise. Converges when the slope changes by less than
    /// 1e-10 relative, or after 50 iterations.
    /// </summary>
    public static LineFit? FitHuber(IReadOnlyList<double> x, IReadOnlyList<double> y, double k = 1.345)
    {
        var fit = Fit(x, y);
        if (fit is null || x.Count < 3)
        {
            return fit;
        }

        var weights = new double[x.Count];
        var iterations = 0;
        for (; iterations < 50; iterations++)
        {
            var scale = Statistics.ScaledMad(fit.Residuals);
            if (!(scale > 0))
            {
                break;
            }

            for (var i = 0; i < weights.Length; i++)
            {
                var r = Math.Abs(fit.Residuals[i]);
                weights[i] = r <= k * scale ? 1.0 : k * scale / r;
            }

            var next = Fit(x, y, weights);
            if (next is null)
            {
                break;
            }

            var converged = Math.Abs(next.Slope - fit.Slope) <= 1e-10 * Math.Max(1e-30, Math.Abs(fit.Slope));
            fit = next;
            if (converged)
            {
                break;
            }
        }

        return fit with { Method = RegressionMethod.RobustHuber, Iterations = iterations + 1 };
    }

    public static LineFit? Fit(IReadOnlyList<double> x, IReadOnlyList<double> y, RegressionMethod method, double huberK) =>
        method == RegressionMethod.RobustHuber ? FitHuber(x, y, huberK) : Fit(x, y);
}
