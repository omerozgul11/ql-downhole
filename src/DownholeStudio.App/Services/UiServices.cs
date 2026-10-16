using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DownholeStudio.App.Services;

public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>A non-modal in-window dialog (shown as an overlay card).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _result = new();

    public DialogViewModel(string title, string message, IReadOnlyList<string> buttons, string? details = null, string? kind = null)
    {
        Title = title;
        Message = message;
        Details = details;
        Kind = kind ?? "info";
        Buttons = buttons.Select((b, i) => new DialogButton(b, i == 0, this)).ToList();
    }

    public string Title { get; }
    public string Message { get; }
    public string? Details { get; }
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public string Kind { get; }
    public IReadOnlyList<DialogButton> Buttons { get; }

    [ObservableProperty]
    private bool _showDetails;

    public Task<string?> Result => _result.Task;

    public void Close(string? button) => _result.TrySetResult(button);

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;
}

public sealed class DialogButton(string text, bool primary, DialogViewModel owner)
{
    public string Text { get; } = text;
    public bool Primary { get; } = primary;
    public IRelayCommand Command { get; } = new RelayCommand(() => owner.Close(text));
}

public sealed partial class Toast : ObservableObject
{
    public required string Message { get; init; }
    public string Kind { get; init; } = "info";
}

/// <summary>File pickers, overlay dialogs, toasts and clipboard, bound to the main window.</summary>
public sealed partial class UiService : ObservableObject
{
    private TopLevel? _top;

    public void Attach(TopLevel top) => _top = top;

    /// <summary>Automation hooks (UI tests): when set, file pickers return these instead of showing a dialog.</summary>
    public Func<string, IReadOnlyList<string>>? OpenFilesOverride { get; set; }

    public Func<string, string?>? SaveFileOverride { get; set; }

    /// <summary>Automation hook: answers overlay dialogs without user interaction (button text to choose).</summary>
    public Func<DialogViewModel, string?>? DialogOverride { get; set; }

    [ObservableProperty]
    private DialogViewModel? _dialog;

    public ObservableCollection<Toast> Toasts { get; } = [];

    public Task<string?> AskAsync(string title, string message, IReadOnlyList<string> buttons, string? details = null, string kind = "info")
    {
        var dialog = new DialogViewModel(title, message, buttons, details, kind);
        if (DialogOverride is { } auto)
        {
            return Task.FromResult(auto(dialog));
        }

        Dialog = dialog;
        return dialog.Result.ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(Dialog, dialog))
                {
                    Dialog = null;
                }
            });
            return t.Result;
        }, TaskScheduler.Default);
    }

    public Task ErrorAsync(string title, string message, string? details = null) => AskAsync(title, message, ["OK"], details, "error");

    public void Notify(string message, string kind = "info")
    {
        void Show()
        {
            var toast = new Toast { Message = message, Kind = kind };
            while (Toasts.Count >= 3)
            {
                Toasts.RemoveAt(0);
            }

            Toasts.Add(toast);
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(kind == "error" ? 8 : 4.5));
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Show();
        }
        else
        {
            Dispatcher.UIThread.Post(Show);
        }
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, IEnumerable<FileFilter> filters, string? startFolder = null)
    {
        if (OpenFilesOverride is { } open)
        {
            return open(title);
        }

        if (_top is null)
        {
            return [];
        }

        var options = new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = multiple,
            FileTypeFilter = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        };
        if (startFolder is not null && await _top.StorageProvider.TryGetFolderFromPathAsync(startFolder) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var files = await _top.StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        if (_top is null)
        {
            return null;
        }

        var folders = await _top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, FileFilter filter, string? startFolder = null)
    {
        if (SaveFileOverride is { } save)
        {
            return save(suggestedName);
        }

        if (_top is null)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggestedName, DefaultExtension = filter.Patterns.FirstOrDefault()?.TrimStart('*', '.'), ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(filter.Name) { Patterns = filter.Patterns }],
        };
        if (startFolder is not null && await _top.StorageProvider.TryGetFolderFromPathAsync(startFolder) is { } folder)
        {
            options.SuggestedStartLocation = folder;
        }

        var file = await _top.StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task CopyTextAsync(string text)
    {
        if (_top?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            Notify("Copied to the clipboard.");
        }
    }

    public IClipboard? Clipboard => _top?.Clipboard;

    public void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify($"Could not open {url}.", "error");
        }
    }

    public static readonly FileFilter ProjectFilter = new("Downhole Studio project", "*.dhsx");
    public static readonly FileFilter Seg2Filter = new("SEG-2 seismic records", "*.sg2", "*.seg2", "*.dat");
    public static readonly FileFilter PdfFilter = new("PDF document", "*.pdf");
    public static readonly FileFilter XlsxFilter = new("Excel workbook", "*.xlsx");
    public static readonly FileFilter CsvFilter = new("CSV table", "*.csv");
    public static readonly FileFilter PngFilter = new("PNG image", "*.png");
    public static readonly FileFilter SvgFilter = new("SVG image", "*.svg");
    public static readonly FileFilter ZipFilter = new("ZIP archive", "*.zip");
    public static readonly FileFilter ImageFilter = new("Images", "*.png", "*.jpg", "*.jpeg");
}
