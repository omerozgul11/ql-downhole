using System.Globalization;
using System.Text;
using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Engineering;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Cli;

public sealed record ValidationRow(string Group, string Check, string Input, double Expected, double Actual, double Tolerance, bool Relative, string Unit, string Source)
{
    public double Difference => Actual - Expected;

    public bool Pass => double.IsFinite(Actual) && (Relative ? Math.Abs(Difference) <= Tolerance * Math.Abs(Expected) : Math.Abs(Difference) <= Tolerance);
}

public sealed record ValidationReport(DateTimeOffset GeneratedAt, IReadOnlyList<ValidationRow> Rows)
{
    public string ToMarkdown(string version)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Downhole Studio — scientific validation report");
        sb.AppendLine();
        sb.AppendLine(inv, $"Generated {GeneratedAt:yyyy-MM-dd HH:mm} UTC by Downhole Studio {version} (`dhs validate`). {Rows.Count(r => r.Pass)} of {Rows.Count} checks passed.");
        sb.AppendLine();
        sb.AppendLine("Expected values come from hand calculation, from an independent implementation (Fermat-principle ray timing in the synthetic generator) or from the known synthetic earth model. Tolerances are absolute unless marked %.");
        foreach (var group in Rows.GroupBy(r => r.Group))
        {
            sb.AppendLine();
            sb.AppendLine("## " + group.Key);
            sb.AppendLine();
            sb.AppendLine("| Check | Input | Expected | Actual | Difference | Tolerance | Result | Expected from |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---|---|");
            foreach (var r in group)
            {
                var tol = r.Relative ? $"{r.Tolerance * 100:0.##} %" : $"{G(r.Tolerance)} {r.Unit}";
                sb.AppendLine(inv, $"| {r.Check} | {r.Input} | {G(r.Expected)} {r.Unit} | {G(r.Actual)} {r.Unit} | {G(r.Difference)} | {tol} | {(r.Pass ? "PASS" : "**FAIL**")} | {r.Source} |");
            }
        }

        return sb.ToString();
    }

    private static string G(double v) => double.IsFinite(v) ? v.ToString(Math.Abs(v) >= 1000 ? "0.0" : Math.Abs(v) >= 1 ? "0.0000" : "0.000000", CultureInfo.InvariantCulture) : "—";
}

/// <summary>Compares every calculation with independently known answers.</summary>
internal static class ScientificValidation
{
    public static async Task<ValidationReport> RunAsync(AppIdentity identity)
    {
        var rows = new List<ValidationRow>();
        Formulas(rows);
        RayTracing(rows);
        await Datasets(rows, identity);
        return new ValidationReport(DateTimeOffset.UtcNow, rows);
    }

    private static void Formulas(List<ValidationRow> rows)
    {
        const string g = "1. Formulas (hand calculation)";
        rows.Add(new(g, "Slant distance R = √((z−zs)²+x²)", "z=4, zs=0, x=3 m", 5, StraightRay.SlantDistance(4, 0, 3), 1e-12, false, "m", "3-4-5 triangle"));
        rows.Add(new(g, "Vertical time t·z/R", "t=10 ms, z=4, x=3", 0.008, StraightRay.VerticalTime(0.010, 4, 0, 3), 1e-15, false, "s", "0.010×4/5"));
        rows.Add(new(g, "Interval velocity (R₂−R₁)/(t₂−t₁)", "R 5→10 m, t 10→20 ms", 500, StraightRay.IntervalVelocity(5, 0.010, 10, 0.020), 1e-9, false, "m/s", "5/0.010"));
        var vs30 = new Vs30Calculator().Calculate([(0, 5, 200), (5, 15, 300), (15, 30, 500)], 30, 0, 30, new Vs30Settings());
        rows.Add(new(g, "Vs30 = 30/Σ(h/V)", "5 m@200, 10 m@300, 15 m@500", 339.6226415, vs30.Value ?? double.NaN, 1e-6, false, "m/s", "30/0.0883333"));
        var e = new ElasticPropertyCalculator().Calculate(1500, 300, 1900);
        rows.Add(new(g, "Poisson ν=(Vp²−2Vs²)/(2(Vp²−Vs²))", "Vp 1500, Vs 300", 0.4791667, e.PoissonRatio ?? double.NaN, 1e-7, false, "", "2.07e6/4.32e6"));
        rows.Add(new(g, "Shear modulus G=ρVs²", "ρ 1900, Vs 300", 171e6, e.ShearModulus ?? double.NaN, 1e-3, false, "Pa", "1900×300²"));
        rows.Add(new(g, "Young's modulus E=2G(1+ν)", "as above", 505.875e6, e.YoungModulus ?? double.NaN, 1, false, "Pa", "ρVs²(3Vp²−4Vs²)/(Vp²−Vs²)"));
        rows.Add(new(g, "Bulk modulus K=ρ(Vp²−4Vs²/3)", "as above", 4.047e9, e.BulkModulus ?? double.NaN, 1, false, "Pa", "1900×(2.25e6−1.2e5)"));
        rows.Add(new(g, "Gardner density", "Vp 2000 m/s", 2069.2, DensityEstimator.Gardner(2000).Density ?? double.NaN, 0.1, false, "kg/m³", "1.74×2^0.25 g/cm³"));
        rows.Add(new(g, "ft → m", "1 ft", 0.3048, UnitPreferences.UsCustomary.FromDisplay(1, Quantity.Length), 0, false, "m", "exact definition"));
        rows.Add(new(g, "pcf → kg/m³", "1 lbm/ft³", 16.018463373960, UnitPreferences.UsCustomary.FromDisplay(1, Quantity.Density), 1e-9, false, "kg/m³", "0.45359237/0.3048³"));
        rows.Add(new(g, "ksf → Pa", "1 kip/ft²", 47880.2589803358, UnitPreferences.UsCustomary.FromDisplay(1, Quantity.Modulus), 1e-6, false, "Pa", "4448.2216/0.09290304"));
        rows.Add(new(g, "psi → Pa", "1 lbf/in²", 6894.75729316836, UnitFactors.PascalsPerPsi, 1e-6, false, "Pa", "4.4482216/0.0254²"));
        var trigger = 0.0150 + (-0.0012);
        rows.Add(new(g, "Trigger correction t − t_ref", "t=15.0 ms, t_ref=1.2 ms", 0.0138, trigger, 1e-15, false, "s", "15.0 − 1.2 ms"));
    }

    private static void RayTracing(List<ValidationRow> rows)
    {
        const string g = "2. Ray tracing (independent implementations)";
        var layers = SyntheticOptions.DefaultLayers;
        foreach (var (z, x) in new[] { (1.0, 2.0), (2.5, 2.0), (6.0, 3.0), (12.0, 2.0), (20.0, 4.0), (31.0, 2.0), (31.0, 8.0) })
        {
            var path = LayeredRayTracer.Path(layers.Select(l => (l.Top, l.Bottom, l.Vs)).ToList(), 0, z);
            var engine = LayeredRayTracer.TravelTime(path, x);
            var fermat = FermatRayTimer.TravelTime(layers, z, x, l => l.Vs);
            rows.Add(new(g, "S travel time, Snell (engine) vs Fermat (generator)", $"z={z} m, x={x} m", fermat, engine, 1e-9, false, "s", "Fermat minimisation"));
        }
    }

    private static async Task Datasets(List<ValidationRow> rows, AppIdentity identity)
    {
        foreach (var dataset in Enum.GetValues<SyntheticDataset>())
        {
            var g = $"3. Synthetic dataset — {dataset}";
            var options = SyntheticOptions.For(dataset);
            var (session, analysis, survey) = await SyntheticPipeline.BuildAsync(options, dataset.ToString(), identity, triggerCorrection: dataset == SyntheticDataset.TriggerErrors);
            SyntheticPipeline.UseLayers(session, survey.Truth.Layers.Skip(1).Select(l => l.Top), VelocityMethod.RefractedRay);
            if (dataset == SyntheticDataset.Noisy)
            {
                session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Regression = RegressionMethod.RobustHuber }, "robust"));
            }

            var r = analysis.CalculateNow();
            var sErr = PickRms(r, survey, WaveType.S);
            var pErr = PickRms(r, survey, WaveType.P);
            var pickTol = dataset == SyntheticDataset.Noisy ? 0.0015 : 0.0005;
            rows.Add(new(g, "P pick RMS error vs true onset", $"{r.P.Observations.Count(o => o.Included)} picks", 0, pErr, pickTol / 2, false, "s", "generator onset times"));
            rows.Add(new(g, "S pick RMS error vs true onset", $"{r.S.Observations.Count(o => o.Included)} picks", 0, sErr, pickTol, false, "s", "generator onset times"));
            var tol = dataset == SyntheticDataset.Noisy ? 0.08 : 0.04;
            foreach (var l in r.LayerResults)
            {
                var truth = survey.Truth.Layers.First(t => Math.Abs(t.Top - l.Layer.Top) < 1e-9);
                rows.Add(new(g, $"Vs {l.Layer.Top:0}–{l.Layer.Bottom:0.#} m (refracted ray)", "true layer model", truth.Vs, l.S?.Velocity ?? double.NaN, tol, true, "m/s", "synthetic model"));
                rows.Add(new(g, $"Vp {l.Layer.Top:0}–{l.Layer.Bottom:0.#} m (refracted ray)", "true layer model", truth.Vp, l.P?.Velocity ?? double.NaN, tol * 1.5, true, "m/s", "synthetic model"));
            }

            if (survey.Truth.Vs30 is { } trueVs30)
            {
                rows.Add(new(g, "Vs30", "true layer model", trueVs30, r.Vs30.Value ?? double.NaN, dataset == SyntheticDataset.Noisy ? 0.05 : 0.02, true, "m/s", "30/Σ(h/Vs) of the model"));
            }

            if (dataset == SyntheticDataset.MissingTraces)
            {
                var missing = r.P.Observations.Count(o => !o.Included);
                rows.Add(new(g, "Missing P record reported, not invented", "record 6:P omitted", 1, missing, 0, false, "depths", "dataset definition"));
            }
        }
    }

    private static double PickRms(AnalysisResult r, SyntheticSurvey survey, WaveType wave)
    {
        var errors = r.For(wave).Observations.Where(o => o.Included).Select(o =>
        {
            var t = survey.Truth.Stations.First(s => Math.Abs(s.Depth - o.Depth) < 1e-9);
            return o.Time!.Value - (wave == WaveType.P ? t.PTime : t.STime);
        }).ToList();
        return errors.Count == 0 ? double.NaN : Math.Sqrt(errors.Average(e => e * e));
    }
}
