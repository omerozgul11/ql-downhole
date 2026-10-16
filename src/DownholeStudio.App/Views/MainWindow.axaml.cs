using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using DownholeStudio.App.Services;
using DownholeStudio.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DownholeStudio.App.Views;

public partial class MainWindow : Window
{
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += (_, _) =>
        {
            if (App.Services?.GetService<UiService>() is { } ui)
            {
                ui.Attach(this);
            }

            if (App.Services?.GetService<Infrastructure.SettingsStore>() is { } settings && settings.Current.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static IReadOnlyList<string> Paths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Paths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var paths = Paths(e);
        var project = paths.FirstOrDefault(p => p.EndsWith(".dhsx", StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            await vm.OpenPathAsync(project);
            return;
        }

        if (paths.Count > 0)
        {
            if (vm.Workspace is null)
            {
                await vm.NewProjectAsync();
            }

            if (vm.Workspace is { } ws)
            {
                ws.Navigate(ws.ImportPage);
                await ws.ImportPage.AddPathsAsync(paths);
            }
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || Vm is not { } vm)
        {
            return;
        }

        if (App.Services?.GetService<Infrastructure.SettingsStore>() is { } settings)
        {
            settings.Update(s => s with { WindowMaximized = WindowState == WindowState.Maximized });
        }

        if (vm.Workspace is { Session.IsDirty: true, Session.ReadOnly: false })
        {
            e.Cancel = true;
            if (await vm.CloseProjectAsync())
            {
                _closingConfirmed = true;
                Close();
            }
        }
    }
}
