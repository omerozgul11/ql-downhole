using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Picking;
using DownholeStudio.Analysis.SWave;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Tests;

public sealed class PickerTests
{
    private const double Dt = 0.0000625;

    private static ProcessedTrace Onset(double onset, double noise, int seed = 1, double f = 150)
    {
        var rng = new Random(seed);
        var x = new double[3000];
        for (var i = 0; i < x.Length; i++)
        {
            var t = i * Dt;
            var tau = t - onset;
            var s = tau >= 0 ? Math.Sin(2 * Math.PI * f * tau) * Math.Exp(-tau * f / 1.2) * (1 - Math.Exp(-tau * f * 8)) : 0;
            x[i] = s + noise * (rng.NextDouble() - 0.5) * 2;
        }

        return new ProcessedTrace(x, Dt, 0, []);
    }

    [Theory]
    [InlineData(PickAlgorithm.Aic)]
    [InlineData(PickAlgorithm.StaLta)]
    [InlineData(PickAlgorithm.EnergyRatio)]
    public void PickersFindCleanOnset(PickAlgorithm algorithm)
    {
        const double onset = 0.05;
        var trace = Onset(onset, 0.002);
        var picker = PickerFactory.Create(algorithm);
        var r = picker.Pick(trace, 0.02, 0.12, new PickerSettings());
        Assert.NotNull(r.Time);
        // Tolerance: a quarter period of the 150 Hz wavelet (1.7 ms) for energy pickers, 0.5 ms for AIC.
        var tol = algorithm == PickAlgorithm.Aic ? 0.0005 : 0.0017;
        Assert.True(Math.Abs(r.Time!.Value - onset) < tol, $"{algorithm}: {r.Time * 1000:0.000} ms vs {onset * 1000} ms");
    }

    [Fact]
    public void AicIsAccurateToAFewSamplesOnModerateNoise()
    {
        var trace = Onset(0.0731, 0.05, 7);
        var r = new AicPicker().Pick(trace, 0.05, 0.12, new PickerSettings());
        Assert.InRange(r.Time!.Value - 0.0731, -6 * Dt, 6 * Dt);
    }

    [Fact]
    public void SnrIsHighForCleanOnsetAndLowForNoise()
    {
        var clean = Onset(0.05, 0.002);
        Assert.True(SnrEstimator.Compute(clean, 0.05, 0.01, 0.01) > 50);
        var noisy = Onset(0.05, 0.8, 3);
        Assert.True(SnrEstimator.Compute(noisy, 0.05, 0.01, 0.01) < 3);
    }

    [Fact]
    public void ConfidenceExplainsLowScore()
    {
        var est = new PickConfidenceEstimator();
        var low = est.Estimate(new ConfidenceEvidence { PickTime = 0.05, Snr = 1.5, Sharpness = 0.1, OtherAlgorithmTimes = [0.056], SampleInterval = Dt }, WaveType.P, new QcThresholds());
        Assert.Equal(ConfidenceLevel.Low, low.Level);
        Assert.Contains(low.Concerns, c => c.Name == "Signal-to-noise ratio" && c.Explanation.Contains("below"));
        var high = est.Estimate(new ConfidenceEvidence { PickTime = 0.05, Snr = 40, Sharpness = 0.9, OtherAlgorithmTimes = [0.0501], SampleInterval = Dt }, WaveType.P, new QcThresholds());
        Assert.Equal(ConfidenceLevel.High, high.Level);
    }

    [Fact]
    public void PolarizationFindsSensorAngle()
    {
        // Linear motion at 35° from X.
        var n = 400;
        var s = Enumerable.Range(0, n).Select(i => Math.Sin(i * 0.15) * Math.Exp(-Math.Pow((i - 200) / 60.0, 2))).ToArray();
        var phi = 35 * Math.PI / 180;
        var x = new ProcessedTrace(s.Select(v => v * Math.Cos(phi)).ToArray(), Dt, 0, []);
        var y = new ProcessedTrace(s.Select(v => v * Math.Sin(phi)).ToArray(), Dt, 0, []);
        var r = new CovariancePolarizationAnalyzer().Analyze(x, y, 0, n * Dt)!;
        Assert.Equal(35, r.AngleDegrees, 6);
        Assert.Equal(1, r.RadialEnergyFraction, 9);
        Assert.Equal(Math.Cos(phi) * Math.Cos(phi), r.XEnergyFraction, 9);
    }

    [Fact]
    public void WindowedCorrelationMeasuresShiftAndCoefficient()
    {
        var a = Onset(0.05, 0.0, f: 80);
        var b = Onset(0.0532, 0.0, f: 80);
        var r = new WindowedCrossCorrelationAnalyzer().Correlate(a, b, 0.045, 0.08, 0.01)!;
        Assert.Equal(0.0032, r.TimeShift, 4);
        Assert.True(r.Coefficient > 0.97, $"r = {r.Coefficient}");
    }

    [Fact]
    public void LayerSuggestionsRecoverBoundaries()
    {
        // Vertical times through 0–4 m @200, 4–12 m @400, 12–25 m @800.
        var obs = new List<StationObservation>();
        for (var z = 1.0; z <= 24; z += 1)
        {
            var t = Math.Min(z, 4) / 200 + Math.Clamp(z - 4, 0, 8) / 400 + Math.Max(0, z - 12) / 800;
            obs.Add(new StationObservation { StationId = Guid.NewGuid(), Wave = WaveType.S, Depth = z, Time = t, VerticalTime = t, SlantDistance = z, Included = true });
        }

        var suggestions = new LayerModeler().SuggestBoundaries(obs);
        Assert.Equal(2, suggestions.Count);
        Assert.InRange(suggestions[0].Depth, 3.5, 4.5);
        Assert.InRange(suggestions[1].Depth, 11.5, 12.5);
    }
}
