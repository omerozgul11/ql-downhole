namespace DownholeStudio.SignalProcessing;

/// <summary>Elementary time-domain operations. All return new arrays; inputs are never modified.</summary>
public static class TimeDomain
{
    public static double[] RemoveMean(ReadOnlySpan<double> x)
    {
        var mean = Statistics.Mean(x);
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            y[i] = x[i] - mean;
        }

        return y;
    }

    /// <summary>Removes the least-squares straight line a + b·i.</summary>
    public static double[] LinearDetrend(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        var y = new double[n];
        if (n < 2)
        {
            return n == 1 ? [0.0] : y;
        }

        // Centred index makes the normal equations diagonal.
        var mid = (n - 1) / 2.0;
        double sxy = 0, sxx = 0, sy = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i - mid;
            sxy += t * x[i];
            sxx += t * t;
            sy += x[i];
        }

        var slope = sxy / sxx;
        var intercept = sy / n;
        for (var i = 0; i < n; i++)
        {
            y[i] = x[i] - (intercept + slope * (i - mid));
        }

        return y;
    }

    /// <summary>Tapers <paramref name="fraction"/> of the samples at each end with a Hann (raised-cosine) ramp.</summary>
    public static double[] Taper(ReadOnlySpan<double> x, double fraction, bool hann = true)
    {
        if (fraction is < 0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "Taper fraction must be between 0 and 0.5.");
        }

        var n = x.Length;
        var y = x.ToArray();
        var m = (int)Math.Floor(fraction * n);
        if (m < 1)
        {
            return y;
        }

        for (var i = 0; i < m; i++)
        {
            var w = hann
                ? 0.5 * (1.0 - Math.Cos(Math.PI * i / m))
                : Math.Sin(0.5 * Math.PI * i / m);
            y[i] *= w;
            y[n - 1 - i] *= w;
        }

        return y;
    }

    public static double[] Scale(ReadOnlySpan<double> x, double factor)
    {
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            y[i] = x[i] * factor;
        }

        return y;
    }

    /// <summary>Divides by the peak absolute amplitude. All-zero traces are returned unchanged.</summary>
    public static double[] NormalizePeak(ReadOnlySpan<double> x)
    {
        var peak = Statistics.MaxAbs(x);
        return peak > 0 ? Scale(x, 1.0 / peak) : x.ToArray();
    }

    public static double[] NormalizeRms(ReadOnlySpan<double> x)
    {
        var rms = Statistics.Rms(x);
        return rms > 0 ? Scale(x, 1.0 / rms) : x.ToArray();
    }

    /// <summary>Scales the trace so the RMS amplitude in samples [start, end) equals one.</summary>
    public static double[] Balance(ReadOnlySpan<double> x, int start, int end)
    {
        start = Math.Clamp(start, 0, x.Length);
        end = Math.Clamp(end, start, x.Length);
        var rms = Statistics.Rms(x[start..end]);
        return rms > 0 ? Scale(x, 1.0 / rms) : x.ToArray();
    }

    /// <summary>
    /// Automatic gain control: each sample is divided by the RMS amplitude in a centred window of
    /// <paramref name="window"/> samples. AGC destroys true relative amplitudes and is a display aid.
    /// </summary>
    public static double[] Agc(ReadOnlySpan<double> x, int window)
    {
        var n = x.Length;
        var y = new double[n];
        if (n == 0)
        {
            return y;
        }

        window = Math.Clamp(window, 1, n);
        var half = window / 2;
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + x[i] * x[i];
        }

        var globalRms = Math.Sqrt(prefix[n] / n);
        var floor = globalRms * 1e-6;
        for (var i = 0; i < n; i++)
        {
            var a = Math.Max(0, i - half);
            var b = Math.Min(n, i + half + 1);
            var rms = Math.Sqrt((prefix[b] - prefix[a]) / (b - a));
            y[i] = rms > floor ? x[i] / rms : 0.0;
        }

        return y;
    }

    public static double[] Invert(ReadOnlySpan<double> x) => Scale(x, -1.0);

    /// <summary>
    /// Shifts a waveform by <paramref name="shift"/> seconds (positive = later). Whole-sample shifts
    /// are exact; fractional shifts use a band-limited Fourier phase shift on a zero-padded copy so
    /// the waveform does not wrap around. Samples shifted in from outside the record are zero.
    /// </summary>
    public static double[] Shift(ReadOnlySpan<double> x, double shift, double sampleInterval)
    {
        var n = x.Length;
        var samples = shift / sampleInterval;
        var whole = Math.Round(samples);
        if (Math.Abs(samples - whole) < 1e-9)
        {
            var k = (int)whole;
            var y = new double[n];
            for (var i = 0; i < n; i++)
            {
                var j = i - k;
                if (j >= 0 && j < n)
                {
                    y[i] = x[j];
                }
            }

            return y;
        }

        var m = Fft.NextPowerOfTwo(2 * n + (int)Math.Ceiling(Math.Abs(samples)) + 2);
        var spec = Fft.ForwardReal(x, m);
        for (var k = 0; k < m; k++)
        {
            // Signed frequency index so the phase ramp is odd-symmetric (real output).
            var f = k <= m / 2 ? k : k - m;
            var phase = -2.0 * Math.PI * f * samples / m;
            if (m % 2 == 0 && k == m / 2)
            {
                // Nyquist bin: keep it real.
                spec[k] *= Math.Cos(phase);
            }
            else
            {
                spec[k] *= System.Numerics.Complex.FromPolarCoordinates(1.0, phase);
            }
        }

        Fft.Inverse(spec);
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = i - samples;
            result[i] = t < -0.5 || t > n - 0.5 ? 0.0 : spec[i].Real;
        }

        return result;
    }

    /// <summary>Linear interpolation of a uniformly sampled trace at time <paramref name="t"/>.</summary>
    public static double InterpolateAt(ReadOnlySpan<double> x, double sampleInterval, double t, double delay = 0)
    {
        var p = (t - delay) / sampleInterval;
        if (p <= 0)
        {
            return x.Length > 0 ? x[0] : 0;
        }

        if (p >= x.Length - 1)
        {
            return x.Length > 0 ? x[^1] : 0;
        }

        var i = (int)Math.Floor(p);
        var f = p - i;
        return x[i] * (1 - f) + x[i + 1] * f;
    }

    /// <summary>Sample-by-sample sum of equally long traces divided by their count.</summary>
    public static double[] Stack(IReadOnlyList<double[]> traces)
    {
        if (traces.Count == 0)
        {
            return [];
        }

        var n = traces.Min(t => t.Length);
        var y = new double[n];
        foreach (var t in traces)
        {
            for (var i = 0; i < n; i++)
            {
                y[i] += t[i];
            }
        }

        for (var i = 0; i < n; i++)
        {
            y[i] /= traces.Count;
        }

        return y;
    }

    /// <summary>(a − b)/2: enhances signals that reverse polarity between opposite shots (S waves).</summary>
    public static double[] HalfDifference(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            y[i] = 0.5 * (a[i] - b[i]);
        }

        return y;
    }

    /// <summary>(a + b)/2: enhances signals with the same polarity in both shots (P-type energy).</summary>
    public static double[] HalfSum(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            y[i] = 0.5 * (a[i] + b[i]);
        }

        return y;
    }
}
