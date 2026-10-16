using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.App.Services;
using DownholeStudio.Domain.Units;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;

namespace DownholeStudio.App.ViewModels;

public static class ClipboardImage
{
    public static async Task CopyPngAsync(UiService ui, byte[] png)
    {
        if (ui.Clipboard is not { } clipboard)
        {
            return;
        }

        using var bitmap = new Bitmap(new MemoryStream(png));
        await clipboard.SetBitmapAsync(bitmap);
        ui.Notify("Figure copied to the clipboard.");
    }
}

/// <summary>Licence screens: welcome, trial, activation, status and management.</summary>
public sealed partial class LicenseViewModel(MainViewModel main, LicenseManager manager) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private bool _showActivate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _shareMachineName = true;
    [ObservableProperty] private IReadOnlyList<ActivatedDeviceInfo>? _devices;

    public LicenseStatus Status => manager.Status;
    public string Title => Status.Title;
    public string Message => Status.Message;
    public bool IsTrial => Status.IsTrial;
    public bool IsLicensed => Status.State is LicenseState.Licensed or LicenseState.LicenseOfflineGrace;
    public bool NeedsActivation => Status.State is LicenseState.NotActivated or LicenseState.TrialExpired or LicenseState.LicenseExpired or LicenseState.LicenseRevoked or LicenseState.ActivationLimitReached;
    public bool NeedsNetwork => Status.State == LicenseState.NetworkValidationRequired;
    public bool CanStartTrial => Status.State == LicenseState.NotActivated;
    public bool ShowActivateLink => NeedsActivation && !CanStartTrial && !ShowActivate;
    public bool ShowTrialBanner => Status.State is LicenseState.TrialActive or LicenseState.TrialExpiring;
    public bool TrialUrgent => Status.State == LicenseState.TrialExpiring;
    public bool ShowStateBanner => Status.State is LicenseState.TrialExpired or LicenseState.LicenseExpired or LicenseState.LicenseRevoked or LicenseState.NetworkValidationRequired or LicenseState.LicenseOfflineGrace;
    public bool StateBannerIsWarning => Status.State == LicenseState.LicenseOfflineGrace;
    public string TrialBanner => $"Downhole Studio {Status.Claims?.Edition ?? "Professional"} Trial · {Status.DaysRemaining} day{(Status.DaysRemaining == 1 ? "" : "s")} remaining";

    public string Chip => Status.State switch
    {
        LicenseState.Licensed => "Licensed",
        LicenseState.LicenseOfflineGrace => "Licensed · offline",
        LicenseState.TrialActive or LicenseState.TrialExpiring => $"Trial · {Status.DaysRemaining} d",
        LicenseState.TrialExpired => "Trial ended",
        LicenseState.LicenseExpired => "License expired",
        LicenseState.LicenseRevoked => "License revoked",
        LicenseState.NetworkValidationRequired => "Verify license",
        _ => "Not activated",
    };

    public string ChipTone => Status.State switch
    {
        LicenseState.Licensed => "good",
        LicenseState.TrialActive => "info",
        LicenseState.TrialExpiring or LicenseState.LicenseOfflineGrace => "warn",
        _ => "bad",
    };

    public string Product => $"Downhole Studio {Status.Claims?.Edition ?? "Professional"}";
    public string LicensedTo => Status.Claims?.LicensedTo ?? Status.Claims?.Email ?? "—";
    public string Device => manager.MachineName;
    public string KeyMasked => LicenseKeyFormat.Masked(Status.Claims?.KeyHint);
    public string LastValidation => Status.LastVerifiedAt is { } v ? $"{v.ToLocalTime():MMMM d, yyyy HH:mm} ({LicenseManager.Ago(v, DateTimeOffset.UtcNow)})" : "—";
    public string Expires => Status.Claims?.IsTrial == true ? (Status.Claims.TrialEnd?.ToLocalTime().ToString("MMMM d, yyyy") ?? "—")
        : Status.Claims?.ExpiresAt is { } e ? e.ToLocalTime().ToString("MMMM d, yyyy") : Status.Claims is null ? "—" : "Perpetual";
    public string Maintenance => Status.Claims?.MaintenanceUntil?.ToLocalTime().ToString("MMMM d, yyyy") ?? "—";
    public string OfflineUntil => Status.Claims?.OfflineValidUntil.ToLocalTime().ToString("MMMM d, yyyy") ?? "—";
    public bool HasDevices => Devices is { Count: > 0 };

    public void Refresh()
    {
        foreach (var p in new[]
                 {
                     nameof(Status), nameof(Title), nameof(Message), nameof(IsTrial), nameof(IsLicensed), nameof(NeedsActivation), nameof(NeedsNetwork), nameof(CanStartTrial), nameof(ShowActivateLink),
                     nameof(ShowTrialBanner), nameof(TrialUrgent), nameof(ShowStateBanner), nameof(StateBannerIsWarning), nameof(TrialBanner), nameof(Chip), nameof(ChipTone), nameof(Product),
                     nameof(LicensedTo), nameof(KeyMasked), nameof(LastValidation), nameof(Expires), nameof(Maintenance), nameof(OfflineUntil),
                 })
        {
            OnPropertyChanged(p);
        }

        if (Status.State == LicenseState.NotActivated && !main.HasProject)
        {
            IsOpen = true;
        }
    }

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        Error = null;
    }

    [RelayCommand]
    private void ShowActivateForm()
    {
        ShowActivate = true;
        OnPropertyChanged(nameof(ShowActivateLink));
    }

    [RelayCommand]
    private async Task StartTrial()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var r = await manager.StartTrialAsync(string.IsNullOrWhiteSpace(Email) ? null : Email, ShareMachineName);
            if (r.Success)
            {
                IsOpen = false;
                main.Ui.Notify("Your 14-day trial has started. Enjoy Downhole Studio.", "success");
            }
            else
            {
                Error = r.Message;
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Activate()
    {
        IsBusy = true;
        Error = null;
        Devices = null;
        try
        {
            var r = await manager.ActivateAsync(LicenseKey, Email, ShareMachineName);
            if (r.Success)
            {
                IsOpen = false;
                ShowActivate = false;
                LicenseKey = "";
                main.Ui.Notify("Downhole Studio is activated on this computer.", "success");
            }
            else
            {
                Error = r.Message;
                Devices = r.Devices;
                OnPropertyChanged(nameof(HasDevices));
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task RefreshOnline()
    {
        IsBusy = true;
        try
        {
            var r = await manager.RefreshAsync();
            main.Ui.Notify(r.Success ? "License verified." : r.Message, r.Success ? "success" : "warning");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Deactivate()
    {
        var answer = await main.Ui.AskAsync("Deactivate this computer?",
            "The license seat is released so you can activate Downhole Studio on another computer. Projects on this computer stay intact and can still be opened read-only.",
            ["Deactivate", "Cancel"], kind: "warning");
        if (answer != "Deactivate")
        {
            return;
        }

        IsBusy = true;
        try
        {
            var r = await manager.DeactivateAsync();
            main.Ui.Notify(r.Message, r.Success ? "success" : "warning");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task CopyInfo() => await main.Ui.CopyTextAsync(manager.DescribeForSupport());

    [RelayCommand]
    private void Buy() => main.Ui.OpenUrl(main.Config.PurchaseUrl);

    [RelayCommand]
    private void Privacy() => main.Ui.OpenUrl(main.Config.PrivacyPolicyUrl);
}

/// <summary>Settings sections. Preferences are stored per user, separately from projects.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Load();
    }

    public static IReadOnlyList<string> SectionNames { get; } = ["General", "Appearance", "Units", "Processing Defaults", "Picking Defaults", "Reporting", "License", "Updates", "Advanced"];

    public IReadOnlyList<string> Sections => SectionNames;

    public LicenseViewModel License => _main.License;

    public MainViewModel Main => _main;

    [ObservableProperty] private string _section = "General";
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _expertMode;
    [ObservableProperty] private bool _usUnits;
    [ObservableProperty] private int _autoSaveMinutes = 5;
    [ObservableProperty] private string? _editorName;
    [ObservableProperty] private int _updateModeIndex;
    [ObservableProperty] private bool _betaChannel;
    [ObservableProperty] private bool _shareMachineName;
    [ObservableProperty] private int _pPickerIndex;
    [ObservableProperty] private int _sPickerIndex;
    [ObservableProperty] private bool _defaultBandPassForS;
    [ObservableProperty] private bool _a4Paper;
    [ObservableProperty] private string? _serverOverride;

    public bool IsGeneral => Section == "General";
    public bool IsAppearance => Section == "Appearance";
    public bool IsUnits => Section == "Units";
    public bool IsProcessing => Section == "Processing Defaults";
    public bool IsPicking => Section == "Picking Defaults";
    public bool IsReporting => Section == "Reporting";
    public bool IsLicense => Section == "License";
    public bool IsUpdates => Section == "Updates";
    public bool IsAdvanced => Section == "Advanced";
    public string DataFolder => _main.Paths.Root;
    public string Version => _main.Version;
    public string ServerUrl => _main.Settings.Current.LicenseServerOverride ?? _main.Config.LicenseServerUrl;
    public string? UpdateMessage => _main.UpdateMessage;

    partial void OnSectionChanged(string value)
    {
        foreach (var p in new[] { nameof(IsGeneral), nameof(IsAppearance), nameof(IsUnits), nameof(IsProcessing), nameof(IsPicking), nameof(IsReporting), nameof(IsLicense), nameof(IsUpdates), nameof(IsAdvanced) })
        {
            OnPropertyChanged(p);
        }

        if (value == "License")
        {
            License.Refresh();
        }
    }

    public void Select(string? section)
    {
        if (section is not null && SectionNames.Contains(section))
        {
            Section = section;
        }
    }

    private void Load()
    {
        _loading = true;
        var s = _main.Settings.Current;
        ThemeIndex = (int)s.Theme;
        ExpertMode = s.Mode == UserMode.Expert;
        UsUnits = s.Units == UnitSystem.USCustomary;
        AutoSaveMinutes = s.AutoSaveMinutes;
        EditorName = s.EditorName;
        UpdateModeIndex = (int)s.Updates;
        BetaChannel = s.Channel == UpdateChannel.Beta;
        ShareMachineName = s.ShareMachineName;
        PPickerIndex = (int)s.DefaultPPicker - 1;
        SPickerIndex = (int)s.DefaultSPicker - 1;
        DefaultBandPassForS = s.DefaultBandPassForS;
        A4Paper = s.PaperSize == "A4";
        ServerOverride = s.LicenseServerOverride;
        _loading = false;
    }

    private void Save(Func<UserSettings, UserSettings> change)
    {
        if (!_loading)
        {
            _main.Settings.Update(change);
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        Save(s => s with { Theme = (ThemePreference)value });
        _main.ApplyTheme((ThemePreference)value);
    }

    partial void OnExpertModeChanged(bool value)
    {
        if (!_loading)
        {
            _main.IsExpert = value;
        }
    }

    partial void OnUsUnitsChanged(bool value) => Save(s => s with { Units = value ? UnitSystem.USCustomary : UnitSystem.SI });
    partial void OnAutoSaveMinutesChanged(int value) => Save(s => s with { AutoSaveMinutes = Math.Clamp(value, 1, 60) });
    partial void OnEditorNameChanged(string? value) => Save(s => s with { EditorName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    partial void OnUpdateModeIndexChanged(int value) => Save(s => s with { Updates = (UpdateMode)value });
    partial void OnBetaChannelChanged(bool value) => Save(s => s with { Channel = value ? UpdateChannel.Beta : UpdateChannel.Stable });
    partial void OnShareMachineNameChanged(bool value) => Save(s => s with { ShareMachineName = value });
    partial void OnPPickerIndexChanged(int value) => Save(s => s with { DefaultPPicker = (Domain.PickAlgorithm)(value + 1) });
    partial void OnSPickerIndexChanged(int value) => Save(s => s with { DefaultSPicker = (Domain.PickAlgorithm)(value + 1) });
    partial void OnDefaultBandPassForSChanged(bool value) => Save(s => s with { DefaultBandPassForS = value });
    partial void OnA4PaperChanged(bool value) => Save(s => s with { PaperSize = value ? "A4" : "Letter" });
    partial void OnServerOverrideChanged(string? value) => Save(s => s with { LicenseServerOverride = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });

    [RelayCommand]
    private void SelectSection(string name) => Section = name;

    [RelayCommand]
    private void SelectTheme(string index) => ThemeIndex = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);

    [RelayCommand]
    private void OpenDataFolder() => _main.Ui.OpenUrl(_main.Paths.Root);
}

public sealed record PaletteCommand(string Title, string Category, string? Shortcut, Action Run);

/// <summary>Ctrl+K: searchable list of every command and page.</summary>
public sealed partial class CommandPaletteViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteCommand? _selected;

    public ObservableCollection<PaletteCommand> Results { get; } = [];

    partial void OnQueryChanged(string value) => Filter();

    public void Open()
    {
        Query = "";
        Filter();
        IsOpen = true;
    }

    private IEnumerable<PaletteCommand> All()
    {
        yield return new("New project", "File", "Ctrl+N", () => main.NewProjectCommand.Execute(null));
        yield return new("Open project…", "File", "Ctrl+O", () => main.OpenProjectCommand.Execute(null));
        yield return new("Save", "File", "Ctrl+S", () => main.SaveCommand.Execute(null));
        yield return new("Save as…", "File", "Ctrl+Shift+S", () => main.SaveAsCommand.Execute(null));
        yield return new("Import SEG-2 files…", "File", "Ctrl+I", () => main.ImportSurveyCommand.Execute(null));
        yield return new("Open sample project", "File", null, () => main.OpenSampleCommand.Execute(null));
        yield return new("Close project", "File", null, () => main.CloseProjectCommand.Execute(null));
        yield return new("Undo", "Edit", "Ctrl+Z", () => main.UndoCommand.Execute(null));
        yield return new("Redo", "Edit", "Ctrl+Y", () => main.RedoCommand.Execute(null));
        yield return new("Generate report", "Report", "Ctrl+R", () => main.GenerateReportCommand.Execute(null));
        yield return new("Settings", "Application", null, () => main.OpenSettingsCommand.Execute(null));
        yield return new("License", "Application", null, () => main.OpenSettingsCommand.Execute("License"));
        yield return new("Check for updates", "Application", null, () => main.CheckUpdatesCommand.Execute(false));
        yield return new("Toggle light/dark theme", "Application", null, () => main.ToggleThemeCommand.Execute(null));
        yield return new("Switch Guided / Expert mode", "Application", null, () => main.ToggleModeCommand.Execute(null));
        yield return new("Help", "Help", "F1", () => main.ToggleHelpCommand.Execute(null));
        yield return new("Export diagnostic package…", "Help", null, () => main.ExportDiagnosticsCommand.Execute(null));
        if (main.Workspace is { } ws)
        {
            foreach (var p in ws.Pages)
            {
                yield return new($"Go to {p.NavTitle}", "Navigate", null, () => ws.Navigate(p));
            }

            yield return new("Run automatic P picking", "Picking", null, () => { ws.Navigate(ws.PPicksPage); ws.PPicksPage.RunAutoPickCommand.Execute(null); });
            yield return new("Run automatic S picking", "Picking", null, () => { ws.Navigate(ws.SPicksPage); ws.SPicksPage.RunAutoPickCommand.Execute(null); });
            yield return new("Suggest layer boundaries", "Velocity", null, () => { ws.Navigate(ws.VelocityPage); ws.VelocityPage.SuggestCommand.Execute(null); });
            yield return new("Export tables to Excel", "Export", null, () => ws.ReportPage.ExportXlsxCommand.Execute(null));
            yield return new("Create snapshot", "Project", null, () => ws.ProjectPage.CreateSnapshotCommand.Execute(null));
            yield return new("Create scenario", "Project", null, () => ws.ProjectPage.CreateScenarioCommand.Execute(null));
        }
    }

    private void Filter()
    {
        Results.Clear();
        var q = Query.Trim();
        foreach (var c in All().Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Category.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Results.Add(c);
        }

        Selected = Results.FirstOrDefault();
    }

    [RelayCommand]
    public void Run(PaletteCommand? command)
    {
        var c = command ?? Selected;
        IsOpen = false;
        c?.Run();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }
}

public sealed record HelpTopic(string Key, string Title, string Body);

/// <summary>Context-sensitive help and the integrated user guide.</summary>
public sealed partial class HelpViewModel : ObservableObject
{
    public HelpViewModel()
    {
        foreach (var t in HelpContent.Topics)
        {
            Topics.Add(t);
        }

        Current = Topics[0];
    }

    public ObservableCollection<HelpTopic> Topics { get; } = [];

    [ObservableProperty] private HelpTopic? _current;
    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value)
    {
        Topics.Clear();
        foreach (var t in HelpContent.Topics.Where(t => value.Length == 0 || t.Title.Contains(value, StringComparison.OrdinalIgnoreCase) || t.Body.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            Topics.Add(t);
        }
    }

    public void ShowFor(string key) => Current = HelpContent.Topics.FirstOrDefault(t => t.Key == key) ?? HelpContent.Topics[0];

    [RelayCommand]
    private void Show(HelpTopic topic) => Current = topic;
}
