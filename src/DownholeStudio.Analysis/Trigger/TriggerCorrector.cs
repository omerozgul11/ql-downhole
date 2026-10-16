using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Trigger;

/// <summary>Timing correction for one record.</summary>
/// <param name="RecordId">Record.</param>
/// <param name="ReferenceTime">Picked onset on the reference/trigger geophone (raw record time), if any.</param>
/// <param name="Correction">Seconds to add to raw arrival times; null when the correction cannot be determined.</param>
/// <param name="Reason">Why the correction is missing, when it is.</param>
public sealed record RecordTimingCorrection(Guid RecordId, double? ReferenceTime, double? Correction, string? Reason);

public interface ITriggerCorrector
{
    /// <summary>Computes per-record corrections. Raw times are never modified; corrections are added on top.</summary>
    IReadOnlyDictionary<Guid, RecordTimingCorrection> Compute(ProjectState project);
}

/// <summary>
/// Reference-geophone trigger correction.
/// <list type="bullet">
/// <item>None: correction = 0 for every record.</item>
/// <item>SubtractReferenceOnset: correction = −t_ref + t_expected, i.e. times are measured from the
/// moment the wave left the source, with t_expected the source-to-reference travel time.</item>
/// <item>AlignToReferenceMedian: correction = −(t_ref − median(t_ref)), which removes shot-to-shot
/// trigger jitter while keeping the common trigger offset.</item>
/// </list>
/// </summary>
public sealed class ReferenceTriggerCorrector : ITriggerCorrector
{
    public IReadOnlyDictionary<Guid, RecordTimingCorrection> Compute(ProjectState project)
    {
        var scenario = project.ActiveScenario;
        var settings = scenario.Trigger;
        var result = new Dictionary<Guid, RecordTimingCorrection>();
        var refTimes = new Dictionary<Guid, double>();
        foreach (var record in project.Survey.Records)
        {
            if (scenario.ReferencePicks.TryGetValue(record.Id, out var pick) && pick.Included && pick.Time is { } t)
            {
                refTimes[record.Id] = t;
            }
        }

        var median = refTimes.Count > 0 ? Statistics.Median(refTimes.Values) : double.NaN;
        foreach (var record in project.Survey.Records)
        {
            double? referenceTime = refTimes.TryGetValue(record.Id, out var rt) ? rt : null;
            switch (settings.Mode)
            {
                case TriggerCorrectionMode.None:
                    result[record.Id] = new RecordTimingCorrection(record.Id, referenceTime, 0.0, null);
                    break;
                case TriggerCorrectionMode.SubtractReferenceOnset:
                    result[record.Id] = referenceTime is { } r1
                        ? new RecordTimingCorrection(record.Id, r1, -r1 + settings.ExpectedReferenceTime, null)
                        : new RecordTimingCorrection(record.Id, null, null, "No reference-geophone onset is picked for this record.");
                    break;
                case TriggerCorrectionMode.AlignToReferenceMedian:
                    result[record.Id] = referenceTime is { } r2
                        ? new RecordTimingCorrection(record.Id, r2, -(r2 - median), null)
                        : new RecordTimingCorrection(record.Id, null, null, "No reference-geophone onset is picked for this record.");
                    break;
            }
        }

        return result;
    }
}
