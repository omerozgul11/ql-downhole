using System.Collections.Immutable;

namespace DownholeStudio.Domain;

/// <summary>One contribution to a confidence score, with a plain-language explanation.</summary>
public sealed record ConfidenceFactor
{
    public required string Name { get; init; }
    /// <summary>The measured value (e.g. SNR = 7.2).</summary>
    public double? Value { get; init; }
    /// <summary>The factor's normalised score between 0 (poor) and 1 (good).</summary>
    public double Score { get; init; }
    public double Weight { get; init; } = 1.0;
    public required string Explanation { get; init; }
}

/// <summary>Numerical confidence (0–1), its level, and why.</summary>
public sealed record PickConfidence
{
    public double Score { get; init; }
    public ConfidenceLevel Level { get; init; }
    public ImmutableList<ConfidenceFactor> Factors { get; init; } = [];

    /// <summary>The weakest factors, worst first, for "why is this low?" explanations.</summary>
    public IEnumerable<ConfidenceFactor> Concerns => Factors.Where(f => f.Score < 0.5).OrderBy(f => f.Score);
}

/// <summary>An automatic picker's suggestion. Kept next to manual picks so it can be restored.</summary>
public sealed record PickSuggestion
{
    /// <summary>Arrival time in raw record time (seconds after the trigger).</summary>
    public double Time { get; init; }
    public PickAlgorithm Algorithm { get; init; }
    public PickConfidence? Confidence { get; init; }
    public double? Snr { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A first-arrival pick. Times are raw record times; corrections are applied in analysis.</summary>
public sealed record ArrivalPick
{
    /// <summary>Arrival time in seconds after the recorded trigger; null when no pick is set.</summary>
    public double? Time { get; init; }
    public PickOrigin Origin { get; init; }
    public PickAlgorithm Algorithm { get; init; }
    public PickConfidence? Confidence { get; init; }
    public double? Snr { get; init; }
    public bool Included { get; init; } = true;
    public string? Comment { get; init; }
    /// <summary>Trace the pick was made on.</summary>
    public Guid? TraceId { get; init; }
    /// <summary>The most recent automatic suggestion for this position.</summary>
    public PickSuggestion? Suggestion { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }

    public bool HasTime => Time is not null;

    public bool IsManual => Origin is PickOrigin.Manual or PickOrigin.AcceptedSuggestion;
}

public sealed record PickerSettings
{
    public PickAlgorithm Algorithm { get; init; } = PickAlgorithm.Aic;
    /// <summary>Start of the search window, seconds of record time; null = start of the record (including any pre-trigger samples).</summary>
    public double? SearchStart { get; init; }
    /// <summary>End of the search window; null = end of trace.</summary>
    public double? SearchEnd { get; init; }
    public double StaWindow { get; init; } = 0.002;
    public double LtaWindow { get; init; } = 0.02;
    public double StaLtaTrigger { get; init; } = 3.0;
    public double EnergyWindow { get; init; } = 0.004;
    /// <summary>Restrict the search around the time predicted from already-picked neighbours.</summary>
    public bool UseNeighbourPrediction { get; init; } = true;
    /// <summary>Half width of the neighbour-predicted window, seconds.</summary>
    public double PredictionHalfWidth { get; init; } = 0.010;
    /// <summary>Window before the pick used as noise for SNR, seconds.</summary>
    public double NoiseWindow { get; init; } = 0.010;
    /// <summary>Window after the pick used as signal for SNR, seconds.</summary>
    public double SignalWindow { get; init; } = 0.010;
}

public sealed record TriggerSettings
{
    public TriggerCorrectionMode Mode { get; init; } = TriggerCorrectionMode.None;
    /// <summary>Travel time from source to reference geophone, seconds (used by SubtractReferenceOnset).</summary>
    public double ExpectedReferenceTime { get; init; }
}

public enum SPickTrace
{
    /// <summary>Half the difference of the positive and negative shots: (S⁺ − S⁻)/2.</summary>
    Difference,
    Positive,
    Negative,
}

public sealed record SWaveSettings
{
    public SComponentMode Component { get; init; } = SComponentMode.HorizontalX;
    /// <summary>User-applied horizontal rotation angle per station, degrees (X towards Y).</summary>
    public ImmutableDictionary<Guid, double> StationRotation { get; init; } = ImmutableDictionary<Guid, double>.Empty;
    public SPickTrace PickTrace { get; init; } = SPickTrace.Difference;
}
