using DownholeStudio.Analysis.Traces;
using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Picking;

/// <summary>Output of a first-break picker on one trace.</summary>
/// <param name="Time">Onset time (record time, seconds), or null when no onset was found.</param>
/// <param name="Sharpness">0–1: how distinct the onset is in the picker's characteristic function.</param>
/// <param name="CharacteristicFunction">The function the picker evaluated, over <paramref name="WindowStartIndex"/>…</param>
/// <param name="WindowStartIndex">First sample index of the search window.</param>
/// <param name="FailureReason">Why no onset was found.</param>
public sealed record PickerOutput(double? Time, double Sharpness, double[] CharacteristicFunction, int WindowStartIndex, string? FailureReason = null);

/// <summary>A first-arrival picker.</summary>
public interface IArrivalPicker
{
    PickAlgorithm Algorithm { get; }

    string Name { get; }

    /// <summary>Finds the onset inside [windowStart, windowEnd] (record time, seconds).</summary>
    PickerOutput Pick(ProcessedTrace trace, double windowStart, double windowEnd, PickerSettings settings);
}

/// <summary>Picker used for P arrivals on the vertical component.</summary>
public interface IPWavePicker : IArrivalPicker;

/// <summary>Picker used for S arrivals on horizontal (difference) traces.</summary>
public interface ISWavePicker : IArrivalPicker;

/// <summary>
/// Akaike information criterion picker (Maeda, 1985): for a window x[0..N−1],
/// AIC(k) = k·ln(var(x[0..k])) + (N−k−1)·ln(var(x[k+1..N−1])). The onset is the global minimum,
/// the point that best splits the window into two stationary segments (noise, then signal). The
/// window is ended shortly after the largest amplitude so that it contains a single change.
/// </summary>
public sealed class AicPicker : IPWavePicker, ISWavePicker
{
    public PickAlgorithm Algorithm => PickAlgorithm.Aic;

    public string Name => "AIC (Maeda)";

    public PickerOutput Pick(ProcessedTrace trace, double windowStart, double windowEnd, PickerSettings settings)
    {
        var (a, b) = Window(trace, windowStart, windowEnd);
        if (b - a + 1 < 8)
        {
            return new PickerOutput(null, 0, [], a, "The search window is too short.");
        }

        // AIC assumes one change (noise → signal). A window that also contains the decay of the arrival
        // has a second change (signal → quiet), so the window is ended a little after the peak amplitude.
        var peak = a;
        for (var i = a; i <= b; i++)
        {
            if (Math.Abs(trace.Samples[i]) > Math.Abs(trace.Samples[peak]))
            {
                peak = i;
            }
        }

        var end = Math.Min(b, peak + Math.Max(8, (peak - a) / 4));
        if (end - a + 1 >= 8)
        {
            b = end;
        }

        var n = b - a + 1;
        var aic = Aic(trace.Samples.AsSpan(a, n));
        var best = -1;
        for (var k = 1; k < n - 2; k++)
        {
            if (double.IsFinite(aic[k]) && (best < 0 || aic[k] < aic[best]))
            {
                best = k;
            }
        }

        if (best < 0)
        {
            return new PickerOutput(null, 0, aic, a, "The trace is constant in the search window.");
        }

        return new PickerOutput(trace.TimeAt(a + best), MinimumSharpness(aic, best), aic, a);
    }

    /// <summary>Direct AIC from cumulative sums (O(N)). Values at the ends are NaN.</summary>
    public static double[] Aic(ReadOnlySpan<double> x)
    {
        var n = x.Length;
        var s1 = new double[n + 1];
        var s2 = new double[n + 1];
        for (var i = 0; i < n; i++)
        {
            s1[i + 1] = s1[i] + x[i];
            s2[i + 1] = s2[i] + x[i] * x[i];
        }

        var aic = new double[n];
        aic[0] = double.NaN;
        aic[n - 1] = double.NaN;
        var tiny = 1e-300;
        for (var k = 1; k < n - 1; k++)
        {
            var n1 = k + 1.0;
            var n2 = n - k - 1.0;
            var v1 = (s2[k + 1] - s1[k + 1] * s1[k + 1] / n1) / n1;
            var v2 = (s2[n] - s2[k + 1] - (s1[n] - s1[k + 1]) * (s1[n] - s1[k + 1]) / n2) / n2;
            aic[k] = k * Math.Log(Math.Max(v1, tiny)) + (n - k - 1) * Math.Log(Math.Max(v2, tiny));
        }

        return aic;
    }

    /// <summary>Depth of the minimum relative to the function's range in a neighbourhood (0–1).</summary>
    internal static double MinimumSharpness(double[] f, int index)
    {
        var finite = f.Where(double.IsFinite).ToArray();
        if (finite.Length < 3)
        {
            return 0;
        }

        var range = finite.Max() - finite.Min();
        if (!(range > 0))
        {
            return 0;
        }

        // Compare with the function a short distance (5 % of the window) either side of the minimum.
        var d = Math.Max(2, f.Length / 20);
        var left = Neighbour(f, index - d);
        var right = Neighbour(f, index + d);
        var rise = Math.Min(left - f[index], right - f[index]);
        return Math.Clamp(rise / (0.15 * range), 0, 1);
    }

    private static double Neighbour(double[] f, int i)
    {
        i = Math.Clamp(i, 0, f.Length - 1);
        while (!double.IsFinite(f[i]) && i > 0)
        {
            i--;
        }

        return double.IsFinite(f[i]) ? f[i] : 0;
    }

    internal static (int Start, int End) Window(ProcessedTrace trace, double windowStart, double windowEnd)
    {
        var a = trace.ClampIndex(windowStart);
        var b = trace.ClampIndex(windowEnd);
        return a <= b ? (a, b) : (b, a);
    }
}

/// <summary>
/// Classic STA/LTA trigger (Allen, 1978): ratio of the mean energy in a short trailing window ending
/// at the current sample to that in a long window immediately preceding it. The onset is taken at the start of the rise that first exceeds the
/// trigger threshold: the last local minimum of the ratio before the crossing.
/// </summary>
public sealed class StaLtaPicker : IPWavePicker, ISWavePicker
{
    public PickAlgorithm Algorithm => PickAlgorithm.StaLta;

    public string Name => "STA/LTA";

    public PickerOutput Pick(ProcessedTrace trace, double windowStart, double windowEnd, PickerSettings settings)
    {
        var dt = trace.SampleInterval;
        var nsta = Math.Max(1, (int)Math.Round(settings.StaWindow / dt));
        var nlta = Math.Max(nsta + 1, (int)Math.Round(settings.LtaWindow / dt));
        var (a, b) = AicPicker.Window(trace, windowStart, windowEnd);
        var x = trace.Samples;
        var prefix = new double[x.Length + 1];
        for (var i = 0; i < x.Length; i++)
        {
            prefix[i + 1] = prefix[i] + x[i] * x[i];
        }

        var count = b - a + 1;
        var ratio = new double[count];
        var noiseFloor = 1e-12 * (prefix[x.Length] / Math.Max(1, x.Length));
        for (var k = 0; k < count; k++)
        {
            var i = a + k;
            // Trailing windows: STA over (i − nsta, i], LTA over the nlta samples before the STA window.
            var staStart = Math.Max(0, i - nsta + 1);
            var ltaEnd = staStart;
            var ltaStart = Math.Max(0, ltaEnd - nlta);
            var sta = (prefix[i + 1] - prefix[staStart]) / (i + 1 - staStart);
            var lta = ltaEnd - ltaStart >= Math.Max(2, nlta / 4) ? (prefix[ltaEnd] - prefix[ltaStart]) / (ltaEnd - ltaStart) : double.NaN;
            ratio[k] = double.IsFinite(lta) ? sta / Math.Max(lta, noiseFloor) : 0;
        }

        var cross = Array.FindIndex(ratio, r => r >= settings.StaLtaTrigger);
        if (cross < 0)
        {
            return new PickerOutput(null, 0, ratio, a, $"The STA/LTA ratio never reaches the trigger level ({settings.StaLtaTrigger:0.#}).");
        }

        var onset = cross;
        while (onset > 0 && ratio[onset - 1] <= ratio[onset])
        {
            onset--;
        }

        var peak = ratio.Skip(cross).Take(Math.Max(1, nsta * 4)).Max();
        var sharpness = Math.Clamp((peak - settings.StaLtaTrigger) / (4 * settings.StaLtaTrigger) + 0.5, 0, 1);
        return new PickerOutput(trace.TimeAt(a + onset), sharpness, ratio, a);
    }
}

/// <summary>
/// Modified energy ratio (Han, Wong &amp; Liu, 2009): ER(i) = Σ_{i..i+L} x² / Σ_{i−L..i} x²,
/// MER(i) = (ER(i)·|x(i)|)³. The onset is the maximum of MER.
/// </summary>
public sealed class EnergyRatioPicker : IPWavePicker, ISWavePicker
{
    public PickAlgorithm Algorithm => PickAlgorithm.EnergyRatio;

    public string Name => "Modified energy ratio";

    public PickerOutput Pick(ProcessedTrace trace, double windowStart, double windowEnd, PickerSettings settings)
    {
        var x = trace.Samples;
        var l = Math.Max(2, (int)Math.Round(settings.EnergyWindow / trace.SampleInterval));
        var (a, b) = AicPicker.Window(trace, windowStart, windowEnd);
        var prefix = new double[x.Length + 1];
        for (var i = 0; i < x.Length; i++)
        {
            prefix[i + 1] = prefix[i] + x[i] * x[i];
        }

        var floor = 1e-12 * prefix[x.Length] / Math.Max(1, x.Length) + 1e-300;
        var count = b - a + 1;
        var mer = new double[count];
        var scale = Statistics.MaxAbs(x.AsSpan(a, count));
        if (!(scale > 0))
        {
            return new PickerOutput(null, 0, mer, a, "The trace is zero in the search window.");
        }

        for (var k = 0; k < count; k++)
        {
            var i = a + k;
            if (i - l < 0 || i + l > x.Length)
            {
                continue;
            }

            var after = prefix[i + l] - prefix[i];
            var before = prefix[i] - prefix[i - l];
            var er = after / Math.Max(before, floor);
            mer[k] = Math.Pow(er * Math.Abs(x[i]) / scale, 3);
        }

        var best = 0;
        for (var k = 1; k < count; k++)
        {
            if (mer[k] > mer[best])
            {
                best = k;
            }
        }

        if (!(mer[best] > 0))
        {
            return new PickerOutput(null, 0, mer, a, "No energy increase was found in the search window.");
        }

        // Sharpness: how much the maximum dominates the rest of the function.
        var sorted = mer.OrderByDescending(v => v).ToArray();
        var second = sorted.Skip(Math.Max(1, l)).FirstOrDefault();
        var sharp = sorted[0] > 0 ? Math.Clamp(1 - second / sorted[0], 0, 1) : 0;
        return new PickerOutput(trace.TimeAt(a + best), sharp, mer, a);
    }
}

public static class PickerFactory
{
    public static IReadOnlyList<IArrivalPicker> All { get; } = [new AicPicker(), new StaLtaPicker(), new EnergyRatioPicker()];

    public static IArrivalPicker Create(PickAlgorithm algorithm) => algorithm switch
    {
        PickAlgorithm.StaLta => new StaLtaPicker(),
        PickAlgorithm.EnergyRatio => new EnergyRatioPicker(),
        _ => new AicPicker(),
    };
}

/// <summary>Signal-to-noise ratio around an onset.</summary>
public static class SnrEstimator
{
    /// <summary>
    /// RMS amplitude in [t, t + signalWindow) divided by RMS amplitude in [t − noiseWindow, t).
    /// Returns null if either window is empty.
    /// </summary>
    public static double? Compute(ProcessedTrace trace, double time, double noiseWindow, double signalWindow)
    {
        var i = trace.IndexAt(time);
        var nNoise = Math.Max(2, (int)Math.Round(noiseWindow / trace.SampleInterval));
        var nSignal = Math.Max(2, (int)Math.Round(signalWindow / trace.SampleInterval));
        var noiseStart = Math.Max(0, i - nNoise);
        var signalEnd = Math.Min(trace.Samples.Length, i + nSignal);
        if (i - noiseStart < 2 || signalEnd - i < 2 || i < 0)
        {
            return null;
        }

        var noise = Statistics.Rms(trace.Samples.AsSpan(noiseStart, i - noiseStart));
        var signal = Statistics.Rms(trace.Samples.AsSpan(i, signalEnd - i));
        if (!(noise > 0))
        {
            return signal > 0 ? 1e6 : null;
        }

        return signal / noise;
    }
}
