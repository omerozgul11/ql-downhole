namespace DownholeStudio.SignalProcessing;

public static class Statistics
{
    public static double Mean(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return 0;
        }

        // Kahan-compensated sum keeps the DC estimate exact for long records.
        double sum = 0, c = 0;
        foreach (var v in x)
        {
            var y = v - c;
            var t = sum + y;
            c = t - sum - y;
            sum = t;
        }

        return sum / x.Length;
    }

    public static double Rms(ReadOnlySpan<double> x)
    {
        if (x.Length == 0)
        {
            return 0;
        }

        double s = 0;
        foreach (var v in x)
        {
            s += v * v;
        }

        return Math.Sqrt(s / x.Length);
    }

    public static double Energy(ReadOnlySpan<double> x)
    {
        double s = 0;
        foreach (var v in x)
        {
            s += v * v;
        }

        return s;
    }

    public static double MaxAbs(ReadOnlySpan<double> x)
    {
        double m = 0;
        foreach (var v in x)
        {
            var a = Math.Abs(v);
            if (a > m)
            {
                m = a;
            }
        }

        return m;
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
        if (a.Length == 0)
        {
            return double.NaN;
        }

        var mid = a.Length / 2;
        return a.Length % 2 == 1 ? a[mid] : 0.5 * (a[mid - 1] + a[mid]);
    }

    /// <summary>Median absolute deviation scaled by 1.4826 to estimate a Gaussian standard deviation.</summary>
    public static double ScaledMad(IReadOnlyList<double> values)
    {
        var med = Median(values);
        return 1.4826 * Median(values.Select(v => Math.Abs(v - med)));
    }

    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    /// <summary>Pearson correlation of two equally long sequences; NaN when either is constant.</summary>
    public static double Pearson(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 2)
        {
            return double.NaN;
        }

        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++)
        {
            ma += a[i];
            mb += b[i];
        }

        ma /= n;
        mb /= n;
        double sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            sab += da * db;
            saa += da * da;
            sbb += db * db;
        }

        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : double.NaN;
    }
}
