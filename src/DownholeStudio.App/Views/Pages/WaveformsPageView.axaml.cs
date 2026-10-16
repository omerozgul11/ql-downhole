using Avalonia.Controls;
using Avalonia.Interactivity;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class WaveformsPageView : UserControl
{
    public WaveformsPageView()
    {
        InitializeComponent();
        Waves.TraceClicked += (_, e) => (DataContext as WaveformsPageViewModel)?.SelectTrace(e.Key!);
        Waves.GainWheel += (_, f) => (DataContext as WaveformsPageViewModel)?.AdjustGain(f);
        Waves.HoverInfo = (i, t) => (DataContext as WaveformsPageViewModel)?.HoverLines(i, t) ?? [];
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Waves.ZoomTime(0.7);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Waves.ZoomTime(1.4);

    private void OnFit(object? sender, RoutedEventArgs e) => Waves.FitAll();
}
