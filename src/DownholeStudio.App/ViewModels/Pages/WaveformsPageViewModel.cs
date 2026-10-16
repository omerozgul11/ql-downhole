using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Picking;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.SignalProcessing;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public enum Comparison
{
    Processed,
    Raw,
    Overlay,
}

public sealed record FlagRow(Severity Severity, string Message, string? Explanation)
{
    public bool IsError => Severity == Severity.Error;
    public bool IsWarning => Severity == Severity.Warning;
    public bool IsInfo => Severity == Severity.Information;
}

public sealed record FactorRow(string Name, string Value, double Score, string Explanation)
{
    public bool Weak => Score < 0.5;
}

/// <summary>Properties of the selected trace, editable in the right-hand inspector.</summary>
public sealed partial class TraceInspectorViewModel : ObservableObject
{
    private readonly WorkspaceViewModel _ws;
    private readonly Guid _traceId;
    private bool _loading = true;

    public TraceInspectorViewModel(WorkspaceViewModel ws, SeismicRecord record, TraceInfo trace, Station? station, WaveType? wave)
    {
        _ws = ws;
        _traceId = trace.Id;
        var units = ws.Units;
        var state = ws.State;
        var role = state.Survey.ChannelMap.RoleOf(trace.ChannelNumber);
        Header = station is null ? record.Source.FileName : $"Depth {units.Format(state.Survey.Geometry.DepthBelowGround(station.MeasuredDepth), Quantity.Length)}";
        Rows =
        [
            ("Record", record.Source.FileName), ("Shot", record.Shot.ToString()), ("Channel", trace.ChannelNumber.ToString()), ("Component", role.ToString()),
            ("Sample interval", $"{trace.SampleInterval * 1000:0.####} ms ({1 / trace.SampleInterval:0.#} Hz)"), ("Samples", $"{trace.SampleCount} ({trace.Duration * 1000:0.#} ms)"),
            ("Trigger delay", $"{trace.Delay * 1000:0.###} ms"), ("Recorded", record.AcquiredAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—"),
        ];
        var group = ChannelMap.GroupOf(role) ?? ComponentGroup.Vertical;
        var pipeline = state.ActiveScenario.PipelineFor(group).Steps.Where(s => s.Enabled).Select(s => s.DisplayName).ToList();
        Pipeline = pipeline.Count == 0 ? "Raw (no processing)" : "Raw → " + string.Join(" → ", pipeline);
        var adj = state.ActiveScenario.AdjustmentFor(trace.Id);
        Gain = adj.Gain;
        ReversePolarity = adj.ReversePolarity;
        TimeShiftMs = adj.TimeShift * 1000;
        Excluded = adj.Excluded;

        if (station is not null && wave is { } w && state.ActiveScenario.PicksFor(w).TryGetValue(station.Id, out var pick))
        {
            PickText = pick.Time is { } t ? $"{w} pick {t * 1000:0.000} ms ({Origin(pick)})" : $"No {w} pick";
            if (pick.Confidence is { } c)
            {
                ConfidenceText = $"{c.Level} confidence ({c.Score:0.00})";
                Factors = c.Factors.Select(f => new FactorRow(f.Name, f.Value is { } v ? v.ToString("0.##") : "", f.Score, f.Explanation)).ToList();
            }

            SnrText = pick.Snr is { } snr ? $"SNR {snr:0.#}" : null;
        }

        if (station is not null && ws.Result is { } r)
        {
            Flags = r.Qc.For(station.Id, wave).Select(f => new FlagRow(f.Severity, f.Message, f.Explanation)).ToList();
        }

        _loading = false;
    }

    public string Header { get; }
    public IReadOnlyList<(string Label, string Value)> Rows { get; }
    public IReadOnlyList<ReadoutRow> RowItems => Rows.Select(r => new ReadoutRow(r.Label, r.Value)).ToList();
    public string Pipeline { get; }
    public string? PickText { get; }
    public string? ConfidenceText { get; }
    public string? SnrText { get; }
    public IReadOnlyList<FactorRow> Factors { get; } = [];
    public IReadOnlyList<FlagRow> Flags { get; } = [];
    public bool HasFactors => Factors.Count > 0;
    public bool HasFlags => Flags.Count > 0;

    [ObservableProperty] private double _gain;
    [ObservableProperty] private bool _reversePolarity;
    [ObservableProperty] private double _timeShiftMs;
    [ObservableProperty] private bool _excluded;

    private static string Origin(ArrivalPick p) => p.Origin switch
    {
        PickOrigin.Manual => "manual",
        PickOrigin.AcceptedSuggestion => $"automatic {p.Algorithm}, reviewed",
        PickOrigin.CrossCorrelation => "cross-correlation",
        _ => $"automatic {p.Algorithm}, not reviewed",
    };

    private void Commit(string description)
    {
        if (_loading)
        {
            return;
        }

        var adj = _ws.State.ActiveScenario.AdjustmentFor(_traceId) with
        {
            Gain = Math.Clamp(Gain, 0.01, 100), ReversePolarity = ReversePolarity, TimeShift = TimeShiftMs / 1000, Excluded = Excluded,
        };
        _ws.Execute(Edits.SetTraceAdjustment(_ws.State, _traceId, adj, description));
    }

    partial void OnGainChanged(double value) => Commit($"Trace gain set to {value:0.##}×");
    partial void OnReversePolarityChanged(bool value) => Commit(value ? "Trace polarity reversed" : "Trace polarity restored");
    partial void OnTimeShiftMsChanged(double value) => Commit($"Trace time shift set to {value:0.###} ms");
    partial void OnExcludedChanged(bool value) => Commit(value ? "Trace excluded" : "Trace included");
}

public sealed partial class WaveformsPageViewModel(WorkspaceViewModel ws) : PageViewModel(ws)
{
    private readonly Dictionary<object, (SeismicRecord Record, TraceInfo Trace, Station Station)> _keys = [];

    public static IReadOnlyList<Choice<ShotKind>> ShotOptions { get; } = [new(ShotKind.P, "P shots"), new(ShotKind.SPositive, "S+ shots"), new(ShotKind.SNegative, "S− shots")];
    public static IReadOnlyList<Choice<ComponentRole>> ComponentOptions { get; } =
        [new(ComponentRole.Vertical, "Vertical (Z)"), new(ComponentRole.HorizontalX, "Horizontal X"), new(ComponentRole.HorizontalY, "Horizontal Y"), new(ComponentRole.Reference, "Reference")];
    public static IReadOnlyList<Choice<WaveDisplayMode>> ModeOptions { get; } =
        [new(WaveDisplayMode.WiggleAndArea, "Wiggle + variable area"), new(WaveDisplayMode.Wiggle, "Wiggle"), new(WaveDisplayMode.VariableArea, "Variable area")];
    public static IReadOnlyList<Choice<Comparison>> ComparisonOptions { get; } =
        [new(Comparison.Processed, "Processed"), new(Comparison.Raw, "Raw (as recorded)"), new(Comparison.Overlay, "Processed over raw")];

    public override string NavTitle => "Waveforms";
    public override string Title => "Inspect waveforms";
    public override string Description => "Look at every record before processing: check that arrivals are visible, polarities make sense and no trace is dead or clipped. Scroll to zoom time, Ctrl+scroll for gain, Shift+scroll to move through depths, double-click to fit.";
    public override string IconKey => "Waveform";
    public override PageViewModel? Next => Workspace.ProcessingPage;

    [ObservableProperty] private Choice<ShotKind> _shot = ShotOptions[0];
    [ObservableProperty] private Choice<ComponentRole> _component = ComponentOptions[0];
    [ObservableProperty] private Choice<WaveDisplayMode> _mode = ModeOptions[0];
    [ObservableProperty] private Choice<Comparison> _comparisonMode = ComparisonOptions[0];
    [ObservableProperty] private bool _normalizeEach = true;
    [ObservableProperty] private bool _overlay;
    [ObservableProperty] private bool _depthScale;
    [ObservableProperty] private double _gain = 1;
    [ObservableProperty] private WaveformSectionModel? _section;
    [ObservableProperty] private XyPlotModel? _spectrum;
    [ObservableProperty] private string _spectrumSummary = "";
    [ObservableProperty] private TraceInspectorViewModel? _selectedTrace;

    public bool IsEmpty => State.Survey.Stations.Count == 0;

    public override object? Inspector => SelectedTrace;

    partial void OnShotChanged(Choice<ShotKind> value) => Rebuild();
    partial void OnComponentChanged(Choice<ComponentRole> value) => Rebuild();
    partial void OnModeChanged(Choice<WaveDisplayMode> value) => Rebuild();
    partial void OnComparisonModeChanged(Choice<Comparison> value) => Rebuild();
    partial void OnNormalizeEachChanged(bool value) => Rebuild();
    partial void OnOverlayChanged(bool value) => Rebuild();
    partial void OnDepthScaleChanged(bool value) => Rebuild();
    partial void OnGainChanged(double value) => Rebuild();

    public override void OnStateChanged()
    {
        if (IsActive)
        {
            Rebuild();
        }
    }

    public override void OnSelectionChanged()
    {
        if (IsActive)
        {
            Rebuild();
        }
    }

    public override void OnActivated() => Rebuild();

    private void Rebuild()
    {
        OnPropertyChanged(nameof(IsEmpty));
        var builder = Workspace.Analysis.Builder();
        var model = new WaveformSectionModel
        {
            Mode = Mode.Value, NormalizeEach = NormalizeEach, OverlayMode = Overlay, SpaceByPosition = DepthScale, GlobalGain = Gain,
            TimeTitle = "Time after trigger (ms)", EmptyMessage = "Assign depths on the Geometry page to see traces by depth.",
        };
        _keys.Clear();
        var theme = PlotTheme;
        var scenario = State.ActiveScenario;
        double maxEnd = 0, maxPick = 0;
        foreach (var station in State.Survey.StationsByDepth())
        {
            var record = State.Survey.RecordsAt(station.Id, Shot.Value).FirstOrDefault();
            if (record is null)
            {
                continue;
            }

            var trace = builder.FindComponent(record, Component.Value) ?? record.Traces.FirstOrDefault(t => State.Survey.ChannelMap.RoleOf(t.ChannelNumber) == Component.Value);
            if (trace is null)
            {
                continue;
            }

            var processed = ComparisonMode.Value == Comparison.Raw ? builder.Raw(trace.Id) : builder.Process(trace.Id);
            var markers = new List<PickMarker>();
            if (Component.Value == ComponentRole.Reference && scenario.ReferencePicks.TryGetValue(record.Id, out var rp) && rp.Time is { } rt)
            {
                markers.Add(new PickMarker(rt, PickMarkerKind.Reference, theme.Reference));
            }
            else
            {
                var wave = Shot.Value == ShotKind.P ? WaveType.P : WaveType.S;
                if (scenario.PicksFor(wave).TryGetValue(station.Id, out var pick) && pick.Time is { } t)
                {
                    markers.Add(new PickMarker(t, wave == WaveType.P ? PickMarkerKind.P : PickMarkerKind.S, wave == WaveType.P ? theme.P : theme.S) { Automatic = pick.Origin == PickOrigin.Automatic });
                    maxPick = Math.Max(maxPick, t);
                }
            }

            var depth = State.Survey.Geometry.DepthBelowGround(station.MeasuredDepth);
            var adj = scenario.AdjustmentFor(trace.Id);
            model.Traces.Add(new WaveTrace
            {
                Key = trace.Id, Samples = processed.Samples, SampleInterval = processed.SampleInterval, Delay = processed.Delay, Position = depth,
                Label = Units.Format(depth, Quantity.Length, decimals: 1), Selected = station.Id == Workspace.SelectedStationId, Excluded = adj.Excluded || station.Excluded || record.Excluded,
                Gain = adj.Gain, Picks = markers, Overlay = ComparisonMode.Value == Comparison.Overlay ? builder.Raw(trace.Id).Samples : null,
                Color = Overlay ? (station.Id == Workspace.SelectedStationId ? theme.Accent : null) : null,
            });
            _keys[trace.Id] = (record, trace, station);
            maxEnd = Math.Max(maxEnd, processed.EndTime);
        }

        model.TimeMin = model.Traces.Count > 0 ? Math.Min(0, model.Traces.Min(t => t.Delay)) : 0;
        model.TimeMax = maxPick > 0 ? Math.Min(maxEnd, Math.Max(0.02, maxPick * 1.8)) : SignalExtent(model, maxEnd);
        Section = model;
        UpdateSelectedTrace(builder);
    }

    private void UpdateSelectedTrace(StationTraceBuilder builder)
    {
        var selected = _keys.Values.FirstOrDefault(k => k.Station.Id == Workspace.SelectedStationId);
        if (selected.Record is null)
        {
            SelectedTrace = null;
            Spectrum = null;
            SpectrumSummary = "Select a trace to see its spectrum.";
            Workspace.NotifyInspectorChanged();
            return;
        }

        var wave = Shot.Value == ShotKind.P ? WaveType.P : WaveType.S;
        SelectedTrace = new TraceInspectorViewModel(Workspace, selected.Record, selected.Trace, selected.Station, Component.Value == ComponentRole.Reference ? null : wave);
        var processed = builder.Process(selected.Trace.Id);
        var raw = builder.Raw(selected.Trace.Id);
        var sp = Spectral.Amplitude(processed.Samples, processed.SampleInterval);
        var spRaw = Spectral.Amplitude(raw.Samples, raw.SampleInterval);
        Spectrum = ChartFactory.Spectrum(sp, PlotTheme, spRaw, $"Spectrum — {SelectedTrace.Header}");
        var (lo, hi) = sp.Bandwidth(0.70710678118654757, 1);
        SpectrumSummary = $"Dominant frequency {sp.DominantFrequency(1):0} Hz · −3 dB band {lo:0}–{hi:0} Hz · Nyquist {0.5 / processed.SampleInterval:0} Hz";
        Workspace.NotifyInspectorChanged();
    }

    /// <summary>
    /// Default time window when nothing is picked: up to 1.4 × the latest time at which any trace still
    /// exceeds a quarter of its peak amplitude, so arrivals are not squeezed into a long record.
    /// </summary>
    private static double SignalExtent(WaveformSectionModel model, double maxEnd)
    {
        double latest = 0;
        foreach (var t in model.Traces)
        {
            var peak = t.Samples.Length == 0 ? 0 : t.Samples.Max(Math.Abs);
            for (var i = t.Samples.Length - 1; i >= 0 && peak > 0; i--)
            {
                if (Math.Abs(t.Samples[i]) >= 0.25 * peak)
                {
                    latest = Math.Max(latest, t.TimeAt(i));
                    break;
                }
            }
        }

        return latest > 0 ? Math.Min(maxEnd, Math.Max(0.02, latest * 1.4 + 0.005)) : Math.Max(0.02, Math.Min(maxEnd, 0.25));
    }

    /// <summary>Hover card text for the waveform view.</summary>
    public IReadOnlyList<string> HoverLines(int index, double time)
    {
        if (Section is not { } s || index < 0 || index >= s.Traces.Count || !_keys.TryGetValue(s.Traces[index].Key, out var k))
        {
            return [];
        }

        var tr = s.Traces[index];
        var i = (int)Math.Round((time - tr.Delay) / tr.SampleInterval);
        var amp = i >= 0 && i < tr.Samples.Length ? tr.Samples[i] : double.NaN;
        var wave = Shot.Value == ShotKind.P ? WaveType.P : WaveType.S;
        var pick = State.ActiveScenario.PicksFor(wave).GetValueOrDefault(k.Station.Id);
        var qc = Workspace.Result?.Qc.WorstFor(k.Station.Id, wave);
        return
        [
            $"Depth {tr.Label} · channel {k.Trace.ChannelNumber} ({State.Survey.ChannelMap.RoleOf(k.Trace.ChannelNumber)})",
            $"Time {time * 1000:0.000} ms · amplitude {amp:G4}",
            $"Sample interval {k.Trace.SampleInterval * 1000:0.####} ms",
            pick?.Time is { } pt ? $"{wave} pick {pt * 1000:0.000} ms{(pick.Snr is { } snr ? $" · SNR {snr:0.#}" : "")}" : $"No {wave} pick",
            $"QC: {(qc is null ? (pick?.Time is null ? "—" : "✓ acceptable") : qc == Severity.Error ? "× likely problem" : qc == Severity.Warning ? "! review" : "note")}",
        ];
    }

    public void SelectTrace(object key)
    {
        if (_keys.TryGetValue(key, out var k))
        {
            Workspace.SelectedStationId = k.Station.Id;
        }
    }

    public void AdjustGain(double factor) => Gain = Math.Clamp(Gain * factor, 0.05, 50);

    [RelayCommand]
    private void ResetGain() => Gain = 1;
}
