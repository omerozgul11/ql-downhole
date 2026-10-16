using System.Numerics;

namespace DownholeStudio.SignalProcessing;

/// <summary>
/// Discrete Fourier transform. Power-of-two lengths use an iterative radix-2 Cooley–Tukey
/// transform; other lengths use Bluestein's chirp-z algorithm, so any length is exact (no padding
/// is introduced unless the caller asks for it).
/// Convention: X[k] = Σ x[n]·exp(−2πi·kn/N); the inverse divides by N.
/// </summary>
public static class Fft
{
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    public static int NextPowerOfTwo(int n)
    {
        if (n < 1)
        {
            return 1;
        }

        var p = 1;
        while (p < n)
        {
            p <<= 1;
        }

        return p;
    }

    /// <summary>In-place forward transform.</summary>
    public static void Forward(Complex[] data) => Transform(data, inverse: false);

    /// <summary>In-place inverse transform (scaled by 1/N).</summary>
    public static void Inverse(Complex[] data)
    {
        Transform(data, inverse: true);
        var scale = 1.0 / data.Length;
        for (var i = 0; i < data.Length; i++)
        {
            data[i] *= scale;
        }
    }

    /// <summary>Forward transform of a real signal (returns all N bins).</summary>
    public static Complex[] ForwardReal(ReadOnlySpan<double> signal, int length = 0)
    {
        var n = Math.Max(length, signal.Length);
        var data = new Complex[n];
        for (var i = 0; i < signal.Length; i++)
        {
            data[i] = new Complex(signal[i], 0);
        }

        Forward(data);
        return data;
    }

    private static void Transform(Complex[] data, bool inverse)
    {
        var n = data.Length;
        if (n <= 1)
        {
            return;
        }

        if (IsPowerOfTwo(n))
        {
            Radix2(data, inverse);
        }
        else
        {
            Bluestein(data, inverse);
        }
    }

    private static void Radix2(Complex[] data, bool inverse)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        var sign = inverse ? 1.0 : -1.0;
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = sign * 2.0 * Math.PI / len;
            var half = len / 2;
            for (var i = 0; i < n; i += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var w = Complex.FromPolarCoordinates(1.0, angle * k);
                    var u = data[i + k];
                    var v = data[i + k + half] * w;
                    data[i + k] = u + v;
                    data[i + k + half] = u - v;
                }
            }
        }
    }

    private static void Bluestein(Complex[] data, bool inverse)
    {
        var n = data.Length;
        var m = NextPowerOfTwo(2 * n - 1);
        var sign = inverse ? 1.0 : -1.0;
        var chirp = new Complex[n];
        for (var k = 0; k < n; k++)
        {
            // k² mod 2n keeps the angle argument small and exact for large k.
            var kk = (long)k * k % (2L * n);
            chirp[k] = Complex.FromPolarCoordinates(1.0, sign * Math.PI * kk / n);
        }

        var a = new Complex[m];
        var b = new Complex[m];
        for (var k = 0; k < n; k++)
        {
            a[k] = data[k] * chirp[k];
        }

        b[0] = Complex.Conjugate(chirp[0]);
        for (var k = 1; k < n; k++)
        {
            b[k] = b[m - k] = Complex.Conjugate(chirp[k]);
        }

        Radix2(a, false);
        Radix2(b, false);
        for (var i = 0; i < m; i++)
        {
            a[i] *= b[i];
        }

        Radix2(a, true);
        var scale = 1.0 / m;
        for (var k = 0; k < n; k++)
        {
            data[k] = a[k] * scale * chirp[k];
        }
    }
}
