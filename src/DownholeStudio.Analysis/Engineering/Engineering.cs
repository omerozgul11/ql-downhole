using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Engineering;

/// <summary>Small-strain elastic properties from Vp, Vs and density (all SI).</summary>
public sealed record ElasticProperties
{
    public double? Vp { get; init; }
    public double? Vs { get; init; }
    /// <summary>kg/m³.</summary>
    public double? Density { get; init; }
    public bool DensityEstimated { get; init; }
    public string? DensityMethod { get; init; }
    public double? VpVsRatio { get; init; }
    public double? PoissonRatio { get; init; }
    /// <summary>Pa.</summary>
    public double? ShearModulus { get; init; }
    public double? YoungModulus { get; init; }
    public double? BulkModulus { get; init; }
    /// <summary>Constrained (P-wave) modulus M = ρVp², Pa.</summary>
    public double? ConstrainedModulus { get; init; }
    public IReadOnlyList<Diagnostic> Issues { get; init; } = [];
}

public interface IEngineeringPropertyCalculator
{
    ElasticProperties Calculate(double? vp, double? vs, double? density, bool densityEstimated = false, string? densityMethod = null);
}

/// <summary>
/// Isotropic linear elasticity:
/// ν = (Vp² − 2Vs²) / (2(Vp² − Vs²)); G = ρVs²; E = 2G(1 + ν); K = ρ(Vp² − 4Vs²/3); M = ρVp².
/// Values that would be physically meaningless are withheld and the reason reported.
/// </summary>
public sealed class ElasticPropertyCalculator : IEngineeringPropertyCalculator
{
    /// <summary>Vp/Vs ratios closer than this to 1 are treated as numerically degenerate for ν.</summary>
    private const double DegenerateRatioTolerance = 1e-6;

    public ElasticProperties Calculate(double? vp, double? vs, double? density, bool densityEstimated = false, string? densityMethod = null)
    {
        var issues = new List<Diagnostic>();
        double? ratio = null, nu = null, g = null, e = null, k = null, m = null;
        var vpOk = vp is > 0;
        var vsOk = vs is > 0;
        var rhoOk = density is > 0;
        if (vp is <= 0 || vs is <= 0)
        {
            issues.Add(Diagnostic.Error("eng.nonPositiveVelocity", "A velocity is zero or negative; check the picks and layer model."));
        }

        if (vpOk && vsOk)
        {
            var p2 = vp!.Value * vp.Value;
            var s2 = vs!.Value * vs.Value;
            ratio = vp.Value / vs.Value;
            if (vs.Value >= vp.Value)
            {
                issues.Add(Diagnostic.Error("eng.vsAboveVp",
                    $"Vs ({vs.Value:0} m/s) is not lower than Vp ({vp.Value:0} m/s). This is physically inconsistent for an isotropic elastic material; review the P and S picks and layer boundaries.",
                    "Poisson's ratio, Young's modulus and bulk modulus are not calculated."));
            }
            else if (Math.Abs(p2 - s2) <= DegenerateRatioTolerance * p2)
            {
                issues.Add(Diagnostic.Error("eng.degenerate", "Vp and Vs are almost equal; Poisson's ratio is undefined."));
            }
            else
            {
                nu = (p2 - 2 * s2) / (2 * (p2 - s2));
                if (nu < 0)
                {
                    issues.Add(Diagnostic.Warning("eng.negativePoisson",
                        $"Poisson's ratio is negative ({nu:0.###}) because Vp/Vs = {ratio:0.###} < √2. This is unusual for soils and rocks; review the picks."));
                }

                if (ratio > 3.0 && vp > 1400)
                {
                    issues.Add(Diagnostic.Info("eng.saturated",
                        $"Vp ({vp:0} m/s) is close to or above the velocity of water and Vp/Vs = {ratio:0.#}. Below the water table Vp reflects the pore fluid, so Poisson's ratio and the bulk modulus describe the saturated mixture, not the soil skeleton."));
                }
            }
        }
        else if (vpOk != vsOk)
        {
            issues.Add(Diagnostic.Warning("eng.missingVelocity", vpOk ? "Vs is not available for this layer." : "Vp is not available for this layer."));
        }

        if (!rhoOk)
        {
            issues.Add(Diagnostic.Warning("eng.missingDensity", "Density is not available, so moduli are not calculated. Enter a measured density or explicitly select an estimation method."));
        }
        else
        {
            var rho = density!.Value;
            if (vsOk)
            {
                g = rho * vs!.Value * vs.Value;
            }

            if (vpOk)
            {
                m = rho * vp!.Value * vp.Value;
            }

            if (vpOk && vsOk && vs!.Value < vp!.Value)
            {
                k = rho * (vp.Value * vp.Value - 4.0 * vs.Value * vs.Value / 3.0);
                if (k <= 0)
                {
                    issues.Add(Diagnostic.Error("eng.negativeBulk",
                        $"The bulk modulus would be {(k < 0 ? "negative" : "zero")} (Vp/Vs = {ratio:0.###} ≤ 2/√3). This indicates a calculation input problem; it is not reported."));
                    k = null;
                }

                if (nu is not null && g is not null)
                {
                    e = 2 * g * (1 + nu);
                    if (e <= 0)
                    {
                        issues.Add(Diagnostic.Error("eng.negativeYoung", "Young's modulus would not be positive; it is not reported."));
                        e = null;
                    }
                }
            }
        }

        return new ElasticProperties
        {
            Vp = vp, Vs = vs, Density = density, DensityEstimated = densityEstimated, DensityMethod = densityMethod, VpVsRatio = ratio,
            PoissonRatio = nu, ShearModulus = g, YoungModulus = e, BulkModulus = k, ConstrainedModulus = m, Issues = issues,
        };
    }
}

/// <summary>Density estimated from Vp with a published empirical relation.</summary>
public sealed record DensityEstimate(double? Density, string Method, string Assumptions, Diagnostic? Issue);

public static class DensityEstimator
{
    /// <summary>
    /// Gardner, Gardner &amp; Gregory (1974): ρ = 1.74·Vp^0.25 with Vp in km/s and ρ in g/cm³.
    /// Calibrated on brine-saturated sedimentary rocks with 1.5 ≤ Vp ≤ 6.1 km/s.
    /// </summary>
    public static DensityEstimate Gardner(double vp)
    {
        var km = vp / 1000.0;
        var rho = 1.74 * Math.Pow(km, 0.25) * 1000.0;
        const string assumptions = "Gardner et al. (1974): ρ[g/cm³] = 1.74·Vp[km/s]^0.25; derived for water-saturated sedimentary rocks, 1.5–6.1 km/s.";
        var issue = km is < 1.5 or > 6.1
            ? Diagnostic.Warning("density.outsideRange", $"Vp = {vp:0} m/s is outside the 1500–6100 m/s range of the Gardner relation; the estimate is unreliable.")
            : null;
        return new DensityEstimate(rho, "Gardner et al. (1974)", assumptions, issue);
    }

    /// <summary>
    /// Brocher (2005), polynomial fit to the Nafe–Drake curve:
    /// ρ = 1.6612Vp − 0.4721Vp² + 0.0671Vp³ − 0.0043Vp⁴ + 0.000106Vp⁵ (Vp km/s, ρ g/cm³), valid 1.5–8.5 km/s.
    /// </summary>
    public static DensityEstimate Brocher(double vp)
    {
        var v = vp / 1000.0;
        var rho = (1.6612 * v - 0.4721 * v * v + 0.0671 * Math.Pow(v, 3) - 0.0043 * Math.Pow(v, 4) + 0.000106 * Math.Pow(v, 5)) * 1000.0;
        const string assumptions = "Brocher (2005) Nafe–Drake polynomial; derived for sedimentary and crystalline crustal rocks, 1.5–8.5 km/s.";
        var issue = v is < 1.5 or > 8.5
            ? Diagnostic.Warning("density.outsideRange", $"Vp = {vp:0} m/s is outside the 1500–8500 m/s range of the Brocher relation; the estimate is unreliable.")
            : null;
        return new DensityEstimate(rho, "Brocher (2005)", assumptions, issue);
    }

    public static DensityEstimate Estimate(DensityEstimationMethod method, double vp) => method switch
    {
        DensityEstimationMethod.Brocher2005NafeDrake => Brocher(vp),
        _ => Gardner(vp),
    };
}

/// <summary>Time-averaged shear-wave velocity.</summary>
public sealed record TimeAveragedVelocity
{
    public double Top { get; init; }
    public double Bottom { get; init; }
    /// <summary>Velocity, m/s; null when it cannot be calculated.</summary>
    public double? Value { get; init; }
    public bool Extrapolated { get; init; }
    /// <summary>Depth range that was extrapolated, when <see cref="Extrapolated"/>.</summary>
    public double? ExtrapolatedFrom { get; init; }
    public string? Reason { get; init; }
    public double? TravelTime { get; init; }
    public string Label => Top == 0 && Bottom == 30 ? "Vs30" : $"Vs({Top:0.##}–{Bottom:0.##} m)";
}

public interface IVs30Calculator
{
    /// <summary>
    /// Time-averaged velocity V = (z_b − z_t) / Σ(h_i / V_i) over [top, bottom].
    /// <paramref name="layers"/> are (top, bottom, velocity) in order; <paramref name="measuredBottom"/> is the deepest
    /// depth supported by measurements.
    /// </summary>
    TimeAveragedVelocity Calculate(IReadOnlyList<(double Top, double Bottom, double? Velocity)> layers, double measuredBottom, double top, double bottom, Vs30Settings settings);
}

public sealed class Vs30Calculator : IVs30Calculator
{
    public TimeAveragedVelocity Calculate(IReadOnlyList<(double Top, double Bottom, double? Velocity)> layers, double measuredBottom, double top, double bottom, Vs30Settings settings)
    {
        var result = new TimeAveragedVelocity { Top = top, Bottom = bottom };
        if (!(bottom > top))
        {
            return result with { Reason = "The depth interval is empty." };
        }

        if (layers.Count == 0)
        {
            return result with { Reason = "No layer model is available." };
        }

        var extrapolated = false;
        var model = layers.ToList();
        if (measuredBottom + 1e-9 < bottom)
        {
            if (!settings.AllowExtrapolation)
            {
                return result with
                {
                    Reason = Math.Abs(top) < 1e-9 && Math.Abs(bottom - 30) < 1e-9
                        ? $"Insufficient measured depth for direct Vs30 calculation (measurements reach {measuredBottom:0.##} m)."
                        : $"Insufficient measured depth: measurements reach {measuredBottom:0.##} m, the interval needs {bottom:0.##} m.",
                };
            }

            var deepest = model[^1];
            if (deepest.Velocity is null)
            {
                return result with { Reason = "The deepest layer has no velocity, so it cannot be extrapolated." };
            }

            model[^1] = (deepest.Top, bottom, deepest.Velocity);
            extrapolated = true;
        }

        double time = 0;
        foreach (var (lt, lb, v) in model)
        {
            var a = Math.Max(lt, top);
            var b = Math.Min(lb, bottom);
            if (b <= a)
            {
                continue;
            }

            if (v is not > 0)
            {
                return result with { Reason = $"The layer from {lt:0.##} to {lb:0.##} m has no valid velocity." };
            }

            time += (b - a) / v.Value;
        }

        var covered = model.Where(l => l.Bottom > top && l.Top < bottom).Sum(l => Math.Min(l.Bottom, bottom) - Math.Max(l.Top, top));
        if (covered + 1e-6 < bottom - top)
        {
            return result with { Reason = "The layer model does not cover the whole interval." };
        }

        return result with
        {
            Value = (bottom - top) / time, TravelTime = time, Extrapolated = extrapolated,
            ExtrapolatedFrom = extrapolated ? measuredBottom : null,
        };
    }
}
