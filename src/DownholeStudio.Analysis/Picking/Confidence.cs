using System.Collections.Immutable;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Picking;

/// <summary>Evidence available for judging a pick.</summary>
public sealed record ConfidenceEvidence
{
    /// <summary>The pick being judged (raw record time).</summary>
    public double? PickTime { get; init; }
    public double? Snr { get; init; }
    public double? Sharpness { get; init; }
    /// <summary>Times found by the other picking algorithms on the same window.</summary>
    public IReadOnlyList<double> OtherAlgorithmTimes { get; init; } = [];
    /// <summary>Raw time predicted from neighbouring picks, if available.</summary>
    public double? PredictedTime { get; init; }
    /// <summary>Correlation of the S+ shot with the S− shot around the pick (expected to be strongly negative).</summary>
    public double? OppositeShotCorrelation { get; init; }
    public double SampleInterval { get; init; }
}

/// <summary>
/// Combines independent measures into a 0–1 confidence with an explanation per factor.
/// Weights: SNR 0.35, onset sharpness 0.20, algorithm agreement 0.20, neighbour consistency 0.15,
/// opposite-polarity agreement 0.10 (S only). Missing evidence is left out and the remaining
/// weights are renormalised, so the score always reflects only what was measured.
/// </summary>
public sealed class PickConfidenceEstimator
{
    public PickConfidence Estimate(ConfidenceEvidence e, WaveType wave, QcThresholds qc)
    {
        var factors = new List<ConfidenceFactor>();
        if (e.Snr is { } snr)
        {
            var score = Math.Clamp((Math.Log10(Math.Max(snr, 1e-9)) - Math.Log10(2)) / (Math.Log10(12) - Math.Log10(2)), 0, 1);
            factors.Add(new ConfidenceFactor
            {
                Name = "Signal-to-noise ratio", Value = snr, Score = score, Weight = 0.35,
                Explanation = snr < qc.MinimumSnr
                    ? $"SNR {snr:0.#} is below {qc.MinimumSnr:0.#}: the onset is weak compared with the noise before it."
                    : snr < qc.ReviewSnr ? $"SNR {snr:0.#} is moderate." : $"SNR {snr:0.#} is good.",
            });
        }

        if (e.Sharpness is { } sharp)
        {
            factors.Add(new ConfidenceFactor
            {
                Name = "Onset sharpness", Value = sharp, Score = sharp, Weight = 0.20,
                Explanation = sharp < 0.35 ? "The picker's response has no distinct minimum/peak: the onset is emergent." : sharp < 0.7 ? "The onset is moderately distinct." : "The onset is sharp.",
            });
        }

        if (e.PickTime is { } pick && e.OtherAlgorithmTimes.Count > 0)
        {
            var tolerance = Math.Max(3 * e.SampleInterval, wave == WaveType.P ? 0.0003 : 0.001);
            // An independent algorithm that finds the same onset is strong evidence; use the closest one.
            var dev = e.OtherAlgorithmTimes.Min(t => Math.Abs(t - pick));
            var score = dev <= tolerance ? 1.0 : Math.Clamp(1 - (dev - tolerance) / (4 * tolerance), 0, 1);
            factors.Add(new ConfidenceFactor
            {
                Name = "Algorithm agreement", Value = dev * 1000, Score = score, Weight = 0.20,
                Explanation = score >= 1 ? "An independent picking algorithm finds the same onset." : $"The closest independent algorithm differs by {dev * 1000:0.##} ms.",
            });
        }

        if (e.PickTime is { } picked && e.PredictedTime is { } predicted && double.IsFinite(predicted))
        {
            var limit = wave == WaveType.P ? qc.PResidualLimit : qc.SResidualLimit;
            var dev = Math.Abs(picked - predicted);
            var score = Math.Exp(-Math.Pow(dev / (2 * limit), 2));
            factors.Add(new ConfidenceFactor
            {
                Name = "Consistency with neighbouring depths", Value = dev * 1000, Score = score, Weight = 0.15,
                Explanation = score < 0.5 ? $"The pick is {dev * 1000:0.##} ms from the time expected from neighbouring depths." : "Consistent with neighbouring depths.",
            });
        }

        if (wave == WaveType.S && e.OppositeShotCorrelation is { } r)
        {
            var score = Math.Clamp(-r, 0, 1);
            factors.Add(new ConfidenceFactor
            {
                Name = "Opposite-polarity agreement", Value = r, Score = score, Weight = 0.10,
                Explanation = r <= -0.6 ? $"S+ and S− reverse polarity as expected (r = {r:0.##})."
                    : r < 0 ? $"S+ and S− show only weak polarity reversal (r = {r:0.##})."
                    : $"S+ and S− do not reverse polarity (r = {r:0.##}); the arrival may not be a shear wave.",
            });
        }

        var weight = factors.Sum(f => f.Weight);
        var total = weight > 0 ? factors.Sum(f => f.Score * f.Weight) / weight : 0;
        var level = total >= qc.HighConfidence ? ConfidenceLevel.High : total >= qc.MediumConfidence ? ConfidenceLevel.Medium : ConfidenceLevel.Low;
        return new PickConfidence { Score = total, Level = level, Factors = [.. factors] };
    }

    /// <summary>Pearson correlation of the S+ trace with the (aligned) S− trace over a window.</summary>
    public static double? OppositeCorrelation(SStationTraces s, double time, double before, double after)
    {
        if (s.Positive is null || s.Difference is null)
        {
            return null;
        }

        var p = s.Positive.Trace;
        var a = p.ClampIndex(time - before);
        var b = p.ClampIndex(time + after);
        if (b - a < 4)
        {
            return null;
        }

        // Aligned negative shot = positive − 2 × half-difference.
        var neg = new double[b - a];
        for (var i = a; i < b; i++)
        {
            neg[i - a] = p.Samples[i] - 2 * s.Difference.Samples[i];
        }

        var r = Statistics.Pearson(p.Samples.AsSpan(a, b - a), neg);
        return double.IsFinite(r) ? r : null;
    }
}

/// <summary>Outcome of automatic picking at one position.</summary>
public sealed record AutoPickOutcome(Guid SubjectId, PickSuggestion? Suggestion, Guid? TraceId, string? FailureReason);

/// <summary>
/// Runs a picker over many stations, top to bottom, narrowing the search window around the time
/// predicted from already-picked neighbours when enabled, then scores every pick.
/// </summary>
public sealed class AutoPicker(PickConfidenceEstimator? estimator = null)
{
    private readonly PickConfidenceEstimator _estimator = estimator ?? new PickConfidenceEstimator();

    public IReadOnlyList<AutoPickOutcome> PickStations(ProjectState project, StationTraceBuilder builder, WaveType wave,
        IReadOnlyCollection<Guid>? onlyStations, DateTimeOffset now, CancellationToken ct = default)
    {
        var scenario = project.ActiveScenario;
        var settings = wave == WaveType.P ? scenario.PPicker : scenario.SPicker;
        var qc = scenario.Analysis.Qc;
        var picker = PickerFactory.Create(settings.Algorithm);
        var others = PickerFactory.All.Where(p => p.Algorithm != picker.Algorithm).ToList();
        var geometry = project.Survey.Geometry;
        var source = geometry.SourceFor(wave);

        // Known times (travel time, i.e. corrected) from existing included picks seed the prediction.
        var known = new Dictionary<Guid, (double Slant, double Time)>();
        foreach (var obs in Geometry.ObservationBuilder.Build(project, wave, builder.Timing).Where(o => o.Included && o.Time is not null))
        {
            if (onlyStations is null || !onlyStations.Contains(obs.StationId))
            {
                known[obs.StationId] = (obs.SlantDistance, obs.Time!.Value);
            }
        }

        var results = new List<AutoPickOutcome>();
        var evidence = new Dictionary<Guid, (ConfidenceEvidence Evidence, double Time, double Correction)>();
        foreach (var station in project.Survey.StationsByDepth())
        {
            ct.ThrowIfCancellationRequested();
            if (onlyStations is not null && !onlyStations.Contains(station.Id))
            {
                continue;
            }

            if (station.Excluded)
            {
                results.Add(new AutoPickOutcome(station.Id, null, null, "Depth is excluded."));
                continue;
            }

            StationTrace? trace;
            SStationTraces? sTraces = null;
            if (wave == WaveType.P)
            {
                trace = builder.BuildP(station.Id);
            }
            else
            {
                sTraces = builder.BuildS(station.Id);
                trace = sTraces.PickTrace;
            }

            if (trace is null)
            {
                results.Add(new AutoPickOutcome(station.Id, null, null, $"No {(wave == WaveType.P ? "vertical P" : "horizontal S")} trace is available at this depth."));
                continue;
            }

            var t = trace.Trace;
            var start = Math.Max(settings.SearchStart ?? t.Delay, t.Delay);
            var end = Math.Min(settings.SearchEnd ?? t.EndTime, t.EndTime);
            var correction = builder.CorrectionFor(trace.PrimaryRecordId) ?? 0.0;
            var depth = geometry.DepthBelowGround(station.MeasuredDepth);
            var slant = Geometry.StraightRay.SlantDistance(depth, source.SourceDepth, source.HorizontalOffset);
            double? predictedRaw = null;
            if (settings.UseNeighbourPrediction && known.Count >= 2)
            {
                var near = known.Values.OrderBy(k => Math.Abs(k.Slant - slant)).Take(3).ToList();
                var fit = Velocity.LinearRegression.Fit(near.Select(k => k.Slant).ToArray(), near.Select(k => k.Time).ToArray());
                if (fit is { Slope: > 0 })
                {
                    predictedRaw = fit.Intercept + fit.Slope * slant - correction;
                    start = Math.Max(start, predictedRaw.Value - settings.PredictionHalfWidth * 2);
                    end = Math.Min(end, predictedRaw.Value + settings.PredictionHalfWidth * 2);
                    if (end - start < 8 * t.SampleInterval)
                    {
                        start = Math.Max(settings.SearchStart ?? t.Delay, t.Delay);
                        end = Math.Min(settings.SearchEnd ?? t.EndTime, t.EndTime);
                    }
                }
            }

            var output = picker.Pick(t, start, end, settings);
            if (output.Time is null)
            {
                results.Add(new AutoPickOutcome(station.Id, null, trace.PrimaryTraceId, output.FailureReason));
                continue;
            }

            var time = output.Time.Value;
            var alt = others.Select(o => o.Pick(t, start, end, settings).Time).Where(x => x is not null).Select(x => x!.Value).ToList();
            var snr = SnrEstimator.Compute(t, time, settings.NoiseWindow, settings.SignalWindow);
            double? oppositeR = sTraces is not null ? PickConfidenceEstimator.OppositeCorrelation(sTraces, time, settings.NoiseWindow * 0.25, settings.SignalWindow * 2) : null;
            var ev = new ConfidenceEvidence
            {
                PickTime = time, Snr = snr, Sharpness = output.Sharpness, OtherAlgorithmTimes = alt, OppositeShotCorrelation = oppositeR, SampleInterval = t.SampleInterval,
            };
            evidence[station.Id] = (ev, time, correction);
            known[station.Id] = (slant, time + correction);
            results.Add(new AutoPickOutcome(station.Id, new PickSuggestion { Time = time, Algorithm = picker.Algorithm, Snr = snr, CreatedAt = now }, trace.PrimaryTraceId, null));
        }

        // Second pass: neighbour consistency uses every pick except the one being scored.
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Suggestion is null || !evidence.TryGetValue(r.SubjectId, out var e))
            {
                continue;
            }

            var station = project.Survey.FindStation(r.SubjectId)!;
            var slant = Geometry.StraightRay.SlantDistance(geometry.DepthBelowGround(station.MeasuredDepth), source.SourceDepth, source.HorizontalOffset);
            var near = known.Where(k => k.Key != r.SubjectId).Select(k => k.Value).OrderBy(k => Math.Abs(k.Slant - slant)).Take(4).ToList();
            double? predicted = null;
            if (near.Count >= 2)
            {
                var fit = Velocity.LinearRegression.Fit(near.Select(k => k.Slant).ToArray(), near.Select(k => k.Time).ToArray());
                if (fit is not null)
                {
                    predicted = fit.Intercept + fit.Slope * slant - e.Correction;
                }
            }

            var confidence = _estimator.Estimate(e.Evidence with { PredictedTime = predicted }, wave, qc);
            results[i] = r with { Suggestion = r.Suggestion with { Confidence = confidence } };
        }

        return results;
    }

    /// <summary>Automatic onset on the reference/trigger channel of each record.</summary>
    public IReadOnlyList<AutoPickOutcome> PickReferences(ProjectState project, StationTraceBuilder builder, IReadOnlyCollection<Guid>? onlyRecords, DateTimeOffset now, CancellationToken ct = default)
    {
        var scenario = project.ActiveScenario;
        var settings = scenario.ReferencePicker;
        var picker = PickerFactory.Create(settings.Algorithm);
        var results = new List<AutoPickOutcome>();
        foreach (var record in project.Survey.Records.Where(r => !r.Excluded))
        {
            ct.ThrowIfCancellationRequested();
            if (onlyRecords is not null && !onlyRecords.Contains(record.Id))
            {
                continue;
            }

            var reference = builder.BuildReference(record.Id);
            if (reference is null)
            {
                results.Add(new AutoPickOutcome(record.Id, null, null, "This record has no channel mapped as Reference."));
                continue;
            }

            var t = reference.Trace;
            var output = picker.Pick(t, Math.Max(settings.SearchStart ?? t.Delay, t.Delay), Math.Min(settings.SearchEnd ?? t.EndTime, t.EndTime), settings);
            if (output.Time is null)
            {
                results.Add(new AutoPickOutcome(record.Id, null, reference.PrimaryTraceId, output.FailureReason));
                continue;
            }

            var snr = SnrEstimator.Compute(t, output.Time.Value, settings.NoiseWindow, settings.SignalWindow);
            var confidence = _estimator.Estimate(new ConfidenceEvidence { Snr = snr, Sharpness = output.Sharpness, SampleInterval = t.SampleInterval }, WaveType.P, scenario.Analysis.Qc);
            results.Add(new AutoPickOutcome(record.Id,
                new PickSuggestion { Time = output.Time.Value, Algorithm = picker.Algorithm, Snr = snr, Confidence = confidence, CreatedAt = now },
                reference.PrimaryTraceId, null));
        }

        return results;
    }

    /// <summary>
    /// Merges suggestions into a pick dictionary. Positions with a manual pick keep the manual time
    /// and only receive the new suggestion; others become automatic picks.
    /// </summary>
    public static ImmutableDictionary<Guid, ArrivalPick> Merge(ImmutableDictionary<Guid, ArrivalPick> picks, IEnumerable<AutoPickOutcome> outcomes, DateTimeOffset now, bool overwriteManual = false)
    {
        var b = picks.ToBuilder();
        foreach (var o in outcomes.Where(o => o.Suggestion is not null))
        {
            var s = o.Suggestion!;
            if (b.TryGetValue(o.SubjectId, out var existing) && existing.IsManual && existing.HasTime && !overwriteManual)
            {
                b[o.SubjectId] = existing with { Suggestion = s };
                continue;
            }

            b[o.SubjectId] = new ArrivalPick
            {
                Time = s.Time, Origin = PickOrigin.Automatic, Algorithm = s.Algorithm, Confidence = s.Confidence, Snr = s.Snr,
                Included = existing?.Included ?? true, Comment = existing?.Comment, TraceId = o.TraceId, Suggestion = s, ModifiedAt = now,
            };
        }

        return b.ToImmutable();
    }
}
