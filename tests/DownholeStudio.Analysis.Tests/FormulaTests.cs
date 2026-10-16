using System.Collections.Immutable;
using DownholeStudio.Analysis.Engineering;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Trigger;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Analysis.Tests;

/// <summary>Every formula checked against hand-calculated values (see docs/validation).</summary>
public sealed class FormulaTests
{
    [Fact]
    public void StraightRaySlantDistance()
    {
        // 3-4-5 triangle: receiver 4 m below a surface source offset 3 m.
        Assert.Equal(5.0, StraightRay.SlantDistance(4, 0, 3), 12);
        // Source 0.5 m deep, receiver at 4.5 m: dz = 4.
        Assert.Equal(5.0, StraightRay.SlantDistance(4.5, 0.5, 3), 12);
    }

    [Fact]
    public void VerticalCorrectedTime()
    {
        // t_v = t · z / R = 0.010 · 4/5.
        Assert.Equal(0.008, StraightRay.VerticalTime(0.010, 4, 0, 3), 15);
    }

    [Fact]
    public void TrueIntervalVelocity()
    {
        // (10 − 5)/(0.020 − 0.010) = 500 m/s.
        Assert.Equal(500.0, StraightRay.IntervalVelocity(5, 0.010, 10, 0.020), 9);
    }

    [Fact]
    public void Vs30HarmonicAverage()
    {
        var layers = new List<(double, double, double?)> { (0, 5, 200), (5, 15, 300), (15, 30, 500) };
        var r = new Vs30Calculator().Calculate(layers, 30, 0, 30, new Vs30Settings());
        // 30 / (5/200 + 10/300 + 15/500) = 30 / 0.0883333… = 339.6226 m/s.
        Assert.Equal(30.0 / (5.0 / 200 + 10.0 / 300 + 15.0 / 500), r.Value!.Value, 9);
        Assert.Equal(339.6226, r.Value!.Value, 3);
        Assert.False(r.Extrapolated);
    }

    [Fact]
    public void Vs30RefusesToExtrapolateSilently()
    {
        var layers = new List<(double, double, double?)> { (0, 5, 200), (5, 20, 300) };
        var r = new Vs30Calculator().Calculate(layers, 20, 0, 30, new Vs30Settings());
        Assert.Null(r.Value);
        Assert.Contains("Insufficient measured depth for direct Vs30 calculation", r.Reason);
    }

    [Fact]
    public void Vs30ExtrapolationIsLabelled()
    {
        var layers = new List<(double, double, double?)> { (0, 5, 200), (5, 20, 300) };
        var r = new Vs30Calculator().Calculate(layers, 20, 0, 30, new Vs30Settings { AllowExtrapolation = true });
        Assert.True(r.Extrapolated);
        Assert.Equal(20.0, r.ExtrapolatedFrom);
        Assert.Equal(30.0 / (5.0 / 200 + 25.0 / 300), r.Value!.Value, 9);
    }

    [Fact]
    public void CustomIntervalVsZ()
    {
        var layers = new List<(double, double, double?)> { (0, 5, 200), (5, 15, 300), (15, 30, 500) };
        var r = new Vs30Calculator().Calculate(layers, 30, 2, 10, new Vs30Settings());
        Assert.Equal(8.0 / (3.0 / 200 + 5.0 / 300), r.Value!.Value, 9);
    }

    [Fact]
    public void ElasticModuliHandCalculated()
    {
        // Vp = 1500, Vs = 300, ρ = 1900.
        var p = new ElasticPropertyCalculator().Calculate(1500, 300, 1900);
        Assert.Equal(2070000.0 / 4320000.0, p.PoissonRatio!.Value, 12); // 0.4791667
        Assert.Equal(171e6, p.ShearModulus!.Value, 3);
        Assert.Equal(505.875e6, p.YoungModulus!.Value, 2);
        Assert.Equal(4.047e9, p.BulkModulus!.Value, 1);
        Assert.Equal(5.0, p.VpVsRatio!.Value, 12);
        Assert.Equal(1900 * 1500.0 * 1500.0, p.ConstrainedModulus!.Value, 3);
    }

    [Theory]
    [InlineData(400, 180, 1800)]
    [InlineData(1600, 380, 1950)]
    [InlineData(2400, 1300, 2300)]
    [InlineData(5500, 3100, 2650)]
    public void EquivalentModulusFormulasAgree(double vp, double vs, double rho)
    {
        var p = new ElasticPropertyCalculator().Calculate(vp, vs, rho);
        double vp2 = vp * vp, vs2 = vs * vs;
        // Independent forms: E = ρVs²(3Vp² − 4Vs²)/(Vp² − Vs²); K = E/(3(1 − 2ν)); G = E/(2(1 + ν)); ν = (Vp/Vs)²−2 / (2((Vp/Vs)²−1)).
        var eAlt = rho * vs2 * (3 * vp2 - 4 * vs2) / (vp2 - vs2);
        var r2 = vp2 / vs2;
        var nuAlt = (r2 - 2) / (2 * (r2 - 1));
        Assert.Equal(1, p.YoungModulus!.Value / eAlt, 12);
        Assert.Equal(nuAlt, p.PoissonRatio!.Value, 12);
        Assert.Equal(1, p.BulkModulus!.Value / (p.YoungModulus.Value / (3 * (1 - 2 * p.PoissonRatio.Value))), 9);
        Assert.Equal(1, p.ShearModulus!.Value / (p.YoungModulus.Value / (2 * (1 + p.PoissonRatio.Value))), 12);
        // λ + 2G = M (P-wave modulus) where λ = K − 2G/3.
        Assert.Equal(1, (p.BulkModulus.Value + 4.0 / 3.0 * p.ShearModulus.Value) / p.ConstrainedModulus!.Value, 12);
    }

    [Fact]
    public void VsAboveVpIsFlaggedAndNotCalculated()
    {
        var p = new ElasticPropertyCalculator().Calculate(300, 400, 1800);
        Assert.Null(p.PoissonRatio);
        Assert.Null(p.YoungModulus);
        Assert.Null(p.BulkModulus);
        Assert.Contains(p.Issues, i => i.Code == "eng.vsAboveVp" && i.Severity == Severity.Error);
        Assert.NotNull(p.ShearModulus); // G = ρVs² is still defined
    }

    [Fact]
    public void NegativeBulkModulusIsWithheld()
    {
        // Vp/Vs = 1.1 < 2/√3: K = ρ(Vp² − 4Vs²/3) < 0.
        var p = new ElasticPropertyCalculator().Calculate(1100, 1000, 2000);
        Assert.Null(p.BulkModulus);
        Assert.Contains(p.Issues, i => i.Code == "eng.negativeBulk");
        Assert.Contains(p.Issues, i => i.Code == "eng.negativePoisson");
    }

    [Fact]
    public void MissingDensityGivesNoModuli()
    {
        var p = new ElasticPropertyCalculator().Calculate(1500, 300, null);
        Assert.Null(p.ShearModulus);
        Assert.NotNull(p.PoissonRatio);
        Assert.Contains(p.Issues, i => i.Code == "eng.missingDensity");
    }

    [Fact]
    public void GardnerDensity()
    {
        // ρ = 1.74 · 2^0.25 g/cm³ = 2.069204 g/cm³.
        var e = DensityEstimator.Gardner(2000);
        Assert.Equal(1.74 * Math.Pow(2, 0.25) * 1000, e.Density!.Value, 9);
        Assert.Null(e.Issue);
        Assert.NotNull(DensityEstimator.Gardner(400).Issue);
    }

    [Fact]
    public void BrocherDensity()
    {
        // At Vp = 5 km/s: 8.306 − 11.8025 + 8.3875 − 2.6875 + 0.33125 = 2.53475 g/cm³.
        var expected = (1.6612 * 5 - 0.4721 * 25 + 0.0671 * 125 - 0.0043 * 625 + 0.000106 * 3125) * 1000;
        Assert.Equal(expected, DensityEstimator.Brocher(5000).Density!.Value, 9);
        Assert.Equal(2534.75, expected, 6);
    }

    [Theory]
    [InlineData(1.0, Quantity.Velocity, 0.3048)]          // 1 ft/s
    [InlineData(1.0, Quantity.Length, 0.3048)]            // 1 ft
    [InlineData(1.0, Quantity.Density, 16.018463373960138)] // 1 pcf
    [InlineData(1.0, Quantity.Modulus, 47880.258980335840)] // 1 ksf
    public void UsUnitConversions(double display, Quantity q, double si)
    {
        var u = UnitPreferences.UsCustomary;
        Assert.Equal(si, u.FromDisplay(display, q), 9);
        Assert.Equal(display, u.ToDisplay(si, q), 12);
    }

    [Fact]
    public void SiUnitConversionsAndPsi()
    {
        Assert.Equal(6894.757293168361, UnitFactors.PascalsPerPsi, 6);
        Assert.Equal(1000.0, UnitFactors.SiPerUnit(DisplayUnit.GramPerCubicCentimetre));
        var si = UnitPreferences.Si;
        Assert.Equal(171.0, si.ToDisplay(171e6, Quantity.Modulus), 12);
        Assert.Equal(12.5, si.ToDisplay(0.0125, Quantity.Time), 12);
        Assert.Equal("171.0 MPa", si.Format(171e6, Quantity.Modulus).Replace(',', '.'));
    }

    [Fact]
    public void TriggerCorrectionModes()
    {
        var recA = Guid.NewGuid();
        var recB = Guid.NewGuid();
        var recC = Guid.NewGuid();
        ProjectState Make(TriggerCorrectionMode mode, double expected = 0) => (ProjectState.CreateNew("t", "1", DateTimeOffset.UnixEpoch) with
        {
            Survey = new Survey { Records = [Rec(recA), Rec(recB), Rec(recC)] },
        }).WithActiveScenario(sc => sc with
        {
            Trigger = new TriggerSettings { Mode = mode, ExpectedReferenceTime = expected },
            ReferencePicks = ImmutableDictionary<Guid, ArrivalPick>.Empty
                .Add(recA, new ArrivalPick { Time = 0.0010 }).Add(recB, new ArrivalPick { Time = 0.0012 }).Add(recC, new ArrivalPick { Time = 0.0014 }),
        });
        var c = new ReferenceTriggerCorrector();
        Assert.Equal(0.0, c.Compute(Make(TriggerCorrectionMode.None))[recB].Correction);
        // Raw 0.0150 at record B with reference onset 0.0012 → corrected 0.0138.
        Assert.Equal(0.0138, 0.0150 + c.Compute(Make(TriggerCorrectionMode.SubtractReferenceOnset)).GetValueOrDefault(recB)!.Correction!.Value, 12);
        Assert.Equal(0.0138 + 0.0002, 0.0150 + c.Compute(Make(TriggerCorrectionMode.SubtractReferenceOnset, 0.0002))[recB].Correction!.Value, 12);
        // Align to median (0.0012): record C is 0.2 ms late.
        Assert.Equal(-0.0002, c.Compute(Make(TriggerCorrectionMode.AlignToReferenceMedian))[recC].Correction!.Value, 12);
    }

    private static SeismicRecord Rec(Guid id) => new()
    {
        Id = id, FormatName = "SEG-2", Source = new SourceFileInfo { FileName = $"{id}.sg2", Sha256 = id.ToString("N"), EmbeddedPath = "raw/x" },
    };

    [Fact]
    public void RegressionRecoversLineAndHuberResistsOutlier()
    {
        double[] z = [1, 2, 3, 4, 5, 6, 7, 8];
        var t = z.Select(v => 0.001 + v / 400.0).ToArray();
        var ols = LinearRegression.Fit(z, t)!;
        Assert.Equal(400.0, 1 / ols.Slope, 9);
        Assert.Equal(1.0, ols.RSquared, 12);
        t[6] += 0.004; // a gross mispick
        var olsBad = LinearRegression.Fit(z, t)!;
        var huber = LinearRegression.FitHuber(z, t)!;
        Assert.True(Math.Abs(1 / huber.Slope - 400) < Math.Abs(1 / olsBad.Slope - 400) / 3, $"Huber {1 / huber.Slope:0.0} vs OLS {1 / olsBad.Slope:0.0}");
        Assert.True(huber.Weights[6] < 0.3);
    }

    [Fact]
    public void RayTracersAgreeAndReduceToStraightRay()
    {
        // Single layer: Snell's-law tracer equals the straight ray R/v.
        var single = LayeredRayTracer.TravelTime([new LayeredRayTracer.Segment(10, 500)], 3);
        Assert.Equal(Math.Sqrt(109) / 500, single, 12);

        // Layered model: the engine's bisection tracer and the independent Fermat minimiser agree.
        var layers = SyntheticOptions.DefaultLayers;
        foreach (var depth in new[] { 1.0, 2.5, 7.0, 14.0, 23.0, 31.0 })
        {
            foreach (var offset in new[] { 0.0, 1.5, 3.0, 6.0 })
            {
                var path = LayeredRayTracer.Path(layers.Select(l => (l.Top, l.Bottom, l.Vs)).ToList(), 0, depth);
                var engine = LayeredRayTracer.TravelTime(path, offset);
                var fermat = FermatRayTimer.TravelTime(layers, depth, offset, l => l.Vs);
                Assert.True(Math.Abs(engine - fermat) < 1e-9, $"z={depth} x={offset}: {engine} vs {fermat}");
            }
        }
    }
}
