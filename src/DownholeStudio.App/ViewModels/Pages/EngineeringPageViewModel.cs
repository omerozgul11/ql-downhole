using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis.Engineering;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting.Charts;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed partial class DensityRow : ObservableObject
{
    private readonly EngineeringPageViewModel _owner;
    private readonly bool _loading;

    public DensityRow(EngineeringPageViewModel owner, Guid id, string range, double? density, string used, string properties)
    {
        _owner = owner;
        Id = id;
        Range = range;
        _loading = true;
        Density = density;
        _loading = false;
        Used = used;
        Properties = properties;
    }

    public Guid Id { get; }
    public string Range { get; }
    public string Used { get; }
    public string Properties { get; }

    [ObservableProperty] private double? _density;

    partial void OnDensityChanged(double? value)
    {
        if (!_loading)
        {
            _owner.SetLayerDensity(Id, value);
        }
    }
}

public sealed partial class ProfileToggle(EngineeringPageViewModel owner, EngineeringProperty property, string label, bool on) : ObservableObject
{
    public EngineeringProperty Property { get; } = property;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isOn = on;

    partial void OnIsOnChanged(bool value) => owner.BuildPlots();
}

public sealed record PropertyPlot(EngineeringProperty Property, XyPlotModel Model);

public sealed record ReadoutRow(string Key, string Value);

public sealed record IntervalRow(int Index, string Label, string Value, string Status);

public sealed partial class EngineeringPageViewModel : PageViewModel
{
    private bool _loading;

    public EngineeringPageViewModel(WorkspaceViewModel ws) : base(ws)
    {
        foreach (var (p, label, on) in new[]
                 {
                     (EngineeringProperty.Vp, "Vp", true), (EngineeringProperty.Vs, "Vs", true), (EngineeringProperty.VpVs, "Vp/Vs", false), (EngineeringProperty.Poisson, "Poisson's ratio", true),
                     (EngineeringProperty.Density, "Density", true), (EngineeringProperty.Shear, "Shear modulus", true), (EngineeringProperty.Young, "Young's modulus", true), (EngineeringProperty.Bulk, "Bulk modulus", false),
                 })
        {
            Toggles.Add(new ProfileToggle(this, p, label, on));
        }
    }

    public static IReadOnlyList<Choice<Domain.DensitySource>> SourceOptions { get; } =
        [new(Domain.DensitySource.Measured, "Measured — enter a density for each layer"), new(Domain.DensitySource.Constant, "One density for all layers"), new(Domain.DensitySource.Estimated, "Estimate from Vp (empirical)")];

    public static IReadOnlyList<Choice<DensityEstimationMethod>> EstimationOptions { get; } =
        [new(DensityEstimationMethod.Gardner1974, "Gardner et al. (1974)"), new(DensityEstimationMethod.Brocher2005NafeDrake, "Brocher (2005) Nafe–Drake fit")];

    public override bool HasInspector => false;

    public override string NavTitle => "Engineering";
    public override string Title => "Engineering parameters";
    public override string Description => "Small-strain elastic properties from Vp, Vs and density for each layer. Density is never assumed: enter measured values, one constant, or deliberately choose an empirical estimate (results are then marked ESTIMATED). Vs30 is only computed from measured depth unless you explicitly allow extrapolation.";
    public override string IconKey => "Engineering";
    public override PageViewModel? Next => Workspace.ResultsPage;

    public ObservableCollection<ProfileToggle> Toggles { get; } = [];
    public ObservableCollection<PropertyPlot> Plots { get; } = [];
    public ObservableCollection<DensityRow> Densities { get; } = [];
    public ObservableCollection<IntervalRow> Intervals { get; } = [];
    public ObservableCollection<ReadoutRow> Readout { get; } = [];

    [ObservableProperty] private Choice<Domain.DensitySource> _densitySource = SourceOptions[0];
    [ObservableProperty] private Choice<DensityEstimationMethod> _estimation = EstimationOptions[0];
    [ObservableProperty] private double? _constantDensity;
    [ObservableProperty] private bool _allowExtrapolation;
    [ObservableProperty] private string _vs30Value = "—";
    [ObservableProperty] private string _vs30Status = "";
    [ObservableProperty] private bool _vs30Extrapolated;
    [ObservableProperty] private bool _vs30Missing;
    [ObservableProperty] private double? _intervalTop;
    [ObservableProperty] private double? _intervalBottom;
    [ObservableProperty] private string _readoutDepth = "Hover over a profile to read all properties at one depth.";
    [ObservableProperty] private double? _hoverDepth;

    public bool IsMeasured => DensitySource.Value == Domain.DensitySource.Measured;
    public bool IsConstant => DensitySource.Value == Domain.DensitySource.Constant;
    public bool IsEstimated => DensitySource.Value == Domain.DensitySource.Estimated;

    public string EstimationAssumptions => Estimation.Value == DensityEstimationMethod.Gardner1974
        ? "ρ = 1.74 · Vp^0.25 (g/cm³, Vp in km/s). Derived for brine-saturated sedimentary rocks with Vp 1.5–6.1 km/s; approximate for soils and outside that range."
        : "Polynomial fit to the Nafe–Drake curve (Brocher 2005), valid for Vp 1.5–8.5 km/s in sedimentary and crystalline rocks; approximate for soils.";

    partial void OnDensitySourceChanged(Choice<Domain.DensitySource> value) => CommitDensity();
    partial void OnEstimationChanged(Choice<DensityEstimationMethod> value)
    {
        OnPropertyChanged(nameof(EstimationAssumptions));
        CommitDensity();
    }

    partial void OnConstantDensityChanged(double? value) => CommitDensity();

    partial void OnHoverDepthChanged(double? value)
    {
        UpdateReadout();
        BuildPlots();
    }

    private void CommitDensity()
    {
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(IsConstant));
        OnPropertyChanged(nameof(IsEstimated));
        if (_loading)
        {
            return;
        }

        var m = new DensityModel
        {
            Source = DensitySource.Value, EstimationMethod = Estimation.Value,
            ConstantDensity = ConstantDensity is { } c ? FromDisplay(c, Quantity.Density) : null,
        };
        if (m != State.ActiveScenario.Density)
        {
            Execute(Edits.SetDensityModel(m));
        }
    }

    partial void OnAllowExtrapolationChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _ = ConfirmExtrapolationAsync(value);
    }

    private async Task ConfirmExtrapolationAsync(bool allow)
    {
        if (allow)
        {
            var answer = await Workspace.Ui.AskAsync("Allow Vs30 extrapolation?",
                "Measurements do not reach 30 m. If you continue, the deepest layer's Vs is assumed to continue to 30 m. The result is not a measurement and is labelled EXTRAPOLATED in the application and in the report.",
                ["Allow extrapolation", "Cancel"], kind: "warning");
            if (answer != "Allow extrapolation")
            {
                _loading = true;
                AllowExtrapolation = false;
                _loading = false;
                return;
            }
        }

        var a = State.ActiveScenario.Analysis;
        Execute(Edits.SetAnalysisSettings(a with { Vs30 = a.Vs30 with { AllowExtrapolation = allow } }, allow ? "Vs30 extrapolation ALLOWED by user" : "Vs30 extrapolation disabled"));
    }

    public void SetLayerDensity(Guid id, double? display)
    {
        var value = display is { } d ? FromDisplay(d, Quantity.Density) : (double?)null;
        if (State.ActiveScenario.Layers.Layers.Count == 0)
        {
            // The analysis uses one implicit layer until layers are interpreted; make it explicit to hold the density.
            Execute(Edits.SetLayerModel(new LayerModel { Layers = [new LayerDefinition { TopDepth = 0, MeasuredDensity = value }] }, "Layer density entered", "density.layer"));
            return;
        }

        Execute(Edits.SetLayerDensity(State, id, value));
    }

    public override void OnStateChanged()
    {
        _loading = true;
        var d = State.ActiveScenario.Density;
        DensitySource = SourceOptions.First(s => s.Value == d.Source);
        Estimation = EstimationOptions.First(e => e.Value == d.EstimationMethod);
        ConstantDensity = ToDisplay(d.ConstantDensity, Quantity.Density);
        AllowExtrapolation = State.ActiveScenario.Analysis.Vs30.AllowExtrapolation;
        _loading = false;
        OnPropertyChanged(nameof(IsMeasured));
        OnPropertyChanged(nameof(IsConstant));
        OnPropertyChanged(nameof(IsEstimated));
        NotifyUnits();
    }

    public override void OnResultChanged()
    {
        if (!IsActive || Workspace.Result is not { } r)
        {
            return;
        }

        BuildPlots();
        Densities.Clear();
        var u = Units;
        foreach (var l in r.LayerResults)
        {
            var used = l.Density.Value is null ? "None" : (l.Density.Estimated ? "ESTIMATED " : "") + u.Format(l.Density.Value, Quantity.Density);
            var props = $"ν {(l.Properties.PoissonRatio is { } nu ? nu.ToString("0.000") : "—")} · G {u.Format(l.Properties.ShearModulus, Quantity.Modulus)}";
            Densities.Add(new DensityRow(this, l.Layer.Id, $"{u.Format(l.Layer.Top, Quantity.Length, false)}–{u.Format(l.Layer.Bottom, Quantity.Length)}",
                ToDisplay(l.Layer.Definition.MeasuredDensity, Quantity.Density), used, props));
        }

        var v = r.Vs30;
        Vs30Missing = v.Value is null;
        Vs30Extrapolated = v.Extrapolated;
        Vs30Value = v.Value is { } val ? u.Format(val, Quantity.Velocity) : "Not calculated";
        Vs30Status = v.Value is null ? v.Reason ?? "" : v.Extrapolated ? $"EXTRAPOLATED below {u.Format(v.ExtrapolatedFrom, Quantity.Length)} by extending the deepest layer." : $"From measured data (travel time through the top 30 m: {v.TravelTime * 1000:0.00} ms).";
        Intervals.Clear();
        for (var i = 0; i < r.CustomIntervals.Count; i++)
        {
            var c = r.CustomIntervals[i];
            Intervals.Add(new IntervalRow(i, $"Vs {u.Format(c.Top, Quantity.Length, false)}–{u.Format(c.Bottom, Quantity.Length)}", c.Value is { } x ? u.Format(x, Quantity.Velocity) : "—",
                c.Value is null ? c.Reason ?? "" : c.Extrapolated ? "EXTRAPOLATED" : "Measured"));
        }

        UpdateReadout();
    }

    public override void OnActivated()
    {
        OnStateChanged();
        OnResultChanged();
    }

    public void BuildPlots()
    {
        if (Workspace.Result is not { } r)
        {
            return;
        }

        Plots.Clear();
        foreach (var t in Toggles.Where(t => t.IsOn))
        {
            var m = ChartFactory.Property(r, t.Property, Units, PlotTheme, HoverDepth);
            Plots.Add(new PropertyPlot(t.Property, m));
        }
    }

    private void UpdateReadout()
    {
        Readout.Clear();
        if (HoverDepth is not { } depth || Workspace.Result is not { } r)
        {
            return;
        }

        var layer = r.LayerResults.FirstOrDefault(l => l.Layer.Owns(depth));
        ReadoutDepth = $"At {Units.Format(depth, Quantity.Length)}" + (layer is null ? " (outside the model)" : $" — layer {layer.Layer.Index + 1}");
        if (layer is null)
        {
            return;
        }

        var u = Units;
        var p = layer.Properties;
        foreach (var kv in new (string, string)[]
                 {
                     ("Vp", u.Format(p.Vp, Quantity.Velocity)), ("Vs", u.Format(p.Vs, Quantity.Velocity)), ("Vp/Vs", p.VpVsRatio is { } x ? x.ToString("0.00") : "—"),
                     ("Poisson's ratio", p.PoissonRatio is { } nu ? nu.ToString("0.000") : "—"), ("Density", u.Format(p.Density, Quantity.Density) + (p.DensityEstimated ? " (est.)" : "")),
                     ("Shear modulus G", u.Format(p.ShearModulus, Quantity.Modulus)), ("Young's modulus E", u.Format(p.YoungModulus, Quantity.Modulus)), ("Bulk modulus K", u.Format(p.BulkModulus, Quantity.Modulus)),
                 })
        {
            Readout.Add(new ReadoutRow(kv.Item1, kv.Item2));
        }
    }

    [RelayCommand]
    private void AddInterval()
    {
        if (IntervalTop is not { } top || IntervalBottom is not { } bottom || bottom <= top)
        {
            Workspace.Ui.Notify("Enter a top and a deeper bottom depth for the interval.", "warning");
            return;
        }

        var a = State.ActiveScenario.Analysis;
        var interval = new DepthInterval { Top = FromDisplay(top, Quantity.Length), Bottom = FromDisplay(bottom, Quantity.Length) };
        Execute(Edits.SetAnalysisSettings(a with { Vs30 = a.Vs30 with { CustomIntervals = a.Vs30.CustomIntervals.Add(interval) } }, $"Added time-averaged Vs interval {top}–{bottom} {LengthUnit}"));
        IntervalTop = IntervalBottom = null;
    }

    [RelayCommand]
    private void RemoveInterval(IntervalRow row)
    {
        var a = State.ActiveScenario.Analysis;
        Execute(Edits.SetAnalysisSettings(a with { Vs30 = a.Vs30 with { CustomIntervals = a.Vs30.CustomIntervals.RemoveAt(row.Index) } }, $"Removed interval {row.Label}"));
    }

    [RelayCommand]
    private void ApplyDensityToAll()
    {
        var first = Densities.FirstOrDefault(d => d.Density is not null)?.Density;
        if (first is not { } value)
        {
            Workspace.Ui.Notify("Enter a density for at least one layer first.", "warning");
            return;
        }

        var si = FromDisplay(value, Quantity.Density);
        var model = State.ActiveScenario.Layers with { Layers = State.ActiveScenario.Layers.Layers.Select(l => l with { MeasuredDensity = l.MeasuredDensity ?? si }).ToImmutableList() };
        Execute(Edits.SetLayerModel(model, $"Density {value} {DensityUnit} applied to layers without a value", "density.layer"));
    }
}
