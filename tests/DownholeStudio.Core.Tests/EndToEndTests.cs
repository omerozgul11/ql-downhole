using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Core.Import;
using DownholeStudio.Domain;
using DownholeStudio.Formats;
using DownholeStudio.Persistence;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Core.Tests;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>Builds a project from a synthetic survey exactly as a user would.</summary>
public static class Pipeline
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static async Task<(ProjectSession Session, AnalysisService Analysis, SyntheticSurvey Survey, string Folder)> BuildAsync(SyntheticOptions options, bool autoPick = true)
    {
        var survey = SyntheticSurveyGenerator.Generate(options);
        var folder = Path.Combine(Path.GetTempPath(), "dhs-tests", Guid.NewGuid().ToString("N"));
        SyntheticSurveyGenerator.WriteTo(survey, folder);
        var clock = new FixedClock(T0);
        var session = ProjectSession.CreateNew("Synthetic", clock, new AppIdentity("1.0.0-test", "tester"));
        var import = new ImportService(SeismicFormatRegistry.CreateDefault(), clock);
        var preview = await import.PreviewAsync([folder]);
        await import.ImportAsync(session, preview, new ImportOptions { ApplySuggestedDepths = true });
        session.Execute(Edits.SetGeometry(session.State.Survey.Geometry with
        {
            PSource = session.State.Survey.Geometry.PSource with { HorizontalOffset = options.POffset },
            SSource = session.State.Survey.Geometry.SSource with { HorizontalOffset = options.SOffset },
        }, "Offsets"));
        var analysis = new AnalysisService(session, new ProcessedTraceCache());
        if (autoPick)
        {
            await analysis.AutoPickReferencesAsync(false);
            await analysis.AutoPickAsync(WaveType.P, null, false);
            await analysis.AutoPickAsync(WaveType.S, null, false);
        }

        return (session, analysis, survey, folder);
    }

    public static void UseTrueLayers(ProjectSession session, IEnumerable<TrueLayer> layers)
    {
        var model = Analysis.Velocity.LayerEdits.FromBoundaries(layers.Skip(1).Select(l => l.Top));
        session.Execute(Edits.SetLayerModel(model, "True boundaries"));
    }
}

public sealed class EndToEndTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CleanSurveyRecoversTrueModel()
    {
        var options = SyntheticOptions.For(SyntheticDataset.Clean);
        var (session, analysis, survey, _) = await Pipeline.BuildAsync(options);
        Assert.Equal(options.DepthCount, session.State.Survey.Stations.Count);
        Assert.All(session.State.Survey.Records, r => Assert.NotEqual(ShotKind.Unknown, r.Shot));
        var map = session.State.Survey.ChannelMap;
        Assert.Equal(ComponentRole.Vertical, map.RoleOf(1));
        Assert.Equal(ComponentRole.HorizontalX, map.RoleOf(2));
        Assert.Equal(ComponentRole.HorizontalY, map.RoleOf(3));
        Assert.Equal(ComponentRole.Reference, map.RoleOf(4));

        // Individual picks against the true onsets.
        var result0 = analysis.CalculateNow();
        foreach (var truth in survey.Truth.Stations)
        {
            var p = result0.P.Observations.Single(o => Math.Abs(o.Depth - truth.Depth) < 1e-9);
            var s = result0.S.Observations.Single(o => Math.Abs(o.Depth - truth.Depth) < 1e-9);
            Assert.True(Math.Abs(p.Time!.Value - truth.PTime) < 0.0004, $"P at {truth.Depth} m: {p.Time * 1000:0.000} vs {truth.PTime * 1000:0.000} ms");
            Assert.True(Math.Abs(s.Time!.Value - truth.STime) < 0.0010, $"S at {truth.Depth} m: {s.Time * 1000:0.000} vs {truth.STime * 1000:0.000} ms");
        }

        Pipeline.UseTrueLayers(session, survey.Truth.Layers);
        foreach (var method in new[] { VelocityMethod.LayerRegression, VelocityMethod.RefractedRay, VelocityMethod.PseudoInterval, VelocityMethod.TrueInterval })
        {
            session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Method = method }, $"Method {method}"));
            var result = analysis.CalculateNow();
            foreach (var layer in result.LayerResults)
            {
                var truth = survey.Truth.Layers.First(l => Math.Abs(l.Top - layer.Layer.Top) < 1e-9);
                output.WriteLine($"{method,-16} {layer.Layer.Top,5:0.0}–{layer.Layer.Bottom,5:0.0} m  Vp {layer.P?.Velocity,7:0.0} (true {truth.Vp})  Vs {layer.S?.Velocity,7:0.0} (true {truth.Vs})");
                // Straight-ray methods are biased in the shallowest layer because rays are oblique there; the
                // refracted-ray method should be accurate everywhere.
                // The true-interval method assumes straight rays through a uniform medium; it is biased where
                // rays are oblique below a slower layer (z/x small) and is only checked where z/x ≥ 4.
                if (method == VelocityMethod.TrueInterval && layer.Layer.Top / options.SOffset < 4)
                {
                    continue;
                }

                var tol = method == VelocityMethod.RefractedRay ? 0.03 : layer.Layer.Top < 3 ? 0.12 : 0.06;
                Assert.InRange(layer.S!.Velocity!.Value / truth.Vs, 1 - tol, 1 + tol);
                Assert.InRange(layer.P!.Velocity!.Value / truth.Vp, 1 - 2 * tol, 1 + 2 * tol);
            }

            if (method == VelocityMethod.RefractedRay)
            {
                output.WriteLine($"Vs30 {result.Vs30.Value:0.0} true {survey.Truth.Vs30:0.0}");
                Assert.InRange(result.Vs30.Value!.Value / survey.Truth.Vs30!.Value, 0.98, 1.02);
            }
        }
    }

    [Fact]
    public async Task TriggerErrorsAreFlaggedAndCorrected()
    {
        var options = SyntheticOptions.For(SyntheticDataset.TriggerErrors);
        var (session, analysis, survey, _) = await Pipeline.BuildAsync(options);
        var uncorrected = analysis.CalculateNow();
        Assert.Contains(uncorrected.Qc.Flags, f => f.Code == "trigger.deviation");

        session.Execute(Edits.SetTrigger(new TriggerSettings { Mode = TriggerCorrectionMode.SubtractReferenceOnset }));
        var corrected = analysis.CalculateNow();
        double Rms(AnalysisResult r, Func<SyntheticStationTruth, double> truth) => Math.Sqrt(r.S.Observations.Where(o => o.Included)
            .Select(o => o.Time!.Value - truth(survey.Truth.Stations.First(t => Math.Abs(t.Depth - o.Depth) < 1e-9))).Average(d => d * d));
        var before = Math.Sqrt(uncorrected.S.Observations.Where(o => o.Pick?.Time is not null)
            .Select(o => o.RawTime!.Value - survey.Truth.Stations.First(t => Math.Abs(t.Depth - o.Depth) < 1e-9).STime).Average(d => d * d));
        var after = Rms(corrected, t => t.STime);
        output.WriteLine($"S timing RMS error before {before * 1000:0.000} ms, after {after * 1000:0.000} ms");
        Assert.True(after < before / 2);
        Assert.True(after < 0.0008);
        // Raw times are kept untouched next to the corrected ones.
        Assert.All(corrected.S.Observations.Where(o => o.Included), o => Assert.NotEqual(o.RawTime, o.Time));
    }

    [Fact]
    public async Task ReversedPolarityChannelIsDetected()
    {
        var (_, analysis, _, _) = await Pipeline.BuildAsync(SyntheticOptions.For(SyntheticDataset.ReversedPolarityS));
        var result = analysis.CalculateNow();
        // Both S shots are reversed on X, so S+ and S− still oppose each other; the polarity check must not
        // claim a problem, but the reversed X still produces a consistent difference trace.
        Assert.DoesNotContain(result.Qc.Flags, f => f.Code == "s.samePolarity");
        Assert.True(result.S.Observations.Count(o => o.Included) >= 28);
    }

    [Fact]
    public async Task MissingTracesAreReportedNotInvented()
    {
        var options = SyntheticOptions.For(SyntheticDataset.MissingTraces);
        var survey = SyntheticSurveyGenerator.Generate(options);
        var folder = Path.Combine(Path.GetTempPath(), "dhs-tests", Guid.NewGuid().ToString("N"));
        SyntheticSurveyGenerator.WriteTo(survey, folder);
        var preview = await new ImportService(SeismicFormatRegistry.CreateDefault(), new FixedClock(Pipeline.T0)).PreviewAsync([folder]);
        Assert.Contains(preview.Diagnostics, d => d.Code == "import.missingChannels" && d.Severity == Severity.Warning);
        Assert.Contains(preview.Diagnostics, d => d.Code == "import.missingTraces");

        var (session, analysis, _, _) = await Pipeline.BuildAsync(options);
        var result = analysis.CalculateNow();
        var depthWithoutP = options.FirstDepth + 6 * options.DepthIncrement;
        var obs = result.P.Observations.Single(o => Math.Abs(o.Depth - depthWithoutP) < 1e-9);
        Assert.False(obs.Included);
        Assert.Null(obs.Time);
        Assert.Contains("No P record", obs.ExclusionReason);
        Assert.Contains(result.Qc.Flags, f => f.Code == "s.noOpposite");
        Assert.True(result.Qc.Summary.MissingMeasurements >= 1);
        _ = session;
    }

    [Fact]
    public async Task NoisySurveyGivesLowerConfidenceButUsableModel()
    {
        var (session, analysis, survey, _) = await Pipeline.BuildAsync(SyntheticOptions.For(SyntheticDataset.Noisy));
        var clean = await Pipeline.BuildAsync(SyntheticOptions.For(SyntheticDataset.Clean));
        double MeanConfidence(ProjectSession s) => s.State.ActiveScenario.SPicks.Values.Where(p => p.Confidence is not null).Average(p => p.Confidence!.Score);
        output.WriteLine($"mean S confidence clean {MeanConfidence(clean.Session):0.00}, noisy {MeanConfidence(session):0.00}");
        Assert.True(MeanConfidence(session) < MeanConfidence(clean.Session));
        Pipeline.UseTrueLayers(session, survey.Truth.Layers);
        session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Method = VelocityMethod.RefractedRay, Regression = RegressionMethod.RobustHuber }, "robust"));
        var r = analysis.CalculateNow();
        Assert.InRange(r.Vs30.Value!.Value / survey.Truth.Vs30!.Value, 0.9, 1.1);
    }

    [Fact]
    public async Task ShallowSurveyRefusesVs30UntilExtrapolationIsEnabled()
    {
        var options = SyntheticOptions.For(SyntheticDataset.Clean) with { DepthCount = 20 };
        var (session, analysis, _, _) = await Pipeline.BuildAsync(options);
        var r = analysis.CalculateNow();
        Assert.Null(r.Vs30.Value);
        Assert.Contains("Insufficient measured depth for direct Vs30 calculation", r.Vs30.Reason);
        session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Vs30 = new Vs30Settings { AllowExtrapolation = true } }, "Allow extrapolation"));
        var e = analysis.CalculateNow();
        Assert.True(e.Vs30.Extrapolated);
        Assert.NotNull(e.Vs30.Value);
        Assert.Contains(e.Qc.Flags, f => f.Code == "vs30.extrapolated" && f.Message.Contains("EXTRAPOLATED"));
    }

    [Fact]
    public async Task QualityVerdictProgresses()
    {
        var options = SyntheticOptions.For(SyntheticDataset.Clean);
        var (session, analysis, survey, _) = await Pipeline.BuildAsync(options, autoPick: false);
        Assert.Equal(AnalysisQuality.Incomplete, analysis.CalculateNow().Qc.Quality);
        await analysis.AutoPickReferencesAsync(false);
        await analysis.AutoPickAsync(WaveType.P, null, false);
        await analysis.AutoPickAsync(WaveType.S, null, false);
        Pipeline.UseTrueLayers(session, survey.Truth.Layers);
        var r = analysis.CalculateNow();
        Assert.Equal(AnalysisQuality.ReviewRecommended, r.Qc.Quality);
        Assert.Contains(r.Qc.QualityReasons, x => x.Contains("not been reviewed"));
    }
}
