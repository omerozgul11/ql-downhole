using System.Collections.Immutable;
using System.Globalization;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Domain;

namespace DownholeStudio.Core;

/// <summary>Factory for every undoable project edit. Each edit is a pure state transformation with an audit description.</summary>
public static class Edits
{
    private static string Ms(double? seconds) => seconds is { } s ? (s * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "none";

    private static string M(double metres) => metres.ToString("0.###", CultureInfo.InvariantCulture) + " m";

    private static string WaveName(WaveType w) => w == WaveType.P ? "P" : "S";

    private static string DepthOf(ProjectState s, Guid stationId) =>
        s.Survey.FindStation(stationId) is { } st ? M(s.Survey.Geometry.DepthBelowGround(st.MeasuredDepth)) : "?";

    // ---------------------------------------------------------------- picks

    /// <summary>Sets or moves a manual pick (the automatic suggestion is kept).</summary>
    public static ProjectEdit SetPick(ProjectState state, WaveType wave, Guid stationId, double time, Guid? traceId, DateTimeOffset now,
        PickOrigin origin = PickOrigin.Manual, PickAlgorithm algorithm = PickAlgorithm.Manual)
    {
        var old = state.ActiveScenario.PicksFor(wave).GetValueOrDefault(stationId);
        var how = origin == PickOrigin.CrossCorrelation ? " by cross-correlation" : "";
        return new ProjectEdit("pick.set", $"{WaveName(wave)} pick at {DepthOf(state, stationId)} set to {Ms(time)}{how}", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave);
            var existing = picks.GetValueOrDefault(stationId);
            var pick = (existing ?? new ArrivalPick()) with
            {
                Time = time, Origin = origin, Algorithm = algorithm, Confidence = null, Snr = null,
                TraceId = traceId ?? existing?.TraceId, ModifiedAt = now, Included = existing?.Included ?? true,
            };
            return sc.WithPicks(wave, picks.SetItem(stationId, pick));
        })).With("wave", wave).With("station", stationId).With("previous", Ms(old?.Time)).With("time", Ms(time)).With("origin", origin);
    }

    public static ProjectEdit DeletePick(ProjectState state, WaveType wave, Guid stationId) =>
        new ProjectEdit("pick.delete", $"{WaveName(wave)} pick at {DepthOf(state, stationId)} removed", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave);
            if (!picks.TryGetValue(stationId, out var p))
            {
                return sc;
            }

            // Keep the automatic suggestion so it can be restored later.
            return sc.WithPicks(wave, p.Suggestion is null ? picks.Remove(stationId) : picks.SetItem(stationId, p with { Time = null, Origin = PickOrigin.Manual, Confidence = null, Snr = null }));
        })).With("wave", wave).With("station", stationId);

    /// <summary>Replaces a manual pick by the stored automatic suggestion (the user accepts it explicitly).</summary>
    public static ProjectEdit AcceptSuggestion(ProjectState state, WaveType wave, Guid stationId, DateTimeOffset now) =>
        new ProjectEdit("pick.acceptSuggestion", $"Accepted suggested {WaveName(wave)} pick at {DepthOf(state, stationId)}", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave);
            if (!picks.TryGetValue(stationId, out var p) || p.Suggestion is null)
            {
                return sc;
            }

            return sc.WithPicks(wave, picks.SetItem(stationId, p with
            {
                Time = p.Suggestion.Time, Origin = PickOrigin.AcceptedSuggestion, Algorithm = p.Suggestion.Algorithm, Confidence = p.Suggestion.Confidence,
                Snr = p.Suggestion.Snr, ModifiedAt = now,
            }));
        })).With("wave", wave).With("station", stationId);

    /// <summary>Restores the automatic pick (as an unreviewed automatic pick).</summary>
    public static ProjectEdit RestoreAutoPick(ProjectState state, WaveType wave, Guid stationId, DateTimeOffset now) =>
        new ProjectEdit("pick.restoreAuto", $"Restored automatic {WaveName(wave)} pick at {DepthOf(state, stationId)}", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave);
            if (!picks.TryGetValue(stationId, out var p) || p.Suggestion is null)
            {
                return sc;
            }

            return sc.WithPicks(wave, picks.SetItem(stationId, p with
            {
                Time = p.Suggestion.Time, Origin = PickOrigin.Automatic, Algorithm = p.Suggestion.Algorithm, Confidence = p.Suggestion.Confidence,
                Snr = p.Suggestion.Snr, ModifiedAt = now,
            }));
        })).With("wave", wave).With("station", stationId);

    /// <summary>Marks automatic picks as reviewed (accepted) without changing their times.</summary>
    public static ProjectEdit AcceptAutomaticPicks(WaveType wave, IReadOnlyCollection<Guid> stations, DateTimeOffset now) =>
        new ProjectEdit("pick.acceptAll", $"Accepted {stations.Count} automatic {WaveName(wave)} pick(s) after review", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave).ToBuilder();
            foreach (var id in stations)
            {
                if (picks.TryGetValue(id, out var p) && p.Origin == PickOrigin.Automatic && p.HasTime)
                {
                    picks[id] = p with { Origin = PickOrigin.AcceptedSuggestion, ModifiedAt = now };
                }
            }

            return sc.WithPicks(wave, picks.ToImmutable());
        })).With("wave", wave).With("count", stations.Count);

    public static ProjectEdit SetPickIncluded(ProjectState state, WaveType wave, Guid stationId, bool included) =>
        new ProjectEdit(included ? "pick.include" : "pick.exclude",
            $"{(included ? "Included" : "Excluded")} {WaveName(wave)} pick at {DepthOf(state, stationId)}", s => s.WithActiveScenario(sc =>
            {
                var picks = sc.PicksFor(wave);
                return picks.TryGetValue(stationId, out var p) ? sc.WithPicks(wave, picks.SetItem(stationId, p with { Included = included })) : sc;
            })).With("wave", wave).With("station", stationId);

    public static ProjectEdit SetPickComment(ProjectState state, WaveType wave, Guid stationId, string? comment) =>
        new ProjectEdit("pick.comment", $"Comment on {WaveName(wave)} pick at {DepthOf(state, stationId)}", s => s.WithActiveScenario(sc =>
        {
            var picks = sc.PicksFor(wave);
            var p = picks.GetValueOrDefault(stationId) ?? new ArrivalPick { Origin = PickOrigin.Manual };
            return sc.WithPicks(wave, picks.SetItem(stationId, p with { Comment = string.IsNullOrWhiteSpace(comment) ? null : comment }));
        })).With("comment", comment);

    public static ProjectEdit ApplyAutoPicks(WaveType wave, IReadOnlyList<Analysis.Picking.AutoPickOutcome> outcomes, PickAlgorithm algorithm, DateTimeOffset now, bool overwriteManual)
    {
        var count = outcomes.Count(o => o.Suggestion is not null);
        return new ProjectEdit("pick.auto", $"Automatic {WaveName(wave)} picking ({algorithm}) on {count} depth(s)",
            s => s.WithActiveScenario(sc => sc.WithPicks(wave, Analysis.Picking.AutoPicker.Merge(sc.PicksFor(wave), outcomes, now, overwriteManual))))
            .With("wave", wave).With("algorithm", algorithm).With("picked", count).With("failed", outcomes.Count - count).With("overwriteManual", overwriteManual);
    }

    public static ProjectEdit SetReferencePick(ProjectState state, Guid recordId, double? time, DateTimeOffset now)
    {
        var name = state.Survey.FindRecord(recordId)?.Source.FileName ?? "record";
        return new ProjectEdit("trigger.pick", $"Reference onset of \"{name}\" set to {Ms(time)}", s => s.WithActiveScenario(sc =>
        {
            var existing = sc.ReferencePicks.GetValueOrDefault(recordId);
            if (time is null)
            {
                return sc with { ReferencePicks = sc.ReferencePicks.Remove(recordId) };
            }

            var pick = (existing ?? new ArrivalPick()) with { Time = time, Origin = PickOrigin.Manual, Algorithm = PickAlgorithm.Manual, Confidence = null, ModifiedAt = now };
            return sc with { ReferencePicks = sc.ReferencePicks.SetItem(recordId, pick) };
        })).With("record", recordId).With("time", Ms(time));
    }

    public static ProjectEdit ApplyAutoReferencePicks(IReadOnlyList<Analysis.Picking.AutoPickOutcome> outcomes, DateTimeOffset now, bool overwriteManual) =>
        new ProjectEdit("trigger.auto", $"Automatic reference-onset picking on {outcomes.Count(o => o.Suggestion is not null)} record(s)",
            s => s.WithActiveScenario(sc => sc with { ReferencePicks = Analysis.Picking.AutoPicker.Merge(sc.ReferencePicks, outcomes, now, overwriteManual) }));

    public static ProjectEdit SetTrigger(TriggerSettings settings) =>
        new ProjectEdit("trigger.settings", $"Trigger correction: {settings.Mode}", s => s.WithActiveScenario(sc => sc with { Trigger = settings }))
            .With("mode", settings.Mode).With("expectedReferenceTime", Ms(settings.ExpectedReferenceTime));

    public static ProjectEdit SetPickerSettings(WaveType? wave, PickerSettings settings) =>
        new ProjectEdit("picker.settings", $"{(wave is null ? "Reference" : WaveName(wave.Value))} picker settings changed", s => s.WithActiveScenario(sc => wave switch
        {
            WaveType.P => sc with { PPicker = settings },
            WaveType.S => sc with { SPicker = settings },
            _ => sc with { ReferencePicker = settings },
        })).With("algorithm", settings.Algorithm);

    // ---------------------------------------------------------------- processing

    public static ProjectEdit SetPipeline(ComponentGroup group, ProcessingPipeline pipeline, string description) =>
        new ProjectEdit("processing.pipeline", description, s => s.WithActiveScenario(sc => sc.WithPipeline(group, pipeline)))
            .With("group", group).With("pipeline", string.Join(" → ", pipeline.Steps.Where(x => x.Enabled).Select(x => x.DisplayName)));

    public static ProjectEdit AddStep(ProjectState state, ComponentGroup group, ProcessingStep step, int? index = null)
    {
        var p = state.ActiveScenario.PipelineFor(group);
        var steps = index is { } i ? p.Steps.Insert(Math.Clamp(i, 0, p.Steps.Count), step) : p.Steps.Add(step);
        return SetPipeline(group, p with { Steps = steps }, $"Added {step.DisplayName} ({group})").With("step", step.DisplayName)
            .With("parameters", string.Join(", ", step.DescribeParameters().Select(kv => $"{kv.Key}={kv.Value}")));
    }

    public static ProjectEdit ReplaceStep(ProjectState state, ComponentGroup group, ProcessingStep step)
    {
        var p = state.ActiveScenario.PipelineFor(group);
        var idx = p.Steps.FindIndex(x => x.Id == step.Id);
        return idx < 0 ? AddStep(state, group, step) : SetPipeline(group, p with { Steps = p.Steps.SetItem(idx, step) }, $"Changed {step.DisplayName} ({group})")
            .With("parameters", string.Join(", ", step.DescribeParameters().Select(kv => $"{kv.Key}={kv.Value}"))).With("enabled", step.Enabled);
    }

    public static ProjectEdit RemoveStep(ProjectState state, ComponentGroup group, Guid stepId)
    {
        var p = state.ActiveScenario.PipelineFor(group);
        var step = p.Steps.FirstOrDefault(x => x.Id == stepId);
        return SetPipeline(group, p with { Steps = p.Steps.RemoveAll(x => x.Id == stepId) }, $"Removed {step?.DisplayName ?? "step"} ({group})");
    }

    public static ProjectEdit MoveStep(ProjectState state, ComponentGroup group, Guid stepId, int delta)
    {
        var p = state.ActiveScenario.PipelineFor(group);
        var idx = p.Steps.FindIndex(x => x.Id == stepId);
        var target = Math.Clamp(idx + delta, 0, p.Steps.Count - 1);
        if (idx < 0 || idx == target)
        {
            return SetPipeline(group, p, "Reordered processing");
        }

        var step = p.Steps[idx];
        return SetPipeline(group, p with { Steps = p.Steps.RemoveAt(idx).Insert(target, step) }, $"Moved {step.DisplayName} {(delta < 0 ? "up" : "down")} ({group})");
    }

    public static ProjectEdit SetTraceAdjustment(ProjectState state, Guid traceId, TraceAdjustment adjustment, string description) =>
        new ProjectEdit("trace.adjust", description, s => s.WithActiveScenario(sc => sc with
        {
            TraceAdjustments = adjustment == TraceAdjustment.Default ? sc.TraceAdjustments.Remove(traceId) : sc.TraceAdjustments.SetItem(traceId, adjustment),
        })).With("trace", traceId).With("gain", adjustment.Gain).With("reversePolarity", adjustment.ReversePolarity)
            .With("timeShift", Ms(adjustment.TimeShift)).With("excluded", adjustment.Excluded);

    // ---------------------------------------------------------------- survey / geometry

    public static ProjectEdit SetGeometry(SurveyGeometry geometry, string description) =>
        new ProjectEdit("geometry.edit", description, s => s.WithSurvey(v => v with { Geometry = geometry }))
            .With("pOffset", M(geometry.PSource.HorizontalOffset)).With("sOffset", M(geometry.SSource.HorizontalOffset))
            .With("pSourceDepth", M(geometry.PSource.SourceDepth)).With("sSourceDepth", M(geometry.SSource.SourceDepth))
            .With("depthReference", geometry.DepthReference).With("collarHeight", M(geometry.Borehole.CollarHeight)).With("direction", geometry.Direction);

    public static ProjectEdit SetChannelMap(ChannelMap map, string description) =>
        new ProjectEdit("channels.map", description, s => s.WithSurvey(v => v with { ChannelMap = map }))
            .With("profile", map.ProfileName).With("channels", string.Join(", ", map.Channels.Select(c => $"{c.ChannelNumber}:{c.Role}{(c.ReversePolarity ? "(rev)" : "")}")));

    public static ProjectEdit SetStations(ImmutableList<Station> stations, ImmutableList<SeismicRecord> records, string description) =>
        new ProjectEdit("geometry.depths", description, s => s.WithSurvey(v => v with { Stations = stations, Records = records }))
            .With("stations", stations.Count);

    public static ProjectEdit SetStationDepth(ProjectState state, Guid stationId, double measuredDepth) =>
        new ProjectEdit("geometry.stationDepth", $"Depth {DepthOf(state, stationId)} changed to {M(measuredDepth)}",
            s => s.WithSurvey(v => v with { Stations = v.Stations.Select(st => st.Id == stationId ? st with { MeasuredDepth = measuredDepth } : st).ToImmutableList() }));

    public static ProjectEdit SetStationExcluded(ProjectState state, Guid stationId, bool excluded) =>
        new ProjectEdit(excluded ? "station.exclude" : "station.include", $"{(excluded ? "Excluded" : "Included")} depth {DepthOf(state, stationId)}",
            s => s.WithSurvey(v => v with { Stations = v.Stations.Select(st => st.Id == stationId ? st with { Excluded = excluded } : st).ToImmutableList() }));

    public static ProjectEdit SetRecordAssignment(ProjectState state, Guid recordId, Guid? stationId, ShotKind shot)
    {
        var name = state.Survey.FindRecord(recordId)?.Source.FileName ?? "record";
        return new ProjectEdit("record.assign", $"\"{name}\" assigned to {(stationId is { } st ? DepthOf(state, st) : "no depth")} as {shot}",
            s => s.WithSurvey(v => v with { Records = v.Records.Select(r => r.Id == recordId ? r with { StationId = stationId, Shot = shot } : r).ToImmutableList() }))
            .With("record", name).With("shot", shot);
    }

    public static ProjectEdit SetRecordExcluded(ProjectState state, Guid recordId, bool excluded)
    {
        var name = state.Survey.FindRecord(recordId)?.Source.FileName ?? "record";
        return new ProjectEdit(excluded ? "record.exclude" : "record.include", $"{(excluded ? "Excluded" : "Included")} \"{name}\"",
            s => s.WithSurvey(v => v with { Records = v.Records.Select(r => r.Id == recordId ? r with { Excluded = excluded } : r).ToImmutableList() }));
    }

    // ---------------------------------------------------------------- S waves

    public static ProjectEdit SetSWaveSettings(SWaveSettings settings, string description) =>
        new ProjectEdit("swave.settings", description, s => s.WithActiveScenario(sc => sc with { SWave = settings }))
            .With("component", settings.Component).With("pickTrace", settings.PickTrace);

    public static ProjectEdit SetRotation(ProjectState state, Guid stationId, double? degrees) =>
        new ProjectEdit("swave.rotation", degrees is null ? $"Removed horizontal rotation at {DepthOf(state, stationId)}" : $"Horizontal rotation at {DepthOf(state, stationId)} set to {degrees:0.#}°",
            s => s.WithActiveScenario(sc => sc with
            {
                SWave = sc.SWave with
                {
                    StationRotation = degrees is { } d ? sc.SWave.StationRotation.SetItem(stationId, d) : sc.SWave.StationRotation.Remove(stationId),
                    Component = degrees is not null ? SComponentMode.Rotated : sc.SWave.Component,
                },
            })).With("station", stationId).With("degrees", degrees);

    public static ProjectEdit SetStackRepeated(bool stack) =>
        new ProjectEdit("processing.stack", stack ? "Stack repeated shots" : "Use first shot only", s => s.WithActiveScenario(sc => sc with { StackRepeatedShots = stack }));

    // ---------------------------------------------------------------- layers / analysis

    public static ProjectEdit SetLayerModel(LayerModel model, string description, string operation = "layer.edit") =>
        new ProjectEdit(operation, description, s => s.WithActiveScenario(sc => sc with { Layers = model }))
            .With("boundaries", string.Join(", ", model.Boundaries.Select(M)));

    public static ProjectEdit AddBoundary(ProjectState state, double depth) =>
        SetLayerModel(LayerEdits.AddBoundary(state.ActiveScenario.Layers, depth), $"Added layer boundary at {M(depth)}", "layer.add");

    public static ProjectEdit MoveBoundary(ProjectState state, Guid layerId, double depth) =>
        SetLayerModel(LayerEdits.MoveBoundary(state.ActiveScenario.Layers, layerId, depth), $"Moved layer boundary to {M(depth)}", "layer.move");

    public static ProjectEdit RemoveBoundary(ProjectState state, Guid layerId)
    {
        var top = state.ActiveScenario.Layers.Layers.FirstOrDefault(l => l.Id == layerId)?.TopDepth ?? 0;
        return SetLayerModel(LayerEdits.RemoveBoundary(state.ActiveScenario.Layers, layerId), $"Deleted layer boundary at {M(top)}", "layer.delete");
    }

    public static ProjectEdit MergeLayer(ProjectState state, Guid layerId) =>
        SetLayerModel(LayerEdits.MergeWithBelow(state.ActiveScenario.Layers, layerId), "Merged layer with the layer below", "layer.merge");

    public static ProjectEdit SplitLayer(ProjectState state, Guid layerId, double modelBottom) =>
        SetLayerModel(LayerEdits.Split(state.ActiveScenario.Layers, layerId, modelBottom), "Split layer", "layer.split");

    public static ProjectEdit SetLayerDensity(ProjectState state, Guid layerId, double? density) =>
        SetLayerModel(LayerEdits.SetDensity(state.ActiveScenario.Layers, layerId, density),
            density is { } d ? $"Layer density set to {d:0} kg/m³" : "Layer density cleared", "density.layer");

    public static ProjectEdit SetDensityModel(DensityModel model) =>
        new ProjectEdit("density.model", $"Density source: {model.Source}{(model.Source == DensitySource.Estimated ? $" ({model.EstimationMethod})" : "")}",
            s => s.WithActiveScenario(sc => sc with { Density = model })).With("source", model.Source).With("method", model.EstimationMethod).With("constant", model.ConstantDensity);

    public static ProjectEdit SetAnalysisSettings(AnalysisSettings settings, string description) =>
        new ProjectEdit("analysis.settings", description, s => s.WithActiveScenario(sc => sc with { Analysis = settings }))
            .With("method", settings.Method).With("regression", settings.Regression).With("vs30Extrapolation", settings.Vs30.AllowExtrapolation);

    // ---------------------------------------------------------------- project / report

    public static ProjectEdit SetMetadata(ProjectMetadata metadata) =>
        new ProjectEdit("project.metadata", "Project information changed", s => s with { Metadata = metadata });

    public static ProjectEdit SetReport(ReportConfiguration report, string description = "Report settings changed") =>
        new ProjectEdit("report.settings", description, s => s with { Report = report });
}
