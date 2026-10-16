using Avalonia.Controls;
using DownholeStudio.App.ViewModels.Pages;

namespace DownholeStudio.App.Views.Pages;

public partial class ProcessingPageView : UserControl
{
    public ProcessingPageView()
    {
        InitializeComponent();
        ReferenceWaves.TraceClicked += (_, e) => (DataContext as ProcessingPageViewModel)?.SetReferencePick(e.Time);
        ReferenceWaves.PickDragged += (_, e) =>
        {
            if (e.Completed)
            {
                (DataContext as ProcessingPageViewModel)?.SetReferencePick(e.Time);
            }
        };
    }
}
