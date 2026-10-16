using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DownholeStudio.App.ViewModels;
using DownholeStudio.App.ViewModels.Pages;
using DownholeStudio.App.Views.Pages;

namespace DownholeStudio.App;

/// <summary>Maps view models to views without reflection (trimming-safe).</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(ProjectPageViewModel)] = () => new ProjectPageView(),
        [typeof(ImportPageViewModel)] = () => new ImportPageView(),
        [typeof(GeometryPageViewModel)] = () => new GeometryPageView(),
        [typeof(WaveformsPageViewModel)] = () => new WaveformsPageView(),
        [typeof(ProcessingPageViewModel)] = () => new ProcessingPageView(),
        [typeof(PickingPageViewModel)] = () => new PickingPageView(),
        [typeof(SPickingPageViewModel)] = () => new PickingPageView(),
        [typeof(VelocityPageViewModel)] = () => new VelocityPageView(),
        [typeof(EngineeringPageViewModel)] = () => new EngineeringPageView(),
        [typeof(ResultsPageViewModel)] = () => new ResultsPageView(),
        [typeof(ReportPageViewModel)] = () => new ReportPageView(),
        [typeof(TraceInspectorViewModel)] = () => new TraceInspectorView(),
        [typeof(StepEditorViewModel)] = () => new StepEditorView(),
        [typeof(PickInspectorViewModel)] = () => new PickInspectorView(),
        [typeof(LayerInspectorViewModel)] = () => new LayerInspectorView(),
    };

    public Control? Build(object? param) =>
        param is not null && Views.TryGetValue(param.GetType(), out var factory) ? factory() : new TextBlock { Text = param?.GetType().Name ?? "" };

    public bool Match(object? data) => data is not null && Views.ContainsKey(data.GetType());
}
