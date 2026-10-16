using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Core;
using DownholeStudio.Core.Import;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Visualization;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed partial class RecordRow : ObservableObject
{
    private readonly GeometryPageViewModel _owner;
    private bool _loading;

    public RecordRow(GeometryPageViewModel owner, SeismicRecord record, double? depthDisplay)
    {
        _owner = owner;
        Record = record;
        _loading = true;
        Depth = depthDisplay;
        Shot = GeometryPageViewModel.ShotChoices.First(c => c.Value == record.Shot);
        Excluded = record.Excluded;
        _loading = false;
    }

    public SeismicRecord Record { get; }
    public string FileName => Record.Source.FileName;
    public int Sequence => Record.Sequence + 1;
    public string Suggested => Record.SuggestedDepth is { } d ? $"{d:0.###} m {(Record.SuggestedShot == ShotKind.Unknown ? "" : Record.SuggestedShot.ToString())}" : "";
    public string Recorded => Record.AcquiredAt?.ToString("HH:mm:ss") ?? "";

    [ObservableProperty] private double? _depth;
    [ObservableProperty] private Choice<ShotKind> _shot;
    [ObservableProperty] private bool _excluded;

    partial void OnDepthChanged(double? value)
    {
        if (!_loading)
        {
            _owner.AssignRecord(this, value, Shot.Value);
        }
    }

    partial void OnShotChanged(Choice<ShotKind> value)
    {
        if (!_loading)
        {
            _owner.AssignRecord(this, Depth, value.Value);
        }
    }

    partial void OnExcludedChanged(bool value)
    {
        if (!_loading)
        {
            _owner.SetRecordExcluded(this, value);
        }
    }
}

public sealed partial class ChannelRow : ObservableObject
{
    private readonly GeometryPageViewModel _owner;
    private bool _loading = true;

    public ChannelRow(GeometryPageViewModel owner, int channel, ChannelAssignment? a, string? fileLabel)
    {
        _owner = owner;
        Channel = channel;
        Role = GeometryPageViewModel.RoleChoices.First(c => c.Value == (a?.Role ?? ComponentRole.Unassigned));
        Reverse = a?.ReversePolarity ?? false;
        Label = a?.Label ?? fileLabel;
        FileLabel = fileLabel;
        _loading = false;
    }

    public int Channel { get; }
    public string? FileLabel { get; }

    [ObservableProperty] private Choice<ComponentRole> _role;
    [ObservableProperty] private bool _reverse;
    [ObservableProperty] private string? _label;

    partial void OnRoleChanged(Choice<ComponentRole> value) => Changed();
    partial void OnReverseChanged(bool value) => Changed();
    partial void OnLabelChanged(string? value) => Changed();

    private void Changed()
    {
        if (!_loading)
        {
            _owner.CommitChannels();
        }
    }

    public ChannelAssignment ToAssignment() => new() { ChannelNumber = Channel, Role = Role.Value, ReversePolarity = Reverse, Label = Label };
}

public sealed partial class GeometryPageViewModel : PageViewModel
{
    private bool _loading;

    public GeometryPageViewModel(WorkspaceViewModel ws) : base(ws) => Load();

    public static IReadOnlyList<Choice<ShotKind>> ShotChoices { get; } =
        [new(ShotKind.Unknown, "Unassigned"), new(ShotKind.P, "P (vertical hit)"), new(ShotKind.SPositive, "S+ (left / positive)"), new(ShotKind.SNegative, "S− (right / negative)")];

    public static IReadOnlyList<Choice<ComponentRole>> RoleChoices { get; } =
    [
        new(ComponentRole.Unassigned, "Unassigned"), new(ComponentRole.Vertical, "Vertical (P)"), new(ComponentRole.HorizontalX, "Horizontal X"),
        new(ComponentRole.HorizontalY, "Horizontal Y"), new(ComponentRole.Reference, "Reference / trigger"), new(ComponentRole.Other, "Other"),
    ];

    public static IReadOnlyList<Choice<Domain.DepthReference>> ReferenceChoices { get; } = [new(Domain.DepthReference.GroundSurface, "Ground surface"), new(Domain.DepthReference.Collar, "Top of casing (collar)")];

    public static IReadOnlyList<Choice<AcquisitionDirection>> DirectionChoices { get; } =
        [new(AcquisitionDirection.TopDown, "Top-down (shallow first)"), new(AcquisitionDirection.BottomUp, "Bottom-up (deep first)"), new(AcquisitionDirection.Unknown, "Not recorded")];

    public static IReadOnlyList<Choice<string>> PatternChoices { get; } =
    [
        new("P,S+,S-", "P, S+, S− per depth"), new("P,S-,S+", "P, S−, S+ per depth"), new("S+,S-,P", "S+, S−, P per depth"),
        new("S+,S-", "S+, S− per depth (no P)"), new("P", "P only"), new("P,S+,S-,S+,S-", "P, then S± twice"),
    ];

    public override bool HasInspector => false;

    public override string NavTitle => "Geometry";
    public override string Title => "Survey geometry & channels";
    public override string Description => "Describe the borehole and where the P and S sources were, then tell Downhole Studio which depth and shot type each record belongs to and which channel is vertical, horizontal or reference. Any change here updates every calculation immediately.";
    public override string IconKey => "Geometry";
    public override PageViewModel? Next => Workspace.WaveformsPage;

    // Borehole.
    [ObservableProperty] private string _boreholeName = "";
    [ObservableProperty] private double? _easting;
    [ObservableProperty] private double? _northing;
    [ObservableProperty] private string? _coordinateSystem;
    [ObservableProperty] private double? _groundElevation;
    [ObservableProperty] private double? _collarHeight;
    [ObservableProperty] private double? _totalDepth;
    [ObservableProperty] private Choice<Domain.DepthReference> _depthReference = ReferenceChoices[0];
    [ObservableProperty] private Choice<AcquisitionDirection> _direction = DirectionChoices[2];
    [ObservableProperty] private double? _sensorAzimuth;

    // Sources.
    [ObservableProperty] private double? _pOffset;
    [ObservableProperty] private double? _pSourceDepth;
    [ObservableProperty] private string? _pDescription;
    [ObservableProperty] private double? _sOffset;
    [ObservableProperty] private double? _sSourceDepth;
    [ObservableProperty] private string? _sDescription;

    // Depth assignment tool.
    [ObservableProperty] private double? _firstDepth = 1;
    [ObservableProperty] private double? _increment = 1;
    [ObservableProperty] private Choice<AcquisitionDirection> _assignDirection = DirectionChoices[0];
    [ObservableProperty] private Choice<string> _pattern = PatternChoices[0];

    [ObservableProperty] private BoreholeDiagramModel? _diagram;
    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private DomainProfileChoice? _selectedProfile;

    public ObservableCollection<RecordRow> Records { get; } = [];
    public ObservableCollection<ChannelRow> Channels { get; } = [];
    public ObservableCollection<DomainProfileChoice> Profiles { get; } = [];

    public string AssignmentSummary { get; private set; } = "";

    public bool HasSuggestions => State.Survey.Records.Any(r => r.SuggestedDepth is not null);

    public override void OnStateChanged() => Load();

    private void Load()
    {
        _loading = true;
        var g = State.Survey.Geometry;
        var u = Units;
        BoreholeName = g.Borehole.Name;
        Easting = g.Borehole.Easting;
        Northing = g.Borehole.Northing;
        CoordinateSystem = g.Borehole.CoordinateSystem;
        GroundElevation = ToDisplay(g.Borehole.GroundElevation, Quantity.Length);
        CollarHeight = ToDisplay(g.Borehole.CollarHeight, Quantity.Length);
        TotalDepth = ToDisplay(g.Borehole.TotalDepth, Quantity.Length);
        DepthReference = ReferenceChoices.First(c => c.Value == g.DepthReference);
        Direction = DirectionChoices.First(c => c.Value == g.Direction);
        SensorAzimuth = g.SensorAzimuth;
        POffset = ToDisplay(g.PSource.HorizontalOffset, Quantity.Length);
        PSourceDepth = ToDisplay(g.PSource.SourceDepth, Quantity.Length);
        PDescription = g.PSource.Description;
        SOffset = ToDisplay(g.SSource.HorizontalOffset, Quantity.Length);
        SSourceDepth = ToDisplay(g.SSource.SourceDepth, Quantity.Length);
        SDescription = g.SSource.Description;

        Records.Clear();
        foreach (var r in State.Survey.Records.OrderBy(r => r.Sequence))
        {
            double? depth = r.StationId is { } s && State.Survey.FindStation(s) is { } st ? u.ToDisplay(st.MeasuredDepth, Quantity.Length) : null;
            Records.Add(new RecordRow(this, r, depth));
        }

        Channels.Clear();
        var channels = State.Survey.Records.SelectMany(r => r.Traces).GroupBy(t => t.ChannelNumber).OrderBy(g2 => g2.Key);
        foreach (var c in channels)
        {
            Channels.Add(new ChannelRow(this, c.Key, State.Survey.ChannelMap.For(c.Key), c.Select(t => t.Label).FirstOrDefault(l => !string.IsNullOrEmpty(l))));
        }

        Profiles.Clear();
        foreach (var p in Workspace.Main.Catalog.Profiles())
        {
            Profiles.Add(new DomainProfileChoice(p));
        }

        var assigned = State.Survey.Records.Count(r => r.StationId is not null && r.Shot != ShotKind.Unknown);
        AssignmentSummary = State.Survey.Records.Count == 0 ? "No records imported yet."
            : $"{State.Survey.Stations.Count} depth(s); {assigned} of {State.Survey.Records.Count} record(s) assigned to a depth and shot type.";
        OnPropertyChanged(nameof(AssignmentSummary));
        OnPropertyChanged(nameof(HasSuggestions));
        NotifyUnits();
        BuildDiagram();
        _loading = false;
    }

    public override void OnSelectionChanged() => BuildDiagram();

    private void BuildDiagram()
    {
        var g = State.Survey.Geometry;
        var m = new BoreholeDiagramModel
        {
            BoreholeName = g.Borehole.Name, TotalDepth = g.Borehole.TotalDepth ?? (State.Survey.Stations.Count > 0 ? State.Survey.Stations.Max(s => g.DepthBelowGround(s.MeasuredDepth)) : 10),
            CollarHeight = g.Borehole.CollarHeight, POffset = g.PSource.HorizontalOffset, PSourceDepth = g.PSource.SourceDepth, SOffset = g.SSource.HorizontalOffset,
            SSourceDepth = g.SSource.SourceDepth, LengthUnit = Units.Symbol(Quantity.Length), LengthScale = Units.ToDisplay(1, Quantity.Length),
            Note = State.Survey.Stations.Count == 0 ? "Receivers appear once depths are assigned" : null,
        };
        foreach (var s in State.Survey.StationsByDepth())
        {
            m.Receivers.Add(new DiagramReceiver(g.DepthBelowGround(s.MeasuredDepth), State.Survey.RecordsAt(s.Id, ShotKind.P).Any(),
                State.Survey.RecordsAt(s.Id, ShotKind.SPositive).Any() || State.Survey.RecordsAt(s.Id, ShotKind.SNegative).Any(), s.Excluded, s.Id == Workspace.SelectedStationId, s.Id));
        }

        Diagram = m;
    }

    private void CommitGeometry(Func<SurveyGeometry, SurveyGeometry> change, string description)
    {
        if (_loading)
        {
            return;
        }

        var updated = change(State.Survey.Geometry);
        if (updated != State.Survey.Geometry)
        {
            Execute(Edits.SetGeometry(updated, description));
        }
    }

    private double L(double? display, double fallback = 0) => display is { } d ? FromDisplay(d, Quantity.Length) : fallback;

    partial void OnBoreholeNameChanged(string value) => CommitGeometry(g => g with { Borehole = g.Borehole with { Name = string.IsNullOrWhiteSpace(value) ? "BH-1" : value.Trim() } }, "Borehole name changed");
    partial void OnEastingChanged(double? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { Easting = value } }, "Borehole coordinates changed");
    partial void OnNorthingChanged(double? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { Northing = value } }, "Borehole coordinates changed");
    partial void OnCoordinateSystemChanged(string? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { CoordinateSystem = value } }, "Coordinate system changed");
    partial void OnGroundElevationChanged(double? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { GroundElevation = value is null ? null : L(value) } }, "Ground elevation changed");
    partial void OnCollarHeightChanged(double? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { CollarHeight = L(value) } }, "Collar height changed");
    partial void OnTotalDepthChanged(double? value) => CommitGeometry(g => g with { Borehole = g.Borehole with { TotalDepth = value is null ? null : L(value) } }, "Borehole depth changed");
    partial void OnDepthReferenceChanged(Choice<Domain.DepthReference> value) => CommitGeometry(g => g with { DepthReference = value.Value }, $"Depth reference: {value.Label}");
    partial void OnDirectionChanged(Choice<AcquisitionDirection> value) => CommitGeometry(g => g with { Direction = value.Value }, $"Acquisition direction: {value.Label}");
    partial void OnSensorAzimuthChanged(double? value) => CommitGeometry(g => g with { SensorAzimuth = value }, "Sensor orientation changed");
    partial void OnPOffsetChanged(double? value) => CommitGeometry(g => g with { PSource = g.PSource with { HorizontalOffset = Math.Max(0, L(value)) } }, "P source offset changed");
    partial void OnPSourceDepthChanged(double? value) => CommitGeometry(g => g with { PSource = g.PSource with { SourceDepth = L(value) } }, "P source depth changed");
    partial void OnPDescriptionChanged(string? value) => CommitGeometry(g => g with { PSource = g.PSource with { Description = value } }, "P source description changed");
    partial void OnSOffsetChanged(double? value) => CommitGeometry(g => g with { SSource = g.SSource with { HorizontalOffset = Math.Max(0, L(value)) } }, "S source offset changed");
    partial void OnSSourceDepthChanged(double? value) => CommitGeometry(g => g with { SSource = g.SSource with { SourceDepth = L(value) } }, "S source depth changed");
    partial void OnSDescriptionChanged(string? value) => CommitGeometry(g => g with { SSource = g.SSource with { Description = value } }, "S source description changed");

    private static IReadOnlyList<ShotKind> ParsePattern(string pattern) => pattern.Split(',').Select(p => p.Trim() switch
    {
        "P" => ShotKind.P,
        "S+" => ShotKind.SPositive,
        _ => ShotKind.SNegative,
    }).ToList();

    [RelayCommand]
    private void AssignByPattern()
    {
        if (FirstDepth is not { } first || Increment is not { } inc || inc <= 0)
        {
            Workspace.Ui.Notify("Enter the first depth and a positive depth increment.", "warning");
            return;
        }

        var (stations, records) = DepthAssigner.ByPattern(State.Survey, FromDisplay(first, Quantity.Length), FromDisplay(inc, Quantity.Length), AssignDirection.Value, ParsePattern(Pattern.Value));
        Execute(Edits.SetStations(stations, records, $"Assigned {stations.Count} depths by acquisition order ({Pattern.Label}, {AssignDirection.Label})")
            .With("firstDepth", first).With("increment", inc).With("pattern", Pattern.Value).With("direction", AssignDirection.Value));
        if (Direction.Value == AcquisitionDirection.Unknown && AssignDirection.Value != AcquisitionDirection.Unknown)
        {
            Direction = DirectionChoices.First(c => c.Value == AssignDirection.Value);
        }
    }

    [RelayCommand]
    private void ApplySuggestions()
    {
        var survey = State.Survey with { Stations = [], Records = State.Survey.Records.Select(r => r with { StationId = null }).ToImmutableList() };
        var (stations, records) = DepthAssigner.FromSuggestions(survey);
        Execute(Edits.SetStations(stations, records, $"Assigned {stations.Count} depths from file suggestions"));
    }

    [RelayCommand]
    private void MirrorDepths()
    {
        if (State.Survey.Stations.Count < 2)
        {
            return;
        }

        Execute(Edits.SetStations(DepthAssigner.Mirror(State.Survey.Stations), State.Survey.Records, "Mirrored depths (reversed acquisition direction)"));
    }

    public void AssignRecord(RecordRow row, double? depthDisplay, ShotKind shot)
    {
        if (!Workspace.CanModify())
        {
            Load();
            return;
        }

        var survey = State.Survey;
        var stations = survey.Stations;
        Guid? stationId = null;
        if (depthDisplay is { } d)
        {
            var measured = Math.Round(FromDisplay(d, Quantity.Length), 6);
            var existing = stations.FirstOrDefault(s => Math.Abs(s.MeasuredDepth - measured) < 1e-6);
            if (existing is null)
            {
                existing = new Station { Id = Guid.NewGuid(), MeasuredDepth = measured };
                stations = stations.Add(existing);
            }

            stationId = existing.Id;
        }

        var records = survey.Records.Select(r => r.Id == row.Record.Id ? r with { StationId = stationId, Shot = shot } : r).ToImmutableList();
        var pruned = stations.Where(s => records.Any(r => r.StationId == s.Id)).ToImmutableList();
        Execute(Edits.SetStations(pruned, records, $"\"{row.FileName}\" assigned to {(depthDisplay is null ? "no depth" : $"{depthDisplay} {LengthUnit}")} as {shot}"));
    }

    public void SetRecordExcluded(RecordRow row, bool excluded)
    {
        Execute(Edits.SetRecordExcluded(State, row.Record.Id, excluded));
    }

    public void CommitChannels()
    {
        if (_loading)
        {
            return;
        }

        var map = State.Survey.ChannelMap with { Channels = [.. Channels.Select(c => c.ToAssignment())], ProfileName = null };
        Execute(Edits.SetChannelMap(map, "Channel mapping changed"));
    }

    [RelayCommand]
    private void ApplyProfile()
    {
        if (SelectedProfile is not { } p)
        {
            return;
        }

        var missing = Channels.Select(c => c.Channel).Except(p.Profile.Channels.Select(c => c.ChannelNumber)).ToList();
        var assignments = p.Profile.Channels.ToList();
        assignments.AddRange(missing.Select(m => new ChannelAssignment { ChannelNumber = m, Role = ComponentRole.Unassigned }));
        Execute(Edits.SetChannelMap(new ChannelMap { Channels = [.. assignments.OrderBy(a => a.ChannelNumber)], ProfileName = p.Profile.Name }, $"Applied channel profile \"{p.Profile.Name}\""));
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0)
        {
            Workspace.Ui.Notify("Enter a name for the profile.", "warning");
            return;
        }

        try
        {
            Workspace.Main.Catalog.SaveProfile(new InstrumentProfile { Name = name, Channels = [.. Channels.Select(c => c.ToAssignment())], Description = "Saved from project " + State.Metadata.Name }, Workspace.Main.Clock.Now);
            NewProfileName = "";
            Load();
            Workspace.Ui.Notify($"Profile \"{name}\" saved for future projects.", "success");
        }
        catch (InvalidOperationException ex)
        {
            Workspace.Ui.Notify(ex.Message, "warning");
        }
    }
}

public sealed record DomainProfileChoice(InstrumentProfile Profile)
{
    public string Label => Profile.BuiltIn ? Profile.Name + " (built-in)" : Profile.Name;

    public override string ToString() => Label;
}
