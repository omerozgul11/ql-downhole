using DownholeStudio.Analysis.Engineering;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Analysis.Trigger;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis;

/// <summary>Density used for one layer and where it came from.</summary>
public sealed record LayerDensity(double? Value, DensitySource Source, bool Estimated, string? Method, string? Assumptions, Diagnostic? Issue);

/// <summary>Combined results for one interpreted layer.</summary>
public sealed record LayerResult
{
    public required ResolvedLayer Layer { get; init; }
    public LayerVelocity? P { get; init; }
    public LayerVelocity? S { get; init; }
    public required LayerDensity Density { get; init; }
    public required ElasticProperties Properties { get; init; }
}

/// <summary>Everything calculated from a project. Immutable and UI-independent.</summary>
public sealed record AnalysisResult
{
    public required DateTimeOffset CalculatedAt { get; init; }
    public required VelocityMethod Method { get; init; }
    public required string MethodDescription { get; init; }
    public IReadOnlyDictionary<Guid, RecordTimingCorrection> Timing { get; init; } = new Dictionary<Guid, RecordTimingCorrection>();
    public required WaveVelocityResult P { get; init; }
    public required WaveVelocityResult S { get; init; }
    public IReadOnlyList<ResolvedLayer> Layers { get; init; } = [];
    public IReadOnlyList<LayerResult> LayerResults { get; init; } = [];
    public required TimeAveragedVelocity Vs30 { get; init; }
    public IReadOnlyList<TimeAveragedVelocity> CustomIntervals { get; init; } = [];
    /// <summary>Deepest depth with an included S observation (or P if no S), metres.</summary>
    public double MeasuredBottom { get; init; }
    public required QcReport Qc { get; init; }

    public WaveVelocityResult For(WaveType wave) => wave == WaveType.P ? P : S;
}

/// <summary>
/// The downhole analysis engine. Usable without any user interface:
/// <code>var result = new DownholeAnalyzer().Calculate(project, rawTraces);</code>
/// </summary>
public sealed class DownholeAnalyzer
{
    private readonly ITriggerCorrector _trigger;
    private readonly ILayerModeler _layers;
    private readonly IEngineeringPropertyCalculator _engineering;
    private readonly IVs30Calculator _vs30;
    private readonly QualityAnalyzer _qc;
    private readonly Func<VelocityMethod, IVelocityCalculator> _velocity;

    public DownholeAnalyzer(ITriggerCorrector? trigger = null, ILayerModeler? layers = null, IEngineeringPropertyCalculator? engineering = null,
        IVs30Calculator? vs30 = null, QualityAnalyzer? qc = null, Func<VelocityMethod, IVelocityCalculator>? velocity = null)
    {
        _trigger = trigger ?? new ReferenceTriggerCorrector();
        _layers = layers ?? new LayerModeler();
        _engineering = engineering ?? new ElasticPropertyCalculator();
        _vs30 = vs30 ?? new Vs30Calculator();
        _qc = qc ?? new QualityAnalyzer();
        _velocity = velocity ?? VelocityCalculatorFactory.Create;
    }

    /// <param name="project">Project state.</param>
    /// <param name="traces">Raw samples; when null, waveform-based quality checks are skipped.</param>
    /// <param name="cache">Optional processed-trace cache.</param>
    /// <param name="now">Calculation timestamp (for reproducible tests).</param>
    public AnalysisResult Calculate(ProjectState project, IRawTraceSource? traces = null, IProcessedTraceCache? cache = null, DateTimeOffset? now = null)
    {
        var scenario = project.ActiveScenario;
        var settings = scenario.Analysis;
        var timing = _trigger.Compute(project);
        var pObs = ObservationBuilder.Build(project, WaveType.P, timing);
        var sObs = ObservationBuilder.Build(project, WaveType.S, timing);

        var sBottom = sObs.Where(o => o.Included).Select(o => o.Depth).DefaultIfEmpty(double.NaN).Max();
        var pBottom = pObs.Where(o => o.Included).Select(o => o.Depth).DefaultIfEmpty(double.NaN).Max();
        var dataBottom = new[] { sBottom, pBottom }.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        var layers = _layers.Resolve(scenario.Layers, dataBottom);

        var calculator = _velocity(settings.Method);
        var p = calculator.Calculate(pObs, layers, project.Survey.Geometry.PSource, settings, WaveType.P);
        var s = calculator.Calculate(sObs, layers, project.Survey.Geometry.SSource, settings, WaveType.S);

        var layerResults = new List<LayerResult>();
        foreach (var layer in layers)
        {
            var lp = p.Layers.FirstOrDefault(l => l.Layer.Id == layer.Id && l.Layer.Index == layer.Index);
            var ls = s.Layers.FirstOrDefault(l => l.Layer.Id == layer.Id && l.Layer.Index == layer.Index);
            var density = ResolveDensity(scenario.Density, layer, lp?.Velocity);
            var props = _engineering.Calculate(lp?.Velocity, ls?.Velocity, density.Value, density.Estimated, density.Method);
            layerResults.Add(new LayerResult { Layer = layer, P = lp, S = ls, Density = density, Properties = props });
        }

        var vsLayers = s.Layers.Select(l => (l.Layer.Top, l.Layer.Bottom, l.Velocity)).ToList();
        var measuredBottom = double.IsFinite(sBottom) ? sBottom : 0;
        // The model bottom may be set deeper than the data by the user; measurements still limit Vs30.
        var vs30 = _vs30.Calculate(vsLayers, measuredBottom, 0, 30, settings.Vs30);
        var custom = settings.Vs30.CustomIntervals.Select(i => _vs30.Calculate(vsLayers, measuredBottom, i.Top, i.Bottom, settings.Vs30)).ToList();

        StationTraceBuilder? builder = traces is null ? null : new StationTraceBuilder(project, traces, cache, _trigger);
        var qc = _qc.Evaluate(project, p, s, layerResults, vs30, timing, builder);
        return new AnalysisResult
        {
            CalculatedAt = now ?? DateTimeOffset.UtcNow, Method = settings.Method, MethodDescription = VelocityCalculatorFactory.Describe(settings.Method),
            Timing = timing, P = p, S = s, Layers = layers, LayerResults = layerResults, Vs30 = vs30, CustomIntervals = custom,
            MeasuredBottom = measuredBottom, Qc = qc,
        };
    }

    private static LayerDensity ResolveDensity(DensityModel model, ResolvedLayer layer, double? vp)
    {
        switch (model.Source)
        {
            case DensitySource.Constant:
                return model.ConstantDensity is > 0
                    ? new LayerDensity(model.ConstantDensity, DensitySource.Constant, false, "User-entered constant", null, null)
                    : new LayerDensity(null, DensitySource.Constant, false, null, null, Diagnostic.Warning("density.missing", "No constant density has been entered."));
            case DensitySource.Estimated:
                if (vp is not > 0)
                {
                    return new LayerDensity(null, DensitySource.Estimated, true, null, null,
                        Diagnostic.Warning("density.noVp", $"Density cannot be estimated for {layer.Top:0.##}–{layer.Bottom:0.##} m because Vp is unavailable."));
                }

                var est = DensityEstimator.Estimate(model.EstimationMethod, vp.Value);
                return new LayerDensity(est.Density, DensitySource.Estimated, true, est.Method, est.Assumptions, est.Issue);
            default:
                return layer.Definition.MeasuredDensity is > 0
                    ? new LayerDensity(layer.Definition.MeasuredDensity, DensitySource.Measured, false, "User-entered (measured)", null, null)
                    : new LayerDensity(null, DensitySource.Measured, false, null, null,
                        Diagnostic.Warning("density.missing", $"No density has been entered for {layer.Top:0.##}–{layer.Bottom:0.##} m."));
        }
    }
}
