using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.Analysis;
using DownholeStudio.App.Services;
using DownholeStudio.App.ViewModels.Pages;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Persistence;

namespace DownholeStudio.App.ViewModels;

/// <summary>One workflow step in the strip at the top of the workspace.</summary>
public sealed partial class WorkflowStepViewModel(WorkspaceViewModel owner, WorkflowStep step) : ObservableObject
{
    public WorkflowStep Step { get; } = step;

    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(NeedsAttention), nameof(IsPending), nameof(StatusText))]
    private StepStatus _status;

    [ObservableProperty]
    private string _hint = "";

    [ObservableProperty]
    private bool _isCurrent;

    public bool IsDone => Status == StepStatus.Complete;
    public bool NeedsAttention => Status == StepStatus.NeedsAttention;
    public bool IsPending => Status is StepStatus.NotStarted or StepStatus.InProgress;

    public string StatusText => Status switch
    {
        StepStatus.Complete => "Done",
        StepStatus.NeedsAttention => "Needs attention",
        StepStatus.InProgress => "In progress",
        _ => "Not started",
    };

    [RelayCommand]
    private void Go() => owner.Navigate(Step);
}

/// <summary>An open project: session, analysis, pages and the shared selection.</summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _refreshTimer;
    private bool _stateDirty;

    public WorkspaceViewModel(MainViewModel main, ProjectSession session)
    {
        Main = main;
        Session = session;
        Analysis = new AnalysisService(session, new ProcessedTraceCache());
        Analysis.Updated += (_, r) => Dispatcher.UIThread.Post(() => SetResult(r));
        session.StateChanged += OnSessionStateChanged;
        session.SaveStateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateSaveState);

        ProjectPage = new ProjectPageViewModel(this);
        ImportPage = new ImportPageViewModel(this);
        GeometryPage = new GeometryPageViewModel(this);
        WaveformsPage = new WaveformsPageViewModel(this);
        ProcessingPage = new ProcessingPageViewModel(this);
        PPicksPage = new PickingPageViewModel(this, WaveType.P);
        SPicksPage = new SPickingPageViewModel(this);
        VelocityPage = new VelocityPageViewModel(this);
        EngineeringPage = new EngineeringPageViewModel(this);
        ResultsPage = new ResultsPageViewModel(this);
        ReportPage = new ReportPageViewModel(this);
        Pages = [ProjectPage, ImportPage, GeometryPage, WaveformsPage, ProcessingPage, PPicksPage, SPicksPage, VelocityPage, EngineeringPage, ResultsPage, ReportPage];
        foreach (var step in Enum.GetValues<WorkflowStep>())
        {
            Steps.Add(new WorkflowStepViewModel(this, step));
        }

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (_stateDirty)
            {
                _stateDirty = false;
                foreach (var p in Pages)
                {
                    p.OnStateChanged();
                }
            }
        };

        SetResult(Analysis.CalculateNow());
        CurrentPage = Session.State.Survey.Records.Count == 0 ? ImportPage : WaveformsPage;
        if (Session.FilePath is null && Session.State.Survey.Records.Count == 0)
        {
            CurrentPage = ProjectPage;
        }

        UpdateSaveState();
    }

    public MainViewModel Main { get; }
    public ProjectSession Session { get; }
    public AnalysisService Analysis { get; }
    public UiService Ui => Main.Ui;

    public ProjectPageViewModel ProjectPage { get; }
    public ImportPageViewModel ImportPage { get; }
    public GeometryPageViewModel GeometryPage { get; }
    public WaveformsPageViewModel WaveformsPage { get; }
    public ProcessingPageViewModel ProcessingPage { get; }
    public PickingPageViewModel PPicksPage { get; }
    public SPickingPageViewModel SPicksPage { get; }
    public VelocityPageViewModel VelocityPage { get; }
    public EngineeringPageViewModel EngineeringPage { get; }
    public ResultsPageViewModel ResultsPage { get; }
    public ReportPageViewModel ReportPage { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ObservableCollection<WorkflowStepViewModel> Steps { get; } = [];

    public ProjectState State => Session.State;

    public UnitPreferences Units => UnitPreferences.For(State.Metadata.DisplayUnits);

    public bool IsExpert => Main.IsExpert;

    public bool CanEdit => Main.CanEdit;

    [ObservableProperty]
    private AnalysisResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Inspector), nameof(ShowInspector))]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private Guid? _selectedStationId;

    [ObservableProperty]
    private Guid? _selectedLayerId;

    [ObservableProperty]
    private Guid? _selectedRecordId;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _saveStatus = "";

    [ObservableProperty]
    private DateTimeOffset? _lastAutoSave;

    [ObservableProperty]
    private string? _recoveredFrom;

    [ObservableProperty]
    private bool _reportGenerated;

    [ObservableProperty]
    private string _undoTip = "Undo";

    [ObservableProperty]
    private string _redoTip = "Redo";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _isInspectorOpen = true;

    /// <summary>The inspector is shown on pages that use it, unless the user hid it.</summary>
    public bool ShowInspector => IsInspectorOpen && CurrentPage?.HasInspector == true;

    public string ProjectName => State.Metadata.Name;

    public object? Inspector => CurrentPage?.Inspector;

    public event EventHandler? SelectionChanged;

    partial void OnCurrentPageChanged(PageViewModel? value)
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = value is not null && PageFor(s.Step) == value;
        }

        value?.OnActivated();
    }

    partial void OnSelectedStationIdChanged(Guid? value) => RaiseSelection();

    partial void OnSelectedLayerIdChanged(Guid? value) => RaiseSelection();

    partial void OnSelectedRecordIdChanged(Guid? value) => RaiseSelection();

    private void RaiseSelection()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        foreach (var p in Pages)
        {
            p.OnSelectionChanged();
        }

        OnPropertyChanged(nameof(Inspector));
    }

    public void NotifyInspectorChanged() => OnPropertyChanged(nameof(Inspector));

    private void OnSessionStateChanged(object? sender, StateChangedEventArgs e)
    {
        void Apply()
        {
            _stateDirty = true;
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }

            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(Units));
            UpdateSaveState();
            _ = Analysis.RequestAsync(e.Origin == ChangeOrigin.Gesture ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMilliseconds(80));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            Dispatcher.UIThread.Post(Apply);
        }
    }

    private void SetResult(AnalysisResult result)
    {
        Result = result;
        foreach (var p in Pages)
        {
            p.OnResultChanged();
        }

        UpdateWorkflow();
    }

    private void UpdateWorkflow()
    {
        var states = WorkflowEvaluator.Evaluate(State, Result, Session.FilePath is not null && !Session.IsDirty, ReportGenerated);
        foreach (var s in states)
        {
            var vm = Steps.First(x => x.Step == s.Step);
            vm.Number = s.Number;
            vm.Title = s.Title;
            vm.Status = s.Status;
            vm.Hint = s.Hint;
        }

        foreach (var p in Pages)
        {
            var relevant = states.Where(s => PageFor(s.Step) == p).ToList();
            p.Status = relevant.Count == 0 ? StepStatus.NotStarted
                : relevant.Any(s => s.Status == StepStatus.NeedsAttention) ? StepStatus.NeedsAttention
                : relevant.All(s => s.Status == StepStatus.Complete) ? StepStatus.Complete
                : relevant.Any(s => s.Status != StepStatus.NotStarted) ? StepStatus.InProgress : StepStatus.NotStarted;
        }

        // The picking step points at one page at a time; the other picking page shows its own progress.
        var pickPage = PageFor(WorkflowStep.Pick);
        foreach (var (page, picks) in new[] { (PPicksPage as PageViewModel, State.ActiveScenario.PPicks), (SPicksPage, State.ActiveScenario.SPicks) })
        {
            if (page != pickPage)
            {
                var stations = State.Survey.Stations.Where(st => !st.Excluded).ToList();
                var picked = stations.Count(st => picks.TryGetValue(st.Id, out var pick) && pick.HasTime);
                page.Status = stations.Count > 0 && picked == stations.Count ? StepStatus.Complete : picked > 0 ? StepStatus.InProgress : StepStatus.NotStarted;
            }
        }
    }

    private void UpdateSaveState()
    {
        IsDirty = Session.IsDirty;
        SaveStatus = Session.ReadOnly ? "Read-only"
            : Session.FilePath is null ? "Not saved yet"
            : IsDirty ? (LastAutoSave is { } a ? $"Unsaved changes · recovery copy {a.ToLocalTime():t}" : "Unsaved changes")
            : "All changes saved";
        UndoTip = Session.UndoDescription is { } u ? $"Undo: {u} (Ctrl+Z)" : "Nothing to undo";
        RedoTip = Session.RedoDescription is { } r ? $"Redo: {r} (Ctrl+Y)" : "Nothing to redo";
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        UpdateWorkflow();
    }

    partial void OnLastAutoSaveChanged(DateTimeOffset? value) => UpdateSaveState();

    public PageViewModel PageFor(WorkflowStep step) => step switch
    {
        WorkflowStep.Project => ProjectPage,
        WorkflowStep.Import => ImportPage,
        WorkflowStep.Geometry => GeometryPage,
        WorkflowStep.Inspect => WaveformsPage,
        WorkflowStep.Preprocess => ProcessingPage,
        WorkflowStep.Pick => State.ActiveScenario.PPicks.Count >= State.Survey.Stations.Count && State.Survey.Stations.Count > 0 ? SPicksPage : PPicksPage,
        WorkflowStep.QualityControl => ResultsPage,
        WorkflowStep.VelocityModel => VelocityPage,
        WorkflowStep.Layers => VelocityPage,
        WorkflowStep.Engineering => EngineeringPage,
        WorkflowStep.Review => ResultsPage,
        _ => ReportPage,
    };

    public void Navigate(WorkflowStep step) => CurrentPage = PageFor(step);

    public void Navigate(PageViewModel page) => CurrentPage = page;

    /// <summary>Executes an edit unless the project is read-only (licence), reporting why.</summary>
    public bool Execute(ProjectEdit edit)
    {
        if (Session.ReadOnly)
        {
            Ui.Notify("This project is read-only. " + Main.LicenseManager.Status.Message, "warning");
            return false;
        }

        Session.Execute(edit);
        return true;
    }

    public bool CanModify()
    {
        if (!Session.ReadOnly)
        {
            return true;
        }

        Ui.Notify("This project is read-only. " + Main.LicenseManager.Status.Message, "warning");
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        Session.Undo();
        Ui.Notify(Session.RedoDescription is { } d ? $"Undone: {d}" : "Undone");
    }

    private bool CanUndo() => Session.CanUndo && !Session.ReadOnly;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        Session.Redo();
        Ui.Notify(Session.UndoDescription is { } d ? $"Redone: {d}" : "Redone");
    }

    private bool CanRedo() => Session.CanRedo && !Session.ReadOnly;

    public async Task<bool> SaveAsync()
    {
        if (Session.ReadOnly)
        {
            Ui.Notify("Saving needs an activated license. The project file has not been changed.", "warning");
            return false;
        }

        if (Session.FilePath is null)
        {
            return await SaveAsAsync();
        }

        return await DoSaveAsync(null);
    }

    public async Task<bool> SaveAsAsync()
    {
        if (Session.ReadOnly)
        {
            Ui.Notify("Saving needs an activated license.", "warning");
            return false;
        }

        var name = string.Concat(ProjectName.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = await Ui.SaveFileAsync("Save project as", (name.Length == 0 ? "Survey" : name) + ".dhsx", UiService.ProjectFilter,
            Main.Settings.Current.LastFolder ?? Main.Paths.DefaultProjectFolder);
        return path is not null && await DoSaveAsync(path);
    }

    private async Task<bool> DoSaveAsync(string? path)
    {
        try
        {
            Main.IsBusy = true;
            Main.BusyMessage = "Saving…";
            await Task.Run(() => Session.Save(path));
            Main.Catalog.TouchRecent(Session.FilePath!, ProjectName, Main.Clock.Now);
            Main.Settings.Update(s => s with { LastFolder = Path.GetDirectoryName(Session.FilePath) });
            Main.Recovery.Discard(Session.SessionId);
            RecoveredFrom = null;
            UpdateSaveState();
            Ui.Notify($"Saved {Path.GetFileName(Session.FilePath)}.", "success");
            return true;
        }
        catch (ProjectFileException ex)
        {
            await Ui.ErrorAsync("The project could not be saved", ex.Message, ex.TechnicalDetails);
            return false;
        }
        finally
        {
            Main.IsBusy = false;
            Main.BusyMessage = null;
        }
    }

    public void OnModeChanged()
    {
        OnPropertyChanged(nameof(IsExpert));
        foreach (var p in Pages)
        {
            p.OnModeChanged();
        }
    }

    public void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        UpdateSaveState();
    }

    public void MarkReportGenerated()
    {
        ReportGenerated = true;
        UpdateWorkflow();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        Session.StateChanged -= OnSessionStateChanged;
    }
}
