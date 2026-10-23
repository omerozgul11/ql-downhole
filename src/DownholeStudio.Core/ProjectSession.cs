using System.Collections.Immutable;
using DownholeStudio.Domain;
using DownholeStudio.Persistence;

namespace DownholeStudio.Core;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

/// <summary>Application identity recorded in projects and history.</summary>
public sealed record AppIdentity(string Version, string UserName)
{
    public static AppIdentity Default { get; } = new(
        typeof(AppIdentity).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        Environment.UserName);
}

/// <summary>One undoable change: a pure function of the project state plus its audit record.</summary>
public sealed record ProjectEdit(string Operation, string Description, Func<ProjectState, ProjectState> Apply)
{
    public ImmutableDictionary<string, string> Parameters { get; init; } = ImmutableDictionary<string, string>.Empty;

    public ProjectEdit With(string key, object? value) =>
        this with { Parameters = Parameters.SetItem(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "") };
}

public enum ChangeOrigin
{
    Edit,
    Undo,
    Redo,
    Load,
    Gesture,
}

public sealed record StateChangedEventArgs(ProjectState Previous, ProjectState Current, ChangeOrigin Origin, string Description);

/// <summary>
/// The open project: current immutable state, application-wide undo/redo, audit history,
/// snapshots, scenarios and save state. All edits go through <see cref="Execute"/>.
/// </summary>
public sealed class ProjectSession
{
    private readonly Stack<(ProjectState State, string Description)> _undo = new();
    private readonly Stack<(ProjectState State, string Description)> _redo = new();
    private readonly IClock _clock;
    private readonly AppIdentity _app;
    private (ProjectState Start, ProjectEdit Last)? _gesture;
    private ProjectState _savedState;

    public ProjectSession(ProjectDocument document, string? filePath, IClock clock, AppIdentity app)
    {
        Document = document;
        FilePath = filePath;
        _clock = clock;
        _app = app;
        _savedState = document.State;
        SessionId = Guid.NewGuid();
    }

    /// <param name="scenarioDefaults">User defaults applied to the first scenario (e.g. preferred pickers).</param>
    public static ProjectSession CreateNew(string name, IClock clock, AppIdentity app, Domain.Units.UnitSystem units = Domain.Units.UnitSystem.SI,
        Func<AnalysisScenario, AnalysisScenario>? scenarioDefaults = null)
    {
        var state = ProjectState.CreateNew(name, app.Version, clock.Now, units);
        if (scenarioDefaults is not null)
        {
            state = state.WithActiveScenario(scenarioDefaults);
        }

        var doc = new ProjectDocument { State = state };
        var session = new ProjectSession(doc, null, clock, app);
        session.AppendHistory(new ProjectEdit("project.create", $"Created project \"{name}\"", s => s));
        session._neverSaved = true;
        return session;
    }

    public static ProjectSession Open(string path, IClock clock, AppIdentity app)
    {
        var doc = ProjectPackage.Load(path);
        var session = new ProjectSession(doc, Path.GetFullPath(path), clock, app);
        session.AppendHistory(new ProjectEdit("project.open", $"Opened in Downhole Studio {app.Version}", s => s));
        session._historyDirty = false;
        return session;
    }

    public Guid SessionId { get; }

    public ProjectDocument Document { get; }

    public ProjectState State => Document.State;

    public string? FilePath { get; private set; }

    public ImmutableList<HistoryEntry> History => Document.History;

    public bool IsDirty => _neverSaved || !ReferenceEquals(Document.State, _savedState) || _historyDirty;

    public bool CanUndo => _undo.Count > 0 && _gesture is null;

    public bool CanRedo => _redo.Count > 0 && _gesture is null;

    public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public bool ReadOnly { get; set; }

    public AppIdentity App => _app;

    public IClock Clock => _clock;

    private bool _historyDirty;
    private bool _neverSaved;

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public event EventHandler? SaveStateChanged;

    /// <summary>Applies an edit, records it for undo and in the audit history.</summary>
    public void Execute(ProjectEdit edit)
    {
        EnsureWritable();
        if (_gesture is not null)
        {
            Gesture(edit);
            return;
        }

        var previous = State;
        var next = edit.Apply(previous);
        if (ReferenceEquals(next, previous))
        {
            return;
        }

        _undo.Push((previous, edit.Description));
        _redo.Clear();
        Document.State = next;
        AppendHistory(edit);
        Raise(previous, ChangeOrigin.Edit, edit.Description);
    }

    /// <summary>Starts a continuous gesture (e.g. dragging a pick); intermediate edits are not individually undoable.</summary>
    public void BeginGesture()
    {
        EnsureWritable();
        _gesture ??= (State, new ProjectEdit("gesture", "", s => s));
    }

    /// <summary>Applies an intermediate gesture state without creating undo entries.</summary>
    public void Gesture(ProjectEdit edit)
    {
        if (_gesture is null)
        {
            Execute(edit);
            return;
        }

        var previous = State;
        Document.State = edit.Apply(previous);
        _gesture = (_gesture.Value.Start, edit);
        Raise(previous, ChangeOrigin.Gesture, edit.Description);
    }

    /// <summary>Completes a gesture as one undo step described by its last edit.</summary>
    public void EndGesture()
    {
        if (_gesture is not { } g)
        {
            return;
        }

        _gesture = null;
        if (ReferenceEquals(g.Start, State) || g.Last.Operation == "gesture")
        {
            return;
        }

        _undo.Push((g.Start, g.Last.Description));
        _redo.Clear();
        AppendHistory(g.Last);
        Raise(g.Start, ChangeOrigin.Edit, g.Last.Description);
    }

    public void CancelGesture()
    {
        if (_gesture is not { } g)
        {
            return;
        }

        _gesture = null;
        var previous = State;
        Document.State = g.Start;
        Raise(previous, ChangeOrigin.Edit, "Cancelled");
    }

    public bool Undo()
    {
        if (!CanUndo || ReadOnly)
        {
            return false;
        }

        var (state, description) = _undo.Pop();
        var previous = State;
        _redo.Push((previous, description));
        Document.State = state;
        AppendHistory(new ProjectEdit("edit.undo", $"Undo: {description}", s => s));
        Raise(previous, ChangeOrigin.Undo, description);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo || ReadOnly)
        {
            return false;
        }

        var (state, description) = _redo.Pop();
        var previous = State;
        _undo.Push((previous, description));
        Document.State = state;
        AppendHistory(new ProjectEdit("edit.redo", $"Redo: {description}", s => s));
        Raise(previous, ChangeOrigin.Redo, description);
        return true;
    }

    /// <summary>Records an event that does not change the project state (e.g. a report export).</summary>
    public void Record(string operation, string description, IReadOnlyDictionary<string, string>? parameters = null)
    {
        AppendHistory(new ProjectEdit(operation, description, s => s) { Parameters = parameters?.ToImmutableDictionary() ?? ImmutableDictionary<string, string>.Empty });
    }

    public void Save(string? path = null)
    {
        EnsureWritable();
        var target = path ?? FilePath ?? throw new InvalidOperationException("Choose where to save the project first.");
        var isSaveAs = path is not null && !string.Equals(Path.GetFullPath(path), FilePath, StringComparison.OrdinalIgnoreCase);
        AppendHistory(new ProjectEdit("project.save", isSaveAs ? $"Saved as \"{Path.GetFileName(target)}\"" : "Saved", s => s).With("path", Path.GetFileName(target)));
        ProjectPackage.Save(Document, target, _app.Version, _clock.Now);
        FilePath = Path.GetFullPath(target);
        _savedState = State;
        _historyDirty = false;
        _neverSaved = false;
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes an independent copy with a new project identity (Duplicate project). The session keeps its own file.</summary>
    public void SaveDuplicate(string path, string newName)
    {
        var copyState = State with { Metadata = State.Metadata with { ProjectId = Guid.NewGuid(), Name = newName, CreatedAt = _clock.Now } };
        var copy = new ProjectDocument
        {
            State = copyState, SnapshotStates = Document.SnapshotStates, Assets = Document.Assets,
            History = Document.History.Add(Entry(new ProjectEdit("project.duplicate", $"Duplicated from \"{State.Metadata.Name}\" ({State.Metadata.ProjectId})", s => s))),
        };
        ProjectPackage.WriteCopy(copy, path, _app.Version, _clock.Now);
    }

    public SnapshotInfo CreateSnapshot(string name, string? note = null)
    {
        var info = new SnapshotInfo { Name = name, Note = note, CreatedAt = _clock.Now };
        var stateForSnapshot = State;
        Execute(new ProjectEdit("snapshot.create", $"Created snapshot \"{name}\"", s => s with { Snapshots = s.Snapshots.Add(info) }));
        Document.SnapshotStates = Document.SnapshotStates.SetItem(info.Id, stateForSnapshot with { Snapshots = [] });
        return info;
    }

    /// <summary>Restores a snapshot's interpretation and survey as one undoable step. Snapshots themselves are kept.</summary>
    public void RestoreSnapshot(Guid id)
    {
        if (!Document.SnapshotStates.TryGetValue(id, out var snap))
        {
            throw new InvalidOperationException("The snapshot no longer exists.");
        }

        var info = State.Snapshots.First(s => s.Id == id);
        Execute(new ProjectEdit("snapshot.restore", $"Restored snapshot \"{info.Name}\"", s => snap with { Snapshots = s.Snapshots, Metadata = s.Metadata }));
    }

    public void DeleteSnapshot(Guid id)
    {
        var info = State.Snapshots.FirstOrDefault(s => s.Id == id);
        if (info is null)
        {
            return;
        }

        Execute(new ProjectEdit("snapshot.delete", $"Deleted snapshot \"{info.Name}\"", s => s with { Snapshots = s.Snapshots.RemoveAll(x => x.Id == id) }));
    }

    /// <summary>Creates a new scenario as a copy of the active one and makes it active. Raw data is shared, not copied.</summary>
    public AnalysisScenario CreateScenario(string name, string? description = null)
    {
        var copy = State.ActiveScenario with { Id = Guid.NewGuid(), Name = name, Description = description, CreatedAt = _clock.Now };
        Execute(new ProjectEdit("scenario.create", $"Created scenario \"{name}\" from \"{State.ActiveScenario.Name}\"",
            s => s with { Scenarios = s.Scenarios.Add(copy), ActiveScenarioId = copy.Id }));
        return copy;
    }

    public void SwitchScenario(Guid id)
    {
        var target = State.Scenarios.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Unknown scenario.");
        Execute(new ProjectEdit("scenario.switch", $"Switched to scenario \"{target.Name}\"", s => s with { ActiveScenarioId = id }));
    }

    public void RenameScenario(Guid id, string name)
    {
        Execute(new ProjectEdit("scenario.rename", $"Renamed scenario to \"{name}\"",
            s => s with { Scenarios = s.Scenarios.Select(x => x.Id == id ? x with { Name = name } : x).ToImmutableList() }));
    }

    public void DeleteScenario(Guid id)
    {
        if (State.Scenarios.Count <= 1)
        {
            throw new InvalidOperationException("A project needs at least one scenario.");
        }

        var target = State.Scenarios.First(s => s.Id == id);
        Execute(new ProjectEdit("scenario.delete", $"Deleted scenario \"{target.Name}\"", s =>
        {
            var remaining = s.Scenarios.RemoveAll(x => x.Id == id);
            return s with { Scenarios = remaining, ActiveScenarioId = s.ActiveScenarioId == id ? remaining[0].Id : s.ActiveScenarioId };
        }));
    }

    private void EnsureWritable()
    {
        if (ReadOnly)
        {
            throw new InvalidOperationException("The project is open in read-only mode.");
        }
    }

    private void AppendHistory(ProjectEdit edit)
    {
        Document.History = Document.History.Add(Entry(edit));
        _historyDirty = true;
    }

    private HistoryEntry Entry(ProjectEdit edit) => new()
    {
        Timestamp = _clock.Now, Operation = edit.Operation, Description = edit.Description, Parameters = edit.Parameters,
        User = _app.UserName, AppVersion = _app.Version, Scenario = State.ActiveScenario.Name,
    };

    private void Raise(ProjectState previous, ChangeOrigin origin, string description)
    {
        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, State, origin, description));
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
