using Avalonia.Controls;
using DownholeStudio.App.Controls;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class EngineeringPageView : UserControl
{
    public EngineeringPageView() => InitializeComponent();

    /// <summary>Hovering any profile shows every property at that depth and a cursor on all profiles.</summary>
    private void OnCursorMoved(object? sender, PlotCursorEventArgs e)
    {
        if (DataContext is EngineeringPageViewModel vm && (vm.HoverDepth is null || Math.Abs(vm.HoverDepth.Value - e.Y) > 0.05))
        {
            vm.HoverDepth = Math.Round(e.Y, 2);
        }
    }
}
