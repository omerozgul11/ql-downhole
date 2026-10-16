using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Picking;
using DownholeStudio.Analysis.Qc;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Domain;
using DownholeStudio.Persistence;

namespace DownholeStudio.Core;

/// <summary>Raw samples of the open project, read-only.</summary>
public sealed class AssetTraceSource(ProjectAssetStore assets) : IRawTraceSource
{
    public ReadOnlyMemory<double> GetSamples(Guid traceId) => assets.GetTrace(traceId);
}

/// <summary>
/// Bounded least-recently-used cache of processed traces, keyed by trace id and processing
/// fingerprint. Raw samples are never duplicated here, only derived arrays.
/// </summary>
public sealed class ProcessedTraceCache(long maxSamples = 20_000_000) : IProcessedTraceCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ProcessedTrace Trace)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, ProcessedTrace Trace)> _lru = new();
    private long _samples;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    public ProcessedTrace GetOrAdd(string key, Func<ProcessedTrace> factory)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Trace;
            }
        }

        var trace = factory();
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                return existing.Value.Trace;
            }

            var node = _lru.AddFirst((key, trace));
            _map[key] = node;
            _samples += trace.Samples.Length;
            while (_samples > maxSamples && _lru.Last is { } last && last != node)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _samples -= last.Value.Trace.Samples.Length;
            }
        }

        return trace;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _samples = 0;
        }
    }
}

/// <summary>Runs the analysis engine for the current state, off the UI thread, and caches the latest result.</summary>
public sealed class AnalysisService
{
    private readonly ProjectSession _session;
    private readonly DownholeAnalyzer _analyzer;
    private readonly ProcessedTraceCache _cache;
    private readonly AssetTraceSource _traces;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;
    private ProjectState? _resultState;

    public AnalysisService(ProjectSession session, ProcessedTraceCache cache, DownholeAnalyzer? analyzer = null)
    {
        _session = session;
        _cache = cache;
        _analyzer = analyzer ?? new DownholeAnalyzer();
        _traces = new AssetTraceSource(session.Document.Assets);
    }

    public AnalysisResult? Latest { get; private set; }

    public IRawTraceSource Traces => _traces;

    public ProcessedTraceCache Cache => _cache;

    public event EventHandler<AnalysisResult>? Updated;

    /// <summary>A trace builder bound to the current state.</summary>
    public StationTraceBuilder Builder(ProjectState? state = null) => new(state ?? _session.State, _traces, _cache);

    /// <summary>Synchronous calculation (used by tests, the CLI and report generation).</summary>
    public AnalysisResult CalculateNow(ProjectState? state = null)
    {
        var s = state ?? _session.State;
        var result = _analyzer.Calculate(s, _traces, _cache, _session.Clock.Now);
        if (state is null)
        {
            lock (_lock)
            {
                Latest = result;
                _resultState = s;
            }
        }

        return result;
    }

    /// <summary>Schedules a recalculation after <paramref name="debounce"/>; newer requests cancel older ones.</summary>
    public Task RequestAsync(TimeSpan? debounce = null)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource();
        }

        var state = _session.State;
        var delay = debounce ?? TimeSpan.FromMilliseconds(60);
        return Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                if (ReferenceEquals(_resultState, state))
                {
                    return;
                }

                var result = _analyzer.Calculate(state, _traces, _cache, _session.Clock.Now);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                lock (_lock)
                {
                    Latest = result;
                    _resultState = state;
                }

                Updated?.Invoke(this, result);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Runs automatic picking and applies it as one undoable edit.</summary>
    public async Task<IReadOnlyList<AutoPickOutcome>> AutoPickAsync(WaveType wave, IReadOnlyCollection<Guid>? stations, bool overwriteManual, CancellationToken ct = default)
    {
        var state = _session.State;
        var now = _session.Clock.Now;
        var outcomes = await Task.Run(() => new AutoPicker().PickStations(state, Builder(state), wave, stations, now, ct), ct).ConfigureAwait(true);
        if (outcomes.Any(o => o.Suggestion is not null))
        {
            var algorithm = wave == WaveType.P ? state.ActiveScenario.PPicker.Algorithm : state.ActiveScenario.SPicker.Algorithm;
            _session.Execute(Edits.ApplyAutoPicks(wave, outcomes, algorithm, now, overwriteManual));
        }

        return outcomes;
    }

    public async Task<IReadOnlyList<AutoPickOutcome>> AutoPickReferencesAsync(bool overwriteManual, CancellationToken ct = default)
    {
        var state = _session.State;
        var now = _session.Clock.Now;
        var outcomes = await Task.Run(() => new AutoPicker().PickReferences(state, Builder(state), null, now, ct), ct).ConfigureAwait(true);
        if (outcomes.Any(o => o.Suggestion is not null))
        {
            _session.Execute(Edits.ApplyAutoReferencePicks(outcomes, now, overwriteManual));
        }

        return outcomes;
    }
}

public enum StepStatus
{
    NotStarted,
    InProgress,
    NeedsAttention,
    Complete,
}

public enum WorkflowStep
{
    Project,
    Import,
    Geometry,
    Inspect,
    Preprocess,
    Pick,
    QualityControl,
    VelocityModel,
    Layers,
    Engineering,
    Review,
    Report,
}

public sealed record WorkflowStepState(WorkflowStep Step, int Number, string Title, StepStatus Status, string Hint);

/// <summary>Derives the status of each workflow step from the project and its analysis. Purely informative; never blocks navigation.</summary>
public static class WorkflowEvaluator
{
    public static IReadOnlyList<WorkflowStepState> Evaluate(ProjectState state, AnalysisResult? result, bool saved, bool reportGenerated)
    {
        var survey = state.Survey;
        var sc = state.ActiveScenario;
        var hasRecords = survey.Records.Count > 0;
        var mapped = survey.ChannelMap.Channels.Any(c => c.Role == ComponentRole.Vertical) &&
                     survey.ChannelMap.Channels.Any(c => c.Role is ComponentRole.HorizontalX or ComponentRole.HorizontalY);
        var assigned = survey.Stations.Count > 0 && survey.Records.Where(r => !r.Excluded).All(r => r.StationId is not null && r.Shot != ShotKind.Unknown);
        var anyAssigned = survey.Records.Any(r => r.StationId is not null);
        var pCount = sc.PPicks.Values.Count(p => p.HasTime);
        var sCount = sc.SPicks.Values.Count(p => p.HasTime);
        var stations = survey.Stations.Count(s => !s.Excluded);
        var processed = sc.VerticalPipeline.Steps.Count > 0 || sc.HorizontalPipeline.Steps.Count > 0;
        var qc = result?.Qc;
        var layersDefined = sc.Layers.Layers.Count > 1;
        var engineering = result?.LayerResults.Any(l => l.Properties.ShearModulus is not null) ?? false;

        StepStatus Picks()
        {
            if (pCount == 0 && sCount == 0)
            {
                return StepStatus.NotStarted;
            }

            if (pCount < stations || sCount < stations)
            {
                return StepStatus.InProgress;
            }

            return qc?.Flags.Any(f => f.Severity == Severity.Error && f.Code.StartsWith("pick.", StringComparison.Ordinal)) == true ? StepStatus.NeedsAttention : StepStatus.Complete;
        }

        return
        [
            new(WorkflowStep.Project, 1, "Project", saved ? StepStatus.Complete : StepStatus.InProgress, saved ? "Project saved." : "Name and save the project."),
            new(WorkflowStep.Import, 2, "Import data", hasRecords ? StepStatus.Complete : StepStatus.NotStarted, hasRecords ? $"{survey.Records.Count} records imported." : "Import SEG-2 files."),
            new(WorkflowStep.Geometry, 3, "Geometry", !hasRecords ? StepStatus.NotStarted : assigned && mapped ? StepStatus.Complete : anyAssigned || mapped ? StepStatus.NeedsAttention : StepStatus.NotStarted,
                !mapped ? "Map channels to components." : !assigned ? "Assign every record to a depth and shot type." : "Depths and channels assigned."),
            new(WorkflowStep.Inspect, 4, "Inspect data", hasRecords ? StepStatus.Complete : StepStatus.NotStarted, "Review waveforms and spectra."),
            new(WorkflowStep.Preprocess, 5, "Preprocess", !hasRecords ? StepStatus.NotStarted : processed ? StepStatus.Complete : StepStatus.InProgress, "Apply and review processing."),
            new(WorkflowStep.Pick, 6, "Pick P/S arrivals", Picks(), $"P {pCount}/{stations}, S {sCount}/{stations} depths picked."),
            new(WorkflowStep.QualityControl, 7, "Quality control", qc is null || (pCount + sCount) == 0 ? StepStatus.NotStarted : qc.Flags.Any(f => f.Severity == Severity.Error) ? StepStatus.NeedsAttention : StepStatus.Complete,
                qc is null ? "" : $"{qc.Flags.Count(f => f.Severity == Severity.Error)} problems, {qc.Flags.Count(f => f.Severity == Severity.Warning)} items to review."),
            new(WorkflowStep.VelocityModel, 8, "Velocity model", result?.S.Layers.Any(l => l.Velocity is not null) == true ? StepStatus.Complete : (pCount + sCount) > 0 ? StepStatus.InProgress : StepStatus.NotStarted, "Travel-time analysis."),
            new(WorkflowStep.Layers, 9, "Interpret layers", layersDefined ? StepStatus.Complete : (sCount > 0 ? StepStatus.InProgress : StepStatus.NotStarted), layersDefined ? $"{sc.Layers.Layers.Count} layers." : "Add layer boundaries."),
            new(WorkflowStep.Engineering, 10, "Engineering", engineering ? StepStatus.Complete : layersDefined ? StepStatus.NeedsAttention : StepStatus.NotStarted, engineering ? "Moduli calculated." : "Enter density to calculate moduli."),
            new(WorkflowStep.Review, 11, "Review results", qc is null ? StepStatus.NotStarted : qc.Quality switch
            {
                AnalysisQuality.Good => StepStatus.Complete,
                AnalysisQuality.ReviewRecommended => StepStatus.NeedsAttention,
                _ => StepStatus.InProgress,
            }, qc is null ? "" : string.Join(" ", qc.QualityReasons.Take(1))),
            new(WorkflowStep.Report, 12, "Report", reportGenerated ? StepStatus.Complete : StepStatus.NotStarted, reportGenerated ? "Report generated." : "Generate the PDF report."),
        ];
    }
}

/// <summary>Key results of one scenario for side-by-side comparison.</summary>
public sealed record ScenarioSummary(Guid ScenarioId, string Name, double? Vs30, bool Vs30Extrapolated, string? Vs30Reason, IReadOnlyList<(double Top, double Bottom, double? Vp, double? Vs)> Layers,
    double? PRms, double? SRms, AnalysisQuality Quality, VelocityMethod Method);

public static class ScenarioComparison
{
    /// <summary>Evaluates every scenario of a project. Raw data is shared; nothing is copied.</summary>
    public static IReadOnlyList<ScenarioSummary> Compare(ProjectState state, IRawTraceSource traces, IProcessedTraceCache? cache, DateTimeOffset now)
    {
        var analyzer = new DownholeAnalyzer();
        return state.Scenarios.Select(sc =>
        {
            var r = analyzer.Calculate(state with { ActiveScenarioId = sc.Id }, traces, cache, now);
            return new ScenarioSummary(sc.Id, sc.Name, r.Vs30.Value, r.Vs30.Extrapolated, r.Vs30.Reason,
                r.LayerResults.Select(l => (l.Layer.Top, l.Layer.Bottom, l.P?.Velocity, l.S?.Velocity)).ToList(), r.P.RmsResidual, r.S.RmsResidual, r.Qc.Quality, r.Method);
        }).ToList();
    }
}
