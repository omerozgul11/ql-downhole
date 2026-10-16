using DownholeStudio.Analysis.Traces;
using DownholeStudio.Analysis.Trigger;
using DownholeStudio.Domain;

namespace DownholeStudio.Analysis.Geometry;

/// <summary>Straight-ray downhole geometry.</summary>
public static class StraightRay
{
    /// <summary>
    /// Slant distance from the source to a receiver: R = √((z − z_s)² + x²), with z the receiver depth,
    /// z_s the source depth and x the horizontal source offset (all metres).
    /// </summary>
    public static double SlantDistance(double receiverDepth, double sourceDepth, double horizontalOffset)
    {
        var dz = receiverDepth - sourceDepth;
        return Math.Sqrt(dz * dz + horizontalOffset * horizontalOffset);
    }

    /// <summary>
    /// Vertical-equivalent ("corrected") travel time t_v = t·(z − z_s)/R. Assumes a straight ray
    /// in a medium whose velocity does not change along the ray.
    /// </summary>
    public static double VerticalTime(double time, double receiverDepth, double sourceDepth, double horizontalOffset)
    {
        var r = SlantDistance(receiverDepth, sourceDepth, horizontalOffset);
        return r > 0 ? time * (receiverDepth - sourceDepth) / r : double.NaN;
    }

    /// <summary>True interval velocity (R₂ − R₁)/(t₂ − t₁) between two receivers.</summary>
    public static double IntervalVelocity(double r1, double t1, double r2, double t2) => (r2 - r1) / (t2 - t1);
}

/// <summary>One arrival time at one station with all corrections applied, and why it may be unusable.</summary>
public sealed record StationObservation
{
    public required Guid StationId { get; init; }
    public required WaveType Wave { get; init; }
    /// <summary>Receiver depth below the ground surface, metres.</summary>
    public double Depth { get; init; }
    public double MeasuredDepth { get; init; }
    /// <summary>Pick in raw record time, seconds.</summary>
    public double? RawTime { get; init; }
    public double? ReferenceTime { get; init; }
    public double? TriggerCorrection { get; init; }
    /// <summary>Raw time + trigger correction: travel time from the source to the receiver.</summary>
    public double? Time { get; init; }
    public double SlantDistance { get; init; }
    /// <summary>Vertical-equivalent travel time (straight ray), seconds.</summary>
    public double? VerticalTime { get; init; }
    public ArrivalPick? Pick { get; init; }
    public Guid? PrimaryRecordId { get; init; }
    /// <summary>True when the observation is used in velocity calculations.</summary>
    public bool Included { get; init; }
    /// <summary>Why the observation is not used (excluded by the user, missing pick, missing trigger correction…).</summary>
    public string? ExclusionReason { get; init; }
}

/// <summary>Builds station observations (picks + trigger + geometry) for a wave type.</summary>
public static class ObservationBuilder
{
    public static IReadOnlyList<StationObservation> Build(ProjectState project, WaveType wave, IReadOnlyDictionary<Guid, RecordTimingCorrection> timing)
    {
        var scenario = project.ActiveScenario;
        var geometry = project.Survey.Geometry;
        var source = geometry.SourceFor(wave);
        var picks = scenario.PicksFor(wave);
        var list = new List<StationObservation>();
        foreach (var station in project.Survey.StationsByDepth())
        {
            var depth = geometry.DepthBelowGround(station.MeasuredDepth);
            var primary = PrimaryRecord(project.Survey, station.Id, wave);
            picks.TryGetValue(station.Id, out var pick);
            RecordTimingCorrection? correction = primary is not null && timing.TryGetValue(primary.Id, out var c) ? c : null;
            double? time = pick?.Time is { } t && correction?.Correction is { } k ? t + k : null;
            var slant = StraightRay.SlantDistance(depth, source.SourceDepth, source.HorizontalOffset);
            double? vertical = time is { } tt ? StraightRay.VerticalTime(tt, depth, source.SourceDepth, source.HorizontalOffset) : null;

            string? reason = null;
            if (station.Excluded)
            {
                reason = "Depth excluded by the user.";
            }
            else if (primary is null)
            {
                reason = $"No {(wave == WaveType.P ? "P" : "S")} record is assigned to this depth.";
            }
            else if (pick?.Time is null)
            {
                reason = $"No {(wave == WaveType.P ? "P" : "S")} arrival is picked.";
            }
            else if (!pick.Included)
            {
                reason = "Pick excluded by the user.";
            }
            else if (correction?.Correction is null)
            {
                reason = correction?.Reason ?? "Trigger correction unavailable.";
            }
            else if (depth <= source.SourceDepth)
            {
                reason = "Receiver is not below the source; the geometry correction is undefined.";
            }

            list.Add(new StationObservation
            {
                StationId = station.Id, Wave = wave, Depth = depth, MeasuredDepth = station.MeasuredDepth, RawTime = pick?.Time,
                ReferenceTime = correction?.ReferenceTime, TriggerCorrection = correction?.Correction, Time = time, SlantDistance = slant,
                VerticalTime = vertical, Pick = pick, PrimaryRecordId = primary?.Id, Included = reason is null, ExclusionReason = reason,
            });
        }

        return list;
    }

    /// <summary>The record whose time base a station's picks use: first P record, or first S+ (else S−) record.</summary>
    public static SeismicRecord? PrimaryRecord(Survey survey, Guid stationId, WaveType wave)
    {
        if (wave == WaveType.P)
        {
            return survey.RecordsAt(stationId, ShotKind.P).FirstOrDefault();
        }

        return survey.RecordsAt(stationId, ShotKind.SPositive).FirstOrDefault() ?? survey.RecordsAt(stationId, ShotKind.SNegative).FirstOrDefault();
    }

    /// <summary>
    /// Predicts the raw arrival time at a station from included neighbouring observations, by
    /// fitting a straight line to time versus slant distance of up to <paramref name="neighbours"/>
    /// nearest stations. Returns null when fewer than two neighbours exist.
    /// </summary>
    public static double? Predict(IReadOnlyList<StationObservation> observations, double slantDistance, Guid excludeStation, int neighbours = 3)
    {
        var near = observations.Where(o => o.Included && o.StationId != excludeStation && o.Time is not null)
            .OrderBy(o => Math.Abs(o.SlantDistance - slantDistance)).Take(neighbours).ToList();
        if (near.Count < 2)
        {
            return null;
        }

        var fit = Velocity.LinearRegression.Fit(near.Select(o => o.SlantDistance).ToArray(), near.Select(o => o.Time!.Value).ToArray());
        return fit is null ? null : fit.Intercept + fit.Slope * slantDistance;
    }
}
