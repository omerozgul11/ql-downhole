using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownholeStudio.App.Services;
using DownholeStudio.Core.Import;
using DownholeStudio.Domain;
using DownholeStudio.Formats;

namespace DownholeStudio.App.ViewModels.Pages;

public sealed partial class ImportFileRow(ImportCandidate c) : ObservableObject
{
    public ImportCandidate Candidate { get; } = c;

    [ObservableProperty]
    private bool _include = c.IsReadable;

    public string FileName => Candidate.FileName;
    public bool Readable => Candidate.IsReadable;
    public string Traces => Candidate.Metadata?.Traces.Count.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string Channels => Candidate.Metadata is { } m ? string.Join(", ", m.Traces.Select(t => t.ChannelNumber)) : "—";
    public string SampleRate => Candidate.Metadata?.Traces.FirstOrDefault() is { } t ? $"{1 / t.SampleInterval:0.#} Hz" : "—";
    public string Samples => Candidate.Metadata?.Traces.FirstOrDefault() is { } t ? $"{t.SampleCount} ({(t.SampleCount - 1) * t.SampleInterval * 1000:0.#} ms)" : "—";
    public string Recorded => Candidate.Metadata?.AcquiredAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "—";
    public string Labels => Candidate.Metadata is { } m ? string.Join(" ", m.Traces.Select(t => t.Label).Where(l => !string.IsNullOrEmpty(l))) : "";
    public string Depth => Candidate.DepthHint is { } d ? $"{d.Depth:0.###} m" : "—";
    public string DepthSource => Candidate.DepthHint?.Source ?? "";
    public string Shot => Candidate.ShotHint switch { ShotKind.P => "P", ShotKind.SPositive => "S+", ShotKind.SNegative => "S−", _ => "—" };
    public string Status => Candidate.IsReadable ? (Candidate.Metadata!.ReaderNotes.Count > 0 ? "Read with notes" : "OK") : "Cannot read";
    public string? Error => Candidate.ErrorMessage;
}

public sealed record DiagnosticRow(Severity Severity, string Message, string? Details, string? Fix)
{
    public bool IsError => Severity == Severity.Error;
    public bool IsWarning => Severity == Severity.Warning;
    public bool IsInfo => Severity == Severity.Information;
    public string SeverityText => Severity switch { Severity.Error => "Error", Severity.Warning => "Warning", _ => "Information" };
    public bool HasDetails => !string.IsNullOrEmpty(Details);
}

public sealed partial class ImportPageViewModel(WorkspaceViewModel ws) : PageViewModel(ws)
{
    private ImportPreview? _preview;
    private readonly List<string> _paths = [];

    public override bool HasInspector => false;

    public override string NavTitle => "Import";
    public override string Title => "Import survey data";
    public override string Description => "Add SEG-2 records (one file per shot). Downhole Studio reads the headers, shows what it found and lists anything questionable before you import. Nothing is changed in your files; each original file is copied byte-for-byte into the project.";
    public override string IconKey => "Import";
    public override PageViewModel? Next => Workspace.GeometryPage;

    public ObservableCollection<ImportFileRow> Files { get; } = [];
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];
    public ObservableCollection<ImportedRecordRow> Imported { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _skipDuplicates = true;
    [ObservableProperty] private bool _orderByTime;
    [ObservableProperty] private bool _applySuggestedDepths = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _channelSuggestion;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;

    public bool HasPreview => Files.Count > 0;
    public bool HasImported => Imported.Count > 0;
    public bool HasDuplicates => Diagnostics.Any(d => d.Message.Contains("identical copies", StringComparison.Ordinal));
    public bool HasOrderIssue => _preview?.Diagnostics.Any(d => d.Code == "import.acquisitionOrder") == true;
    public bool HasDepthHints => Files.Any(f => f.Candidate.DepthHint is not null);
    public string SupportedFormats => string.Join(", ", SeismicFormatRegistry.CreateDefault().SupportedExtensions);

    public override void OnStateChanged()
    {
        Imported.Clear();
        foreach (var r in State.Survey.Records.OrderBy(r => r.Sequence))
        {
            var depth = r.StationId is { } s && State.Survey.FindStation(s) is { } st ? Units.Format(State.Survey.Geometry.DepthBelowGround(st.MeasuredDepth), Domain.Units.Quantity.Length) : "Not assigned";
            Imported.Add(new ImportedRecordRow(r.Source.FileName, r.Traces.Count, depth, r.Shot.ToString(), r.Source.Sha256[..12], r.Excluded));
        }

        OnPropertyChanged(nameof(HasImported));
    }

    [RelayCommand]
    public async Task AddFilesAsync()
    {
        var files = await Workspace.Ui.OpenFilesAsync("Add SEG-2 records", true, [UiService.Seg2Filter, new FileFilter("All files", "*")], Workspace.Main.Settings.Current.LastFolder);
        await AddPathsAsync(files);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = await Workspace.Ui.OpenFolderAsync("Add every SEG-2 file in a folder");
        if (folder is not null)
        {
            await AddPathsAsync([folder]);
        }
    }

    /// <summary>Adds files or folders (drag and drop, pickers) and refreshes the preview.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var expanded = Workspace.Main.ImportService.Inspector.ExpandPaths(list);
        if (expanded.Count == 0)
        {
            Workspace.Ui.Notify("No SEG-2 files were found in the selection.", "warning");
            return;
        }

        foreach (var p in expanded.Where(p => !_paths.Contains(p)))
        {
            _paths.Add(p);
        }

        if (list.FirstOrDefault(Directory.Exists) is { } dir)
        {
            Workspace.Main.Settings.Update(s => s with { LastFolder = dir });
        }
        else if (Path.GetDirectoryName(list[0]) is { } folder)
        {
            Workspace.Main.Settings.Update(s => s with { LastFolder = folder });
        }

        await PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => Progress = p * 100);
            _preview = await Workspace.Main.ImportService.Inspector.InspectAsync(_paths, progress);
            Files.Clear();
            foreach (var f in _preview.Files)
            {
                Files.Add(new ImportFileRow(f));
            }

            Diagnostics.Clear();
            foreach (var d in _preview.Diagnostics.OrderByDescending(d => d.Severity))
            {
                Diagnostics.Add(new DiagnosticRow(d.Severity, d.Message, d.Details, d.SuggestedFix));
            }

            ErrorCount = Diagnostics.Count(d => d.IsError);
            WarningCount = Diagnostics.Count(d => d.IsWarning);
            Summary = $"{_preview.ReadableCount} of {_preview.Files.Count} file(s) can be imported · {ErrorCount} error(s) · {WarningCount} warning(s)";
            OrderByTime = false;
            ApplySuggestedDepths = HasDepthHints && State.Survey.Stations.Count == 0;
            var readable = _preview.Files.Where(f => f.IsReadable).ToList();
            ChannelSuggestion = readable.Count == 0 || State.Survey.ChannelMap.Channels.Count > 0 ? null
                : ChannelMapSuggester.Suggest(readable.Select(f => new SeismicRecord
                {
                    Id = Guid.Empty, FormatName = f.ReaderName ?? "", Source = new SourceFileInfo { FileName = f.FileName, Sha256 = f.Sha256, EmbeddedPath = "" },
                    Traces = [.. f.Metadata!.Traces.Select(t => new TraceInfo { Id = Guid.Empty, ChannelNumber = t.ChannelNumber, Label = t.Label })],
                }).ToList(), Persistence.AppCatalog.BuiltInProfiles).Reason;
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasDuplicates));
            OnPropertyChanged(nameof(HasOrderIssue));
            OnPropertyChanged(nameof(HasDepthHints));
            ImportCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsBusy = false;
            ImportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _paths.Clear();
        _preview = null;
        Files.Clear();
        Diagnostics.Clear();
        Summary = "";
        ChannelSuggestion = null;
        OnPropertyChanged(nameof(HasPreview));
        ImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Removes a file from the selection and re-inspects the rest, so it is not imported and the checks (duplicates, missing channels) no longer count it.</summary>
    [RelayCommand]
    private async Task RemoveFile(ImportFileRow row)
    {
        _paths.Remove(row.Candidate.Path);
        if (_paths.Count == 0)
        {
            Clear();
            return;
        }

        await PreviewAsync();
    }

    private bool CanImport() => _preview is not null && Files.Any(f => f.Include && f.Readable) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task Import()
    {
        if (_preview is null || !Workspace.CanModify())
        {
            return;
        }

        IsBusy = true;
        try
        {
            var options = new ImportOptions
            {
                ExcludedPaths = Files.Where(f => !f.Include).Select(f => f.Candidate.Path).ToHashSet(),
                SkipDuplicates = SkipDuplicates, OrderByRecordingTime = OrderByTime, ApplySuggestedDepths = ApplySuggestedDepths && HasDepthHints,
            };
            var progress = new Progress<double>(p => Progress = p * 100);
            var result = await Workspace.Main.ImportService.ImportAsync(Workspace.Session, _preview, options, progress);
            foreach (var m in result.Messages)
            {
                Workspace.Ui.Notify(m, "warning");
            }

            Workspace.Ui.Notify($"Imported {result.Imported} record(s).", "success");
            Clear();
            if (result.Imported > 0)
            {
                Workspace.Navigate(Workspace.GeometryPage);
            }
        }
        catch (Exception ex) when (ex is IOException or SeismicFormatException or UnauthorizedAccessException)
        {
            await Workspace.Ui.ErrorAsync("Import failed", "The records could not be imported. Nothing was added to the project.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record ImportedRecordRow(string FileName, int Traces, string Depth, string Shot, string Sha, bool Excluded);
