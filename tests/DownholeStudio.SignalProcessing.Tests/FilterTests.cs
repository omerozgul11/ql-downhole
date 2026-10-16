using System.Numerics;
using System.Text.Json;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.SignalProcessing.Tests;

/// <summary>Compares filter designs and filtering against SciPy 1.18 (generate_golden_scipy.py).</summary>
public sealed class FilterTests
{
    private const double Fs = 1000.0;
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden-scipy.json"))).RootElement;

    private static double[] Arr(string name) => Golden.GetProperty(name).EnumerateArray().Select(e => e.GetDouble()).ToArray();

    private static void AssertResponse(SosFilter filter, string key, string freqKey = "freqs", double tol = 1e-9)
    {
        var freqs = Arr(freqKey);
        var expected = Arr(key);
        for (var i = 0; i < freqs.Length; i++)
        {
            var actual = filter.Magnitude(freqs[i], Fs);
            Assert.True(Math.Abs(actual - expected[i]) <= tol * Math.Max(1, expected[i]),
                $"{key} at {freqs[i]} Hz: expected {expected[i]:R}, actual {actual:R}");
        }
    }

    [Fact]
    public void LowPassMatchesScipy() => AssertResponse(Butterworth.LowPass(4, 100, Fs), "lp4_100");

    [Fact]
    public void OddOrderLowPassMatchesScipy() => AssertResponse(Butterworth.LowPass(3, 50, Fs), "lp3_50");

    [Fact]
    public void HighPassMatchesScipy() => AssertResponse(Butterworth.HighPass(4, 10, Fs), "hp4_10");

    [Fact]
    public void BandPassMatchesScipy() => AssertResponse(Butterworth.BandPass(4, 8, 80, Fs), "bp4_8_80");

    [Fact]
    public void OddOrderBandPassMatchesScipy() => AssertResponse(Butterworth.BandPass(3, 20, 120, Fs), "bp3_20_120");

    [Fact]
    public void NotchMatchesScipy() => AssertResponse(Butterworth.Notch(60, 30, Fs), "notch60", "notch_freqs");

    [Fact]
    public void CornerFrequencyIsMinus3dB()
    {
        Assert.Equal(1 / Math.Sqrt(2), Butterworth.LowPass(6, 120, Fs).Magnitude(120, Fs), 9);
        Assert.Equal(1 / Math.Sqrt(2), Butterworth.HighPass(2, 15, Fs).Magnitude(15, Fs), 9);
    }

    [Fact]
    public void ZeroPhaseFilteringMatchesScipySosfiltfilt()
    {
        var x = Arr("x");
        AssertSeries(Arr("filtfilt_bp4_8_80"), Butterworth.BandPass(4, 8, 80, Fs).ApplyZeroPhase(x), 1e-8);
        AssertSeries(Arr("filtfilt_lp4_100"), Butterworth.LowPass(4, 100, Fs).ApplyZeroPhase(x), 1e-8);
    }

    [Fact]
    public void CausalFilteringMatchesScipySosfilt()
    {
        var x = Arr("x");
        AssertSeries(Arr("sosfilt_hp4_10"), Butterworth.HighPass(4, 10, Fs).Apply(x), 1e-9);
    }

    [Fact]
    public void LinearDetrendMatchesScipy() => AssertSeries(Arr("detrend"), TimeDomain.LinearDetrend(Arr("x")), 1e-12);

    [Fact]
    public void ZeroPhaseFilterDoesNotShiftSymmetricPulse()
    {
        // A symmetric pulse filtered forward and backward keeps its peak at the same sample.
        var x = new double[512];
        for (var i = 0; i < x.Length; i++)
        {
            var t = (i - 256) / Fs;
            x[i] = Math.Exp(-t * t / (2 * 0.004 * 0.004));
        }

        var y = Butterworth.LowPass(4, 60, Fs).ApplyZeroPhase(x);
        Assert.Equal(256, Array.IndexOf(y, y.Max()));
    }

    [Fact]
    public void RejectsCornerAboveNyquist()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Butterworth.LowPass(4, 600, Fs));
        Assert.Contains("Nyquist", ex.Message);
    }

    private static void AssertSeries(double[] expected, double[] actual, double tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        var scale = expected.Max(Math.Abs);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tol * scale, $"index {i}: expected {expected[i]:R}, actual {actual[i]:R}");
        }
    }
}

public sealed class FftTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(97)]
    [InlineData(1000)]
    public void MatchesDirectDft(int n)
    {
        var rng = new Random(n);
        var x = Enumerable.Range(0, n).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var fast = x.ToArray();
        Fft.Forward(fast);
        for (var k = 0; k < n; k += Math.Max(1, n / 17))
        {
            var sum = Complex.Zero;
            for (var j = 0; j < n; j++)
            {
                sum += x[j] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * j / n);
            }

            Assert.True((sum - fast[k]).Magnitude < 1e-9 * n, $"bin {k}");
        }

        Fft.Inverse(fast);
        for (var j = 0; j < n; j++)
        {
            Assert.True((fast[j] - x[j]).Magnitude < 1e-12 * n);
        }
    }

    [Fact]
    public void SpectrumFindsDominantFrequencyAndAmplitude()
    {
        const double dt = 0.0005;
        var x = Enumerable.Range(0, 2000).Select(i => 3.0 * Math.Sin(2 * Math.PI * 100 * i * dt)).ToArray();
        var spectrum = Spectral.Amplitude(x, dt, hannWindow: false);
        Assert.Equal(100, spectrum.DominantFrequency(), 6);
        Assert.Equal(3.0, spectrum.Amplitudes.Max(), 6);
        var (lo, hi) = spectrum.Bandwidth();
        Assert.True(lo <= 100 && hi >= 100);
    }

    [Fact]
    public void EnvelopeOfModulatedSineIsTheModulation()
    {
        const int n = 4096;
        var x = Enumerable.Range(0, n).Select(i => (1 + 0.5 * Math.Cos(2 * Math.PI * 2 * i / n)) * Math.Sin(2 * Math.PI * 200 * i / n)).ToArray();
        var env = Spectral.Envelope(x);
        for (var i = 200; i < n - 200; i += 97)
        {
            Assert.Equal(1 + 0.5 * Math.Cos(2 * Math.PI * 2 * i / n), env[i], 3);
        }
    }
}

public sealed class TimeDomainTests
{
    [Fact]
    public void RemoveMeanZeroesTheMean()
    {
        var y = TimeDomain.RemoveMean([1, 2, 3, 4, 10]);
        Assert.Equal(0, y.Sum(), 12);
        Assert.Equal(-3, y[0], 12);
    }

    [Fact]
    public void InputIsNeverModified()
    {
        double[] x = [1, 2, 3, 4];
        _ = TimeDomain.RemoveMean(x);
        _ = TimeDomain.Taper(x, 0.25);
        _ = TimeDomain.Agc(x, 2);
        _ = Butterworth.LowPass(2, 100, 1000).ApplyZeroPhase(x);
        Assert.Equal([1, 2, 3, 4], x);
    }

    [Fact]
    public void WholeSampleShiftIsExact()
    {
        double[] x = [0, 1, 2, 3, 4, 5];
        Assert.Equal([0, 0, 0, 1, 2, 3], TimeDomain.Shift(x, 0.002, 0.001));
        Assert.Equal([2, 3, 4, 5, 0, 0], TimeDomain.Shift(x, -0.002, 0.001));
    }

    [Fact]
    public void FractionalShiftMovesBandLimitedPulse()
    {
        const double dt = 0.001;
        var x = Enumerable.Range(0, 400).Select(i => Math.Exp(-Math.Pow((i - 150) * dt / 0.006, 2))).ToArray();
        var shifted = TimeDomain.Shift(x, 0.0025, dt);
        var expected = Enumerable.Range(0, 400).Select(i => Math.Exp(-Math.Pow((i - 152.5) * dt / 0.006, 2))).ToArray();
        for (var i = 100; i < 220; i++)
        {
            Assert.Equal(expected[i], shifted[i], 4);
        }
    }

    [Fact]
    public void AgcEqualisesAmplitude()
    {
        var x = Enumerable.Range(0, 2000).Select(i => (i < 1000 ? 0.01 : 10.0) * Math.Sin(i * 0.3)).ToArray();
        var y = TimeDomain.Agc(x, 101);
        Assert.InRange(Statistics.Rms(y.AsSpan(200, 600)), 0.9, 1.1);
        Assert.InRange(Statistics.Rms(y.AsSpan(1200, 600)), 0.9, 1.1);
    }

    [Fact]
    public void TaperZeroesEnds()
    {
        var y = TimeDomain.Taper(Enumerable.Repeat(1.0, 100).ToArray(), 0.1);
        Assert.Equal(0, y[0]);
        Assert.Equal(0, y[99]);
        Assert.Equal(1, y[50]);
    }

    [Fact]
    public void HalfDifferenceKeepsReversedSignalAndCancelsCommonSignal()
    {
        double[] s = [0, 1, -1, 0.5];
        double[] common = [2, 2, 2, 2];
        var a = s.Zip(common, (p, q) => p + q).ToArray();
        var b = s.Zip(common, (p, q) => -p + q).ToArray();
        Assert.Equal(s, TimeDomain.HalfDifference(a, b));
        Assert.Equal(common, TimeDomain.HalfSum(a, b));
    }
}

public sealed class CorrelationAndResamplingTests
{
    [Fact]
    public void CrossCorrelationRecoversSubSampleDelay()
    {
        const double dt = 0.0005;
        double Pulse(double t) => Math.Exp(-Math.Pow(t / 0.004, 2)) * Math.Cos(2 * Math.PI * 80 * t);
        var a = Enumerable.Range(0, 800).Select(i => Pulse(i * dt - 0.1)).ToArray();
        var b = Enumerable.Range(0, 800).Select(i => Pulse(i * dt - 0.1 - 0.00312)).ToArray();
        var r = CrossCorrelation.Compute(a, b, dt, 0.02);
        Assert.Equal(0.00312, r.BestLag, 4);
        Assert.True(r.BestValue > 0.98);
    }

    [Fact]
    public void AbsoluteCorrelationDetectsReversedPolarity()
    {
        var a = Enumerable.Range(0, 300).Select(i => Math.Sin(i * 0.2) * Math.Exp(-Math.Pow((i - 150) / 30.0, 2))).ToArray();
        var b = a.Select(v => -v).ToArray();
        var r = CrossCorrelation.Compute(a, b, 0.001, 0.01, absolute: true);
        Assert.True(r.BestValue < -0.99);
        Assert.Equal(0, r.BestLag, 6);
    }

    [Fact]
    public void ResamplingPreservesBandLimitedSignal()
    {
        const double dt = 0.0002;
        var x = Enumerable.Range(0, 2500).Select(i => Math.Sin(2 * Math.PI * 40 * i * dt)).ToArray();
        var y = Resampler.Resample(x, dt, 0.001);
        Assert.Equal(500, y.Length);
        for (var j = 50; j < 450; j++)
        {
            Assert.Equal(Math.Sin(2 * Math.PI * 40 * j * 0.001), y[j], 3);
        }
    }

    [Fact]
    public void DownsamplingSuppressesAliasedEnergy()
    {
        // 450 Hz is above the new Nyquist frequency (250 Hz) and must not alias to 50 Hz.
        const double dt = 0.0001;
        var x = Enumerable.Range(0, 10000).Select(i => Math.Sin(2 * Math.PI * 450 * i * dt)).ToArray();
        var y = Resampler.Resample(x, dt, 0.002);
        Assert.True(Statistics.Rms(y.AsSpan(50, y.Length - 100)) < 0.02);
    }

    [Fact]
    public void RotationByNinetyDegreesSwapsComponents()
    {
        double[] x = [1, 0, 2];
        double[] y = [0, 1, 3];
        var (r, t) = Rotation.Rotate(x, y, Math.PI / 2);
        Assert.Equal(y[2], r[2], 12);
        Assert.Equal(-x[2], t[2], 12);
    }
}
