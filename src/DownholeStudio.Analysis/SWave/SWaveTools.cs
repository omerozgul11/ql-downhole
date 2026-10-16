using DownholeStudio.Analysis.Traces;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.SWave;

/// <summary>Result of a horizontal polarization analysis in a time window.</summary>
public sealed record PolarizationResult
{
    /// <summary>Angle (degrees, from X towards Y, 0–180) that maximises energy on the rotated radial component.</summary>
    public double AngleDegrees { get; init; }
    /// <summary>Share of horizontal energy on the radial component after rotation (λ₁/(λ₁+λ₂)).</summary>
    public double RadialEnergyFraction { get; init; }
    /// <summary>Share of horizontal energy on X before rotation.</summary>
    public double XEnergyFraction { get; init; }
    /// <summary>Rectilinearity 1 − λ₂/λ₁ (1 = perfectly linear particle motion).</summary>
    public double Rectilinearity { get; init; }
    public double[] ScanAngles { get; init; } = [];
    /// <summary>Radial energy fraction for each scan angle.</summary>
    public double[] ScanEnergy { get; init; } = [];
    public double[] HodogramX { get; init; } = [];
    public double[] HodogramY { get; init; } = [];
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
}

public interface IPolarizationAnalyzer
{
    /// <summary>Analyses X/Y horizontal motion between <paramref name="start"/> and <paramref name="end"/> (record time, s).</summary>
    PolarizationResult? Analyze(ProcessedTrace x, ProcessedTrace y, double start, double end);
}

/// <summary>
/// Principal-component (covariance) polarization analysis: with C = [[Σx², Σxy], [Σxy, Σy²]] over the
/// window, the azimuth of maximum energy is θ = ½·atan2(2Σxy, Σx² − Σy²) and the radial energy
/// fraction is λ₁/(λ₁ + λ₂). Rotation is never applied automatically; the user applies the angle.
/// </summary>
public sealed class CovariancePolarizationAnalyzer : IPolarizationAnalyzer
{
    public PolarizationResult? Analyze(ProcessedTrace x, ProcessedTrace y, double start, double end)
    {
        if (Math.Abs(x.SampleInterval - y.SampleInterval) > 1e-12)
        {
            return null;
        }

        var a = x.ClampIndex(Math.Min(start, end));
        var b = Math.Min(x.ClampIndex(Math.Max(start, end)), y.Samples.Length - 1);
        var n = b - a + 1;
        if (n < 4)
        {
            return null;
        }

        var xs = x.Samples.AsSpan(a, n).ToArray();
        var ys = y.Samples.AsSpan(a, n).ToArray();
        double sxx = 0, syy = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sxx += xs[i] * xs[i];
            syy += ys[i] * ys[i];
            sxy += xs[i] * ys[i];
        }

        var total = sxx + syy;
        if (!(total > 0))
        {
            return null;
        }

        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var half = Math.Sqrt(0.25 * (sxx - syy) * (sxx - syy) + sxy * sxy);
        var l1 = 0.5 * total + half;
        var l2 = 0.5 * total - half;
        var deg = theta * 180 / Math.PI;
        if (deg < 0)
        {
            deg += 180;
        }

        var angles = Enumerable.Range(0, 181).Select(i => (double)i).ToArray();
        var energy = angles.Select(t =>
        {
            var r = t * Math.PI / 180;
            var c = Math.Cos(r);
            var s = Math.Sin(r);
            return (c * c * sxx + 2 * c * s * sxy + s * s * syy) / total;
        }).ToArray();

        return new PolarizationResult
        {
            AngleDegrees = deg, RadialEnergyFraction = l1 / total, XEnergyFraction = sxx / total, Rectilinearity = l1 > 0 ? 1 - l2 / l1 : 0,
            ScanAngles = angles, ScanEnergy = energy, HodogramX = xs, HodogramY = ys, WindowStart = x.TimeAt(a), WindowEnd = x.TimeAt(b),
        };
    }
}

/// <summary>Cross-correlation between two traces in a window.</summary>
public sealed record CorrelationAnalysis
{
    public required CorrelationResult Correlation { get; init; }
    /// <summary>Time shift (s) of the second trace relative to the first.</summary>
    public double TimeShift => Correlation.BestLag;
    public double Coefficient => Correlation.BestValue;
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
}

public interface ICrossCorrelationAnalyzer
{
    /// <summary>Correlates <paramref name="b"/> against <paramref name="a"/> over [start, end] of a, allowing lags up to ±maxLag.</summary>
    CorrelationAnalysis? Correlate(ProcessedTrace a, ProcessedTrace b, double start, double end, double maxLag, bool polarityInsensitive = false);
}

public sealed class WindowedCrossCorrelationAnalyzer : ICrossCorrelationAnalyzer
{
    public CorrelationAnalysis? Correlate(ProcessedTrace a, ProcessedTrace b, double start, double end, double maxLag, bool polarityInsensitive = false)
    {
        if (Math.Abs(a.SampleInterval - b.SampleInterval) > 1e-12)
        {
            return null;
        }

        var dt = a.SampleInterval;
        var i0 = a.ClampIndex(Math.Min(start, end));
        var i1 = a.ClampIndex(Math.Max(start, end));
        var lag = (int)Math.Ceiling(maxLag / dt);
        var n = i1 - i0 + 1;
        if (n < 4)
        {
            return null;
        }

        // Window of a, and the same window of b padded by the maximum lag on both sides (Hann-tapered ends).
        var wa = new double[n + 2 * lag];
        var wb = new double[n + 2 * lag];
        var taper = TimeDomain.Taper(Enumerable.Repeat(1.0, n).ToArray(), 0.1);
        for (var i = 0; i < n; i++)
        {
            wa[lag + i] = a.Samples[i0 + i] * taper[i];
        }

        var bOffset = b.IndexAt(a.TimeAt(i0)) - lag;
        for (var i = 0; i < wb.Length; i++)
        {
            var j = bOffset + i;
            wb[i] = j >= 0 && j < b.Samples.Length ? b.Samples[j] : 0;
        }

        var result = CrossCorrelation.Compute(wa, wb, dt, maxLag, polarityInsensitive);
        if (double.IsFinite(result.BestLag))
        {
            // Report the normalised coefficient over the analysis window only (b shifted by the best lag),
            // not over the lag-padded window used for the search.
            var shifted = new double[n];
            for (var i = 0; i < n; i++)
            {
                shifted[i] = TimeDomain.InterpolateAt(b.Samples, dt, a.TimeAt(i0 + i) + result.BestLag, b.Delay) * taper[i];
            }

            var coefficient = Statistics.Pearson(wa.AsSpan(lag, n), shifted);
            if (double.IsFinite(coefficient))
            {
                result = result with { BestValue = coefficient };
            }
        }

        return new CorrelationAnalysis { Correlation = result, WindowStart = a.TimeAt(i0), WindowEnd = a.TimeAt(i1) };
    }
}
