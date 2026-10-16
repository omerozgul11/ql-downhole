using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Analysis.SWave;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.SignalProcessing;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

/// <summary>S-wave picking with opposite-shot comparison, horizontal rotation and cross-correlation.</summary>
public sealed partial class SPickingPageViewModel : PickingPageViewModel
{
    private bool _loading;

    public SPickingPageViewModel(WorkspaceViewModel ws) : base(ws, WaveType.S)
    {
    }

    public static IReadOnlyList<Choice<SComponentMode>> ComponentOptions { get; } =
        [new(SComponentMode.HorizontalX, "Horizontal X as recorded"), new(SComponentMode.HorizontalY, "Horizontal Y as recorded"), new(SComponentMode.Rotated, "Rotated (angle per depth)")];

    public static IReadOnlyList<Choice<SPickTrace>> PickTraceOptions { get; } =
        [new(SPickTrace.Difference, "(S+ − S−)/2 difference — recommended"), new(SPickTrace.Positive, "S+ shot only"), new(SPickTrace.Negative, "S− shot only")];

    [ObservableProperty] private Choice<SComponentMode> _componentMode = ComponentOptions[0];
    [ObservableProperty] private Choice<SPickTrace> _pickTrace = PickTraceOptions[0];
    [ObservableProperty] private WaveformSectionModel? _detail;
    [ObservableProperty] private string _detailTitle = "Select a depth";
    [ObservableProperty] private string? _detailNotes;
    [ObservableProperty] private XyPlotModel? _hodogram;
    [ObservableProperty] private XyPlotModel? _energy;
    [ObservableProperty] private XyPlotModel? _correlationPlot;
    [ObservableProperty] private string? _rotationSummary;
    [ObservableProperty] private string? _correlationSummary;
    [ObservableProperty] private double _rotationAngle;
    [ObservableProperty] private double? _appliedAngle;
    [ObservableProperty] private double _windowMs = 20;
    [ObservableProperty] private int _detailTab;

    private PolarizationResult? _polarization;
    private CorrelationAnalysis? _adjacent;
    private Guid? _adjacentFrom;

    partial void OnComponentModeChanged(Choice<SComponentMode> value)
    {
        if (!_loading && value.Value != State.ActiveScenario.SWave.Component)
        {
            Execute(Edits.SetSWaveSettings(State.ActiveScenario.SWave with { Component = value.Value }, $"S component: {value.Label}"));
        }
    }

    partial void OnPickTraceChanged(Choice<SPickTrace> value)
    {
        if (!_loading && value.Value != State.ActiveScenario.SWave.PickTrace)
        {
            Execute(Edits.SetSWaveSettings(State.ActiveScenario.SWave with { PickTrace = value.Value }, $"S pick trace: {value.Label}"));
        }
    }

    protected override void Rebuild()
    {
        _loading = true;
        ComponentMode = ComponentOptions.First(c => c.Value == State.ActiveScenario.SWave.Component);
        PickTrace = PickTraceOptions.First(c => c.Value == State.ActiveScenario.SWave.PickTrace);
        _loading = false;
        base.Rebuild();
        BuildDetail();
    }

    private void BuildDetail()
    {
        _polarization = null;
        _adjacent = null;
        Hodogram = Energy = CorrelationPlot = null;
        RotationSummary = CorrelationSummary = null;
        if (SelectedStation is not { } station)
        {
            Detail = null;
            DetailTitle = "Select a depth to compare opposite shots";
            return;
        }

        var builder = Workspace.Analysis.Builder();
        var s = builder.BuildS(station.Id);
        var theme = PlotTheme;
        var depth = Units.Format(State.Survey.Geometry.DepthBelowGround(station.MeasuredDepth), Quantity.Length);
        DetailTitle = $"Depth {depth} — opposite shots";
        DetailNotes = s.Notes.Count > 0 ? string.Join(" ", s.Notes) : null;
        AppliedAngle = State.ActiveScenario.SWave.StationRotation.TryGetValue(station.Id, out var a) ? a : null;
        RotationAngle = AppliedAngle ?? RotationAngle;
        var pick = State.ActiveScenario.SPicks.GetValueOrDefault(station.Id);
        var markers = pick?.Time is { } pt ? new List<PickMarker> { new(pt, PickMarkerKind.S, theme.S) } : [];
        var model = new WaveformSectionModel { OverlayMode = true, NormalizeEach = false, Mode = WaveDisplayMode.Wiggle, TimeTitle = "Time after trigger (ms)" };
        double end = 0;
        if (s.Positive is { } p)
        {
            model.Traces.Add(new WaveTrace { Key = "pos", Samples = p.Trace.Samples, SampleInterval = p.Trace.SampleInterval, Delay = p.Trace.Delay, Color = theme.P, Label = "S+" });
            end = p.Trace.EndTime;
        }

        if (s.Negative is { } n && s.Difference is { } d && s.Positive is { } p2)
        {
            // Show −S− in the S+ time base so shear arrivals line up with S+.
            var negAligned = new double[d.Samples.Length];
            for (var i = 0; i < negAligned.Length; i++)
            {
                negAligned[i] = -(p2.Trace.Samples[i] - 2 * d.Samples[i]);
            }

            model.Traces.Add(new WaveTrace { Key = "neg", Samples = negAligned, SampleInterval = d.SampleInterval, Delay = d.Delay, Color = theme.S, Label = "−S−" });
            model.Traces.Add(new WaveTrace { Key = "diff", Samples = d.Samples, SampleInterval = d.SampleInterval, Delay = d.Delay, Color = theme.Wiggle, Label = "(S+ − S−)/2", Picks = markers });
        }
        else if (s.Negative is { } n2)
        {
            model.Traces.Add(new WaveTrace { Key = "neg", Samples = TimeDomain.Invert(n2.Trace.Samples), SampleInterval = n2.Trace.SampleInterval, Delay = n2.Trace.Delay, Color = theme.S, Label = "−S−", Picks = markers });
            end = Math.Max(end, n2.Trace.EndTime);
        }

        model.TimeMin = 0;
        model.TimeMax = pick?.Time is { } t ? Math.Min(end, t * 2 + 0.01) : Math.Min(end, 0.2);
        model.EmptyMessage = "No horizontal S records at this depth.";
        Detail = model;
    }

    private double CenterTime(Guid station)
    {
        var pick = State.ActiveScenario.SPicks.GetValueOrDefault(station)?.Time;
        if (pick is { } t)
        {
            return t;
        }

        var obs = Workspace.Result?.S.Observations ?? [];
        var me = obs.FirstOrDefault(o => o.StationId == station);
        var predicted = me is null ? null : ObservationBuilder.Predict(obs, me.SlantDistance, station);
        return predicted ?? 0.05;
    }

    /// <summary>Covariance polarization in a window starting at the pick (or predicted time).</summary>
    [RelayCommand]
    private void AnalyzeRotation()
    {
        if (SelectedStation is not { } station)
        {
            return;
        }

        var builder = Workspace.Analysis.Builder();
        var rec = State.Survey.RecordsAt(station.Id, ShotKind.SPositive).FirstOrDefault() ?? State.Survey.RecordsAt(station.Id, ShotKind.SNegative).FirstOrDefault();
        var pair = rec is null ? null : builder.HorizontalPair(rec);
        if (pair is not { } xy)
        {
            RotationSummary = "Rotation needs both horizontal components (X and Y) mapped and recorded.";
            return;
        }

        var t0 = CenterTime(station.Id) - WindowMs / 4000;
        var result = new CovariancePolarizationAnalyzer().Analyze(xy.X, xy.Y, t0, t0 + WindowMs / 1000);
        if (result is null)
        {
            RotationSummary = "The window is too short or the traces are silent.";
            return;
        }

        _polarization = result;
        RotationAngle = Math.Round(result.AngleDegrees, 1);
        Hodogram = ChartFactory.Hodogram(result, PlotTheme);
        Energy = ChartFactory.EnergyScan(result, PlotTheme, AppliedAngle);
        RotationSummary = $"Maximum horizontal energy at {result.AngleDegrees:0.0}° from X: {result.RadialEnergyFraction:P0} of the energy (X alone: {result.XEnergyFraction:P0}); rectilinearity {result.Rectilinearity:0.00}. " +
                          "Rotation is applied only if you choose Apply.";
    }

    [RelayCommand]
    private void ApplyRotation()
    {
        if (SelectedStation is { } station)
        {
            Execute(Edits.SetRotation(State, station.Id, Math.Round(RotationAngle, 2)));
        }
    }

    [RelayCommand]
    private void ApplyRotationToAll()
    {
        var edit = new ProjectEdit("swave.rotation", $"Horizontal rotation {RotationAngle:0.#}° applied to all depths", s => s.WithActiveScenario(sc => sc with
        {
            SWave = sc.SWave with
            {
                Component = SComponentMode.Rotated,
                StationRotation = s.Survey.Stations.ToDictionary(st => st.Id, _ => Math.Round(RotationAngle, 2)).ToImmutableDictionaryHelper(),
            },
        }));
        Execute(edit.With("degrees", RotationAngle));
    }

    [RelayCommand]
    private void ClearRotation()
    {
        if (SelectedStation is { } station)
        {
            Execute(Edits.SetRotation(State, station.Id, null));
        }
    }

    /// <summary>Correlates S+ with S− around the arrival (polarity-insensitive): a negative coefficient confirms an S wave.</summary>
    [RelayCommand]
    private void CorrelateOpposite()
    {
        if (SelectedStation is not { } station)
        {
            return;
        }

        var s = Workspace.Analysis.Builder().BuildS(station.Id);
        if (s.Positive is null || s.Difference is null)
        {
            CorrelationSummary = "Both S+ and S− records are needed.";
            return;
        }

        var neg = s.Positive.Trace with { Samples = s.Positive.Trace.Samples.Select((v, i) => v - 2 * s.Difference.Samples[i]).ToArray() };
        var t0 = CenterTime(station.Id) - WindowMs / 4000;
        var c = new WindowedCrossCorrelationAnalyzer().Correlate(s.Positive.Trace, neg, t0, t0 + WindowMs / 1000, 0.005, polarityInsensitive: true);
        if (c is null)
        {
            return;
        }

        CorrelationPlot = ChartFactory.Correlation(c, PlotTheme);
        CorrelationSummary = c.Coefficient < 0
            ? $"S+ and S− are reversed copies (r = {c.Coefficient:0.00}, shift {c.TimeShift * 1000:+0.00;-0.00} ms): consistent with a shear wave."
            : $"S+ and S− have the same polarity (r = {c.Coefficient:0.00}): this window is probably not a shear arrival, or the shots are mislabelled.";
    }

    /// <summary>Correlates this depth's S trace with the nearest picked shallower depth to propose a relative arrival.</summary>
    [RelayCommand]
    private void CorrelateAdjacent()
    {
        if (SelectedStation is not { } station)
        {
            return;
        }

        var stations = State.Survey.StationsByDepth();
        var idx = stations.ToList().FindIndex(s => s.Id == station.Id);
        var reference = stations.Take(idx).Reverse().FirstOrDefault(s => State.ActiveScenario.SPicks.GetValueOrDefault(s.Id)?.Time is not null)
                        ?? stations.Skip(idx + 1).FirstOrDefault(s => State.ActiveScenario.SPicks.GetValueOrDefault(s.Id)?.Time is not null);
        if (reference is null)
        {
            CorrelationSummary = "Pick at least one neighbouring depth first.";
            return;
        }

        var builder = Workspace.Analysis.Builder();
        var a = builder.BuildS(reference.Id).PickTrace;
        var b = builder.BuildS(station.Id).PickTrace;
        if (a is null || b is null)
        {
            return;
        }

        var refPick = State.ActiveScenario.SPicks[reference.Id].Time!.Value;
        var c = new WindowedCrossCorrelationAnalyzer().Correlate(a.Trace, b.Trace, refPick - WindowMs / 4000, refPick + WindowMs * 0.75 / 1000, 0.02);
        if (c is null)
        {
            return;
        }

        _adjacent = c;
        _adjacentFrom = reference.Id;
        CorrelationPlot = ChartFactory.Correlation(c, PlotTheme);
        var proposed = refPick + c.TimeShift;
        CorrelationSummary = $"Relative to depth {Units.Format(State.Survey.Geometry.DepthBelowGround(reference.MeasuredDepth), Quantity.Length)}: shift {c.TimeShift * 1000:+0.000;-0.000} ms, r = {c.Coefficient:0.00}. " +
                             $"Proposed S arrival {proposed * 1000:0.000} ms." + (c.Coefficient < State.ActiveScenario.Analysis.Qc.MinimumCorrelation ? " The correlation is weak; check visually before using it." : "");
    }

    [RelayCommand]
    private void UseCorrelationPick()
    {
        if (SelectedStation is not { } station || _adjacent is not { } c || _adjacentFrom is not { } from)
        {
            CorrelationSummary = "Run the adjacent-depth correlation first.";
            return;
        }

        var refPick = State.ActiveScenario.SPicks[from].Time!.Value;
        Execute(Edits.SetPick(State, WaveType.S, station.Id, refPick + c.TimeShift, null, Workspace.Main.Clock.Now, PickOrigin.CrossCorrelation, PickAlgorithm.CrossCorrelation));
    }
}

internal static class ImmutableHelpers
{
    public static System.Collections.Immutable.ImmutableDictionary<TKey, TValue> ToImmutableDictionaryHelper<TKey, TValue>(this Dictionary<TKey, TValue> d) where TKey : notnull =>
        System.Collections.Immutable.ImmutableDictionary.CreateRange(d);
}
