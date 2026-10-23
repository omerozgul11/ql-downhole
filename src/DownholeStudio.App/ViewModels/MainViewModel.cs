using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.App.Services;
using DownholeStudio.Core;
using DownholeStudio.Core.Import;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;
using DownholeStudio.Persistence;
using Microsoft.Extensions.Logging;

namespace DownholeStudio.App.ViewModels;

public sealed record RecentItem(string Path, string Name, string When, bool Exists);

/// <summary>Application shell: start screen, open project, licence state, overlays and global commands.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MainViewModel> _log;
    private DispatcherTimer? _autoSave;
    private DispatcherTimer? _licenseTimer;

    public MainViewModel(IServiceProvider services, UiService ui, SettingsStore settings, AppCatalog catalog, RecoveryStore recovery, LicenseManager license,
        UpdateService updates, AppPaths paths, AppConfiguration config, IClock clock, AppIdentity identity, ImportService import, ILogger<MainViewModel> log)
    {
        _services = services;
        _log = log;
        Ui = ui;
        Settings = settings;
        Catalog = catalog;
        Recovery = recovery;
        Paths = paths;
        Config = config;
        Clock = clock;
        Identity = identity;
        ImportService = import;
        Updates = updates;
        LicenseManager = license;
        License = new LicenseViewModel(this, license);
        Palette = new CommandPaletteViewModel(this);
        Help = new HelpViewModel();
        SettingsPage = new SettingsViewModel(this);
        _isExpert = settings.Current.Mode == UserMode.Expert;
        license.StatusChanged += (_, _) => Dispatcher.UIThread.Post(OnLicenseChanged);
        RefreshRecent();
        OnLicenseChanged();
    }

    public UiService Ui { get; }
    public SettingsStore Settings { get; }
    public AppCatalog Catalog { get; }
    public RecoveryStore Recovery { get; }
    public AppPaths Paths { get; }
    public AppConfiguration Config { get; }
    public IClock Clock { get; }
    public AppIdentity Identity { get; }
    public ImportService ImportService { get; }
    public UpdateService Updates { get; }
    public LicenseManager LicenseManager { get; }
    public LicenseViewModel License { get; }
    public CommandPaletteViewModel Palette { get; }
    public HelpViewModel Help { get; }
    public SettingsViewModel SettingsPage { get; }
    public string Version => AppHost.Version;
    public string VersionLabel => $"Version {AppHost.Version}";

    public ObservableCollection<RecentItem> Recent { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(Title))]
    private WorkspaceViewModel? _workspace;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isHelpOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel))]
    private bool _isExpert;

    [ObservableProperty]
    private string? _updateMessage;

    [ObservableProperty]
    private UpdateManifest? _availableUpdate;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    public bool HasProject => Workspace is not null;

    public string Title => Workspace is null ? "Downhole Studio" : $"{Workspace.ProjectName}{(Workspace.IsDirty ? " •" : "")} — Downhole Studio";

    public string ModeLabel => IsExpert ? "Expert" : "Guided";

    public bool CanEdit => LicenseManager.Status.CanEdit;

    partial void OnIsExpertChanged(bool value)
    {
        Settings.Update(s => s with { Mode = value ? UserMode.Expert : UserMode.Guided });
        Workspace?.OnModeChanged();
    }

    partial void OnWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        oldValue?.Dispose();
        if (newValue is not null)
        {
            newValue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(WorkspaceViewModel.IsDirty) or nameof(WorkspaceViewModel.ProjectName))
                {
                    OnPropertyChanged(nameof(Title));
                }
            };
            newValue.Session.ReadOnly = !CanEdit;
        }

        RefreshRecent();
        OnPropertyChanged(nameof(Title));
    }

    private void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        License.Refresh();
        if (Workspace is not null)
        {
            Workspace.Session.ReadOnly = !CanEdit;
            Workspace.OnLicenseChanged();
        }
    }

    // ------------------------------------------------------------------ start-up

    /// <summary>Called once the main window is visible.</summary>
    public async Task StartupAsync(string? fileToOpen)
    {
        _autoSave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.Current.AutoSaveMinutes)) };
        _autoSave.Tick += (_, _) => AutoSave();
        _autoSave.Start();
        _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _licenseTimer.Tick += async (_, _) => await RefreshLicenseQuietlyAsync();
        _licenseTimer.Start();

        foreach (var orphan in Recovery.FindOrphans().Take(3))
        {
            var answer = await Ui.AskAsync("Downhole Studio recovered an unsaved project.",
                $"\"{orphan.ProjectName}\" was not saved when Downhole Studio last closed (auto-saved {orphan.SavedAt.ToLocalTime():g}). Recover it now? Your original file{(orphan.OriginalPath is null ? "" : $" ({orphan.OriginalPath})")} has not been changed.",
                ["Recover", "Discard", "Later"]);
            if (answer == "Recover")
            {
                await OpenRecoveredAsync(orphan);
                break;
            }

            if (answer == "Discard")
            {
                Recovery.Discard(orphan.SessionId);
            }
        }

        if (fileToOpen is not null && File.Exists(fileToOpen) && Workspace is null)
        {
            await OpenPathAsync(fileToOpen);
        }

        _ = RefreshLicenseQuietlyAsync();
        if (Updates.Online && Settings.Current.Updates != UpdateMode.Manual &&
            (Settings.Current.LastUpdateCheck is null || Clock.Now - Settings.Current.LastUpdateCheck > TimeSpan.FromDays(1)))
        {
            _ = CheckUpdatesAsync(quiet: true);
        }
    }

    private async Task RefreshLicenseQuietlyAsync()
    {
        var status = LicenseManager.Status;
        if (status.State is LicenseState.NotActivated or LicenseState.LicenseRevoked)
        {
            return;
        }

        if (status.LastVerifiedAt is { } last && Clock.Now - last < TimeSpan.FromHours(20) && status.State is LicenseState.Licensed or LicenseState.TrialActive)
        {
            return;
        }

        await LicenseManager.RefreshAsync();
    }

    private void AutoSave()
    {
        if (Workspace is { } ws && ws.Session.IsDirty && !ws.Session.ReadOnly)
        {
            try
            {
                Recovery.Save(ws.Session.SessionId, ws.Session.Document, ws.Session.FilePath, Version, Clock.Now);
                ws.LastAutoSave = Clock.Now;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFileException)
            {
                _log.LogWarning("Auto-save failed: {Error}", ex.Message);
            }
        }
    }

    /// <summary>Saves a recovery copy immediately (crash handler).</summary>
    public void EmergencySave()
    {
        try
        {
            if (Workspace is { } ws && ws.Session.IsDirty)
            {
                Recovery.Save(ws.Session.SessionId, ws.Session.Document, ws.Session.FilePath, Version, Clock.Now);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Emergency save failed");
        }
    }

    // ------------------------------------------------------------------ project lifecycle

    public void RefreshRecent()
    {
        Recent.Clear();
        foreach (var r in Catalog.RecentProjects())
        {
            Recent.Add(new RecentItem(r.Path, r.Name, r.LastOpened.ToLocalTime().ToString("g"), File.Exists(r.Path)));
        }
    }

    private bool RequireEdit(string action)
    {
        if (CanEdit)
        {
            return true;
        }

        Ui.Notify($"{action} needs an activated license. {LicenseManager.Status.Message}", "warning");
        License.IsOpen = true;
        return false;
    }

    /// <summary>A new, unsaved project with the user's default pickers (Settings → Picking defaults).</summary>
    private ProjectSession NewSession()
    {
        var s = Settings.Current;
        return ProjectSession.CreateNew("Untitled survey", Clock, Identity, s.Units, sc => sc with
        {
            PPicker = sc.PPicker with { Algorithm = s.DefaultPPicker }, SPicker = sc.SPicker with { Algorithm = s.DefaultSPicker },
        });
    }

    /// <summary>Applies a changed recovery-copy interval to the running timer.</summary>
    public void UpdateAutoSaveInterval()
    {
        if (_autoSave is not null)
        {
            _autoSave.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.Current.AutoSaveMinutes));
        }
    }

    [RelayCommand]
    public async Task NewProjectAsync()
    {
        if (!RequireEdit("Creating a project") || !await CloseProjectAsync())
        {
            return;
        }

        var session = NewSession();
        Workspace = CreateWorkspace(session);
        Workspace.Navigate(WorkflowStep.Project);
    }

    [RelayCommand]
    public async Task OpenProjectAsync()
    {
        var files = await Ui.OpenFilesAsync("Open project", false, [UiService.ProjectFilter], Settings.Current.LastFolder ?? Paths.DefaultProjectFolder);
        if (files.Count > 0)
        {
            await OpenPathAsync(files[0]);
        }
    }

    [RelayCommand]
    public async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            var answer = await Ui.AskAsync("Project not found", $"\"{path}\" no longer exists or has been moved.", ["Remove from list", "Keep"]);
            if (answer == "Remove from list")
            {
                Catalog.RemoveRecent(path);
                RefreshRecent();
            }

            return;
        }

        await OpenPathAsync(path);
    }

    public async Task OpenPathAsync(string path)
    {
        if (!await CloseProjectAsync())
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "Opening project…";
        try
        {
            var session = await Task.Run(() => ProjectSession.Open(path, Clock, Identity));
            Workspace = CreateWorkspace(session);
            Catalog.TouchRecent(path, session.State.Metadata.Name, Clock.Now);
            Settings.Update(s => s with { LastFolder = Path.GetDirectoryName(path) });
            RefreshRecent();
            if (!CanEdit)
            {
                Ui.Notify("The project is open in read-only mode. " + LicenseManager.Status.Message, "warning");
            }
        }
        catch (ProjectFileException ex)
        {
            await Ui.ErrorAsync("The project could not be opened", ex.Message, ex.TechnicalDetails);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.ErrorAsync("The project could not be opened", $"\"{path}\" could not be read. Check that the file exists and is not open in another program.", ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    private async Task OpenRecoveredAsync(RecoveryInfo info)
    {
        try
        {
            var doc = ProjectPackage.Load(info.PackagePath);
            var session = new ProjectSession(doc, null, Clock, Identity);
            session.Record("project.recover", $"Recovered unsaved changes from {info.SavedAt:u}" + (info.OriginalPath is null ? "" : $" (original file {info.OriginalPath})"));
            Workspace = CreateWorkspace(session);
            Workspace.RecoveredFrom = info.OriginalPath;
            Recovery.Discard(info.SessionId);
            Ui.Notify("Recovered project opened. Use Save As to keep it.", "success");
        }
        catch (ProjectFileException ex)
        {
            await Ui.ErrorAsync("The recovery copy could not be opened", ex.Message, ex.TechnicalDetails);
        }
    }

    private WorkspaceViewModel CreateWorkspace(ProjectSession session) => new(this, session);

    /// <summary>Asks about unsaved changes; returns false when the user cancels.</summary>
    public async Task<bool> CloseProjectAsync()
    {
        if (Workspace is not { } ws)
        {
            return true;
        }

        if (ws.Session.IsDirty && !ws.Session.ReadOnly)
        {
            var answer = await Ui.AskAsync("Save changes?", $"\"{ws.ProjectName}\" has unsaved changes.", ["Save", "Don't save", "Cancel"]);
            if (answer is null or "Cancel")
            {
                return false;
            }

            if (answer == "Save" && !await ws.SaveAsync())
            {
                return false;
            }
        }

        Recovery.Discard(ws.Session.SessionId);
        Workspace = null;
        return true;
    }

    [RelayCommand]
    private async Task CloseProject() => await CloseProjectAsync();

    [RelayCommand]
    public async Task ImportSurveyAsync()
    {
        if (!RequireEdit("Importing data"))
        {
            return;
        }

        if (Workspace is null)
        {
            var session = NewSession();
            Workspace = CreateWorkspace(session);
        }

        Workspace.Navigate(WorkflowStep.Import);
        await Workspace.ImportPage.AddFilesAsync();
    }

    /// <summary>Copies the bundled sample project to the user's documents and opens the copy.</summary>
    [RelayCommand]
    public async Task OpenSampleAsync()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Samples", "Downhole Studio Sample.dhsx");
        if (!File.Exists(bundled))
        {
            await Ui.ErrorAsync("Sample project not found", "The sample project is not installed. Reinstall Downhole Studio to restore it.", bundled);
            return;
        }

        var target = Path.Combine(Paths.DefaultProjectFolder, "Downhole Studio Sample.dhsx");
        if (!File.Exists(target))
        {
            File.Copy(bundled, target);
        }

        await OpenPathAsync(target);
    }

    // ------------------------------------------------------------------ global commands

    [RelayCommand]
    private async Task Save()
    {
        if (Workspace is { } ws)
        {
            await ws.SaveAsync();
        }
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (Workspace is { } ws)
        {
            await ws.SaveAsAsync();
        }
    }

    [RelayCommand]
    private void Undo() => Workspace?.Undo();

    [RelayCommand]
    private void Redo() => Workspace?.Redo();

    [RelayCommand]
    private void OpenSettings(string? section)
    {
        SettingsPage.Select(section);
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ToggleHelp()
    {
        Help.ShowFor(Workspace?.CurrentPage?.HelpTopic ?? "start");
        IsHelpOpen = !IsHelpOpen;
    }

    [RelayCommand]
    private void ShowHelp(string topic)
    {
        Help.ShowFor(topic);
        IsHelpOpen = true;
    }

    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    [RelayCommand]
    private void ToggleMode() => IsExpert = !IsExpert;

    [RelayCommand]
    private void GenerateReport()
    {
        if (Workspace is { } ws)
        {
            ws.Navigate(WorkflowStep.Report);
            if (CanEdit)
            {
                ws.ReportPage.GenerateCommand.Execute(null);
            }
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (Workspace is { } ws)
        {
            ws.Navigate(WorkflowStep.Report);
        }
    }

    [RelayCommand]
    private void Import()
    {
        if (Workspace is { } ws)
        {
            ws.Navigate(WorkflowStep.Import);
        }
        else
        {
            ImportSurveyCommand.Execute(null);
        }
    }

    public void ApplyTheme(ThemePreference theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var current = Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? ThemePreference.Light : ThemePreference.Dark;
        Settings.Update(s => s with { Theme = current });
        ApplyTheme(current);
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync(bool quiet = false)
    {
        var result = await Updates.CheckAsync(Settings.Current.Channel);
        Settings.Update(s => s with { LastUpdateCheck = Clock.Now });
        UpdateMessage = result.Message;
        AvailableUpdate = result.Available;
        if (!quiet)
        {
            Ui.Notify(result.Message, result.Failed ? "warning" : "info");
        }

        if (result.Available is { } manifest && Settings.Current.Updates == UpdateMode.Automatic && !manifest.IsMajor)
        {
            await InstallUpdateAsync();
        }
    }

    [RelayCommand]
    public async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } manifest)
        {
            return;
        }

        var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? "" : "\n\nWhat's new:\n" + manifest.ReleaseNotes;
        var answer = await Ui.AskAsync($"Install Downhole Studio {manifest.Version}?",
            (manifest.IsMajor ? "This is a major upgrade. Please read the release notes before continuing." : "The update will download, be verified and then installed.") +
            " Your projects are not changed by updates." + notes, ["Download and install", "Not now"]);
        if (answer != "Download and install" || !await CloseProjectAsync())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => BusyMessage = $"Downloading update… {p:P0}");
            var installer = await Updates.DownloadAsync(manifest, progress);
            UpdateService.LaunchInstaller(installer);
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            await Ui.ErrorAsync("The update could not be installed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        var path = await Ui.SaveFileAsync("Export diagnostic package", $"DownholeStudio-diagnostics-{Clock.Now:yyyyMMdd-HHmm}.zip", UiService.ZipFilter);
        if (path is null)
        {
            return;
        }

        List<string> projects = [];
        if (Workspace?.Session.FilePath is { } current)
        {
            var include = await Ui.AskAsync("Include the project file?",
                "The diagnostic package contains logs, version and basic system information. Survey data is not included unless you choose to include the current project file.",
                ["Don't include project", "Include project file"]);
            if (include == "Include project file")
            {
                projects.Add(current);
            }
        }

        DiagnosticPackage.Create(path, Paths, Version, LicenseManager.DescribeForSupport(), projects);
        Ui.Notify($"Diagnostic package saved to {path}.", "success");
    }

    [RelayCommand]
    private void OpenUrl(string url) => Ui.OpenUrl(url);

    public void Shutdown()
    {
        _autoSave?.Stop();
        _licenseTimer?.Stop();
        if (Workspace is { } ws && !ws.Session.IsDirty)
        {
            Recovery.Discard(ws.Session.SessionId);
        }
    }

    public T Service<T>() where T : notnull => (T)_services.GetService(typeof(T))!;
}
