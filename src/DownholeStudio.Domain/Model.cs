using System.Collections.Immutable;

namespace DownholeStudio.Domain;

/// <summary>An interpreted layer. Its bottom is the next layer's top (or the model bottom).</summary>
public sealed record LayerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Top of the layer, metres below ground surface.</summary>
    public double TopDepth { get; init; }
    /// <summary>User-supplied density in kg/m³ (used when density source is Measured).</summary>
    public double? MeasuredDensity { get; init; }
    public string? Label { get; init; }
    public string? Note { get; init; }
}

public sealed record LayerModel
{
    public ImmutableList<LayerDefinition> Layers { get; init; } = [];
    /// <summary>Bottom of the deepest layer, metres; null = deepest included measurement.</summary>
    public double? BottomDepth { get; init; }

    public IReadOnlyList<double> Boundaries => Layers.Skip(1).Select(l => l.TopDepth).ToList();
}

public sealed record DensityModel
{
    public DensitySource Source { get; init; } = DensitySource.Measured;
    /// <summary>kg/m³, used when Source is Constant.</summary>
    public double? ConstantDensity { get; init; }
    public DensityEstimationMethod EstimationMethod { get; init; } = DensityEstimationMethod.Gardner1974;
}

public sealed record DepthInterval
{
    public double Top { get; init; }
    public double Bottom { get; init; }
}

public sealed record Vs30Settings
{
    /// <summary>Allows extrapolating below the deepest measurement. Results are labelled EXTRAPOLATED.</summary>
    public bool AllowExtrapolation { get; init; }
    public Vs30ExtrapolationMethod ExtrapolationMethod { get; init; } = Vs30ExtrapolationMethod.ExtendDeepestLayer;
    /// <summary>Additional time-averaged velocity intervals (e.g. VsZ over 0–10 m).</summary>
    public ImmutableList<DepthInterval> CustomIntervals { get; init; } = [];
}

public sealed record QcThresholds
{
    public double MinimumSnr { get; init; } = 3.0;
    public double ReviewSnr { get; init; } = 6.0;
    /// <summary>Residual (s) above which a P pick is flagged.</summary>
    public double PResidualLimit { get; init; } = 0.0005;
    /// <summary>Residual (s) above which an S pick is flagged.</summary>
    public double SResidualLimit { get; init; } = 0.0015;
    /// <summary>Ratio between successive interval velocities above which a jump is flagged.</summary>
    public double VelocityJumpRatio { get; init; } = 2.5;
    /// <summary>Deviation of a reference onset from the median (s) above which trigger timing is flagged.</summary>
    public double TriggerDeviationLimit { get; init; } = 0.0005;
    public double MinimumCorrelation { get; init; } = 0.6;
    public double HighConfidence { get; init; } = 0.75;
    public double MediumConfidence { get; init; } = 0.45;
    /// <summary>Minimum R² for a layer regression to be accepted without review.</summary>
    public double MinimumRSquared { get; init; } = 0.95;
}

public sealed record AnalysisSettings
{
    public VelocityMethod Method { get; init; } = VelocityMethod.LayerRegression;
    public RegressionMethod Regression { get; init; } = RegressionMethod.OrdinaryLeastSquares;
    /// <summary>Huber tuning constant in units of the robust residual scale (1.345 ⇒ 95 % efficiency).</summary>
    public double HuberThreshold { get; init; } = 1.345;
    public Vs30Settings Vs30 { get; init; } = new();
    public QcThresholds Qc { get; init; } = new();
}

/// <summary>Everything interpretive. Scenarios share the survey's raw data.</summary>
public sealed record AnalysisScenario
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Scenario A";
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public ProcessingPipeline VerticalPipeline { get; init; } = ProcessingPipeline.Empty;
    public ProcessingPipeline HorizontalPipeline { get; init; } = ProcessingPipeline.Empty;
    public ProcessingPipeline ReferencePipeline { get; init; } = ProcessingPipeline.Empty;
    public ImmutableDictionary<Guid, TraceAdjustment> TraceAdjustments { get; init; } = ImmutableDictionary<Guid, TraceAdjustment>.Empty;

    public TriggerSettings Trigger { get; init; } = new();
    /// <summary>Reference/trigger geophone onsets keyed by record id.</summary>
    public ImmutableDictionary<Guid, ArrivalPick> ReferencePicks { get; init; } = ImmutableDictionary<Guid, ArrivalPick>.Empty;
    /// <summary>P arrivals keyed by station id.</summary>
    public ImmutableDictionary<Guid, ArrivalPick> PPicks { get; init; } = ImmutableDictionary<Guid, ArrivalPick>.Empty;
    /// <summary>S arrivals keyed by station id.</summary>
    public ImmutableDictionary<Guid, ArrivalPick> SPicks { get; init; } = ImmutableDictionary<Guid, ArrivalPick>.Empty;

    public PickerSettings PPicker { get; init; } = new();
    public PickerSettings SPicker { get; init; } = new() { StaWindow = 0.004, LtaWindow = 0.04, EnergyWindow = 0.008, PredictionHalfWidth = 0.02, NoiseWindow = 0.02, SignalWindow = 0.02 };
    public PickerSettings ReferencePicker { get; init; } = new() { Algorithm = PickAlgorithm.Aic, UseNeighbourPrediction = false, SearchEnd = 0.05, NoiseWindow = 0.002, SignalWindow = 0.002 };
    public SWaveSettings SWave { get; init; } = new();
    /// <summary>Stack repeated shots of the same kind at a station (after trigger alignment) before picking.</summary>
    public bool StackRepeatedShots { get; init; } = true;

    public LayerModel Layers { get; init; } = new();
    public DensityModel Density { get; init; } = new();
    public AnalysisSettings Analysis { get; init; } = new();

    public ProcessingPipeline PipelineFor(ComponentGroup group) => group switch
    {
        ComponentGroup.Vertical => VerticalPipeline,
        ComponentGroup.Horizontal => HorizontalPipeline,
        _ => ReferencePipeline,
    };

    public AnalysisScenario WithPipeline(ComponentGroup group, ProcessingPipeline pipeline) => group switch
    {
        ComponentGroup.Vertical => this with { VerticalPipeline = pipeline },
        ComponentGroup.Horizontal => this with { HorizontalPipeline = pipeline },
        _ => this with { ReferencePipeline = pipeline },
    };

    public ImmutableDictionary<Guid, ArrivalPick> PicksFor(WaveType wave) => wave == WaveType.P ? PPicks : SPicks;

    public AnalysisScenario WithPicks(WaveType wave, ImmutableDictionary<Guid, ArrivalPick> picks) =>
        wave == WaveType.P ? this with { PPicks = picks } : this with { SPicks = picks };

    public TraceAdjustment AdjustmentFor(Guid traceId) =>
        TraceAdjustments.TryGetValue(traceId, out var a) ? a : TraceAdjustment.Default;
}
