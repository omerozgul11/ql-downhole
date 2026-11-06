using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DownholeStudio.App.ViewModels;

namespace DownholeStudio.App.Views;

public partial class PaletteView : UserControl
{
    public PaletteView()
    {
        InitializeComponent();
        // The parent panel shows and hides this view, so follow the palette itself. Posted: a control cannot take
        // focus until it has been laid out.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CommandPaletteViewModel palette)
            {
                palette.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && palette.IsOpen)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            Search.Focus();
                            Search.SelectAll();
                        }, DispatcherPriority.Loaded);
                    }
                };
            }
        };
    }

    private CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Vm?.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Vm?.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Vm?.Run(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm?.CloseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnRun(object? sender, TappedEventArgs e) => Vm?.Run(null);

    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e) => Vm?.CloseCommand.Execute(null);

    private void OnCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
