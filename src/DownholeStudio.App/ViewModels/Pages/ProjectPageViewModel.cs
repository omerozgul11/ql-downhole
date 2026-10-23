using System.Collections.ObjectModel;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.App.Services;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Reporting;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed record SnapshotRow(Guid Id, string Name, string Created, string? Note);

public sealed record ScenarioRow(Guid Id, string Name, string? Description, bool Active);

public sealed partial class ProjectPageViewModel : PageViewModel
{
    private bool _loading;

    public ProjectPageViewModel(WorkspaceViewModel ws) : base(ws) => Load();

    public override bool HasInspector => false;

    public override string NavTitle => "Project";
    public override string Title => "Project";
    public override string Description => "Name the survey and record who, where and when. Everything you do afterwards is saved in one project file (.dhsx) together with the original records, so another engineer can reproduce your analysis.";
    public override string IconKey => "Project";
    public override PageViewModel? Next => Workspace.ImportPage;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _projectDescription;
    [ObservableProperty] private string? _jobNumber;
    [ObservableProperty] private DateTime? _surveyDate;
    [ObservableProperty] private string? _fieldCrew;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private bool _useUsUnits;
    [ObservableProperty] private string _newSnapshotName = "";
    [ObservableProperty] private string _newScenarioName = "";
    [ObservableProperty] private ScenarioRow? _selectedScenario;

    public ObservableCollection<SnapshotRow> Snapshots { get; } = [];
    public ObservableCollection<ScenarioRow> Scenarios { get; } = [];

    public string FileLocation => Workspace.Session.FilePath ?? "Not saved yet";
    public string ProjectId => State.Metadata.ProjectId.ToString();
    public string CreatedWith => $"Created {State.Metadata.CreatedAt.ToLocalTime():g} with Downhole Studio {State.Metadata.CreatedWithVersion}";

    public override void OnStateChanged() => Load();

    private void Load()
    {
        _loading = true;
        var m = State.Metadata;
        Name = m.Name;
        ProjectDescription = m.Description;
        JobNumber = m.JobNumber;
        SurveyDate = m.SurveyDate?.ToDateTime(TimeOnly.MinValue);
        FieldCrew = m.FieldCrew;
        Notes = m.Notes;
        UseUsUnits = m.DisplayUnits == UnitSystem.USCustomary;
        Snapshots.Clear();
        foreach (var s in State.Snapshots.OrderByDescending(s => s.CreatedAt))
        {
            Snapshots.Add(new SnapshotRow(s.Id, s.Name, s.CreatedAt.ToLocalTime().ToString("g"), s.Note));
        }

        Scenarios.Clear();
        foreach (var s in State.Scenarios)
        {
            Scenarios.Add(new ScenarioRow(s.Id, s.Name, s.Description, s.Id == State.ActiveScenario.Id));
        }

        OnPropertyChanged(nameof(FileLocation));
        OnPropertyChanged(nameof(ProjectId));
        NotifyUnits();
        _loading = false;
    }

    private void Commit(Func<ProjectMetadata, ProjectMetadata> change)
    {
        if (_loading)
        {
            return;
        }

        var updated = change(State.Metadata);
        if (updated != State.Metadata)
        {
            Execute(Edits.SetMetadata(updated));
        }
    }

    partial void OnNameChanged(string value) => Commit(m => m with { Name = string.IsNullOrWhiteSpace(value) ? "Untitled survey" : value.Trim() });
    partial void OnProjectDescriptionChanged(string? value) => Commit(m => m with { Description = Clean(value) });
    partial void OnJobNumberChanged(string? value) => Commit(m => m with { JobNumber = Clean(value) });
    partial void OnSurveyDateChanged(DateTime? value) => Commit(m => m with { SurveyDate = value is { } d ? DateOnly.FromDateTime(d) : null });
    partial void OnFieldCrewChanged(string? value) => Commit(m => m with { FieldCrew = Clean(value) });
    partial void OnNotesChanged(string? value) => Commit(m => m with { Notes = Clean(value) });
    partial void OnUseUsUnitsChanged(bool value) => Commit(m => m with { DisplayUnits = value ? UnitSystem.USCustomary : UnitSystem.SI });

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private async Task Save() => await Workspace.SaveAsync();

    [RelayCommand]
    private async Task SaveAs() => await Workspace.SaveAsAsync();

    [RelayCommand]
    private async Task Duplicate()
    {
        // A duplicate is a new project file, like Save As, so it needs an activated licence.
        if (!Workspace.CanModify())
        {
            return;
        }

        var path = await Workspace.Ui.SaveFileAsync("Duplicate project", $"{State.Metadata.Name} (copy).dhsx", UiService.ProjectFilter);
        if (path is null)
        {
            return;
        }

        await Task.Run(() => Workspace.Session.SaveDuplicate(path, Path.GetFileNameWithoutExtension(path)));
        Workspace.Ui.Notify($"Duplicate saved as {Path.GetFileName(path)}. The current project is unchanged.", "success");
    }

    /// <summary>Archive: project file, audit record and result tables in one ZIP.</summary>
    [RelayCommand]
    private async Task Archive()
    {
        if (Workspace.Session.FilePath is null || Workspace.Session.IsDirty)
        {
            var answer = await Workspace.Ui.AskAsync("Save before archiving?", "The archive contains the saved project file. Save the current changes first?", ["Save and continue", "Cancel"]);
            if (answer != "Save and continue" || !await Workspace.SaveAsync())
            {
                return;
            }
        }

        var path = await Workspace.Ui.SaveFileAsync("Create project archive", $"{State.Metadata.Name} archive {DateTime.Now:yyyy-MM-dd}.zip", UiService.ZipFilter);
        if (path is null || Workspace.Result is not { } result)
        {
            return;
        }

        await Task.Run(() =>
        {
            // FileMode.Create: the user may have chosen to replace an earlier archive in the save dialog.
            using var zip = new ZipArchive(new FileStream(path, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(Workspace.Session.FilePath!, Path.GetFileName(Workspace.Session.FilePath!));
            var audit = AuditExporter.Create(State, result, Workspace.Session.History, Workspace.Main.Version, Workspace.Main.Clock.Now);
            using (var w = new StreamWriter(zip.CreateEntry("audit.json").Open()))
            {
                w.Write(audit);
            }

            var xlsx = XlsxWriter.Write([ReportTables.Picks(State, result), ReportTables.Layers(result), ReportTables.Engineering(result), ReportTables.Qc(result.Qc),
                ReportTables.History(Workspace.Session.History)], Units, State.Metadata.Name);
            using var x = zip.CreateEntry("results.xlsx").Open();
            x.Write(xlsx);
        });
        Workspace.Session.Record("project.archive", $"Archive created: {Path.GetFileName(path)}");
        Workspace.Ui.Notify($"Archive saved to {path}.", "success");
    }

    [RelayCommand]
    private void CreateSnapshot()
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewSnapshotName) ? $"Snapshot {DateTime.Now:g}" : NewSnapshotName.Trim();
        Workspace.Session.CreateSnapshot(name);
        NewSnapshotName = "";
        Workspace.Ui.Notify($"Snapshot \"{name}\" created.", "success");
    }

    [RelayCommand]
    private async Task RestoreSnapshot(SnapshotRow row)
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        var answer = await Workspace.Ui.AskAsync($"Restore \"{row.Name}\"?", "The project returns to the state saved in the snapshot. You can undo this with Ctrl+Z.", ["Restore", "Cancel"]);
        if (answer == "Restore")
        {
            Workspace.Session.RestoreSnapshot(row.Id);
        }
    }

    [RelayCommand]
    private void DeleteSnapshot(SnapshotRow row)
    {
        if (Workspace.CanModify())
        {
            Workspace.Session.DeleteSnapshot(row.Id);
        }
    }

    [RelayCommand]
    private void CreateScenario()
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewScenarioName) ? $"Scenario {(char)('A' + State.Scenarios.Count)}" : NewScenarioName.Trim();
        Workspace.Session.CreateScenario(name, $"Copy of {State.ActiveScenario.Name}");
        NewScenarioName = "";
        Workspace.Ui.Notify($"Scenario \"{name}\" created and activated. Raw data is shared, not copied.", "success");
    }

    [RelayCommand]
    private void ActivateScenario(ScenarioRow row)
    {
        if (Workspace.CanModify() && !row.Active)
        {
            Workspace.Session.SwitchScenario(row.Id);
        }
    }

    [RelayCommand]
    private async Task DeleteScenario(ScenarioRow row)
    {
        if (!Workspace.CanModify())
        {
            return;
        }

        if (State.Scenarios.Count <= 1)
        {
            Workspace.Ui.Notify("A project needs at least one scenario.", "warning");
            return;
        }

        var answer = await Workspace.Ui.AskAsync($"Delete scenario \"{row.Name}\"?", "Its picks, processing and layer model are removed (you can undo this).", ["Delete", "Cancel"]);
        if (answer == "Delete")
        {
            Workspace.Session.DeleteScenario(row.Id);
        }
    }

    [RelayCommand]
    private async Task ExportAudit()
    {
        if (Workspace.Result is not { } result)
        {
            return;
        }

        var path = await Workspace.Ui.SaveFileAsync("Export audit record", $"{State.Metadata.Name} audit.json", new FileFilter("JSON", "*.json"));
        if (path is null)
        {
            return;
        }

        await File.WriteAllTextAsync(path, AuditExporter.Create(State, result, Workspace.Session.History, Workspace.Main.Version, Workspace.Main.Clock.Now));
        Workspace.Ui.Notify("Audit record exported.", "success");
    }
}
