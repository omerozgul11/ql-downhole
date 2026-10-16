using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DownholeStudio.App.ViewModels;

namespace DownholeStudio.App.Views;

public partial class PaletteView : UserControl
{
    public PaletteView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Search.Focus();
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
