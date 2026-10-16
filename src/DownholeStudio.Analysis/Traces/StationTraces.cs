using DownholeStudio.Analysis.Trigger;
using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Traces;

/// <summary>Read-only access to raw decoded samples. Implementations must never let callers modify them.</summary>
public interface IRawTraceSource
{
    ReadOnlyMemory<double> GetSamples(Guid traceId);
}

/// <summary>Optional cache for processed traces (implemented by the application core).</summary>
public interface IProcessedTraceCache
{
    ProcessedTrace GetOrAdd(string key, Func<ProcessedTrace> factory);
}

/// <summary>The trace used to pick one wave type at one station, with its provenance.</summary>
/// <param name="Trace">Processed (and, if applicable, stacked/differenced) samples in the primary record's time base.</param>
/// <param name="PrimaryRecordId">Record whose trigger time base the trace uses.</param>
/// <param name="PrimaryTraceId">Raw trace of the primary record.</param>
/// <param name="RecordIds">All records that contributed.</param>
/// <param name="Description">Human-readable description, e.g. "Vertical, stack of 2 shots".</param>
/// <param name="Notes">Anything the user should know (missing records, skipped alignment…).</param>
public sealed record StationTrace(ProcessedTrace Trace, Guid PrimaryRecordId, Guid PrimaryTraceId, IReadOnlyList<Guid> RecordIds, string Description, IReadOnlyList<string> Notes);

/// <summary>S-wave traces at a station: the opposite-polarity shots and their combinations.</summary>
public sealed record SStationTraces(StationTrace? Positive, StationTrace? Negative, ProcessedTrace? Difference, ProcessedTrace? Sum, StationTrace? PickTrace, IReadOnlyList<string> Notes);

/// <summary>Builds processed station traces from a project. Pure: depends only on project state and raw samples.</summary>
public sealed class StationTraceBuilder
{
    private readonly ProjectState _project;
    private readonly IRawTraceSource _raw;
    private readonly IProcessedTraceCache? _cache;
    private readonly IReadOnlyDictionary<Guid, RecordTimingCorrection> _timing;

    public StationTraceBuilder(ProjectState project, IRawTraceSource raw, IProcessedTraceCache? cache = null, ITriggerCorrector? trigger = null)
    {
        _project = project;
        _raw = raw;
        _cache = cache;
        _timing = (trigger ?? new ReferenceTriggerCorrector()).Compute(project);
    }

    public IReadOnlyDictionary<Guid, RecordTimingCorrection> Timing => _timing;

    private AnalysisScenario Scenario => _project.ActiveScenario;

    /// <summary>Processed trace of one raw trace with its group pipeline and adjustments.</summary>
    public ProcessedTrace Process(Guid traceId, bool applyAdjustment = true, ProcessingPipeline? pipelineOverride = null)
    {
        var found = _project.Survey.FindTrace(traceId) ?? throw new ArgumentException("Unknown trace.", nameof(traceId));
        var (_, trace) = found;
        var assignment = _project.Survey.ChannelMap.For(trace.ChannelNumber);
        var group = ChannelMap.GroupOf(assignment?.Role ?? ComponentRole.Unassigned) ?? ComponentGroup.Vertical;
        var pipeline = pipelineOverride ?? Scenario.PipelineFor(group);
        var adjustment = applyAdjustment ? Scenario.AdjustmentFor(traceId) : null;
        var reversed = assignment?.ReversePolarity ?? false;
        var key = $"{traceId:N}|{pipeline.Fingerprint()}|{reversed}|{adjustment?.Fingerprint() ?? "-"}";
        ProcessedTrace Make() => TraceProcessor.Apply(_raw.GetSamples(traceId).Span, trace.SampleInterval, trace.Delay, pipeline, reversed, adjustment);
        return _cache is null ? Make() : _cache.GetOrAdd(key, Make);
    }

    /// <summary>Raw samples with only the channel-map polarity applied (for raw/processed comparison).</summary>
    public ProcessedTrace Raw(Guid traceId)
    {
        var (_, trace) = _project.Survey.FindTrace(traceId) ?? throw new ArgumentException("Unknown trace.", nameof(traceId));
        var reversed = _project.Survey.ChannelMap.For(trace.ChannelNumber)?.ReversePolarity ?? false;
        var span = _raw.GetSamples(traceId).Span;
        return new ProcessedTrace(reversed ? TimeDomain.Invert(span) : span.ToArray(), trace.SampleInterval, trace.Delay, []);
    }

    public TraceInfo? FindComponent(SeismicRecord record, ComponentRole role) =>
        record.Traces.FirstOrDefault(t => _project.Survey.ChannelMap.RoleOf(t.ChannelNumber) == role && !Scenario.AdjustmentFor(t.Id).Excluded);

    public double? CorrectionFor(Guid recordId) => _timing.TryGetValue(recordId, out var c) ? c.Correction : null;

    /// <summary>The vertical-component trace used for P picking at a station.</summary>
    public StationTrace? BuildP(Guid stationId)
    {
        var records = _project.Survey.RecordsAt(stationId, ShotKind.P).ToList();
        return BuildComponentStack(records, r => ComponentFor(r, ComponentRole.Vertical), "Vertical");
    }

    /// <summary>The reference/trigger channel of a record.</summary>
    public StationTrace? BuildReference(Guid recordId)
    {
        var record = _project.Survey.FindRecord(recordId);
        if (record is null)
        {
            return null;
        }

        var trace = FindComponent(record, ComponentRole.Reference);
        return trace is null ? null : new StationTrace(Process(trace.Id), record.Id, trace.Id, [record.Id], $"Reference channel {trace.ChannelNumber}", []);
    }

    /// <summary>Horizontal traces at a station for S-wave analysis, honouring the component mode and rotation.</summary>
    public SStationTraces BuildS(Guid stationId)
    {
        var notes = new List<string>();
        var pos = BuildComponentStack(_project.Survey.RecordsAt(stationId, ShotKind.SPositive).ToList(), r => HorizontalFor(r, stationId), "S+");
        var neg = BuildComponentStack(_project.Survey.RecordsAt(stationId, ShotKind.SNegative).ToList(), r => HorizontalFor(r, stationId), "S−");
        if (pos is null)
        {
            notes.Add("No S+ record is assigned to this depth.");
        }

        if (neg is null)
        {
            notes.Add("No S− record is assigned to this depth, so opposite-polarity confirmation is not possible.");
        }

        ProcessedTrace? diff = null, sum = null;
        if (pos is not null && neg is not null)
        {
            var aligned = AlignTo(neg.Trace, neg.PrimaryRecordId, pos.PrimaryRecordId, pos.Trace, notes);
            if (aligned is not null)
            {
                diff = pos.Trace with { Samples = TimeDomain.HalfDifference(pos.Trace.Samples, aligned), Warnings = [] };
                sum = pos.Trace with { Samples = TimeDomain.HalfSum(pos.Trace.Samples, aligned), Warnings = [] };
            }
        }

        StationTrace? pick = Scenario.SWave.PickTrace switch
        {
            SPickTrace.Difference when diff is not null => pos! with { Trace = diff, Description = "(S+ − S−)/2", RecordIds = [.. pos!.RecordIds, .. neg!.RecordIds] },
            SPickTrace.Negative when neg is not null => neg,
            _ => pos ?? neg,
        };
        if (Scenario.SWave.PickTrace == SPickTrace.Difference && diff is null && pick is not null)
        {
            notes.Add($"The difference trace is unavailable; picking on the {(pos is not null ? "S+" : "S−")} trace instead.");
        }

        return new SStationTraces(pos, neg, diff, sum, pick, notes);
    }

    /// <summary>Raw X and Y horizontal traces of a record (processed with the horizontal pipeline), unrotated.</summary>
    public (ProcessedTrace X, ProcessedTrace Y)? HorizontalPair(SeismicRecord record)
    {
        var x = FindComponent(record, ComponentRole.HorizontalX);
        var y = FindComponent(record, ComponentRole.HorizontalY);
        if (x is null || y is null)
        {
            return null;
        }

        var px = Process(x.Id);
        var py = Process(y.Id);
        return (px, py);
    }

    private (TraceInfo Trace, ProcessedTrace Processed)? ComponentFor(SeismicRecord record, ComponentRole role)
    {
        var t = FindComponent(record, role);
        return t is null ? null : (t, Process(t.Id));
    }

    private (TraceInfo Trace, ProcessedTrace Processed)? HorizontalFor(SeismicRecord record, Guid stationId)
    {
        var s = Scenario.SWave;
        switch (s.Component)
        {
            case SComponentMode.HorizontalY:
                return ComponentFor(record, ComponentRole.HorizontalY);
            case SComponentMode.Rotated:
            {
                var x = FindComponent(record, ComponentRole.HorizontalX);
                var y = FindComponent(record, ComponentRole.HorizontalY);
                if (x is null || y is null)
                {
                    return x is null ? null : ComponentFor(record, ComponentRole.HorizontalX);
                }

                var angle = s.StationRotation.TryGetValue(stationId, out var deg) ? deg : 0.0;
                var px = Process(x.Id);
                var py = Process(y.Id);
                if (Math.Abs(px.SampleInterval - py.SampleInterval) > 1e-12)
                {
                    return (x, px);
                }

                var (radial, _) = Rotation.Rotate(px.Samples, py.Samples, angle * Math.PI / 180.0);
                return (x, px with { Samples = radial, Warnings = [.. px.Warnings, .. py.Warnings] });
            }

            default:
                return ComponentFor(record, ComponentRole.HorizontalX);
        }
    }

    private StationTrace? BuildComponentStack(IReadOnlyList<SeismicRecord> records, Func<SeismicRecord, (TraceInfo Trace, ProcessedTrace Processed)?> select, string label)
    {
        var parts = records.Select(r => (Record: r, Part: select(r))).Where(p => p.Part is not null).ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var notes = new List<string>();
        var primary = parts[0];
        var primaryTrace = primary.Part!.Value.Processed;
        if (parts.Count == 1 || !Scenario.StackRepeatedShots)
        {
            if (parts.Count > 1)
            {
                notes.Add($"{parts.Count} {label} shots are assigned; only the first is used because stacking is off.");
            }

            return new StationTrace(primaryTrace, primary.Record.Id, primary.Part.Value.Trace.Id, [primary.Record.Id],
                $"{label}, channel {primary.Part.Value.Trace.ChannelNumber}", [.. notes, .. primaryTrace.Warnings]);
        }

        var stackInput = new List<double[]> { primaryTrace.Samples };
        var used = new List<Guid> { primary.Record.Id };
        foreach (var (record, part) in parts.Skip(1))
        {
            var aligned = AlignTo(part!.Value.Processed, record.Id, primary.Record.Id, primaryTrace, notes);
            if (aligned is not null)
            {
                stackInput.Add(aligned);
                used.Add(record.Id);
            }
        }

        var stacked = primaryTrace with { Samples = TimeDomain.Stack(stackInput) };
        return new StationTrace(stacked, primary.Record.Id, primary.Part!.Value.Trace.Id, used,
            $"{label}, stack of {used.Count} shots", [.. notes, .. primaryTrace.Warnings]);
    }

    /// <summary>
    /// Expresses a trace in another record's time base by shifting it with the difference of the two
    /// records' trigger corrections. Returns null (with a note) when that is not possible.
    /// </summary>
    private double[]? AlignTo(ProcessedTrace trace, Guid recordId, Guid targetRecordId, ProcessedTrace target, List<string> notes)
    {
        if (Math.Abs(trace.SampleInterval - target.SampleInterval) > 1e-12 || Math.Abs(trace.Delay - target.Delay) > 1e-12)
        {
            notes.Add("Records with different sampling or delay cannot be combined; one was left out.");
            return null;
        }

        var shift = 0.0;
        if (Scenario.Trigger.Mode != TriggerCorrectionMode.None)
        {
            var a = CorrectionFor(recordId);
            var b = CorrectionFor(targetRecordId);
            if (a is null || b is null)
            {
                notes.Add("A record without a reference onset was combined without trigger alignment.");
            }
            else
            {
                shift = a.Value - b.Value;
            }
        }

        var samples = shift == 0 ? trace.Samples : TimeDomain.Shift(trace.Samples, shift, trace.SampleInterval);
        if (samples.Length != target.Samples.Length)
        {
            var resized = new double[target.Samples.Length];
            Array.Copy(samples, resized, Math.Min(samples.Length, resized.Length));
            samples = resized;
        }

        return samples;
    }
}
