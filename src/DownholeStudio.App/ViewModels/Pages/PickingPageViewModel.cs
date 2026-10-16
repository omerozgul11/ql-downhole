using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Geometry;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

/// <summary>One depth in the synchronized pick table (both waves shown, the page's wave editable).</summary>
public sealed partial class PickRow : ObservableObject
{
    private readonly PickingPageViewModel _owner;
    private bool _loading = true;

    public PickRow(PickingPageViewModel owner, Guid stationId, string depth, StationObservation? p, StationObservation? s, Severity? qc, WaveType wave)
    {
        _owner = owner;
        StationId = stationId;
        Depth = depth;
        static string Ms(double? v) => v is { } x ? (x * 1000).ToString("0.000") : "—";
        PRaw = Ms(p?.RawTime);
        PCorrected = Ms(p?.Time);
        SRaw = Ms(s?.RawTime);
        SCorrected = Ms(s?.Time);
        var o = wave == WaveType.P ? p : s;
        Reference = Ms(o?.ReferenceTime);
        PConfidence = Confidence(p);
        SConfidence = Confidence(s);
        Origin = o?.Pick is { } pk && pk.Time is not null ? pk.Origin switch
        {
            PickOrigin.Manual => "Manual",
            PickOrigin.AcceptedSuggestion => "Auto ✓",
            PickOrigin.CrossCorrelation => "Correlation",
            _ => "Auto",
        } : "";
        _pickText = o?.RawTime is { } t ? (t * 1000).ToString("0.000", System.Globalization.CultureInfo.CurrentCulture) : "";
        HasPick = o?.RawTime is not null;
        Included = o?.Pick?.Included ?? true;
        Comment = o?.Pick?.Comment;
        Qc = qc;
        Reason = o is { Included: false } ? o.ExclusionReason : null;
        _loading = false;
    }

    public Guid StationId { get; }
    public string Depth { get; }
    public string PRaw { get; }
    public string PCorrected { get; }
    public string SRaw { get; }
    public string SCorrected { get; }
    public string Reference { get; }
    public string PConfidence { get; }
    public string SConfidence { get; }
    public string Origin { get; }
    public Severity? Qc { get; }
    public string? Reason { get; }
    public string QcText => Qc switch { Severity.Error => "× Problem", Severity.Warning => "! Review", _ => HasPick ? "✓ OK" : "" };
    public bool IsProblem => Qc == Severity.Error;
    public bool IsReview => Qc == Severity.Warning;
    public bool IsOk => Qc is null or Severity.Information && HasPick;

    public bool HasPick { get; }

    private string _pickText;

    /// <summary>
    /// Pick time as shown in the table (ms, rounded for display). Only a change typed by the user is
    /// committed; the rounded text written back by the grid is ignored, so display rounding can never
    /// alter the stored full-precision pick.
    /// </summary>
    public string PickText
    {
        get => _pickText;
        set
        {
            if (_loading || string.Equals(value?.Trim(), _pickText, StringComparison.Ordinal))
            {
                return;
            }

            _pickText = value?.Trim() ?? "";
            if (_pickText.Length == 0)
            {
                _owner.SetPickFromTable(StationId, null);
            }
            else if (double.TryParse(_pickText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var ms) && double.IsFinite(ms) && ms >= 0)
            {
                _owner.SetPickFromTable(StationId, ms);
            }

            OnPropertyChanged();
        }
    }

    [ObservableProperty] private bool _included;
    [ObservableProperty] private string? _comment;

    partial void OnIncludedChanged(bool value)
    {
        if (!_loading)
        {
            _owner.SetIncluded(StationId, value);
        }
    }

    partial void OnCommentChanged(string? value)
    {
        if (!_loading)
        {
            _owner.SetComment(StationId, value);
        }
    }

    private static string Confidence(StationObservation? o) => o?.Pick switch
    {
        { Time: null } or null => "",
        { Confidence: { } c } => $"{c.Level} {c.Score:0.00}",
        { IsManual: true } => "Manual",
        _ => "",
    };
}

/// <summary>Inspector for the selected pick: times, origin, confidence and why.</summary>
public sealed partial class PickInspectorViewModel(PickingPageViewModel owner, string header, StationObservation obs, ArrivalPick? pick, IReadOnlyList<FlagRow> flags) : ObservableObject
{
    public string Header { get; } = header;
    public string Raw => obs.RawTime is { } t ? $"{t * 1000:0.000} ms" : "No pick";
    public string Trigger => obs.TriggerCorrection is { } c ? $"{c * 1000:+0.000;-0.000} ms" : "—";
    public string Corrected => obs.Time is { } t ? $"{t * 1000:0.000} ms" : "—";
    public string Vertical => obs.VerticalTime is { } t ? $"{t * 1000:0.000} ms" : "—";
    public string Slant => $"{obs.SlantDistance:0.###} m";
    public string Origin => pick?.Time is null ? "—" : pick.Origin switch
    {
        PickOrigin.Manual => "Manual pick",
        PickOrigin.AcceptedSuggestion => $"Automatic ({pick.Algorithm}), reviewed and accepted",
        PickOrigin.CrossCorrelation => "From cross-correlation",
        _ => $"Automatic ({pick.Algorithm}), not yet reviewed",
    };
    public string? Confidence => pick?.Confidence is { } c ? $"{c.Level} — {c.Score:0.00}" : pick?.IsManual == true ? "Manual picks have no automatic score" : null;
    public IReadOnlyList<FactorRow> Factors { get; } = pick?.Confidence?.Factors.Select(f => new FactorRow(f.Name, f.Value is { } v ? v.ToString("0.##") : "", f.Score, f.Explanation)).ToList() ?? [];
    public string? Snr => pick?.Snr is { } s ? $"{s:0.#}" : null;
    public string? Suggestion => pick?.Suggestion is { } s ? $"{s.Time * 1000:0.000} ms ({s.Algorithm}{(s.Confidence is { } c ? $", {c.Level} {c.Score:0.00}" : "")})" : null;
    public bool HasSuggestion => pick?.Suggestion is not null;
    public bool CanAccept => pick?.Suggestion is not null && pick.Origin != PickOrigin.AcceptedSuggestion;
    public bool Included => obs.Included;
    public string? ExclusionReason => obs.ExclusionReason;
    public IReadOnlyList<FlagRow> Flags { get; } = flags;
    public bool HasFlags => Flags.Count > 0;
    public bool HasFactors => Factors.Count > 0;

    [RelayCommand]
    private void Accept() => owner.AcceptSuggestion();

    [RelayCommand]
    private void Restore() => owner.RestoreAuto();

    [RelayCommand]
    private void Delete() => owner.DeleteSelected();

    [RelayCommand]
    private void ToggleInclude() => owner.ToggleIncludeSelected();
}

public enum PickScope
{
    All,
    Unpicked,
    Selected,
}

public partial class PickingPageViewModel : PageViewModel
{
    private bool _gesture;

    public PickingPageViewModel(WorkspaceViewModel ws, WaveType wave) : base(ws)
    {
        Wave = wave;
        _algorithm = AlgorithmOptions[0];
    }

    public WaveType Wave { get; }

    /// <summary>The S-wave page (for S-only panels), or null on the P page.</summary>
    public SPickingPageViewModel? SPage => this as SPickingPageViewModel;

    public bool IsS => Wave == WaveType.S;

    public static IReadOnlyList<Choice<PickAlgorithm>> AlgorithmOptions { get; } =
        [new(PickAlgorithm.Aic, "AIC (Maeda) — recommended"), new(PickAlgorithm.StaLta, "STA/LTA"), new(PickAlgorithm.EnergyRatio, "Modified energy ratio")];

    public static IReadOnlyList<Choice<PickScope>> ScopeOptions { get; } =
        [new(PickScope.All, "All depths"), new(PickScope.Unpicked, "Depths without a pick"), new(PickScope.Selected, "Selected depth")];

    public override string NavTitle => Wave == WaveType.P ? "P Picks" : "S Picks";
    public override string Title => Wave == WaveType.P ? "P-wave first arrivals" : "S-wave arrivals";
    public override string Description => Wave == WaveType.P
        ? "Pick the first P arrival on the vertical component at every depth. Run the automatic picker, then review: click to place a pick, drag to move it, ←/→ nudge by one sample (Shift: 10), Enter accepts the suggestion, Delete removes. Dashed marks are automatic and not yet reviewed."
        : "Pick the S arrival on the horizontal component. The (S+ − S−)/2 trace keeps energy that reverses with the hammer direction (shear waves) and cancels the rest. Use the depth detail below to compare opposite shots, rotate horizontals and cross-correlate.";
    public override string IconKey => Wave == WaveType.P ? "PPick" : "SPick";
    public override PageViewModel? Next => Wave == WaveType.P ? Workspace.SPicksPage : Workspace.VelocityPage;

    [ObservableProperty] private WaveformSectionModel? _section;
    [ObservableProperty] private XyPlotModel? _travelTime;
    [ObservableProperty] private Choice<PickAlgorithm> _algorithm;
    [ObservableProperty] private Choice<PickScope> _scope = ScopeOptions[0];
    [ObservableProperty] private bool _overwriteManual;
    [ObservableProperty] private bool _pickingEnabled = true;
    [ObservableProperty] private bool _magnifier = true;
    [ObservableProperty] private bool _depthScale;
    [ObservableProperty] private bool _showRaw;
    [ObservableProperty] private double _gain = 1;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private PickRow? _selectedRow;
    [ObservableProperty] private PickInspectorViewModel? _pickInspector;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private double _searchStartMs;
    [ObservableProperty] private double? _searchEndMs;
    [ObservableProperty] private double _predictionHalfWidthMs;
    [ObservableProperty] private bool _useNeighbours = true;

    public ObservableCollection<PickRow> Rows { get; } = [];

    public override object? Inspector => PickInspector;

    private bool _loadingSettings;

    partial void OnDepthScaleChanged(bool value) => Rebuild();
    partial void OnShowRawChanged(bool value) => Rebuild();
    partial void OnGainChanged(double value) => Rebuild();

    partial void OnSelectedRowChanged(PickRow? value)
    {
        if (value is not null && Workspace.SelectedStationId != value.StationId)
        {
            Workspace.SelectedStationId = value.StationId;
        }
    }

    partial void OnAlgorithmChanged(Choice<PickAlgorithm> value) => CommitPicker();
    partial void OnSearchStartMsChanged(double value) => CommitPicker();
    partial void OnSearchEndMsChanged(double? value) => CommitPicker();
    partial void OnPredictionHalfWidthMsChanged(double value) => CommitPicker();
    partial void OnUseNeighboursChanged(bool value) => CommitPicker();

    private PickerSettings Picker => Wave == WaveType.P ? State.ActiveScenario.PPicker : State.ActiveScenario.SPicker;

    private void LoadPickerSettings()
    {
        _loadingSettings = true;
        var p = Picker;
        Algorithm = AlgorithmOptions.First(a => a.Value == p.Algorithm);
        SearchStartMs = (p.SearchStart ?? 0) * 1000;
        SearchEndMs = p.SearchEnd * 1000;
        PredictionHalfWidthMs = p.PredictionHalfWidth * 1000;
        UseNeighbours = p.UseNeighbourPrediction;
        _loadingSettings = false;
    }

    private void CommitPicker()
    {
        if (_loadingSettings)
        {
            return;
        }

        var p = Picker with
        {
            Algorithm = Algorithm.Value, SearchStart = SearchStartMs <= 0 ? null : SearchStartMs / 1000, SearchEnd = SearchEndMs is > 0 ? SearchEndMs / 1000 : null,
            PredictionHalfWidth = Math.Max(0.0005, PredictionHalfWidthMs / 1000), UseNeighbourPrediction = UseNeighbours,
        };
        if (p != Picker)
        {
            Execute(Edits.SetPickerSettings(Wave, p));
        }
    }

    public override void OnActivated()
    {
        LoadPickerSettings();
        Rebuild();
    }

    public override void OnStateChanged()
    {
        if (IsActive)
        {
            if (!_gesture)
            {
                LoadPickerSettings();
            }

            Rebuild();
        }
    }

    public override void OnResultChanged()
    {
        if (!IsActive)
        {
            return;
        }

        if (_gesture)
        {
            // During a drag only the light-weight views follow; the section is rebuilt when the drag ends.
            RebuildTable();
            BuildTravelTime();
            UpdateInspector();
        }
        else
        {
            // QC glyphs come from the latest analysis; processed traces are cached, so this is cheap.
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

    protected virtual void Rebuild()
    {
        var builder = Workspace.Analysis.Builder();
        var section = SectionFactory.Build(State, Workspace.Result, builder, Wave, PlotTheme, Units, Workspace.SelectedStationId, ShowRaw);
        section.SpaceByPosition = DepthScale;
        section.GlobalGain = Gain;
        Section = section;
        RebuildTable();
        BuildTravelTime();
        UpdateInspector();
    }

    private void RebuildTable()
    {
        var r = Workspace.Result;
        var selected = Workspace.SelectedStationId;
        Rows.Clear();
        if (r is null)
        {
            return;
        }

        foreach (var station in State.Survey.StationsByDepth())
        {
            var p = r.P.Observations.FirstOrDefault(o => o.StationId == station.Id);
            var s = r.S.Observations.FirstOrDefault(o => o.StationId == station.Id);
            var depth = Units.Format(State.Survey.Geometry.DepthBelowGround(station.MeasuredDepth), Quantity.Length, withUnit: false);
            Rows.Add(new PickRow(this, station.Id, depth, p, s, r.Qc.WorstFor(station.Id, Wave), Wave));
        }

        var picked = Rows.Count(x => x.HasPick);
        var auto = State.ActiveScenario.PicksFor(Wave).Values.Count(p => p.Origin == PickOrigin.Automatic && p.HasTime);
        Summary = $"{picked} of {Rows.Count} depths picked · {auto} automatic pick(s) not yet reviewed · {Rows.Count(x => x.IsProblem)} problem(s), {Rows.Count(x => x.IsReview)} to review";
        SelectedRow = Rows.FirstOrDefault(x => x.StationId == selected);
    }

    private void BuildTravelTime()
    {
        if (Workspace.Result is { } r)
        {
            var m = ChartFactory.TravelTime(r, Units, PlotTheme, Wave == WaveType.P, Wave == WaveType.S, true, Workspace.SelectedStationId, r.Qc);
            m.Title = null;
            TravelTime = m;
        }
    }

    protected Station? SelectedStation => Workspace.SelectedStationId is { } id ? State.Survey.FindStation(id) : null;

    private void UpdateInspector()
    {
        var station = SelectedStation;
        var r = Workspace.Result;
        if (station is null || r is null || r.For(Wave).Observations.FirstOrDefault(o => o.StationId == station.Id) is not { } obs)
        {
            PickInspector = null;
        }
        else
        {
            var pick = State.ActiveScenario.PicksFor(Wave).GetValueOrDefault(station.Id);
            var flags = r.Qc.For(station.Id, Wave).Select(f => new FlagRow(f.Severity, f.Message, f.Explanation)).ToList();
            PickInspector = new PickInspectorViewModel(this, $"{Wave} pick · depth {Units.Format(obs.Depth, Quantity.Length)}", obs, pick, flags);
        }

        Workspace.NotifyInspectorChanged();
    }

    // ---------------------------------------------------------------- interactions

    public void TraceClicked(object key, double time, bool selectOnly)
    {
        if (key is not Guid stationId)
        {
            return;
        }

        Workspace.SelectedStationId = stationId;
        if (PickingEnabled && !selectOnly)
        {
            SetPick(stationId, time);
        }
    }

    public void PickDragged(object key, double time, bool completed)
    {
        if (key is not Guid stationId || !Workspace.CanModify())
        {
            return;
        }

        if (!_gesture)
        {
            Workspace.Session.BeginGesture();
            _gesture = true;
        }

        Workspace.Session.Gesture(Edits.SetPick(State, Wave, stationId, time, null, Workspace.Main.Clock.Now));
        if (completed)
        {
            Workspace.Session.EndGesture();
            _gesture = false;
            Rebuild();
        }
    }

    private void SetPick(Guid stationId, double time)
    {
        var trace = Wave == WaveType.P ? Workspace.Analysis.Builder().BuildP(stationId) : Workspace.Analysis.Builder().BuildS(stationId).PickTrace;
        if (trace is null)
        {
            return;
        }

        // Snap to the nearest sample of the trace: picks are never finer than the data.
        var t = trace.Trace.TimeAt(trace.Trace.ClampIndex(time));
        Execute(Edits.SetPick(State, Wave, stationId, t, trace.PrimaryTraceId, Workspace.Main.Clock.Now));
    }

    public void SetPickFromTable(Guid stationId, double? ms)
    {
        if (ms is { } v)
        {
            SetPick(stationId, v / 1000);
        }
        else
        {
            Execute(Edits.DeletePick(State, Wave, stationId));
        }
    }

    public void SetIncluded(Guid stationId, bool included) => Execute(Edits.SetPickIncluded(State, Wave, stationId, included));

    public void SetComment(Guid stationId, string? comment) => Execute(Edits.SetPickComment(State, Wave, stationId, comment));

    /// <summary>Moves the selected pick by a number of samples (keyboard).</summary>
    public void Nudge(int samples)
    {
        if (SelectedStation is not { } st || State.ActiveScenario.PicksFor(Wave).GetValueOrDefault(st.Id)?.Time is not { } t)
        {
            return;
        }

        var dt = State.Survey.RecordsAt(st.Id, Wave == WaveType.P ? ShotKind.P : ShotKind.SPositive).FirstOrDefault()?.Traces.FirstOrDefault()?.SampleInterval ?? 0.0001;
        SetPick(st.Id, t + samples * dt);
    }

    public void SelectAdjacent(int delta)
    {
        var stations = State.Survey.StationsByDepth();
        if (stations.Count == 0)
        {
            return;
        }

        var idx = stations.ToList().FindIndex(s => s.Id == Workspace.SelectedStationId);
        Workspace.SelectedStationId = stations[Math.Clamp(idx < 0 ? 0 : idx + delta, 0, stations.Count - 1)].Id;
    }

    public void AcceptSuggestion()
    {
        if (SelectedStation is { } st)
        {
            Execute(Edits.AcceptSuggestion(State, Wave, st.Id, Workspace.Main.Clock.Now));
        }
    }

    public void RestoreAuto()
    {
        if (SelectedStation is { } st)
        {
            Execute(Edits.RestoreAutoPick(State, Wave, st.Id, Workspace.Main.Clock.Now));
        }
    }

    public void DeleteSelected()
    {
        if (SelectedStation is { } st)
        {
            Execute(Edits.DeletePick(State, Wave, st.Id));
        }
    }

    public void ToggleIncludeSelected()
    {
        if (SelectedStation is { } st && State.ActiveScenario.PicksFor(Wave).GetValueOrDefault(st.Id) is { } p)
        {
            Execute(Edits.SetPickIncluded(State, Wave, st.Id, !p.Included));
        }
    }

    public void AdjustGain(double factor) => Gain = Math.Clamp(Gain * factor, 0.05, 50);

    [RelayCommand]
    private async Task RunAutoPick()
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        IReadOnlyCollection<Guid>? stations = Scope.Value switch
        {
            PickScope.Selected => SelectedStation is { } s ? [s.Id] : [],
            PickScope.Unpicked => State.Survey.Stations.Where(s => State.ActiveScenario.PicksFor(Wave).GetValueOrDefault(s.Id)?.Time is null).Select(s => s.Id).ToList(),
            _ => null,
        };
        if (stations is { Count: 0 })
        {
            Workspace.Ui.Notify(Scope.Value == PickScope.Selected ? "Select a depth first." : "Every depth already has a pick.", "info");
            return;
        }

        IsBusy = true;
        try
        {
            var outcomes = await Workspace.Analysis.AutoPickAsync(Wave, stations, OverwriteManual);
            var ok = outcomes.Count(o => o.Suggestion is not null);
            var failed = outcomes.Where(o => o.Suggestion is null).ToList();
            Workspace.Ui.Notify($"Automatic {Wave} picks at {ok} depth(s){(failed.Count > 0 ? $"; no onset found at {failed.Count}" : "")}. {(OverwriteManual ? "" : "Manual picks were kept; their new suggestion is shown as a hollow mark.")}",
                failed.Count > 0 ? "warning" : "success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AcceptAll()
    {
        var auto = State.ActiveScenario.PicksFor(Wave).Where(kv => kv.Value.Origin == PickOrigin.Automatic && kv.Value.HasTime).Select(kv => kv.Key).ToList();
        if (auto.Count == 0)
        {
            Workspace.Ui.Notify("There are no unreviewed automatic picks.");
            return;
        }

        var low = auto.Count(id => State.ActiveScenario.PicksFor(Wave)[id].Confidence?.Level == ConfidenceLevel.Low);
        var answer = await Workspace.Ui.AskAsync($"Mark {auto.Count} automatic pick(s) as reviewed?",
            $"Only do this after you have looked at every trace. {(low > 0 ? $"{low} of them have LOW confidence. " : "")}Pick times are not changed; the picks are recorded as automatic picks accepted by you.",
            ["Mark as reviewed", "Cancel"]);
        if (answer == "Mark as reviewed")
        {
            Execute(Edits.AcceptAutomaticPicks(Wave, auto, Workspace.Main.Clock.Now));
        }
    }

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    public IReadOnlyList<string> HoverLines(int index, double time)
    {
        if (Section is not { } s || index < 0 || index >= s.Traces.Count || s.Traces[index].Key is not Guid id || Workspace.Result is not { } r)
        {
            return [];
        }

        var tr = s.Traces[index];
        var i = (int)Math.Round((time - tr.Delay) / tr.SampleInterval);
        var obs = r.For(Wave).Observations.FirstOrDefault(o => o.StationId == id);
        var pick = obs?.Pick;
        var qc = r.Qc.WorstFor(id, Wave);
        var component = Wave == WaveType.P ? "vertical" : State.ActiveScenario.SWave.PickTrace == SPickTrace.Difference ? "(S+ − S−)/2" : "horizontal";
        return
        [
            $"Depth {tr.Label} · {component}",
            $"Time {time * 1000:0.000} ms · amplitude {(i >= 0 && i < tr.Samples.Length ? tr.Samples[i].ToString("G4") : "—")}",
            $"Sample interval {tr.SampleInterval * 1000:0.####} ms",
            pick?.Time is { } pt ? $"Pick {pt * 1000:0.000} ms{(pick.Snr is { } snr ? $" · SNR {snr:0.#}" : "")}{(pick.Confidence is { } c ? $" · {c.Level}" : "")}" : "No pick",
            $"QC: {(qc is null ? (pick?.Time is null ? "—" : "✓ acceptable") : qc == Severity.Error ? "× likely problem" : qc == Severity.Warning ? "! review" : "note")}",
        ];
    }

    public void TravelTimePointClicked(object? tag)
    {
        if (tag is ValueTuple<Guid, WaveType> t)
        {
            Workspace.SelectedStationId = t.Item1;
        }
    }
}
