using System.Globalization;
using DownholeStudio.Formats;

namespace DownholeStudio.Synthetics;

/// <summary>A layer of the true earth model (SI units).</summary>
public sealed record TrueLayer(double Top, double Bottom, double Vp, double Vs, double Density);

public enum SyntheticDataset
{
    Clean,
    Noisy,
    MissingTraces,
    TriggerErrors,
    ReversedPolarityS,
}

public sealed record SyntheticOptions
{
    public IReadOnlyList<TrueLayer> Layers { get; init; } = DefaultLayers;
    public double FirstDepth { get; init; } = 1.0;
    public double DepthIncrement { get; init; } = 1.0;
    public int DepthCount { get; init; } = 32;
    public double POffset { get; init; } = 2.0;
    public double SOffset { get; init; } = 2.0;
    public double SampleInterval { get; init; } = 0.0000625; // 16 kHz
    public int SampleCount { get; init; } = 4096;
    /// <summary>Recording starts this long before the trigger (SEG-2 DELAY = −PreTrigger), seconds.</summary>
    public double PreTrigger { get; init; } = 0.002;
    public double PFrequency { get; init; } = 180;
    public double SFrequency { get; init; } = 70;
    /// <summary>Noise RMS relative to the arrival amplitude at the deepest receiver.</summary>
    public double NoiseLevel { get; init; } = 0.01;
    /// <summary>Maximum absolute trigger delay error, seconds (uniformly distributed).</summary>
    public double TriggerJitter { get; init; }
    /// <summary>Time between the trigger and the source impact common to all shots, seconds.</summary>
    public double CommonTriggerDelay { get; init; }
    public bool BottomUp { get; init; }
    public bool ReverseXChannel { get; init; }
    public int Seed { get; init; } = 1234;
    /// <summary>Records to omit entirely (by depth index and shot letter, e.g. "7:P").</summary>
    public IReadOnlySet<string> OmitRecords { get; init; } = new HashSet<string>();
    /// <summary>Channels to omit (by depth index and channel, e.g. "12:3").</summary>
    public IReadOnlySet<string> OmitChannels { get; init; } = new HashSet<string>();
    public Seg2SampleFormat SampleFormat { get; init; } = Seg2SampleFormat.Float32;
    public bool BigEndian { get; init; }

    public static IReadOnlyList<TrueLayer> DefaultLayers { get; } =
    [
        new(0, 3, 420, 160, 1800),
        new(3, 8, 900, 260, 1900),
        new(8, 15, 1600, 380, 1950),
        new(15, 24, 1850, 520, 2000),
        new(24, 40, 2400, 760, 2100),
    ];

    public static SyntheticOptions For(SyntheticDataset dataset) => dataset switch
    {
        SyntheticDataset.Noisy => new SyntheticOptions { NoiseLevel = 0.25, Seed = 2024 },
        SyntheticDataset.MissingTraces => new SyntheticOptions
        {
            OmitRecords = new HashSet<string> { "6:P", "14:SN" }, OmitChannels = new HashSet<string> { "20:3" }, Seed = 77,
        },
        // Impact 2 ms after the trigger on average, varying ±0.8 ms between shots (always inside the record).
        SyntheticDataset.TriggerErrors => new SyntheticOptions { TriggerJitter = 0.0008, CommonTriggerDelay = 0.002, Seed = 99 },
        SyntheticDataset.ReversedPolarityS => new SyntheticOptions { ReverseXChannel = true, Seed = 5 },
        _ => new SyntheticOptions(),
    };
}

/// <summary>What the survey should yield, for validation.</summary>
public sealed record SyntheticTruth
{
    public required IReadOnlyList<TrueLayer> Layers { get; init; }
    public required IReadOnlyList<SyntheticStationTruth> Stations { get; init; }
    public double? Vs30 { get; init; }
    public double POffset { get; init; }
    public double SOffset { get; init; }
}

public sealed record SyntheticStationTruth(double Depth, double PTime, double STime, IReadOnlyDictionary<string, double> TriggerDelays, double SensorAngleDegrees);

public sealed record SyntheticFile(string FileName, byte[] Content);

public sealed record SyntheticSurvey(IReadOnlyList<SyntheticFile> Files, SyntheticTruth Truth);

/// <summary>
/// Generates deterministic downhole surveys with known arrival times. Channel layout:
/// 1 = vertical (Z), 2 = horizontal X, 3 = horizontal Y, 4 = reference geophone at the source.
/// Arrival times are computed with <see cref="FermatRayTimer"/>, an implementation independent of
/// the analysis engine's ray tracer.
/// </summary>
public static class SyntheticSurveyGenerator
{
    public static SyntheticSurvey Generate(SyntheticOptions o)
    {
        var rng = new Random(o.Seed);
        var files = new List<SyntheticFile>();
        var stations = new List<SyntheticStationTruth>();
        var order = Enumerable.Range(0, o.DepthCount).ToList();
        if (o.BottomUp)
        {
            order.Reverse();
        }

        var deepest = o.FirstDepth + (o.DepthCount - 1) * o.DepthIncrement;
        var refAmplitude = 1.0 / Math.Sqrt(deepest * deepest + o.SOffset * o.SOffset);
        var sequence = 0;
        var start = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Unspecified);
        foreach (var index in order)
        {
            var depth = o.FirstDepth + index * o.DepthIncrement;
            var tp = FermatRayTimer.TravelTime(o.Layers, depth, o.POffset, l => l.Vp);
            var ts = FermatRayTimer.TravelTime(o.Layers, depth, o.SOffset, l => l.Vs);
            var tpS = FermatRayTimer.TravelTime(o.Layers, depth, o.SOffset, l => l.Vp);
            // The probe rotates in the hole: a slowly varying sensor azimuth, deterministic per depth.
            var angle = 25 + 20 * Math.Sin(depth * 0.37);
            var delays = new Dictionary<string, double>();
            foreach (var shot in new[] { "P", "SP", "SN" })
            {
                var delay = o.CommonTriggerDelay + (o.TriggerJitter > 0 ? (rng.NextDouble() * 2 - 1) * o.TriggerJitter : 0);
                delays[shot] = delay;
                if (o.OmitRecords.Contains($"{index}:{shot}"))
                {
                    sequence++;
                    continue;
                }

                var rP = Math.Sqrt(depth * depth + o.POffset * o.POffset);
                var rS = Math.Sqrt(depth * depth + o.SOffset * o.SOffset);
                var traces = new double[4][];
                for (var c = 0; c < 4; c++)
                {
                    traces[c] = new double[o.SampleCount];
                }

                if (shot == "P")
                {
                    var a = 1.0 / rP;
                    // Vertical-incidence P: mostly vertical, small horizontal leakage.
                    AddWavelet(traces[0], o, tp + delay, o.PFrequency, a);
                    AddWavelet(traces[1], o, tp + delay, o.PFrequency, 0.08 * a * Math.Cos(angle * Math.PI / 180));
                    AddWavelet(traces[2], o, tp + delay, o.PFrequency, 0.08 * a * Math.Sin(angle * Math.PI / 180));
                }
                else
                {
                    var sign = shot == "SP" ? 1.0 : -1.0;
                    var a = 1.0 / rS;
                    var phi = angle * Math.PI / 180;
                    // SH motion polarised along the source beam, projected on the rotated sensor.
                    AddWavelet(traces[1], o, ts + delay, o.SFrequency, sign * a * Math.Cos(phi));
                    AddWavelet(traces[2], o, ts + delay, o.SFrequency, sign * a * Math.Sin(phi));
                    AddWavelet(traces[0], o, ts + delay, o.SFrequency, sign * 0.1 * a);
                    // A beam strike also radiates P energy with the same polarity for both directions.
                    AddWavelet(traces[0], o, tpS + delay, o.PFrequency, 0.25 * a);
                    AddWavelet(traces[1], o, tpS + delay, o.PFrequency, 0.05 * a);
                }

                // Reference geophone on the source: onset at the impact time.
                AddWavelet(traces[3], o, delay, shot == "P" ? o.PFrequency : o.SFrequency, 1.0);
                for (var c = 0; c < 3; c++)
                {
                    AddNoise(traces[c], o.NoiseLevel * refAmplitude, rng);
                }

                AddNoise(traces[3], 0.002, rng);
                if (o.ReverseXChannel)
                {
                    for (var i = 0; i < o.SampleCount; i++)
                    {
                        traces[1][i] = -traces[1][i];
                    }
                }

                var shotName = shot switch { "P" => "P", "SP" => "SL", _ => "SR" };
                var fileName = $"DH_{depth.ToString("000.0", CultureInfo.InvariantCulture)}m_{shotName}.sg2";
                var acquired = start.AddSeconds(30 * sequence);
                var fileKeywords = new Dictionary<string, string>
                {
                    ["ACQUISITION_DATE"] = acquired.ToString("dd/MMM/yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(),
                    ["ACQUISITION_TIME"] = acquired.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    ["COMPANY"] = "QuakeLogic synthetic data",
                    ["INSTRUMENT"] = "Synthetic generator",
                    ["NOTE"] = $"DEPTH {depth.ToString("0.###", CultureInfo.InvariantCulture)} m\nSHOT {shotName}",
                };
                var toWrite = new List<Seg2TraceToWrite>();
                for (var c = 0; c < 4; c++)
                {
                    if (o.OmitChannels.Contains($"{index}:{c + 1}"))
                    {
                        continue;
                    }

                    var scale = o.SampleFormat is Seg2SampleFormat.Int16 or Seg2SampleFormat.Int32 ? 20000.0 / Math.Max(1e-12, traces[c].Max(Math.Abs)) : 1.0;
                    var samples = traces[c].Select(v => v * scale).ToArray();
                    toWrite.Add(new Seg2TraceToWrite(samples, o.SampleInterval, new Dictionary<string, string>
                    {
                        ["CHANNEL_NUMBER"] = (c + 1).ToString(CultureInfo.InvariantCulture),
                        ["DELAY"] = (-o.PreTrigger).ToString("R", CultureInfo.InvariantCulture),
                        ["RECEIVER_LOCATION"] = c == 3 ? "0" : depth.ToString("0.###", CultureInfo.InvariantCulture),
                        ["SOURCE_LOCATION"] = (shot == "P" ? o.POffset : o.SOffset).ToString("0.###", CultureInfo.InvariantCulture),
                        ["RECEIVER"] = c switch { 0 => "Z", 1 => "X", 2 => "Y", _ => "REF" },
                        ["DESCALING_FACTOR"] = (1.0 / scale).ToString("R", CultureInfo.InvariantCulture),
                    }));
                }

                files.Add(new SyntheticFile(fileName, Seg2Writer.Write(toWrite, fileKeywords, o.SampleFormat, !o.BigEndian)));
                sequence++;
            }

            stations.Add(new SyntheticStationTruth(depth, tp, ts, delays, angle));
        }

        double? vs30 = null;
        if (o.Layers[^1].Bottom >= 30 && deepest >= 30)
        {
            vs30 = 30.0 / o.Layers.Where(l => l.Top < 30).Sum(l => (Math.Min(l.Bottom, 30) - l.Top) / l.Vs);
        }

        return new SyntheticSurvey(files, new SyntheticTruth
        {
            Layers = o.Layers, Stations = stations.OrderBy(s => s.Depth).ToList(), Vs30 = vs30, POffset = o.POffset, SOffset = o.SOffset,
        });
    }

    /// <summary>Writes the files of a survey to a folder (created if missing). Existing files are overwritten.</summary>
    public static IReadOnlyList<string> WriteTo(SyntheticSurvey survey, string folder)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        foreach (var f in survey.Files)
        {
            var path = Path.Combine(folder, f.FileName);
            File.WriteAllBytes(path, f.Content);
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// Causal wavelet starting exactly at <paramref name="onset"/>:
    /// w(τ) = sin(2πfτ)·exp(−τ·f/1.2)·(1 − exp(−τ·f·8)) for τ ≥ 0, zero before.
    /// </summary>
    private static void AddWavelet(double[] trace, SyntheticOptions o, double onset, double f, double amplitude)
    {
        var dt = o.SampleInterval;
        // Sample i is recorded at time i·dt − PreTrigger.
        var first = Math.Max(0, (int)Math.Ceiling((onset + o.PreTrigger) / dt));
        var last = Math.Min(trace.Length, first + (int)Math.Ceiling(8.0 / f / dt));
        for (var i = first; i < last; i++)
        {
            var tau = i * dt - o.PreTrigger - onset;
            if (tau < 0)
            {
                continue;
            }

            trace[i] += amplitude * Math.Sin(2 * Math.PI * f * tau) * Math.Exp(-tau * f / 1.2) * (1 - Math.Exp(-tau * f * 8));
        }
    }

    private static void AddNoise(double[] trace, double rms, Random rng)
    {
        if (rms <= 0)
        {
            return;
        }

        // Gaussian noise (Box–Muller) smoothed with a 3-point filter to give it a realistic band.
        var raw = new double[trace.Length];
        for (var i = 0; i < raw.Length; i += 2)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            var mag = Math.Sqrt(-2 * Math.Log(u1));
            raw[i] = mag * Math.Cos(2 * Math.PI * u2);
            if (i + 1 < raw.Length)
            {
                raw[i + 1] = mag * Math.Sin(2 * Math.PI * u2);
            }
        }

        // The kernel (1, 2, 1)/6 has an RMS gain of √6/6 on unit white noise.
        var gain = rms / (Math.Sqrt(6.0) / 6.0);
        for (var i = 1; i < raw.Length - 1; i++)
        {
            trace[i] += gain * (raw[i - 1] + 2 * raw[i] + raw[i + 1]) / 6.0;
        }
    }
}

/// <summary>
/// Travel time through horizontal layers by Fermat's principle: the horizontal crossing points of
/// the ray at each interface are found by minimising the total time T = Σ √(hᵢ² + Δxᵢ²)/vᵢ with
/// coordinate-wise Newton iterations. Independent of the engine's Snell's-law bisection.
/// </summary>
public static class FermatRayTimer
{
    public static double TravelTime(IReadOnlyList<TrueLayer> layers, double receiverDepth, double offset, Func<TrueLayer, double> velocity)
    {
        var segs = new List<(double H, double V)>();
        foreach (var l in layers)
        {
            var b = Math.Min(l.Bottom, receiverDepth);
            if (b > l.Top)
            {
                segs.Add((b - l.Top, velocity(l)));
            }
        }

        if (receiverDepth > layers[^1].Bottom)
        {
            segs.Add((receiverDepth - layers[^1].Bottom, velocity(layers[^1])));
        }

        var n = segs.Count;
        // Horizontal positions of the n−1 interior crossing points; start on the straight line.
        var total = segs.Sum(s => s.H);
        var x = new double[n + 1];
        var acc = 0.0;
        for (var i = 0; i <= n; i++)
        {
            x[i] = offset * (1 - acc / total);
            if (i < n)
            {
                acc += segs[i].H;
            }
        }

        for (var iter = 0; iter < 500; iter++)
        {
            var maxStep = 0.0;
            for (var i = 1; i < n; i++)
            {
                // dT/dx_i and d²T/dx_i² for the two segments touching crossing point i.
                double Grad(int k, double xi, out double curvature)
                {
                    var (h, v) = segs[k];
                    var dx = k == i - 1 ? x[i - 1] - xi : xi - x[i + 1];
                    var l = Math.Sqrt(h * h + dx * dx);
                    curvature = h * h / (l * l * l * v);
                    var sign = k == i - 1 ? -1.0 : 1.0;
                    return sign * dx / (l * v);
                }

                var g = Grad(i - 1, x[i], out var c1) + Grad(i, x[i], out var c2);
                var step = g / (c1 + c2);
                x[i] -= step;
                maxStep = Math.Max(maxStep, Math.Abs(step));
            }

            if (maxStep < 1e-13)
            {
                break;
            }
        }

        double t = 0;
        for (var k = 0; k < n; k++)
        {
            var dx = x[k] - x[k + 1];
            t += Math.Sqrt(segs[k].H * segs[k].H + dx * dx) / segs[k].V;
        }

        return t;
    }
}
