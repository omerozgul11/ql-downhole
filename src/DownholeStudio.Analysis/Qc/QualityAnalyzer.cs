using DownholeStudio.Analysis.Engineering;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.Picking;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Analysis.Trigger;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;
using DownholeStudio.SignalProcessing;

namespace DownholeStudio.Analysis.Qc;

/// <summary>A quality-control finding. Error = likely problem (red), Warning = review (yellow), Information = note.</summary>
public sealed record QcFlag
{
    public required Severity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Explanation { get; init; }
    public Guid? SubjectId { get; init; }
    public WaveType? Wave { get; init; }
    public double? Depth { get; init; }
}

public enum AnalysisQuality
{
    Incomplete,
    ReviewRecommended,
    Good,
}

public sealed record QcSummary
{
    public int ImportedRecords { get; init; }
    public int Stations { get; init; }
    public int MissingMeasurements { get; init; }
    public int PPicksAccepted { get; init; }
    public int SPicksAccepted { get; init; }
    public int LowConfidencePicks { get; init; }
    public int TriggerCorrectedRecords { get; init; }
    public TriggerCorrectionMode TriggerMode { get; init; }
    public int ExcludedMeasurements { get; init; }
    public double? MinDepth { get; init; }
    public double? MaxDepth { get; init; }
    public bool Vs30Available { get; init; }
    public bool Vs30Extrapolated { get; init; }
    public double? PRms { get; init; }
    public double? SRms { get; init; }
}

public sealed record QcReport
{
    public IReadOnlyList<QcFlag> Flags { get; init; } = [];
    public AnalysisQuality Quality { get; init; }
    public IReadOnlyList<string> QualityReasons { get; init; } = [];
    public required QcSummary Summary { get; init; }

    public IEnumerable<QcFlag> For(Guid subject, WaveType? wave = null) =>
        Flags.Where(f => f.SubjectId == subject && (wave is null || f.Wave is null || f.Wave == wave));

    /// <summary>Worst severity for a subject, or null when it has no flags.</summary>
    public Severity? WorstFor(Guid subject, WaveType? wave = null)
    {
        var flags = For(subject, wave).ToList();
        return flags.Count == 0 ? null : flags.Max(f => f.Severity);
    }
}

/// <summary>Automatic quality checks. They flag; they never change data.</summary>
public sealed class QualityAnalyzer
{
    public QcReport Evaluate(ProjectState project, WaveVelocityResult p, WaveVelocityResult s, IReadOnlyList<LayerResult> layers,
        TimeAveragedVelocity vs30, IReadOnlyDictionary<Guid, RecordTimingCorrection> timing, StationTraceBuilder? traces)
    {
        var scenario = project.ActiveScenario;
        var qc = scenario.Analysis.Qc;
        var flags = new List<QcFlag>();

        // Residuals are only meaningful against an interpreted layer model; with a single implicit layer
        // every curvature of the travel-time curve would be reported as a problem.
        var layersInterpreted = scenario.Layers.Layers.Count > 1;
        foreach (var wave in new[] { p, s })
        {
            CheckPicks(wave, qc, flags, layersInterpreted);
            CheckIntervals(wave, qc, flags);
        }

        if (!layersInterpreted && (p.Observations.Any(o => o.Included) || s.Observations.Any(o => o.Included)))
        {
            flags.Add(new QcFlag { Severity = Severity.Information, Code = "model.noLayers", Message = "Residual checks start once layer boundaries are interpreted on the Velocity Model page." });
        }

        if (traces is not null)
        {
            CheckWaveforms(project, s, p, qc, traces, flags);
        }

        CheckTrigger(project, timing, qc, flags);
        CheckLayers(layers, qc, flags);
        if (vs30.Value is null)
        {
            flags.Add(new QcFlag { Severity = Severity.Warning, Code = "vs30.unavailable", Message = vs30.Reason ?? "Vs30 is not available." });
        }
        else if (vs30.Extrapolated)
        {
            flags.Add(new QcFlag
            {
                Severity = Severity.Warning, Code = "vs30.extrapolated",
                Message = $"Vs30 is EXTRAPOLATED below {vs30.ExtrapolatedFrom:0.##} m by extending the deepest layer.",
            });
        }

        var firstS = s.Observations.Where(o => o.Included).Select(o => o.Depth).DefaultIfEmpty(double.NaN).Min();
        if (double.IsFinite(firstS) && firstS > 2.0)
        {
            flags.Add(new QcFlag
            {
                Severity = Severity.Information, Code = "model.topUnmeasured",
                Message = $"The top {firstS:0.##} m has no S measurement; the first layer's velocity is assumed to apply from the ground surface.",
            });
        }

        var summary = Summarise(project, p, s, vs30, timing, flags);
        var (quality, reasons) = Verdict(project, p, s, layers, vs30, flags, summary);
        return new QcReport { Flags = flags, Quality = quality, QualityReasons = reasons, Summary = summary };
    }

    private static void CheckPicks(WaveVelocityResult wave, QcThresholds qc, List<QcFlag> flags, bool checkResiduals)
    {
        var name = wave.Wave == WaveType.P ? "P" : "S";
        var limit = wave.Wave == WaveType.P ? qc.PResidualLimit : qc.SResidualLimit;
        StationObservation? previous = null;
        foreach (var o in wave.Observations.OrderBy(o => o.Depth))
        {
            if (o.Pick?.Time is null)
            {
                continue;
            }

            var pick = o.Pick;
            if (pick.Snr is { } snr)
            {
                if (snr < qc.MinimumSnr)
                {
                    flags.Add(Flag(Severity.Error, "pick.lowSnr", $"{name} pick at {o.Depth:0.##} m has a low signal-to-noise ratio ({snr:0.#}).", o, wave.Wave,
                        "The arrival is weak compared with the noise before it; the pick may be late or on noise."));
                }
                else if (snr < qc.ReviewSnr)
                {
                    flags.Add(Flag(Severity.Warning, "pick.moderateSnr", $"{name} pick at {o.Depth:0.##} m has a moderate signal-to-noise ratio ({snr:0.#}).", o, wave.Wave, null));
                }
            }

            var sharp = pick.Confidence?.Factors.FirstOrDefault(f => f.Name == "Onset sharpness");
            if (sharp is { Score: < 0.35 })
            {
                flags.Add(Flag(Severity.Warning, "pick.weakOnset", $"{name} onset at {o.Depth:0.##} m is emergent (weak onset).", o, wave.Wave, sharp.Explanation));
            }

            if (pick.Confidence is { Level: ConfidenceLevel.Low } c && !pick.IsManual)
            {
                var why = string.Join(" ", c.Concerns.Select(f => f.Explanation));
                flags.Add(Flag(Severity.Warning, "pick.lowConfidence", $"Automatic {name} pick at {o.Depth:0.##} m has low confidence ({c.Score:0.00}).", o, wave.Wave, why));
            }

            if (pick.Origin == PickOrigin.Automatic)
            {
                flags.Add(Flag(Severity.Information, "pick.unreviewed", $"{name} pick at {o.Depth:0.##} m is automatic and has not been reviewed.", o, wave.Wave, null));
            }

            if (checkResiduals && o.Included && wave.Residuals.TryGetValue(o.StationId, out var res) && Math.Abs(res) > limit)
            {
                flags.Add(Flag(Math.Abs(res) > 2 * limit ? Severity.Error : Severity.Warning, "pick.residual",
                    $"{name} pick at {o.Depth:0.##} m deviates {res * 1000:+0.00;-0.00} ms from the layer model (limit {limit * 1000:0.##} ms).", o, wave.Wave,
                    "A large residual can mean a mispick, a layer boundary in the wrong place, or a timing problem."));
            }

            if (o.Included && previous is { Included: true, Time: not null } && o.Time is not null && o.Time < previous.Time && o.SlantDistance > previous.SlantDistance)
            {
                flags.Add(Flag(Severity.Error, "pick.decreasing",
                    $"{name} arrival at {o.Depth:0.##} m is earlier than at {previous.Depth:0.##} m although the receiver is farther from the source.", o, wave.Wave,
                    "Review the pick, the depth assignment and the trigger correction."));
            }

            if (o.Included)
            {
                previous = o;
            }
        }
    }

    private static void CheckIntervals(WaveVelocityResult wave, QcThresholds qc, List<QcFlag> flags)
    {
        var name = wave.Wave == WaveType.P ? "Vp" : "Vs";
        var valid = wave.PseudoIntervals.Where(i => i.Velocity is not null).ToList();
        for (var i = 1; i < valid.Count; i++)
        {
            var a = valid[i - 1].Velocity!.Value;
            var b = valid[i].Velocity!.Value;
            var ratio = Math.Max(a, b) / Math.Min(a, b);
            if (ratio > qc.VelocityJumpRatio)
            {
                flags.Add(new QcFlag
                {
                    Severity = Severity.Warning, Code = "velocity.jump", Wave = wave.Wave, SubjectId = valid[i].UpperStation, Depth = valid[i].Top,
                    Message = $"Interval {name} changes by a factor of {ratio:0.#} at {valid[i].Top:0.##} m ({a:0} → {b:0} m/s).",
                    Explanation = "Large jumps between adjacent intervals often come from inconsistent adjacent picks; they can also be real boundaries.",
                });
            }
        }

        foreach (var iv in wave.PseudoIntervals.Where(i => i.Velocity is null))
        {
            flags.Add(new QcFlag
            {
                Severity = Severity.Warning, Code = "velocity.invalidInterval", Wave = wave.Wave, SubjectId = iv.LowerStation, Depth = iv.Bottom,
                Message = $"No interval {name} between {iv.Top:0.##} and {iv.Bottom:0.##} m: {iv.Reason}",
            });
        }
    }

    private static void CheckWaveforms(ProjectState project, WaveVelocityResult s, WaveVelocityResult p, QcThresholds qc, StationTraceBuilder traces, List<QcFlag> flags)
    {
        var settings = project.ActiveScenario.SPicker;
        foreach (var o in s.Observations.Where(o => o.Pick?.Time is not null))
        {
            var st = traces.BuildS(o.StationId);
            if (st.Positive is null || st.Negative is null)
            {
                flags.Add(Flag(Severity.Warning, "s.noOpposite", $"S pick at {o.Depth:0.##} m has no opposite-polarity confirmation (S+ or S− record missing).", o, WaveType.S, null));
                continue;
            }

            var r = PickConfidenceEstimator.OppositeCorrelation(st, o.Pick!.Time!.Value, settings.NoiseWindow * 0.25, settings.SignalWindow * 2);
            if (r is { } rv)
            {
                if (rv > 0)
                {
                    flags.Add(Flag(Severity.Error, "s.samePolarity", $"At {o.Depth:0.##} m the S+ and S− shots do not reverse polarity (r = {rv:0.##}).", o, WaveType.S,
                        "A shear wave should change sign when the source direction is reversed. Check the shot assignment and component."));
                }
                else if (-rv < qc.MinimumCorrelation)
                {
                    flags.Add(Flag(Severity.Warning, "s.poorCorrelation", $"At {o.Depth:0.##} m the opposite shots correlate poorly (r = {rv:0.##}).", o, WaveType.S, null));
                }
            }

            CheckComponent(project, traces, o, WaveType.S, flags);
        }

        foreach (var o in p.Observations.Where(o => o.Pick?.Time is not null))
        {
            CheckComponent(project, traces, o, WaveType.P, flags);
        }
    }

    /// <summary>Flags arrivals whose energy is concentrated on an unexpected component.</summary>
    private static void CheckComponent(ProjectState project, StationTraceBuilder traces, StationObservation o, WaveType wave, List<QcFlag> flags)
    {
        var record = o.PrimaryRecordId is { } id ? project.Survey.FindRecord(id) : null;
        if (record is null)
        {
            return;
        }

        var z = traces.FindComponent(record, ComponentRole.Vertical);
        var x = traces.FindComponent(record, ComponentRole.HorizontalX);
        var y = traces.FindComponent(record, ComponentRole.HorizontalY);
        if (z is null || (x is null && y is null))
        {
            return;
        }

        var t = o.Pick!.Time!.Value;
        var window = wave == WaveType.P ? 0.005 : 0.015;
        double Energy(TraceInfo? info)
        {
            if (info is null)
            {
                return 0;
            }

            var tr = traces.Process(info.Id);
            var a = tr.ClampIndex(t);
            var b = tr.ClampIndex(t + window);
            return b > a ? Statistics.Rms(tr.Samples.AsSpan(a, b - a)) : 0;
        }

        var ez = Energy(z);
        var eh = Math.Sqrt(Energy(x) * Energy(x) + Energy(y) * Energy(y));
        if (wave == WaveType.P && eh > 3 * ez && ez > 0)
        {
            flags.Add(Flag(Severity.Warning, "component.p", $"Around the P pick at {o.Depth:0.##} m the horizontal components are much stronger than the vertical.", o, wave,
                "This can indicate an incorrect channel mapping or that the pick is on a later (shear) arrival."));
        }
        else if (wave == WaveType.S && ez > 3 * eh && eh > 0)
        {
            flags.Add(Flag(Severity.Warning, "component.s", $"Around the S pick at {o.Depth:0.##} m the vertical component is much stronger than the horizontals.", o, wave,
                "This can indicate an incorrect channel mapping."));
        }
    }

    private static void CheckTrigger(ProjectState project, IReadOnlyDictionary<Guid, RecordTimingCorrection> timing, QcThresholds qc, List<QcFlag> flags)
    {
        var mode = project.ActiveScenario.Trigger.Mode;
        var refs = timing.Values.Where(t => t.ReferenceTime is not null).Select(t => t.ReferenceTime!.Value).ToList();
        if (refs.Count >= 3)
        {
            var median = Statistics.Median(refs);
            foreach (var t in timing.Values.Where(t => t.ReferenceTime is not null))
            {
                var dev = t.ReferenceTime!.Value - median;
                if (Math.Abs(dev) > qc.TriggerDeviationLimit)
                {
                    var record = project.Survey.FindRecord(t.RecordId);
                    // Without correction the deviation biases arrival times (likely problem). With correction it is
                    // removed; only an extreme deviation still deserves a look (possible reference mispick).
                    var severity = mode == TriggerCorrectionMode.None ? Severity.Error
                        : Math.Abs(dev) > 4 * qc.TriggerDeviationLimit ? Severity.Warning : Severity.Information;
                    flags.Add(new QcFlag
                    {
                        Severity = severity, Code = "trigger.deviation", SubjectId = t.RecordId,
                        Message = $"Reference onset in \"{record?.Source.FileName}\" differs from the median by {dev * 1000:+0.00;-0.00} ms.",
                        Explanation = mode == TriggerCorrectionMode.None
                            ? "This suggests a trigger timing problem. Enable trigger correction or review the reference pick."
                            : "The correction removes this difference; confirm the reference pick is correct.",
                    });
                }
            }
        }

        if (mode != TriggerCorrectionMode.None)
        {
            foreach (var t in timing.Values.Where(t => t.Correction is null))
            {
                var record = project.Survey.FindRecord(t.RecordId);
                if (record is { Excluded: false, StationId: not null })
                {
                    flags.Add(new QcFlag
                    {
                        Severity = Severity.Error, Code = "trigger.missing", SubjectId = t.RecordId,
                        Message = $"\"{record.Source.FileName}\" has no reference onset, so its arrivals cannot be trigger-corrected and are not used.",
                    });
                }
            }
        }
    }

    private static void CheckLayers(IReadOnlyList<LayerResult> layers, QcThresholds qc, List<QcFlag> flags)
    {
        foreach (var l in layers)
        {
            var range = $"{l.Layer.Top:0.##}–{l.Layer.Bottom:0.##} m";
            foreach (var (lv, name) in new[] { (l.P, "Vp"), (l.S, "Vs") })
            {
                if (lv is null)
                {
                    continue;
                }

                if (lv.Velocity is null)
                {
                    flags.Add(new QcFlag { Severity = Severity.Warning, Code = "layer.noVelocity", SubjectId = l.Layer.Id, Depth = l.Layer.Top, Message = $"{name} for {range}: {lv.Reason}" });
                }
                else if (lv.Fit is { } fit && double.IsFinite(fit.RSquared) && lv.Method == VelocityMethod.LayerRegression && fit.Count >= 3 && fit.RSquared < qc.MinimumRSquared)
                {
                    flags.Add(new QcFlag
                    {
                        Severity = Severity.Warning, Code = "layer.poorFit", SubjectId = l.Layer.Id, Depth = l.Layer.Top,
                        Message = $"{name} regression for {range} is poor (R² = {fit.RSquared:0.###}).",
                        Explanation = "Consider moving or adding a layer boundary, or reviewing outlying picks.",
                    });
                }
                else if (lv.PointCount == 2)
                {
                    flags.Add(new QcFlag { Severity = Severity.Information, Code = "layer.twoPoints", SubjectId = l.Layer.Id, Depth = l.Layer.Top, Message = $"{name} for {range} rests on only two measurements; no goodness of fit is available." });
                }
            }

            foreach (var issue in l.Properties.Issues.Where(i => i.Code is not "eng.missingDensity" and not "eng.missingVelocity"))
            {
                flags.Add(new QcFlag { Severity = issue.Severity, Code = issue.Code, SubjectId = l.Layer.Id, Depth = l.Layer.Top, Message = $"{range}: {issue.Message}", Explanation = issue.Details });
            }

            if (l.Density.Issue is { } d)
            {
                flags.Add(new QcFlag { Severity = d.Severity == Severity.Warning && d.Code == "density.missing" ? Severity.Information : d.Severity, Code = d.Code, SubjectId = l.Layer.Id, Depth = l.Layer.Top, Message = d.Message });
            }

            if (l.Density.Estimated && l.Density.Value is not null)
            {
                flags.Add(new QcFlag { Severity = Severity.Information, Code = "density.estimated", SubjectId = l.Layer.Id, Depth = l.Layer.Top, Message = $"{range}: density is ESTIMATED with {l.Density.Method}." });
            }
        }
    }

    private static QcSummary Summarise(ProjectState project, WaveVelocityResult p, WaveVelocityResult s, TimeAveragedVelocity vs30,
        IReadOnlyDictionary<Guid, RecordTimingCorrection> timing, List<QcFlag> flags)
    {
        var stations = project.Survey.Stations.Count(st => !st.Excluded);
        var depths = p.Observations.Concat(s.Observations).Where(o => o.Included).Select(o => o.Depth).ToList();
        var lowConfidence = p.Observations.Concat(s.Observations).Count(o => o.Pick is { HasTime: true, IsManual: false, Confidence.Level: ConfidenceLevel.Low });
        var excluded = p.Observations.Concat(s.Observations).Count(o => o.Pick?.Time is not null && !o.Included);
        var missing = project.Survey.Stations.Where(st => !st.Excluded).Sum(st =>
            (p.Observations.Any(o => o.StationId == st.Id && o.Pick?.Time is not null) ? 0 : 1) +
            (s.Observations.Any(o => o.StationId == st.Id && o.Pick?.Time is not null) ? 0 : 1));
        var mode = project.ActiveScenario.Trigger.Mode;
        return new QcSummary
        {
            ImportedRecords = project.Survey.Records.Count, Stations = stations, MissingMeasurements = missing,
            PPicksAccepted = p.Observations.Count(o => o.Included), SPicksAccepted = s.Observations.Count(o => o.Included),
            LowConfidencePicks = lowConfidence, TriggerMode = mode,
            TriggerCorrectedRecords = mode == TriggerCorrectionMode.None ? 0 : timing.Values.Count(t => t.Correction is not null && Math.Abs(t.Correction.Value) > 0),
            ExcludedMeasurements = excluded, MinDepth = depths.Count > 0 ? depths.Min() : null, MaxDepth = depths.Count > 0 ? depths.Max() : null,
            Vs30Available = vs30.Value is not null, Vs30Extrapolated = vs30.Extrapolated, PRms = p.RmsResidual, SRms = s.RmsResidual,
        };
    }

    private static (AnalysisQuality, IReadOnlyList<string>) Verdict(ProjectState project, WaveVelocityResult p, WaveVelocityResult s,
        IReadOnlyList<LayerResult> layers, TimeAveragedVelocity vs30, List<QcFlag> flags, QcSummary summary)
    {
        var reasons = new List<string>();
        if (project.Survey.Records.Count == 0)
        {
            return (AnalysisQuality.Incomplete, ["No survey data has been imported."]);
        }

        if (summary.Stations == 0)
        {
            return (AnalysisQuality.Incomplete, ["No receiver depths have been assigned."]);
        }

        if (summary.PPicksAccepted < 2 && summary.SPicksAccepted < 2)
        {
            reasons.Add("Fewer than two usable P or S arrivals.");
        }

        if (summary.SPicksAccepted < summary.Stations / 2)
        {
            reasons.Add($"Only {summary.SPicksAccepted} of {summary.Stations} depths have a usable S arrival.");
        }

        if (layers.All(l => l.S?.Velocity is null) && layers.All(l => l.P?.Velocity is null))
        {
            reasons.Add("No layer velocities could be calculated.");
        }

        if (reasons.Count > 0)
        {
            return (AnalysisQuality.Incomplete, reasons);
        }

        var errors = flags.Count(f => f.Severity == Severity.Error);
        var warnings = flags.Count(f => f.Severity == Severity.Warning);
        if (errors > 0)
        {
            reasons.Add($"{errors} likely problem(s) flagged in red.");
        }

        var picks = summary.PPicksAccepted + summary.SPicksAccepted;
        var reviewPicks = flags.Where(f => f.Severity == Severity.Warning && f.Code.StartsWith("pick.", StringComparison.Ordinal)).Select(f => (f.SubjectId, f.Wave)).Distinct().Count();
        if (picks > 0 && reviewPicks > 0.2 * picks)
        {
            reasons.Add($"{reviewPicks} pick(s) need review ({100.0 * reviewPicks / picks:0}% of usable picks).");
        }

        if (vs30.Value is null)
        {
            reasons.Add(vs30.Reason ?? "Vs30 is not available.");
        }
        else if (vs30.Extrapolated)
        {
            reasons.Add("Vs30 is extrapolated.");
        }

        if (flags.Any(f => f.Code == "pick.unreviewed"))
        {
            reasons.Add("Some automatic picks have not been reviewed.");
        }

        if (reasons.Count > 0)
        {
            return (AnalysisQuality.ReviewRecommended, reasons);
        }

        return (AnalysisQuality.Good, [warnings == 0
            ? "All checks passed. Results still require professional review."
            : $"No likely problems; {warnings} minor item(s) for review. Results still require professional review."]);
    }

    private static QcFlag Flag(Severity severity, string code, string message, StationObservation o, WaveType wave, string? explanation) => new()
    {
        Severity = severity, Code = code, Message = message, SubjectId = o.StationId, Wave = wave, Depth = o.Depth, Explanation = explanation,
    };
}
