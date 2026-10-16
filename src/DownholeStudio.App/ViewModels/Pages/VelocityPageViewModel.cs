using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis;
using DownholeStudio.Analysis.Velocity;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed record LayerRow(Guid Id, int Index, string Top, string Bottom, string Thickness, string Vp, string Vs, string PFit, string SFit, string Notes, bool Selected);

public sealed record SuggestionRow(double Depth, string DepthText, string Reason);

/// <summary>Properties of the selected layer (inspector).</summary>
public sealed partial class LayerInspectorViewModel : ObservableObject
{
    private readonly VelocityPageViewModel _owner;
    private readonly Guid _id;
    private bool _loading = true;

    public LayerInspectorViewModel(VelocityPageViewModel owner, LayerResult layer, UnitPreferences u, bool first)
    {
        _owner = owner;
        _id = layer.Layer.Id;
        Header = $"Layer {layer.Layer.Index + 1}";
        Top = u.ToDisplay(layer.Layer.Top, Quantity.Length);
        CanMoveTop = !first;
        Bottom = u.Format(layer.Layer.Bottom, Quantity.Length);
        var p = layer.Properties;
        Rows =
        [
            new ReadoutRow("Vp", u.Format(layer.P?.Velocity, Quantity.Velocity) + (layer.P?.VelocityStandardError is { } a ? $" ± {u.Format(a, Quantity.Velocity, false)}" : "")),
            new ReadoutRow("Vs", u.Format(layer.S?.Velocity, Quantity.Velocity) + (layer.S?.VelocityStandardError is { } b ? $" ± {u.Format(b, Quantity.Velocity, false)}" : "")),
            new ReadoutRow("Vp/Vs", p.VpVsRatio is { } r ? r.ToString("0.00") : "—"),
            new ReadoutRow("Density", p.Density is null ? "—" : u.Format(p.Density, Quantity.Density) + (p.DensityEstimated ? " (ESTIMATED)" : "")),
            new ReadoutRow("Poisson's ratio ν", p.PoissonRatio is { } nu ? nu.ToString("0.000") : "—"),
            new ReadoutRow("Shear modulus G", u.Format(p.ShearModulus, Quantity.Modulus)),
            new ReadoutRow("Young's modulus E", u.Format(p.YoungModulus, Quantity.Modulus)),
            new ReadoutRow("Bulk modulus K", u.Format(p.BulkModulus, Quantity.Modulus)),
            new ReadoutRow("Vp regression", layer.P?.Fit is { } fp && double.IsFinite(fp.RSquared) ? $"R² {fp.RSquared:0.0000}, n = {fp.Count}, RMS {fp.RmsResidual * 1000:0.000} ms" : layer.P?.Reason ?? "—"),
            new ReadoutRow("Vs regression", layer.S?.Fit is { } fs && double.IsFinite(fs.RSquared) ? $"R² {fs.RSquared:0.0000}, n = {fs.Count}, RMS {fs.RmsResidual * 1000:0.000} ms" : layer.S?.Reason ?? "—"),
        ];
        Issues = p.Issues.Select(i => new FlagRow(i.Severity, i.Message, i.Details)).ToList();
        _loading = false;
    }

    public string Header { get; }
    public string Bottom { get; }
    public bool CanMoveTop { get; }
    public IReadOnlyList<ReadoutRow> Rows { get; }
    public IReadOnlyList<FlagRow> Issues { get; }
    public bool HasIssues => Issues.Count > 0;

    [ObservableProperty] private double _top;

    partial void OnTopChanged(double value)
    {
        if (!_loading && CanMoveTop)
        {
            _owner.MoveBoundary(_id, value, completed: true, fromDisplay: true);
        }
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteLayer(_id);

    [RelayCommand]
    private void Merge() => _owner.MergeLayer(_id);

    [RelayCommand]
    private void Split() => _owner.SplitLayer(_id);
}

public sealed partial class VelocityPageViewModel(WorkspaceViewModel ws) : PageViewModel(ws)
{
    private bool _loading;
    private bool _dragging;

    public static IReadOnlyList<Choice<VelocityMethod>> MethodOptions { get; } =
    [
        new(VelocityMethod.LayerRegression, "Layer regression (corrected time vs depth)"), new(VelocityMethod.RefractedRay, "Refracted-ray layer stripping (Snell's law)"),
        new(VelocityMethod.PseudoInterval, "Pseudo-interval (Δz/Δt corrected)"), new(VelocityMethod.TrueInterval, "True interval, straight rays (ΔR/Δt)"),
    ];

    public static IReadOnlyList<Choice<RegressionMethod>> RegressionOptions { get; } =
        [new(RegressionMethod.OrdinaryLeastSquares, "Ordinary least squares"), new(RegressionMethod.RobustHuber, "Robust (Huber)")];

    public static IReadOnlyList<Choice<Reporting.Charts.ProfileKind>> ProfileOptions { get; } =
        [new(Reporting.Charts.ProfileKind.Layer, "Layer velocities"), new(Reporting.Charts.ProfileKind.Interval, "Interval velocities"), new(Reporting.Charts.ProfileKind.Average, "Average velocity (R/t)")];

    public override string NavTitle => "Velocity Model";
    public override string Title => "Velocity model & layers";
    public override string Description => "Interpret layers on the travel-time graph: each straight segment of corrected time versus depth is a layer, and its slope is 1/velocity. Drag a dashed boundary to move it; the regression updates immediately. Excluded points stay visible as crosses.";
    public override string IconKey => "Velocity";
    public override PageViewModel? Next => Workspace.EngineeringPage;

    [ObservableProperty] private Choice<VelocityMethod> _method = MethodOptions[0];
    [ObservableProperty] private Choice<RegressionMethod> _regression = RegressionOptions[0];
    [ObservableProperty] private double _huberK = 1.345;
    [ObservableProperty] private Choice<Reporting.Charts.ProfileKind> _profileKind = ProfileOptions[0];
    [ObservableProperty] private bool _showP = true;
    [ObservableProperty] private bool _showS = true;
    [ObservableProperty] private bool _verticalTime = true;
    [ObservableProperty] private bool _labels = true;
    [ObservableProperty] private XyPlotModel? _travelTime;
    [ObservableProperty] private XyPlotModel? _residuals;
    [ObservableProperty] private XyPlotModel? _profile;
    [ObservableProperty] private double? _newBoundary;
    [ObservableProperty] private LayerInspectorViewModel? _layerInspector;
    [ObservableProperty] private LayerRow? _selectedLayerRow;
    [ObservableProperty] private string _methodNote = "";
    [ObservableProperty] private string _fitSummary = "";

    public ObservableCollection<LayerRow> Layers { get; } = [];
    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;

    public override object? Inspector => LayerInspector;

    partial void OnMethodChanged(Choice<VelocityMethod> value) => CommitSettings($"Velocity method: {value.Label}");
    partial void OnRegressionChanged(Choice<RegressionMethod> value) => CommitSettings($"Regression: {value.Label}");
    partial void OnHuberKChanged(double value) => CommitSettings($"Huber constant {value:0.###}");
    partial void OnProfileKindChanged(Choice<Reporting.Charts.ProfileKind> value) => BuildPlots();
    partial void OnShowPChanged(bool value) => BuildPlots();
    partial void OnShowSChanged(bool value) => BuildPlots();
    partial void OnVerticalTimeChanged(bool value) => BuildPlots();
    partial void OnLabelsChanged(bool value) => BuildPlots();

    partial void OnSelectedLayerRowChanged(LayerRow? value)
    {
        if (value is not null && Workspace.SelectedLayerId != value.Id)
        {
            Workspace.SelectedLayerId = value.Id;
        }
    }

    private void CommitSettings(string description)
    {
        if (_loading)
        {
            return;
        }

        var a = State.ActiveScenario.Analysis with { Method = Method.Value, Regression = Regression.Value, HuberThreshold = Math.Clamp(HuberK, 0.5, 5) };
        if (a != State.ActiveScenario.Analysis)
        {
            Execute(Edits.SetAnalysisSettings(a, description));
        }
    }

    public override void OnStateChanged()
    {
        _loading = true;
        var a = State.ActiveScenario.Analysis;
        Method = MethodOptions.First(m => m.Value == a.Method);
        Regression = RegressionOptions.First(r => r.Value == a.Regression);
        HuberK = a.HuberThreshold;
        MethodNote = a.Method switch
        {
            VelocityMethod.LayerRegression => "Straight lines through corrected (vertical-equivalent) travel times inside each layer. Robust and standard; assumes straight rays.",
            VelocityMethod.RefractedRay => "Rays bend at layer boundaries following Snell's law; each layer's velocity is solved from the top down. Best near the surface where rays are oblique.",
            VelocityMethod.PseudoInterval => "Velocity between pairs of adjacent receivers from corrected times. Sensitive to small pick errors.",
            _ => "Straight slant distances between adjacent receivers. Biased below slower layers where rays bend; use only where depth ≫ source offset.",
        };
        _loading = false;
        NotifyUnits();
    }

    public override void OnResultChanged()
    {
        if (!IsActive)
        {
            return;
        }

        BuildPlots();
        BuildTable();
        UpdateInspector();
    }

    public override void OnSelectionChanged()
    {
        if (IsActive)
        {
            BuildPlots();
            BuildTable();
            UpdateInspector();
        }
    }

    private void BuildPlots()
    {
        if (Workspace.Result is not { } r)
        {
            return;
        }

        var theme = PlotTheme;
        var tt = ChartFactory.TravelTime(r, Units, theme, ShowP, ShowS, VerticalTime, Workspace.SelectedStationId, r.Qc);
        if (Workspace.SelectedLayerId is { } id)
        {
            var l = r.Layers.FirstOrDefault(x => x.Id == id);
            if (l is not null)
            {
                tt.Bands.Add(new ShadedBand(LineOrientation.Horizontal, l.Top, l.Bottom, theme.Selection.WithAlpha(22)));
            }
        }

        TravelTime = tt;
        Residuals = ChartFactory.Residuals(r, Units, theme);
        Profile = ChartFactory.VelocityProfile(r, Units, theme, ProfileKind.Value, ShowP, ShowS, Labels);
        FitSummary = $"RMS residual: P {Ms(r.P.RmsResidual)}, S {Ms(r.S.RmsResidual)} · {r.Layers.Count} layer(s) · model bottom {Units.Format(r.Layers.LastOrDefault()?.Bottom, Quantity.Length)}";
    }

    private static string Ms(double? v) => v is { } x ? $"{x * 1000:0.000} ms" : "—";

    private void BuildTable()
    {
        if (Workspace.Result is not { } r)
        {
            return;
        }

        Layers.Clear();
        foreach (var l in r.LayerResults)
        {
            string Fit(LayerVelocity? v) => v?.Fit is { } f && double.IsFinite(f.RSquared) ? $"R² {f.RSquared:0.000} · n {f.Count}" : v?.PointCount is { } n ? $"n {n}" : "";
            var notes = string.Join(" ", new[] { l.P?.Reason, l.S?.Reason }.Where(x => x is not null).Distinct());
            Layers.Add(new LayerRow(l.Layer.Id, l.Layer.Index, Units.Format(l.Layer.Top, Quantity.Length, false), Units.Format(l.Layer.Bottom, Quantity.Length, false),
                Units.Format(l.Layer.Thickness, Quantity.Length, false), Units.Format(l.P?.Velocity, Quantity.Velocity, false), Units.Format(l.S?.Velocity, Quantity.Velocity, false),
                Fit(l.P), Fit(l.S), notes, l.Layer.Id == Workspace.SelectedLayerId));
        }

        SelectedLayerRow = Layers.FirstOrDefault(x => x.Selected);
    }

    private void UpdateInspector()
    {
        var r = Workspace.Result;
        var layer = r?.LayerResults.FirstOrDefault(l => l.Layer.Id == Workspace.SelectedLayerId);
        LayerInspector = layer is null ? null : new LayerInspectorViewModel(this, layer, Units, layer.Layer.Index == 0);
        Workspace.NotifyInspectorChanged();
    }

    private void EnsureModel()
    {
        if (State.ActiveScenario.Layers.Layers.Count == 0)
        {
            // The analysis treats an empty model as one layer; make it explicit so it can be edited.
            Execute(Edits.SetLayerModel(new LayerModel { Layers = [new LayerDefinition { TopDepth = 0 }] }, "Created layer model"));
        }
    }

    [RelayCommand]
    private void AddBoundary()
    {
        if (NewBoundary is not { } d || d <= 0)
        {
            Workspace.Ui.Notify($"Enter the depth of the new boundary ({LengthUnit}).", "warning");
            return;
        }

        EnsureModel();
        Execute(Edits.AddBoundary(State, FromDisplay(d, Quantity.Length)));
        NewBoundary = null;
    }

    /// <summary>Adds a boundary where the user clicked on the travel-time graph (with Ctrl).</summary>
    public void AddBoundaryAt(double depth)
    {
        if (depth <= 0)
        {
            return;
        }

        EnsureModel();
        Execute(Edits.AddBoundary(State, Math.Round(depth, 2)));
    }

    public void MoveBoundary(Guid layerId, double depth, bool completed, bool fromDisplay = false)
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        var si = fromDisplay ? FromDisplay(depth, Quantity.Length) : depth;
        if (!_dragging && !completed)
        {
            Workspace.Session.BeginGesture();
            _dragging = true;
        }

        var edit = Edits.MoveBoundary(State, layerId, Math.Round(si, 2));
        if (_dragging)
        {
            Workspace.Session.Gesture(edit);
            if (completed)
            {
                Workspace.Session.EndGesture();
                _dragging = false;
            }
        }
        else
        {
            Execute(edit);
        }
    }

    public void DeleteLayer(Guid id)
    {
        Execute(Edits.RemoveBoundary(State, id));
        Workspace.SelectedLayerId = null;
    }

    public void MergeLayer(Guid id) => Execute(Edits.MergeLayer(State, id));

    public void SplitLayer(Guid id) => Execute(Edits.SplitLayer(State, id, Workspace.Result?.Layers.LastOrDefault()?.Bottom ?? 30));

    [RelayCommand]
    private void Suggest()
    {
        Suggestions.Clear();
        if (Workspace.Result is not { } r)
        {
            return;
        }

        var obs = r.S.Observations.Count(o => o.Included) >= 6 ? r.S.Observations : r.P.Observations;
        foreach (var s in new LayerModeler().SuggestBoundaries(obs))
        {
            Suggestions.Add(new SuggestionRow(s.Depth, Units.Format(s.Depth, Quantity.Length), s.Reason));
        }

        OnPropertyChanged(nameof(HasSuggestions));
        Workspace.Ui.Notify(Suggestions.Count == 0 ? "No clear slope changes were found; a single layer fits the data." : $"{Suggestions.Count} possible boundary(ies) found. Review and apply the ones you agree with.");
    }

    [RelayCommand]
    private void ApplySuggestion(SuggestionRow row)
    {
        EnsureModel();
        Execute(Edits.AddBoundary(State, row.Depth).With("source", "suggestion"));
        Suggestions.Remove(row);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private void ApplyAllSuggestions()
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        var model = LayerEdits.FromBoundaries(Suggestions.Select(s => s.Depth), State.ActiveScenario.Layers);
        Execute(Edits.SetLayerModel(model, $"Applied {Suggestions.Count} suggested boundaries (replacing the previous layers)", "layer.suggest"));
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private void DismissSuggestions()
    {
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private void SelectLayer(LayerRow row) => Workspace.SelectedLayerId = row.Id;

    public void PlotPointClicked(object? tag)
    {
        if (tag is ValueTuple<Guid, WaveType> t)
        {
            Workspace.SelectedStationId = t.Item1;
            if (Workspace.Result?.LayerResults.FirstOrDefault(l => l.Layer.Owns(Workspace.Result.For(t.Item2).Observations.First(o => o.StationId == t.Item1).Depth)) is { } layer)
            {
                Workspace.SelectedLayerId = layer.Layer.Id;
            }
        }
    }

    public void TogglePointExcluded(object? tag)
    {
        if (tag is ValueTuple<Guid, WaveType> t && State.ActiveScenario.PicksFor(t.Item2).GetValueOrDefault(t.Item1) is { } p)
        {
            Execute(Edits.SetPickIncluded(State, t.Item2, t.Item1, !p.Included));
        }
    }
}
