using System.Numerics;

namespace DownholeStudio.SignalProcessing;

/// <summary>A second-order section b0..b2 / (1 + a1 z⁻¹ + a2 z⁻²) (a0 normalised to 1).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Complex frequency response at normalised angular frequency ω (rad/sample).</summary>
    public Complex Response(double omega)
    {
        var z1 = Complex.FromPolarCoordinates(1.0, -omega);
        var z2 = z1 * z1;
        return (B0 + B1 * z1 + B2 * z2) / (1.0 + A1 * z1 + A2 * z2);
    }

    /// <summary>DC gain H(z=1).</summary>
    public double DcGain => (B0 + B1 + B2) / (1.0 + A1 + A2);
}

/// <summary>A cascade of second-order sections.</summary>
public sealed class SosFilter
{
    public SosFilter(IReadOnlyList<Biquad> sections) => Sections = sections;

    public IReadOnlyList<Biquad> Sections { get; }

    /// <summary>|H| at frequency f (Hz) for sampling rate fs (Hz).</summary>
    public double Magnitude(double frequency, double sampleRate) => Response(frequency, sampleRate).Magnitude;

    public Complex Response(double frequency, double sampleRate)
    {
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var h = Complex.One;
        foreach (var s in Sections)
        {
            h *= s.Response(omega);
        }

        return h;
    }

    /// <summary>Causal filtering (direct form II transposed) from zero initial state.</summary>
    public double[] Apply(ReadOnlySpan<double> input)
    {
        var y = input.ToArray();
        foreach (var s in Sections)
        {
            FilterInPlace(s, y, 0.0, 0.0);
        }

        return y;
    }

    /// <summary>
    /// Zero-phase forward–backward filtering. The signal is extended at both ends by odd reflection
    /// and each pass starts from steady-state initial conditions, which suppresses edge transients.
    /// The effective amplitude response is |H|², and the phase response is zero. Note that a
    /// zero-phase filter is acausal: energy can appear slightly before a sharp onset.
    /// </summary>
    public double[] ApplyZeroPhase(ReadOnlySpan<double> input)
    {
        var n = input.Length;
        if (n == 0)
        {
            return [];
        }

        var pad = Math.Min(n - 1, 3 * (2 * Sections.Count + 1));
        var ext = new double[n + 2 * pad];
        for (var i = 0; i < pad; i++)
        {
            ext[i] = 2.0 * input[0] - input[pad - i];
            ext[n + pad + i] = 2.0 * input[n - 1] - input[n - 2 - i];
        }

        input.CopyTo(ext.AsSpan(pad));
        FilterWithSteadyState(ext);
        Array.Reverse(ext);
        FilterWithSteadyState(ext);
        Array.Reverse(ext);
        return ext.AsSpan(pad, n).ToArray();
    }

    private void FilterWithSteadyState(double[] x)
    {
        var level = x[0];
        foreach (var s in Sections)
        {
            // Steady-state state for a constant input `level` (see docs/algorithms/signal-processing.md).
            var gain = s.DcGain;
            var z2 = (s.B2 - s.A2 * gain) * level;
            var z1 = (s.B1 - s.A1 * gain) * level + z2;
            FilterInPlace(s, x, z1, z2);
            level *= gain;
        }
    }

    private static void FilterInPlace(Biquad s, double[] x, double z1, double z2)
    {
        for (var i = 0; i < x.Length; i++)
        {
            var xi = x[i];
            var yi = s.B0 * xi + z1;
            z1 = s.B1 * xi - s.A1 * yi + z2;
            z2 = s.B2 * xi - s.A2 * yi;
            x[i] = yi;
        }
    }
}

/// <summary>
/// Digital Butterworth designs from the analogue prototype through the bilinear transform with
/// frequency pre-warping, returned as second-order sections.
/// </summary>
public static class Butterworth
{
    public static SosFilter LowPass(int order, double cornerHz, double sampleRate)
    {
        Validate(order, sampleRate, cornerHz);
        var fs2 = 2.0 * sampleRate;
        var wc = Prewarp(cornerHz, sampleRate);
        var poles = Prototype(order).Select(p => p * wc).Select(p => Bilinear(p, fs2)).ToList();
        var zeros = Enumerable.Repeat(new Complex(-1, 0), order).ToList();
        return Normalise(Assemble(zeros, poles), 0.0, sampleRate);
    }

    public static SosFilter HighPass(int order, double cornerHz, double sampleRate)
    {
        Validate(order, sampleRate, cornerHz);
        var fs2 = 2.0 * sampleRate;
        var wc = Prewarp(cornerHz, sampleRate);
        var poles = Prototype(order).Select(p => wc / p).Select(p => Bilinear(p, fs2)).ToList();
        var zeros = Enumerable.Repeat(new Complex(1, 0), order).ToList();
        return Normalise(Assemble(zeros, poles), sampleRate / 2.0, sampleRate);
    }

    /// <summary>Band-pass of prototype order <paramref name="order"/> (2×order poles in total).</summary>
    public static SosFilter BandPass(int order, double lowHz, double highHz, double sampleRate)
    {
        Validate(order, sampleRate, lowHz);
        Validate(order, sampleRate, highHz);
        if (lowHz >= highHz)
        {
            throw new ArgumentException("The low corner must be below the high corner.");
        }

        var fs2 = 2.0 * sampleRate;
        var w1 = Prewarp(lowHz, sampleRate);
        var w2 = Prewarp(highHz, sampleRate);
        var bw = w2 - w1;
        var w0Sq = w1 * w2;
        var poles = new List<Complex>();
        foreach (var p in Prototype(order))
        {
            // Roots of s² − p·B·s + ω0² = 0 (low-pass → band-pass transformation).
            var pb = p * bw;
            var disc = Complex.Sqrt(pb * pb - 4.0 * w0Sq);
            poles.Add(Bilinear((pb + disc) / 2.0, fs2));
            poles.Add(Bilinear((pb - disc) / 2.0, fs2));
        }

        var zeros = new List<Complex>();
        for (var i = 0; i < order; i++)
        {
            zeros.Add(new Complex(1, 0));
            zeros.Add(new Complex(-1, 0));
        }

        // Reference the gain at the pre-warped geometric centre mapped back to digital frequency.
        var digitalCentre = Math.Atan(Math.Sqrt(w0Sq) / fs2) * sampleRate / Math.PI;
        return Normalise(Assemble(zeros, poles), digitalCentre, sampleRate);
    }

    /// <summary>Second-order IIR notch; Q = centre frequency / −3 dB bandwidth.</summary>
    public static SosFilter Notch(double frequencyHz, double q, double sampleRate)
    {
        Validate(2, sampleRate, frequencyHz);
        if (q <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(q), "The quality factor must be positive.");
        }

        // Bilinear-transform notch whose −3 dB bandwidth is exactly f0/Q in the digital domain.
        var w0 = 2.0 * Math.PI * frequencyHz / sampleRate;
        var beta = Math.Tan(w0 / q / 2.0);
        var g = 1.0 / (1.0 + beta);
        var cos = Math.Cos(w0);
        return new SosFilter([new Biquad(g, -2.0 * g * cos, g, -2.0 * g * cos, 2.0 * g - 1.0)]);
    }

    /// <summary>Analogue Butterworth prototype poles (unit cut-off, left half-plane).</summary>
    public static IReadOnlyList<Complex> Prototype(int order)
    {
        var poles = new Complex[order];
        for (var k = 0; k < order; k++)
        {
            var theta = Math.PI * (2.0 * k + order + 1) / (2.0 * order);
            poles[k] = Complex.FromPolarCoordinates(1.0, theta);
        }

        return poles;
    }

    private static void Validate(int order, double sampleRate, double frequency)
    {
        if (order is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Filter order must be between 1 and 12.");
        }

        if (!(sampleRate > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sampling rate must be positive.");
        }

        if (!(frequency > 0) || frequency >= sampleRate / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency),
                $"Corner frequency {frequency:0.###} Hz must be between 0 and the Nyquist frequency ({sampleRate / 2.0:0.###} Hz).");
        }
    }

    private static double Prewarp(double hz, double sampleRate) => 2.0 * sampleRate * Math.Tan(Math.PI * hz / sampleRate);

    private static Complex Bilinear(Complex s, double fs2) => (fs2 + s) / (fs2 - s);

    private static List<Biquad> Assemble(List<Complex> zeros, List<Complex> poles)
    {
        // Pair complex-conjugate poles; real poles are paired with each other.
        var pairs = new List<(Complex A, Complex? B)>();
        var reals = poles.Where(p => Math.Abs(p.Imaginary) < 1e-12 * Math.Max(1.0, p.Magnitude)).ToList();
        var complexes = poles.Where(p => p.Imaginary > 1e-12 * Math.Max(1.0, p.Magnitude)).OrderBy(p => p.Magnitude).ToList();
        foreach (var c in complexes)
        {
            pairs.Add((c, Complex.Conjugate(c)));
        }

        for (var i = 0; i < reals.Count; i += 2)
        {
            pairs.Add((reals[i], i + 1 < reals.Count ? reals[i + 1] : null));
        }

        var plus = new Queue<Complex>(zeros.Where(z => z.Real > 0));
        var minus = new Queue<Complex>(zeros.Where(z => z.Real < 0));
        var sections = new List<Biquad>();
        foreach (var (a, b) in pairs)
        {
            double a1, a2;
            if (b is null)
            {
                a1 = -a.Real;
                a2 = 0.0;
            }
            else
            {
                var sum = a + b.Value;
                var prod = a * b.Value;
                a1 = -sum.Real;
                a2 = prod.Real;
            }

            var order = b is null ? 1 : 2;
            var zs = new List<Complex>();
            for (var i = 0; i < order; i++)
            {
                // Mix zeros at +1 and −1 within a section for band-pass designs.
                if (plus.Count > 0 && (minus.Count == 0 || (zs.Count > 0 && zs[0].Real < 0)))
                {
                    zs.Add(plus.Dequeue());
                }
                else if (minus.Count > 0)
                {
                    zs.Add(minus.Dequeue());
                }
            }

            double b0 = 1, b1, b2;
            if (zs.Count == 2)
            {
                b1 = -(zs[0] + zs[1]).Real;
                b2 = (zs[0] * zs[1]).Real;
            }
            else if (zs.Count == 1)
            {
                b1 = -zs[0].Real;
                b2 = 0;
            }
            else
            {
                b1 = 0;
                b2 = 0;
            }

            sections.Add(new Biquad(b0, b1, b2, a1, a2));
        }

        return sections;
    }

    private static SosFilter Normalise(List<Biquad> sections, double referenceHz, double sampleRate)
    {
        var filter = new SosFilter(sections);
        var gain = filter.Magnitude(referenceHz, sampleRate);
        if (!(gain > 0) || !double.IsFinite(gain))
        {
            throw new InvalidOperationException("Filter design failed: reference gain is not finite.");
        }

        var first = sections[0];
        var k = 1.0 / gain;
        sections[0] = first with { B0 = first.B0 * k, B1 = first.B1 * k, B2 = first.B2 * k };
        return new SosFilter(sections);
    }
}
