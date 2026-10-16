using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class PickingPageView : UserControl
{
    public PickingPageView()
    {
        InitializeComponent();
        Waves.TraceClicked += (_, e) => Vm?.TraceClicked(e.Key!, e.Time, e.Modifiers.HasFlag(KeyModifiers.Control));
        Waves.PickDragged += (_, e) => Vm?.PickDragged(e.Key!, e.Time, e.Completed);
        Waves.GainWheel += (_, f) => Vm?.AdjustGain(f);
        Waves.HoverInfo = (i, t) => Vm?.HoverLines(i, t) ?? [];
        TravelTimePlot.PointClicked += (_, e) => Vm?.TravelTimePointClicked(e.Hit.Point.Tag);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private PickingPageViewModel? Vm => DataContext as PickingPageViewModel;

    /// <summary>Keyboard picking. Text inputs (table cells, numeric boxes) keep their own keys.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox || e.Source is NumericUpDown)
        {
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Left:
                vm.Nudge(shift ? -10 : -1);
                break;
            case Key.Right:
                vm.Nudge(shift ? 10 : 1);
                break;
            case Key.Up:
                vm.SelectAdjacent(-1);
                break;
            case Key.Down:
                vm.SelectAdjacent(1);
                break;
            case Key.Enter:
                vm.AcceptSuggestion();
                break;
            case Key.Delete or Key.Back:
                vm.DeleteSelected();
                break;
            case Key.Space:
                vm.ToggleIncludeSelected();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Waves.ZoomTime(0.7);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Waves.ZoomTime(1.4);

    private void OnFit(object? sender, RoutedEventArgs e) => Waves.FitAll();
}
