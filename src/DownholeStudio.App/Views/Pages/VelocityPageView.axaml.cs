using Avalonia.Controls;
using Avalonia.Input;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class VelocityPageView : UserControl
{
    public VelocityPageView()
    {
        InitializeComponent();
        TravelTimePlot.PointClicked += (_, e) =>
        {
            if (e.Modifiers.HasFlag(KeyModifiers.Shift))
            {
                Vm?.TogglePointExcluded(e.Hit.Point.Tag);
            }
            else
            {
                Vm?.PlotPointClicked(e.Hit.Point.Tag);
            }
        };
        TravelTimePlot.LineDragged += (_, e) =>
        {
            if (e.DragId is Guid id)
            {
                Vm?.MoveBoundary(id, e.Value, e.Completed);
            }
        };
        TravelTimePlot.BackgroundClicked += (_, e) =>
        {
            if (_ctrl)
            {
                Vm?.AddBoundaryAt(e.Y);
            }
        };
        ProfilePlot.LineDragged += (_, e) =>
        {
            if (e.DragId is Guid id)
            {
                Vm?.MoveBoundary(id, e.Value, e.Completed);
            }
        };
        AddHandler(PointerPressedEvent, (_, e) => _ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private bool _ctrl;

    private VelocityPageViewModel? Vm => DataContext as VelocityPageViewModel;
}
