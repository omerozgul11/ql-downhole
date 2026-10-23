using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Traces;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.SignalProcessing;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed partial class StepRow(ProcessingPageViewModel owner, ProcessingStep step, int index) : ObservableObject
{
    public ProcessingStep Step { get; } = step;
    public int Index { get; } = index;
    public string Name => Step.DisplayName;
    public string Parameters => string.Join(", ", Step.DescribeParameters().Where(kv => kv.Key != "design").Select(kv => $"{kv.Key} {kv.Value}"));
    public bool ChangesTiming => Step.ChangesTiming;

    [ObservableProperty]
    private bool _enabled = step.Enabled;

    partial void OnEnabledChanged(bool value) => owner.SetEnabled(this, value);
}

/// <summary>Editor for one processing step (shown in the inspector).</summary>
public sealed partial class StepEditorViewModel : ObservableObject
{
    private readonly ProcessingPageViewModel _owner;
    private readonly ProcessingStep _step;
    private bool _loading = true;

    public StepEditorViewModel(ProcessingPageViewModel owner, ProcessingStep step)
    {
        _owner = owner;
        _step = step;
        Name = step.DisplayName;
        switch (step)
        {
            case HighPassStep h:
                Low = h.CornerHz; Order = h.Order; ZeroPhase = h.ZeroPhase; break;
            case LowPassStep l:
                High = l.CornerHz; Order = l.Order; ZeroPhase = l.ZeroPhase; break;
            case BandPassStep b:
                Low = b.LowHz; High = b.HighHz; Order = b.Order; ZeroPhase = b.ZeroPhase; break;
            case NotchStep n:
                Low = n.FrequencyHz; Q = n.QualityFactor; Harmonics = n.Harmonics; ZeroPhase = n.ZeroPhase; break;
            case TaperStep t:
                Value = t.Fraction * 100; break;
            case AgcStep a:
                Value = a.WindowLength * 1000; break;
            case TimeShiftStep s:
                Value = s.Shift * 1000; break;
            case ResampleStep r:
                Value = 1 / r.TargetInterval; break;
            case NormalizeStep nm:
                Rms = nm.Mode == NormalizationMode.Rms; break;
            case BalanceStep bal:
                Value = bal.WindowStart * 1000; Value2 = bal.WindowEnd * 1000; break;
        }

        Help = StepHelp.For(step);
        _loading = false;
    }

    public string Name { get; }
    public (string What, string When, string WhenNot) Help { get; }
    public string HelpWhat => Help.What;
    public string HelpWhen => Help.When;
    public string HelpWhenNot => Help.WhenNot;

    public bool ShowLow => _step is HighPassStep or BandPassStep or NotchStep;
    public bool ShowHigh => _step is LowPassStep or BandPassStep;
    public bool ShowOrder => _step is HighPassStep or LowPassStep or BandPassStep;
    public bool ShowZeroPhase => _step is HighPassStep or LowPassStep or BandPassStep or NotchStep;
    public bool ShowQ => _step is NotchStep;
    public bool ShowValue => _step is TaperStep or AgcStep or TimeShiftStep or ResampleStep or BalanceStep;
    public bool ShowValue2 => _step is BalanceStep;
    public bool ShowRms => _step is NormalizeStep;
    public bool HasParameters => ShowLow || ShowHigh || ShowValue || ShowRms;
    public string LowLabel => _step is NotchStep ? "Frequency (Hz)" : "Low corner (Hz)";
    public string ValueLabel => _step switch
    {
        TaperStep => "Taper length (% of trace at each end)",
        AgcStep => "AGC window (ms)",
        TimeShiftStep => "Shift (ms, positive = later)",
        ResampleStep => "New sampling rate (Hz)",
        BalanceStep => "Window start (ms)",
        _ => "Value",
    };

    [ObservableProperty] private double? _low;
    [ObservableProperty] private double? _high;
    [ObservableProperty] private int _order = 4;
    [ObservableProperty] private bool _zeroPhase = true;
    [ObservableProperty] private double? _q = 30;
    [ObservableProperty] private int _harmonics = 1;
    [ObservableProperty] private double? _value;
    [ObservableProperty] private double? _value2;
    [ObservableProperty] private bool _rms;

    partial void OnLowChanged(double? value) => Commit();
    partial void OnHighChanged(double? value) => Commit();
    partial void OnOrderChanged(int value) => Commit();
    partial void OnZeroPhaseChanged(bool value) => Commit();
    partial void OnQChanged(double? value) => Commit();
    partial void OnHarmonicsChanged(int value) => Commit();
    partial void OnValueChanged(double? value) => Commit();
    partial void OnValue2Changed(double? value) => Commit();
    partial void OnRmsChanged(bool value) => Commit();

    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        ProcessingStep updated = _step switch
        {
            HighPassStep h => h with { CornerHz = Low ?? h.CornerHz, Order = Math.Clamp(Order, 1, 12), ZeroPhase = ZeroPhase },
            LowPassStep l => l with { CornerHz = High ?? l.CornerHz, Order = Math.Clamp(Order, 1, 12), ZeroPhase = ZeroPhase },
            BandPassStep b => b with { LowHz = Low ?? b.LowHz, HighHz = High ?? b.HighHz, Order = Math.Clamp(Order, 1, 12), ZeroPhase = ZeroPhase },
            NotchStep n => n with { FrequencyHz = Low ?? n.FrequencyHz, QualityFactor = Math.Max(0.5, Q ?? n.QualityFactor), Harmonics = Math.Clamp(Harmonics, 1, 10), ZeroPhase = ZeroPhase },
            TaperStep t => t with { Fraction = Math.Clamp((Value ?? 5) / 100, 0, 0.5) },
            AgcStep a => a with { WindowLength = Math.Max(0.0005, (Value ?? 50) / 1000) },
            TimeShiftStep s => s with { Shift = (Value ?? 0) / 1000 },
            ResampleStep r => r with { TargetInterval = 1 / Math.Max(1, Value ?? 1000) },
            NormalizeStep nm => nm with { Mode = Rms ? NormalizationMode.Rms : NormalizationMode.PeakAmplitude },
            BalanceStep bal => bal with { WindowStart = (Value ?? 0) / 1000, WindowEnd = Value2 is { } e ? e / 1000 : null },
            _ => _step,
        };
        if (updated != _step)
        {
            _owner.Replace(updated);
        }
    }
}

/// <summary>What each processing step does, when to use it and when not to.</summary>
public static class StepHelp
{
    public static (string What, string When, string WhenNot) For(ProcessingStep step) => step switch
    {
        RemoveMeanStep => ("Subtracts the average value so the trace oscillates around zero.", "Almost always; recorders often add a small DC offset.", "Rarely harmful."),
        LinearDetrendStep => ("Removes a straight-line drift fitted through the whole trace.", "When the trace slowly drifts (sensor settling, temperature).", "When the drift is not linear; use a high-pass filter instead."),
        TaperStep => ("Smoothly reduces the ends of the trace to zero.", "Before filtering long records to avoid edge ringing.", "When arrivals are close to the start of the record — the taper would weaken them."),
        HighPassStep => ("Removes energy below the corner frequency (Butterworth).", "To remove low-frequency drift or ground roll.", "With a corner close to the signal's dominant frequency: it distorts the onset."),
        LowPassStep => ("Removes energy above the corner frequency (Butterworth).", "To suppress high-frequency noise such as electrical interference.", "With a corner below the signal band: it smears and delays the onset."),
        BandPassStep => ("Keeps energy between the two corners (Butterworth).", "To isolate the arrival band seen in the spectrum (S waves are usually lower in frequency than P).",
            "Zero-phase filters are acausal: they can move energy slightly before a sharp onset. Pick first breaks on raw or lightly filtered data and compare (Processed over raw)."),
        NotchStep => ("Removes a narrow band around one frequency (and optionally its harmonics).", "For mains interference (50 or 60 Hz).", "When the notch frequency lies inside the signal band."),
        NormalizeStep => ("Scales each trace to a peak (or RMS) amplitude of one.", "To compare waveform shapes between depths.", "When relative amplitudes matter (it hides attenuation)."),
        BalanceStep => ("Scales each trace so the RMS amplitude in a window is one.", "To equalise traces before stacking or display.", "When true amplitudes are needed."),
        AgcStep => ("Automatic gain control: divides by the local RMS amplitude in a sliding window.", "To see weak late arrivals in a display.", "For amplitude analysis — AGC changes amplitudes along the trace; it does not move picks."),
        PolarityStep => ("Multiplies every sample by −1.", "When a sensor is wired with reversed polarity.", "To make S+ and S− look alike — use the difference trace instead."),
        TimeShiftStep => ("Moves the waveform in time by a fixed amount.", "To correct a known constant recording delay.", "To align traces by eye — use trigger correction or cross-correlation."),
        ResampleStep => ("Changes the sampling rate with band-limited interpolation (anti-aliased when downsampling).", "To reduce very large records or match records of different rates.",
            "Upsampling does not add information and does not improve pick precision beyond the original bandwidth."),
        _ => ("", "", ""),
    };
}

public sealed record ReferenceRow(Guid RecordId, string FileName, string Depth, string Shot, string Time, string Deviation, string Origin, bool Flagged);

public sealed partial class ProcessingPageViewModel(WorkspaceViewModel ws) : PageViewModel(ws)
{
    public static IReadOnlyList<Choice<ComponentGroup>> GroupOptions { get; } =
        [new(ComponentGroup.Vertical, "Vertical (P analysis)"), new(ComponentGroup.Horizontal, "Horizontal (S analysis)"), new(ComponentGroup.Reference, "Reference / trigger")];

    public static IReadOnlyList<Choice<TriggerCorrectionMode>> TriggerModes { get; } =
    [
        new(TriggerCorrectionMode.None, "No correction (use times as recorded)"),
        new(TriggerCorrectionMode.SubtractReferenceOnset, "Subtract reference onset (reference geophone at the source)"),
        new(TriggerCorrectionMode.AlignToReferenceMedian, "Align to median reference onset (remove trigger jitter only)"),
    ];

    public override string NavTitle => "Processing";
    public override string Title => "Signal processing";
    public override string Description => "Build a processing chain for each component group. Processing never changes the recorded data: each step is applied to a copy, can be switched off, edited, reordered or removed, and is recorded in the project history. Use the spectrum to choose filters.";
    public override string IconKey => "Processing";
    public override PageViewModel? Next => Workspace.PPicksPage;

    [ObservableProperty] private Choice<ComponentGroup> _group = GroupOptions[0];
    [ObservableProperty] private StepRow? _selectedStep;
    [ObservableProperty] private StepEditorViewModel? _editor;
    [ObservableProperty] private Choice<Comparison> _comparisonMode = WaveformsPageViewModel.ComparisonOptions[2];
    [ObservableProperty] private WaveformSectionModel? _preview;
    [ObservableProperty] private XyPlotModel? _spectrum;
    [ObservableProperty] private XyPlotModel? _response;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string? _suggestion;
    [ObservableProperty] private BandPassStep? _suggestedStep;
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private Choice<TriggerCorrectionMode> _triggerMode = TriggerModes[0];
    [ObservableProperty] private double _expectedReferenceMs;
    [ObservableProperty] private bool _stackRepeated = true;
    [ObservableProperty] private ReferenceRow? _selectedReference;
    [ObservableProperty] private WaveformSectionModel? _referenceSection;
    [ObservableProperty] private string _referenceSummary = "";
    [ObservableProperty] private bool _isBusy;

    private bool _loading;

    public ObservableCollection<StepRow> Steps { get; } = [];
    public ObservableCollection<ReferenceRow> References { get; } = [];

    public bool HasSuggestion => SuggestedStep is not null;

    public override object? Inspector => Editor;

    partial void OnGroupChanged(Choice<ComponentGroup> value)
    {
        SelectedStep = null;
        LoadSteps();
        BuildPreview();
        // The suggestion belongs to the chain shown: a vertical (P) suggestion must never be added to the S chain.
        BuildSuggestion();
    }

    partial void OnSelectedStepChanged(StepRow? value)
    {
        Editor = value is null ? null : new StepEditorViewModel(this, value.Step);
        BuildResponse();
        Workspace.NotifyInspectorChanged();
    }

    partial void OnComparisonModeChanged(Choice<Comparison> value) => BuildPreview();

    partial void OnTriggerModeChanged(Choice<TriggerCorrectionMode> value) => CommitTrigger();

    partial void OnExpectedReferenceMsChanged(double value) => CommitTrigger();

    partial void OnStackRepeatedChanged(bool value)
    {
        if (!_loading && value != State.ActiveScenario.StackRepeatedShots)
        {
            Execute(Edits.SetStackRepeated(value));
        }
    }

    partial void OnSelectedReferenceChanged(ReferenceRow? value) => BuildReferenceSection();

    public override void OnStateChanged()
    {
        _loading = true;
        var t = State.ActiveScenario.Trigger;
        TriggerMode = TriggerModes.First(m => m.Value == t.Mode);
        ExpectedReferenceMs = t.ExpectedReferenceTime * 1000;
        StackRepeated = State.ActiveScenario.StackRepeatedShots;
        _loading = false;
        if (IsActive)
        {
            var selectedId = SelectedStep?.Step.Id;
            LoadSteps();
            SelectedStep = Steps.FirstOrDefault(s => s.Step.Id == selectedId);
            BuildPreview();
            LoadReferences();
        }
    }

    public override void OnSelectionChanged()
    {
        if (IsActive)
        {
            BuildPreview();
        }
    }

    public override void OnActivated()
    {
        OnStateChanged();
        BuildSuggestion();
    }

    private void LoadSteps()
    {
        Steps.Clear();
        var p = State.ActiveScenario.PipelineFor(Group.Value);
        for (var i = 0; i < p.Steps.Count; i++)
        {
            Steps.Add(new StepRow(this, p.Steps[i], i));
        }
    }

    public void SetEnabled(StepRow row, bool enabled)
    {
        if (row.Step.Enabled != enabled)
        {
            Execute(Edits.ReplaceStep(State, Group.Value, row.Step with { Enabled = enabled }));
        }
    }

    public void Replace(ProcessingStep step) => Execute(Edits.ReplaceStep(State, Group.Value, step));

    [RelayCommand]
    private void AddStep(string kind)
    {
        var fs = SampleRate();
        ProcessingStep step = kind switch
        {
            "removeMean" => new RemoveMeanStep(),
            "detrend" => new LinearDetrendStep(),
            "taper" => new TaperStep(),
            "bandPass" => Group.Value == ComponentGroup.Horizontal ? new BandPassStep { LowHz = 10, HighHz = Math.Min(200, 0.4 * fs) } : new BandPassStep { LowHz = 20, HighHz = Math.Min(400, 0.4 * fs) },
            "highPass" => new HighPassStep(),
            "lowPass" => new LowPassStep { CornerHz = Math.Min(250, 0.4 * fs) },
            "notch" => new NotchStep(),
            "normalize" => new NormalizeStep(),
            "balance" => new BalanceStep(),
            "agc" => new AgcStep(),
            "polarity" => new PolarityStep(),
            "timeShift" => new TimeShiftStep(),
            _ => new ResampleStep { TargetInterval = 2.0 / fs },
        };
        if (Execute(Edits.AddStep(State, Group.Value, step)))
        {
            // Page refreshes are deferred; load the chain now so the new step opens in the inspector.
            LoadSteps();
            SelectedStep = Steps.FirstOrDefault(s => s.Step.Id == step.Id);
        }
    }

    [RelayCommand]
    private void RemoveStep(StepRow row) => Execute(Edits.RemoveStep(State, Group.Value, row.Step.Id));

    [RelayCommand]
    private void MoveUp(StepRow row) => Execute(Edits.MoveStep(State, Group.Value, row.Step.Id, -1));

    [RelayCommand]
    private void MoveDown(StepRow row) => Execute(Edits.MoveStep(State, Group.Value, row.Step.Id, 1));

    [RelayCommand]
    private void AddSuggestion()
    {
        if (SuggestedStep is { } s && Execute(Edits.AddStep(State, Group.Value, s with { Id = Guid.NewGuid() })))
        {
            Workspace.Ui.Notify("Suggested band-pass added. Compare processed and raw onsets before picking.", "success");
        }
    }

    private double SampleRate()
    {
        var t = State.Survey.Records.SelectMany(r => r.Traces).FirstOrDefault();
        return t is null ? 1000 : 1 / t.SampleInterval;
    }

    private (Guid TraceId, Station Station)? PreviewTrace(StationTraceBuilder builder)
    {
        var stations = State.Survey.StationsByDepth();
        var station = stations.FirstOrDefault(s => s.Id == Workspace.SelectedStationId) ?? stations.ElementAtOrDefault(stations.Count / 2);
        if (station is null)
        {
            return null;
        }

        var (shot, role) = Group.Value switch
        {
            ComponentGroup.Horizontal => (ShotKind.SPositive, ComponentRole.HorizontalX),
            ComponentGroup.Reference => (ShotKind.P, ComponentRole.Reference),
            _ => (ShotKind.P, ComponentRole.Vertical),
        };
        var record = State.Survey.RecordsAt(station.Id, shot).FirstOrDefault() ?? State.Survey.Records.FirstOrDefault(r => r.StationId == station.Id);
        var trace = record is null ? null : builder.FindComponent(record, role) ?? builder.FindComponent(record, role == ComponentRole.HorizontalX ? ComponentRole.HorizontalY : role);
        return trace is null ? null : (trace.Id, station);
    }

    private void BuildPreview()
    {
        var builder = Workspace.Analysis.Builder();
        var target = PreviewTrace(builder);
        if (target is not { } t)
        {
            Preview = null;
            Spectrum = null;
            PreviewTitle = "Assign depths and channels to preview processing.";
            return;
        }

        var processed = builder.Process(t.TraceId);
        var raw = builder.Raw(t.TraceId);
        var theme = PlotTheme;
        var depth = Units.Format(State.Survey.Geometry.DepthBelowGround(t.Station.MeasuredDepth), Quantity.Length);
        PreviewTitle = $"Depth {depth} — {Group.Label}";
        Warnings = string.Join("\n", processed.Warnings);
        var model = new WaveformSectionModel { OverlayMode = true, NormalizeEach = true, Mode = WaveDisplayMode.Wiggle, TimeMin = Math.Min(0, raw.Delay), TimeTitle = "Time after trigger (ms)" };
        var pickWave = Group.Value == ComponentGroup.Horizontal ? WaveType.S : WaveType.P;
        var picks = new List<PickMarker>();
        if (State.ActiveScenario.PicksFor(pickWave).TryGetValue(t.Station.Id, out var pick) && pick.Time is { } pt && Group.Value != ComponentGroup.Reference)
        {
            picks.Add(new PickMarker(pt, pickWave == WaveType.P ? PickMarkerKind.P : PickMarkerKind.S, pickWave == WaveType.P ? theme.P : theme.S));
        }

        if (ComparisonMode.Value != Comparison.Processed)
        {
            model.Traces.Add(new WaveTrace { Key = "raw", Samples = raw.Samples, SampleInterval = raw.SampleInterval, Delay = raw.Delay, Color = theme.Raw, Label = "Raw" });
        }

        if (ComparisonMode.Value != Comparison.Raw)
        {
            model.Traces.Add(new WaveTrace { Key = "processed", Samples = processed.Samples, SampleInterval = processed.SampleInterval, Delay = processed.Delay, Color = theme.Accent, Label = "Processed", Picks = picks });
        }

        var end = pick?.Time is { } p2 ? Math.Min(raw.EndTime, p2 * 2.2 + 0.01) : Math.Min(raw.EndTime, 0.2);
        model.TimeMax = Math.Max(model.TimeMin + 0.005, end);
        Preview = model;
        var sp = Spectral.Amplitude(processed.Samples, processed.SampleInterval);
        var spRaw = Spectral.Amplitude(raw.Samples, raw.SampleInterval);
        Spectrum = ChartFactory.Spectrum(sp, theme, spRaw, "Spectrum (raw vs processed)");
        BuildResponse();
    }

    private void BuildResponse()
    {
        var fs = SampleRate();
        try
        {
            Response = SelectedStep?.Step switch
            {
                BandPassStep b => ChartFactory.FilterResponse(Butterworth.BandPass(b.Order, b.LowHz, b.HighHz, fs), fs, PlotTheme, b.ZeroPhase, "Filter response"),
                HighPassStep h => ChartFactory.FilterResponse(Butterworth.HighPass(h.Order, h.CornerHz, fs), fs, PlotTheme, h.ZeroPhase, "Filter response"),
                LowPassStep l => ChartFactory.FilterResponse(Butterworth.LowPass(l.Order, l.CornerHz, fs), fs, PlotTheme, l.ZeroPhase, "Filter response"),
                NotchStep n => ChartFactory.FilterResponse(Butterworth.Notch(n.FrequencyHz, n.QualityFactor, fs), fs, PlotTheme, n.ZeroPhase, "Filter response"),
                _ => null,
            };
        }
        catch (ArgumentException ex)
        {
            Response = null;
            Warnings = ex.Message.Split(" (Parameter")[0];
        }
    }

    /// <summary>
    /// Suggests a band-pass from the average amplitude spectrum of the group's traces: corners at half
    /// the lower and twice the upper −6 dB frequency. Only a suggestion; adding it is the user's choice.
    /// </summary>
    private void BuildSuggestion()
    {
        SuggestedStep = null;
        Suggestion = null;
        var builder = Workspace.Analysis.Builder();
        var role = Group.Value switch { ComponentGroup.Horizontal => ComponentRole.HorizontalX, ComponentGroup.Reference => ComponentRole.Reference, _ => ComponentRole.Vertical };
        // Average only the shots each chain is used for: P shots for the vertical (P) chain and S± shots for the
        // horizontal (S) chain. Mixing them would centre a P filter on the lower S-wave band, or the reverse.
        // Records without an assigned shot type are used only when no shot types have been assigned yet.
        var records = State.Survey.Records.Where(r => !r.Excluded).ToList();
        Func<SeismicRecord, bool>? matches = Group.Value switch
        {
            ComponentGroup.Vertical => r => r.Shot == ShotKind.P,
            ComponentGroup.Horizontal => r => r.Shot is ShotKind.SPositive or ShotKind.SNegative,
            _ => null,
        };
        if (matches is not null && records.Any(matches))
        {
            records = records.Where(matches).ToList();
        }

        var traces = records.Select(r => builder.FindComponent(r, role)).Where(t => t is not null).Take(60).ToList();
        if (traces.Count == 0)
        {
            OnPropertyChanged(nameof(HasSuggestion));
            return;
        }

        AmplitudeSpectrum? mean = null;
        double[]? sum = null;
        foreach (var t in traces)
        {
            var raw = builder.Raw(t!.Id);
            var sp = Spectral.Amplitude(raw.Samples, raw.SampleInterval, zeroPadTo: 8192);
            var peak = sp.Amplitudes.Max();
            if (!(peak > 0))
            {
                continue;
            }

            sum ??= new double[sp.Amplitudes.Length];
            if (sum.Length != sp.Amplitudes.Length)
            {
                continue;
            }

            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += sp.Amplitudes[i] / peak;
            }

            mean = sp;
        }

        if (mean is null || sum is null)
        {
            return;
        }

        var avg = new AmplitudeSpectrum(mean.Frequencies, sum);
        var (lo, hi) = avg.Bandwidth(0.5, 2);
        var fs = 1 / State.Survey.Records.SelectMany(r => r.Traces).First().SampleInterval;
        var low = Math.Max(1, Math.Round(lo / 2));
        var high = Math.Min(Math.Round(0.45 * fs), Math.Round(hi * 2));
        if (high <= low * 1.5)
        {
            return;
        }

        SuggestedStep = new BandPassStep { LowHz = low, HighHz = high, Order = 4, ZeroPhase = true };
        var shots = Group.Value switch { ComponentGroup.Vertical when matches is not null && State.Survey.Records.Any(matches) => "P-shot ", ComponentGroup.Horizontal when matches is not null && State.Survey.Records.Any(matches) => "S-shot ", _ => "" };
        Suggestion = $"The average spectrum of the {Group.Label.ToLowerInvariant()} channel in {shots}records peaks at {avg.DominantFrequency(2):0} Hz with most energy between {lo:0} and {hi:0} Hz (−6 dB). " +
                     $"A band-pass of {low:0}–{high:0} Hz keeps that band with margin. This is a suggestion only; check that onsets are not distorted.";
        OnPropertyChanged(nameof(HasSuggestion));
    }

    // ---------------------------------------------------------------- trigger

    private void CommitTrigger()
    {
        if (_loading)
        {
            return;
        }

        var t = new TriggerSettings { Mode = TriggerMode.Value, ExpectedReferenceTime = ExpectedReferenceMs / 1000 };
        if (t != State.ActiveScenario.Trigger)
        {
            Execute(Edits.SetTrigger(t));
        }
    }

    private void LoadReferences()
    {
        var selected = SelectedReference?.RecordId;
        References.Clear();
        var refs = State.ActiveScenario.ReferencePicks.Values.Where(p => p.Time is not null).Select(p => p.Time!.Value).ToList();
        var median = refs.Count > 0 ? Statistics.Median(refs) : double.NaN;
        var limit = State.ActiveScenario.Analysis.Qc.TriggerDeviationLimit;
        foreach (var r in State.Survey.Records.OrderBy(r => r.Sequence))
        {
            var pick = State.ActiveScenario.ReferencePicks.GetValueOrDefault(r.Id);
            var depth = r.StationId is { } s && State.Survey.FindStation(s) is { } st ? Units.Format(State.Survey.Geometry.DepthBelowGround(st.MeasuredDepth), Quantity.Length) : "—";
            var dev = pick?.Time is { } t && double.IsFinite(median) ? t - median : (double?)null;
            References.Add(new ReferenceRow(r.Id, r.Source.FileName, depth, r.Shot.ToString(), pick?.Time is { } tt ? $"{tt * 1000:0.000}" : "—",
                dev is { } d ? $"{d * 1000:+0.000;-0.000}" : "—", pick is null ? "" : pick.Origin.ToString(), dev is { } d2 && Math.Abs(d2) > limit));
        }

        ReferenceSummary = refs.Count == 0 ? "No reference onsets picked yet."
            : $"{refs.Count} reference onset(s); median {median * 1000:0.000} ms; spread {(refs.Max() - refs.Min()) * 1000:0.000} ms; {References.Count(r => r.Flagged)} beyond ±{limit * 1000:0.##} ms.";
        SelectedReference = References.FirstOrDefault(r => r.RecordId == selected) ?? References.FirstOrDefault();
    }

    private void BuildReferenceSection()
    {
        if (SelectedReference is not { } row)
        {
            ReferenceSection = null;
            return;
        }

        var builder = Workspace.Analysis.Builder();
        var st = builder.BuildReference(row.RecordId);
        if (st is null)
        {
            ReferenceSection = new WaveformSectionModel { EmptyMessage = "This record has no channel mapped as Reference." };
            return;
        }

        var theme = PlotTheme;
        var pick = State.ActiveScenario.ReferencePicks.GetValueOrDefault(row.RecordId);
        var markers = new List<PickMarker>();
        if (pick?.Time is { } t)
        {
            markers.Add(new PickMarker(t, PickMarkerKind.Reference, theme.Reference) { Automatic = pick.Origin == PickOrigin.Automatic });
        }

        var model = new WaveformSectionModel { OverlayMode = true, TimeMin = st.Trace.Delay, TimeMax = Math.Min(st.Trace.EndTime, (State.ActiveScenario.ReferencePicker.SearchEnd ?? 0.05) + 0.01), Mode = WaveDisplayMode.Wiggle };
        model.Traces.Add(new WaveTrace { Key = row.RecordId, Samples = st.Trace.Samples, SampleInterval = st.Trace.SampleInterval, Delay = st.Trace.Delay, Picks = markers, Label = row.FileName });
        ReferenceSection = model;
    }

    /// <summary>Click on the reference trace sets the onset manually.</summary>
    public void SetReferencePick(double time)
    {
        if (SelectedReference is { } row)
        {
            Execute(Edits.SetReferencePick(State, row.RecordId, time, Workspace.Main.Clock.Now));
        }
    }

    [RelayCommand]
    private async Task AutoPickReferences()
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var results = await Workspace.Analysis.AutoPickReferencesAsync(overwriteManual: false);
            var failed = results.Count(r => r.Suggestion is null);
            Workspace.Ui.Notify($"Reference onsets picked on {results.Count - failed} record(s){(failed > 0 ? $"; {failed} could not be picked" : "")}. Manual picks were kept.", failed > 0 ? "warning" : "success");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearReferencePick()
    {
        if (SelectedReference is { } row)
        {
            Execute(Edits.SetReferencePick(State, row.RecordId, null, Workspace.Main.Clock.Now));
        }
    }
}
