using System.Numerics;

namespace DownholeStudio.SignalProcessing;

/// <summary>One-sided amplitude spectrum with summary measures.</summary>
public sealed record AmplitudeSpectrum(double[] Frequencies, double[] Amplitudes)
{
    /// <summary>Frequency of the largest amplitude above <c>minFrequency</c>, Hz.</summary>
    public double DominantFrequency(double minFrequency = 0)
    {
        var best = -1;
        for (var i = 0; i < Frequencies.Length; i++)
        {
            if (Frequencies[i] >= minFrequency && (best < 0 || Amplitudes[i] > Amplitudes[best]))
            {
                best = i;
            }
        }

        return best < 0 ? double.NaN : Frequencies[best];
    }

    /// <summary>
    /// Contiguous band around the dominant peak where the amplitude stays at or above
    /// <paramref name="fractionOfPeak"/> × peak (0.7071 = −3 dB, 0.5 = −6 dB).
    /// </summary>
    public (double Low, double High) Bandwidth(double fractionOfPeak = 0.70710678118654757, double minFrequency = 0)
    {
        var dominant = DominantFrequency(minFrequency);
        if (double.IsNaN(dominant))
        {
            return (double.NaN, double.NaN);
        }

        var peakIndex = Array.IndexOf(Frequencies, dominant);
        var threshold = Amplitudes[peakIndex] * fractionOfPeak;
        var lo = peakIndex;
        while (lo > 0 && Amplitudes[lo - 1] >= threshold && Frequencies[lo - 1] >= minFrequency)
        {
            lo--;
        }

        var hi = peakIndex;
        while (hi < Amplitudes.Length - 1 && Amplitudes[hi + 1] >= threshold)
        {
            hi++;
        }

        return (Frequencies[lo], Frequencies[hi]);
    }
}

public static class Spectral
{
    /// <summary>
    /// Single-sided amplitude spectrum |X(f)|·2/N (DC and Nyquist not doubled) of a Hann-windowed
    /// copy of the input when <paramref name="hannWindow"/> is set (window gain is compensated).
    /// </summary>
    public static AmplitudeSpectrum Amplitude(ReadOnlySpan<double> x, double sampleInterval, bool hannWindow = true, int zeroPadTo = 0)
    {
        var n = x.Length;
        if (n < 2)
        {
            return new AmplitudeSpectrum([], []);
        }

        var w = new double[n];
        var gain = 0.0;
        for (var i = 0; i < n; i++)
        {
            var wi = hannWindow ? 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1))) : 1.0;
            w[i] = x[i] * wi;
            gain += wi;
        }

        gain /= n;
        var m = Math.Max(n, zeroPadTo);
        var spec = Fft.ForwardReal(w, m);
        var bins = m / 2 + 1;
        var freqs = new double[bins];
        var amps = new double[bins];
        var df = 1.0 / (m * sampleInterval);
        for (var k = 0; k < bins; k++)
        {
            freqs[k] = k * df;
            var scale = (k == 0 || (m % 2 == 0 && k == m / 2)) ? 1.0 : 2.0;
            amps[k] = spec[k].Magnitude * scale / (n * gain);
        }

        return new AmplitudeSpectrum(freqs, amps);
    }

    /// <summary>Analytic-signal envelope |x + iH{x}| computed with the FFT.</summary>
    public static double[] Envelope(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        if (n == 0)
        {
            return [];
        }

        var spec = Fft.ForwardReal(x);
        var h = new double[n];
        h[0] = 1;
        if (n % 2 == 0)
        {
            h[n / 2] = 1;
            for (var k = 1; k < n / 2; k++)
            {
                h[k] = 2;
            }
        }
        else
        {
            for (var k = 1; k <= (n - 1) / 2; k++)
            {
                h[k] = 2;
            }
        }

        for (var k = 0; k < n; k++)
        {
            spec[k] *= h[k];
        }

        Fft.Inverse(spec);
        var env = new double[n];
        for (var i = 0; i < n; i++)
        {
            env[i] = spec[i].Magnitude;
        }

        return env;
    }
}

/// <summary>Band-limited resampling by windowed-sinc interpolation.</summary>
public static class Resampler
{
    /// <summary>
    /// Resamples a trace to <paramref name="newInterval"/>. When downsampling, the interpolation
    /// kernel is also a low-pass filter at 0.9 × the new Nyquist frequency (anti-aliasing).
    /// The time of the first sample is preserved. Kernel: sinc windowed by a Blackman window over
    /// ±<paramref name="halfWidth"/> zero crossings.
    /// </summary>
    public static double[] Resample(ReadOnlySpan<double> x, double interval, double newInterval, int halfWidth = 16)
    {
        if (!(interval > 0) || !(newInterval > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(newInterval), "Sample intervals must be positive.");
        }

        var n = x.Length;
        if (n == 0)
        {
            return [];
        }

        var duration = (n - 1) * interval;
        var m = (int)Math.Floor(duration / newInterval + 1e-9) + 1;
        var y = new double[m];
        // Cut-off as a fraction of the input Nyquist frequency.
        var cutoff = newInterval > interval ? 0.9 * interval / newInterval : 1.0;
        var support = halfWidth / cutoff;
        for (var j = 0; j < m; j++)
        {
            var p = j * newInterval / interval;
            var lo = Math.Max(0, (int)Math.Ceiling(p - support));
            var hi = Math.Min(n - 1, (int)Math.Floor(p + support));
            double sum = 0, wsum = 0;
            for (var i = lo; i <= hi; i++)
            {
                var d = p - i;
                var k = cutoff * Sinc(cutoff * d) * Blackman(d / support);
                sum += x[i] * k;
                wsum += k;
            }

            // Normalising by the kernel sum keeps DC exact near the edges.
            y[j] = wsum != 0 ? sum / wsum : 0;
        }

        return y;
    }

    private static double Sinc(double v) => Math.Abs(v) < 1e-12 ? 1.0 : Math.Sin(Math.PI * v) / (Math.PI * v);

    private static double Blackman(double u)
    {
        if (Math.Abs(u) >= 1)
        {
            return 0;
        }

        var a = Math.PI * (u + 1);
        return 0.42 - 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2 * a);
    }
}

/// <summary>Result of a normalised cross-correlation.</summary>
/// <param name="Lags">Lag of each value, seconds (positive = second trace later than the first).</param>
/// <param name="Values">Normalised correlation at each lag (−1…1).</param>
/// <param name="BestLag">Lag of the maximum, refined to sub-sample precision by parabolic interpolation.</param>
/// <param name="BestValue">Correlation coefficient at the maximum.</param>
public sealed record CorrelationResult(double[] Lags, double[] Values, double BestLag, double BestValue);

public static class CrossCorrelation
{
    /// <summary>
    /// Normalised cross-correlation of b against a for lags in ±<paramref name="maxLag"/> seconds.
    /// The value at lag τ is Σ a(t)·b(t+τ) / √(Σa²·Σb²) over the overlap, so a positive best lag
    /// means b is delayed relative to a. With <paramref name="absolute"/> the maximum of |r| is
    /// used (polarity-insensitive) and the signed value is reported.
    /// </summary>
    public static CorrelationResult Compute(ReadOnlySpan<double> a, ReadOnlySpan<double> b, double sampleInterval, double maxLag, bool absolute = false)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 2)
        {
            return new CorrelationResult([], [], double.NaN, double.NaN);
        }

        var maxShift = Math.Clamp((int)Math.Round(maxLag / sampleInterval), 0, n - 1);
        var m = Fft.NextPowerOfTwo(2 * n);
        var fa = Fft.ForwardReal(a[..n], m);
        var fb = Fft.ForwardReal(b[..n], m);
        for (var k = 0; k < m; k++)
        {
            fa[k] = Complex.Conjugate(fa[k]) * fb[k];
        }

        Fft.Inverse(fa);
        var ea = Statistics.Energy(a[..n]);
        var eb = Statistics.Energy(b[..n]);
        var norm = Math.Sqrt(ea * eb);
        var count = 2 * maxShift + 1;
        var lags = new double[count];
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var shift = i - maxShift;
            var idx = shift >= 0 ? shift : m + shift;
            lags[i] = shift * sampleInterval;
            values[i] = norm > 0 ? fa[idx].Real / norm : 0;
        }

        var best = 0;
        for (var i = 1; i < count; i++)
        {
            var better = absolute ? Math.Abs(values[i]) > Math.Abs(values[best]) : values[i] > values[best];
            if (better)
            {
                best = i;
            }
        }

        var bestLag = lags[best];
        var bestValue = values[best];
        if (best > 0 && best < count - 1)
        {
            var y0 = values[best - 1];
            var y1 = values[best];
            var y2 = values[best + 1];
            var denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-15)
            {
                var delta = 0.5 * (y0 - y2) / denom;
                if (Math.Abs(delta) <= 1)
                {
                    bestLag += delta * sampleInterval;
                    bestValue = y1 - 0.25 * (y0 - y2) * delta;
                }
            }
        }

        return new CorrelationResult(lags, values, bestLag, bestValue);
    }
}

public static class Rotation
{
    /// <summary>
    /// Rotates horizontal components by θ (radians, from X towards Y):
    /// R = X·cosθ + Y·sinθ, T = −X·sinθ + Y·cosθ.
    /// </summary>
    public static (double[] Radial, double[] Transverse) Rotate(ReadOnlySpan<double> x, ReadOnlySpan<double> y, double theta)
    {
        var n = Math.Min(x.Length, y.Length);
        var c = Math.Cos(theta);
        var s = Math.Sin(theta);
        var r = new double[n];
        var t = new double[n];
        for (var i = 0; i < n; i++)
        {
            r[i] = x[i] * c + y[i] * s;
            t[i] = -x[i] * s + y[i] * c;
        }

        return (r, t);
    }
}
