using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class ImportPageView : UserControl
{
    public ImportPageView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ImportPageViewModel vm)
        {
            return;
        }

        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList() ?? [];
        e.Handled = true;
        await vm.AddPathsAsync(paths);
    }
}
