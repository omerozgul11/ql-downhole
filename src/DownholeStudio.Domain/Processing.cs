using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DownholeStudio.Domain;

public enum TaperShape
{
    Hann,
    Cosine,
}

public enum NormalizationMode
{
    /// <summary>Divide by the maximum absolute amplitude.</summary>
    PeakAmplitude,
    /// <summary>Divide by the root-mean-square amplitude.</summary>
    Rms,
}

/// <summary>
/// One non-destructive processing operation. Steps are descriptions; they are applied to a copy of
/// the raw samples by the signal-processing engine.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RemoveMeanStep), "removeMean")]
[JsonDerivedType(typeof(LinearDetrendStep), "linearDetrend")]
[JsonDerivedType(typeof(TaperStep), "taper")]
[JsonDerivedType(typeof(HighPassStep), "highPass")]
[JsonDerivedType(typeof(LowPassStep), "lowPass")]
[JsonDerivedType(typeof(BandPassStep), "bandPass")]
[JsonDerivedType(typeof(NotchStep), "notch")]
[JsonDerivedType(typeof(NormalizeStep), "normalize")]
[JsonDerivedType(typeof(BalanceStep), "balance")]
[JsonDerivedType(typeof(AgcStep), "agc")]
[JsonDerivedType(typeof(PolarityStep), "polarity")]
[JsonDerivedType(typeof(TimeShiftStep), "timeShift")]
[JsonDerivedType(typeof(ResampleStep), "resample")]
public abstract record ProcessingStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Enabled { get; init; } = true;

    /// <summary>Short human-readable name, e.g. "Band-pass 8–80 Hz".</summary>
    [JsonIgnore]
    public abstract string DisplayName { get; }

    /// <summary>Parameters for the processing history and reports.</summary>
    public abstract IReadOnlyDictionary<string, string> DescribeParameters();

    /// <summary>Whether the step changes amplitudes only (safe for arrival-time analysis).</summary>
    [JsonIgnore]
    public virtual bool ChangesTiming => false;

    protected static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record RemoveMeanStep : ProcessingStep
{
    public override string DisplayName => "Remove mean (DC offset)";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>();
}

public sealed record LinearDetrendStep : ProcessingStep
{
    public override string DisplayName => "Linear detrend";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>();
}

public sealed record TaperStep : ProcessingStep
{
    /// <summary>Fraction of the trace length tapered at each end (0–0.5).</summary>
    public double Fraction { get; init; } = 0.05;
    public TaperShape Shape { get; init; } = TaperShape.Hann;
    public override string DisplayName => $"Taper {F(Fraction * 100)} %";
    public override IReadOnlyDictionary<string, string> DescribeParameters() =>
        new Dictionary<string, string> { ["fraction"] = F(Fraction), ["shape"] = Shape.ToString() };
}

public sealed record HighPassStep : ProcessingStep
{
    public double CornerHz { get; init; } = 5;
    public int Order { get; init; } = 4;
    public bool ZeroPhase { get; init; } = true;
    public override string DisplayName => $"High-pass {F(CornerHz)} Hz";
    public override bool ChangesTiming => !ZeroPhase;
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["cornerHz"] = F(CornerHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture), ["zeroPhase"] = ZeroPhase.ToString(),
        ["design"] = "Butterworth",
    };
}

public sealed record LowPassStep : ProcessingStep
{
    public double CornerHz { get; init; } = 200;
    public int Order { get; init; } = 4;
    public bool ZeroPhase { get; init; } = true;
    public override string DisplayName => $"Low-pass {F(CornerHz)} Hz";
    public override bool ChangesTiming => !ZeroPhase;
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["cornerHz"] = F(CornerHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture), ["zeroPhase"] = ZeroPhase.ToString(),
        ["design"] = "Butterworth",
    };
}

public sealed record BandPassStep : ProcessingStep
{
    public double LowHz { get; init; } = 8;
    public double HighHz { get; init; } = 80;
    public int Order { get; init; } = 4;
    public bool ZeroPhase { get; init; } = true;
    public override string DisplayName => $"Band-pass {F(LowHz)}–{F(HighHz)} Hz";
    public override bool ChangesTiming => !ZeroPhase;
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["lowHz"] = F(LowHz), ["highHz"] = F(HighHz), ["order"] = Order.ToString(CultureInfo.InvariantCulture),
        ["zeroPhase"] = ZeroPhase.ToString(), ["design"] = "Butterworth",
    };
}

public sealed record NotchStep : ProcessingStep
{
    public double FrequencyHz { get; init; } = 60;
    /// <summary>Quality factor: centre frequency divided by the −3 dB bandwidth.</summary>
    public double QualityFactor { get; init; } = 30;
    /// <summary>Also notch integer harmonics up to this count (1 = fundamental only).</summary>
    public int Harmonics { get; init; } = 1;
    public bool ZeroPhase { get; init; } = true;
    public override string DisplayName => $"Notch {F(FrequencyHz)} Hz";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["frequencyHz"] = F(FrequencyHz), ["q"] = F(QualityFactor), ["harmonics"] = Harmonics.ToString(CultureInfo.InvariantCulture),
        ["zeroPhase"] = ZeroPhase.ToString(),
    };
}

public sealed record NormalizeStep : ProcessingStep
{
    public NormalizationMode Mode { get; init; } = NormalizationMode.PeakAmplitude;
    public override string DisplayName => Mode == NormalizationMode.PeakAmplitude ? "Normalize (peak)" : "Normalize (RMS)";
    public override IReadOnlyDictionary<string, string> DescribeParameters() =>
        new Dictionary<string, string> { ["mode"] = Mode.ToString() };
}

/// <summary>Scales each trace so its RMS amplitude in a time window equals one.</summary>
public sealed record BalanceStep : ProcessingStep
{
    public double WindowStart { get; init; }
    /// <summary>End of the balancing window in seconds; null means the end of the trace.</summary>
    public double? WindowEnd { get; init; }
    public override string DisplayName => "Trace balance";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["windowStart"] = F(WindowStart), ["windowEnd"] = WindowEnd is { } e ? F(e) : "end",
    };
}

public sealed record AgcStep : ProcessingStep
{
    /// <summary>Length of the sliding RMS window, seconds.</summary>
    public double WindowLength { get; init; } = 0.05;
    public override string DisplayName => $"AGC {F(WindowLength * 1000)} ms";
    public override IReadOnlyDictionary<string, string> DescribeParameters() =>
        new Dictionary<string, string> { ["windowSeconds"] = F(WindowLength) };
}

public sealed record PolarityStep : ProcessingStep
{
    public override string DisplayName => "Reverse polarity";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>();
}

public sealed record TimeShiftStep : ProcessingStep
{
    /// <summary>Shift in seconds; positive values move the waveform later in time.</summary>
    public double Shift { get; init; }
    public override string DisplayName => $"Time shift {F(Shift * 1000)} ms";
    public override bool ChangesTiming => true;
    public override IReadOnlyDictionary<string, string> DescribeParameters() =>
        new Dictionary<string, string> { ["shiftSeconds"] = F(Shift) };
}

public sealed record ResampleStep : ProcessingStep
{
    /// <summary>Target sample interval, seconds.</summary>
    public double TargetInterval { get; init; } = 0.000125;
    public override string DisplayName => $"Resample to {F(1.0 / TargetInterval)} Hz";
    public override IReadOnlyDictionary<string, string> DescribeParameters() => new Dictionary<string, string>
    {
        ["targetIntervalSeconds"] = TargetInterval.ToString("R", CultureInfo.InvariantCulture),
        ["antiAlias"] = "windowed-sinc low-pass at 0.9 × new Nyquist when downsampling",
    };
}

/// <summary>Ordered processing chain for one component group.</summary>
public sealed record ProcessingPipeline
{
    public ImmutableList<ProcessingStep> Steps { get; init; } = [];

    /// <summary>A stable fingerprint of the enabled steps, used as a cache key.</summary>
    public string Fingerprint()
    {
        var parts = Steps.Where(s => s.Enabled).Select(s =>
            s.GetType().Name + "(" + string.Join(",", s.DescribeParameters().OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + ")");
        return string.Join("|", parts);
    }

    public static ProcessingPipeline Empty { get; } = new();
}

/// <summary>Per-trace display/processing adjustments that sit on top of the pipeline.</summary>
public sealed record TraceAdjustment
{
    /// <summary>Linear amplitude multiplier for display.</summary>
    public double Gain { get; init; } = 1.0;
    public bool ReversePolarity { get; init; }
    /// <summary>Static time shift in seconds (positive = later).</summary>
    public double TimeShift { get; init; }
    public bool Excluded { get; init; }
    public string? Comment { get; init; }

    public static TraceAdjustment Default { get; } = new();

    public string Fingerprint() => FormattableString.Invariant($"{ReversePolarity}|{TimeShift:R}");
}
