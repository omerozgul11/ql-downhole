using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DownholeStudio.Domain.Units;

namespace DownholeStudio.Domain;

public sealed record ProjectMetadata
{
    public Guid ProjectId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Untitled survey";
    public string? Description { get; init; }
    public string? JobNumber { get; init; }
    public DateOnly? SurveyDate { get; init; }
    public string? FieldCrew { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedWithVersion { get; init; } = "";
    public UnitSystem DisplayUnits { get; init; } = UnitSystem.SI;
    public string? Notes { get; init; }
}

public sealed record ContactInfo
{
    public string? Name { get; init; }
    public string? Contact { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
}

public sealed record SiteInfo
{
    public string? Name { get; init; }
    public string? Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Description { get; init; }
}

public sealed record InstrumentInfo
{
    public string? Seismograph { get; init; }
    public string? BoreholeSensor { get; init; }
    public string? SensorSerial { get; init; }
    public string? PSource { get; init; }
    public string? SSource { get; init; }
    public string? TriggerType { get; init; }
    public string? Notes { get; init; }
}

public sealed record ReportBranding
{
    public string? CompanyName { get; init; }
    /// <summary>Path of the logo inside the project container (assets/…), when set.</summary>
    public string? LogoAsset { get; init; }
    public string? Address { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Website { get; init; }
    public string? Footer { get; init; }
    public string? EngineerName { get; init; }
    public string? EngineerTitle { get; init; }
    public string? Registration { get; init; }
}

public enum ReportSection
{
    CoverPage,
    ProjectInformation,
    ClientInformation,
    SiteInformation,
    SurveyConfiguration,
    InstrumentInformation,
    SurveyGeometry,
    ProcessingMethodology,
    QualityControl,
    PWaveAnalysis,
    SWaveAnalysis,
    TravelTimeCurves,
    VelocityProfiles,
    LayerModel,
    EngineeringParameters,
    Vs30,
    ProcessingNotes,
    Warnings,
    Conclusions,
    AppendixPickTable,
    AppendixProcessingHistory,
}

public sealed record ReportConfiguration
{
    public string? Title { get; init; }
    public string? Revision { get; init; } = "0";
    public ContactInfo Client { get; init; } = new();
    public SiteInfo Site { get; init; } = new();
    public InstrumentInfo Instrument { get; init; } = new();
    public ReportBranding Branding { get; init; } = new();
    public ImmutableList<ReportSection> Sections { get; init; } = [.. Enum.GetValues<ReportSection>()];
    public string? Conclusions { get; init; }
    public string? ProcessingNotes { get; init; }
    public string? PreparedBy { get; init; }
    public string? CheckedBy { get; init; }
}

public sealed record SnapshotInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The complete editable state of a project. Immutable: every edit produces a new instance,
/// which is what makes application-wide undo/redo exact.
/// </summary>
public sealed record ProjectState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ProjectMetadata Metadata { get; init; } = new();
    public Survey Survey { get; init; } = new();
    public ImmutableList<AnalysisScenario> Scenarios { get; init; } = [];
    public Guid ActiveScenarioId { get; init; }
    public ReportConfiguration Report { get; init; } = new();
    public ImmutableList<SnapshotInfo> Snapshots { get; init; } = [];

    [JsonIgnore]
    public AnalysisScenario ActiveScenario =>
        Scenarios.FirstOrDefault(s => s.Id == ActiveScenarioId) ?? Scenarios.FirstOrDefault() ?? new AnalysisScenario();

    public ProjectState WithActiveScenario(Func<AnalysisScenario, AnalysisScenario> change)
    {
        var current = ActiveScenario;
        var updated = change(current);
        var index = Scenarios.FindIndex(s => s.Id == current.Id);
        return index < 0
            ? this with { Scenarios = Scenarios.Add(updated), ActiveScenarioId = updated.Id }
            : this with { Scenarios = Scenarios.SetItem(index, updated) };
    }

    public ProjectState WithSurvey(Func<Survey, Survey> change) => this with { Survey = change(Survey) };

    /// <summary>Creates an empty project with one default scenario.</summary>
    public static ProjectState CreateNew(string name, string appVersion, DateTimeOffset now, UnitSystem units = UnitSystem.SI)
    {
        var scenario = new AnalysisScenario
        {
            Name = "Scenario A",
            Description = "Primary interpretation",
            CreatedAt = now,
            VerticalPipeline = new ProcessingPipeline { Steps = [new RemoveMeanStep()] },
            HorizontalPipeline = new ProcessingPipeline { Steps = [new RemoveMeanStep()] },
            ReferencePipeline = new ProcessingPipeline { Steps = [new RemoveMeanStep()] },
        };
        return new ProjectState
        {
            Metadata = new ProjectMetadata { Name = name, CreatedAt = now, CreatedWithVersion = appVersion, DisplayUnits = units },
            Scenarios = [scenario],
            ActiveScenarioId = scenario.Id,
        };
    }
}

/// <summary>One audited change to the project.</summary>
public sealed record HistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Machine-readable operation code, e.g. "pick.move".</summary>
    public required string Operation { get; init; }
    public required string Description { get; init; }
    public ImmutableDictionary<string, string> Parameters { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string? User { get; init; }
    public string? AppVersion { get; init; }
    public string? Scenario { get; init; }
}

/// <summary>A validation or quality-control finding.</summary>
public sealed record Diagnostic
{
    public Severity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Details { get; init; }
    /// <summary>Id of the record, trace, station or layer the finding concerns.</summary>
    public Guid? SubjectId { get; init; }
    /// <summary>An optional correction the user may explicitly approve.</summary>
    public string? SuggestedFix { get; init; }

    public static Diagnostic Error(string code, string message, string? details = null, Guid? subject = null) =>
        new() { Severity = Severity.Error, Code = code, Message = message, Details = details, SubjectId = subject };

    public static Diagnostic Warning(string code, string message, string? details = null, Guid? subject = null) =>
        new() { Severity = Severity.Warning, Code = code, Message = message, Details = details, SubjectId = subject };

    public static Diagnostic Info(string code, string message, string? details = null, Guid? subject = null) =>
        new() { Severity = Severity.Information, Code = code, Message = message, Details = details, SubjectId = subject };
}
