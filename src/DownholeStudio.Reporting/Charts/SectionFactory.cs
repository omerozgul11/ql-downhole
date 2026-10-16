using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Visualization;

namespace DownholeStudio.Reporting.Charts;

/// <summary>Builds waveform sections (one trace per depth) for P or S picking displays and reports.</summary>
public static class SectionFactory
{
    public static WaveformSectionModel Build(ProjectState project, AnalysisResult? result, StationTraceBuilder traces, WaveType wave, PlotTheme theme,
        UnitPreferences units, Guid? selectedStation = null, bool showRawOverlay = false)
    {
        var model = new WaveformSectionModel
        {
            PositionTitle = $"Depth ({units.Symbol(Quantity.Length)})", TimeTitle = "Time after trigger (ms)",
            EmptyMessage = project.Survey.Stations.Count == 0 ? "Assign depths on the Geometry page to see traces." : "No traces for this wave type.",
        };
        var scenario = project.ActiveScenario;
        var picks = scenario.PicksFor(wave);
        var maxPick = 0.0;
        var maxEnd = 0.0;
        foreach (var station in project.Survey.StationsByDepth())
        {
            StationTrace? st = wave == WaveType.P ? traces.BuildP(station.Id) : traces.BuildS(station.Id).PickTrace;
            if (st is null)
            {
                continue;
            }

            var depth = project.Survey.Geometry.DepthBelowGround(station.MeasuredDepth);
            var markers = new List<PickMarker>();
            var color = wave == WaveType.P ? theme.P : theme.S;
            if (picks.TryGetValue(station.Id, out var pick))
            {
                if (pick.Time is { } t)
                {
                    markers.Add(new PickMarker(t, wave == WaveType.P ? PickMarkerKind.P : PickMarkerKind.S, color)
                    {
                        Automatic = pick.Origin == PickOrigin.Automatic, Excluded = !pick.Included, Selected = station.Id == selectedStation,
                    });
                    maxPick = Math.Max(maxPick, t);
                }

                if (pick.Suggestion is { } s && (pick.Time is null || Math.Abs(s.Time - pick.Time.Value) > st.Trace.SampleInterval * 0.5))
                {
                    markers.Add(new PickMarker(s.Time, PickMarkerKind.Suggestion, theme.Suggestion));
                }
            }

            var severity = result?.Qc.WorstFor(station.Id, wave);
            var qc = severity switch
            {
                Severity.Error => TraceQc.Problem,
                Severity.Warning => TraceQc.Review,
                _ => pick?.Time is not null ? TraceQc.Good : TraceQc.None,
            };
            double[]? overlay = null;
            if (showRawOverlay)
            {
                overlay = traces.Raw(st.PrimaryTraceId).Samples;
            }

            maxEnd = Math.Max(maxEnd, st.Trace.EndTime);
            model.Traces.Add(new WaveTrace
            {
                Key = station.Id, Samples = st.Trace.Samples, SampleInterval = st.Trace.SampleInterval, Delay = st.Trace.Delay,
                Label = units.Format(depth, Quantity.Length, decimals: 1), Position = depth, Selected = station.Id == selectedStation,
                Excluded = station.Excluded || pick is { Included: false }, Picks = markers, Qc = qc, Overlay = overlay,
                Gain = scenario.AdjustmentFor(st.PrimaryTraceId).Gain,
            });
        }

        model.TimeMin = 0;
        model.TimeMax = maxPick > 0 ? Math.Min(maxEnd, Math.Max(0.02, maxPick * 1.8)) : Math.Max(0.02, Math.Min(maxEnd, 0.25));
        return model;
    }
}
