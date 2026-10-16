using Avalonia.Headless.XUnit;
using DownholeStudio.App.Views;
using DownholeStudio.Licensing;

namespace DownholeStudio.UiTests;

/// <summary>
/// Renders the product-page screenshots in docs/images from the bundled sample project. Explicit: run with
/// <c>DHS_SCREENSHOTS=$PWD/docs/images dotnet test tests/DownholeStudio.UiTests --filter FullyQualifiedName~ProductScreenshots -- xUnit.Explicit=only</c>.
/// </summary>
public sealed class ProductScreenshots
{
    [AvaloniaFact(Explicit = true)]
    public async Task RenderProductPageScreenshots()
    {
        var main = UiHarness.Main;
        main.Ui.DialogOverride = d => d.Buttons[0].Text;
        var window = new MainWindow { DataContext = main, Width = 1600, Height = 1000 };
        window.Show();
        await UiHarness.SettleAsync(200);
        if (main.LicenseManager.Status.State != LicenseState.Licensed)
        {
            main.License.IsOpen = true;
            main.License.ShowActivateFormCommand.Execute(null);
            main.License.Email = FakeLicenseServer.Email;
            main.License.LicenseKey = FakeLicenseServer.ValidKey;
            await main.License.ActivateCommand.ExecuteAsync(null);
            await UiHarness.SettleAsync();
            main.License.IsOpen = false;
        }

        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sample = Path.Combine(UiHarness.Root, "Downhole Studio Sample.dhsx");
        Directory.CreateDirectory(UiHarness.Root);
        File.Copy(Path.Combine(repo, "packaging", "samples", "Downhole Studio Sample.dhsx"), sample, overwrite: true);
        await main.OpenPathAsync(sample);
        await UiHarness.SettleAsync(1500);
        var ws = main.Workspace!;
        Assert.NotNull(ws.Result);

        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[11].Id;
        (Action Show, string Name)[] pages =
        [
            (() => ws.Navigate(ws.VelocityPage), "velocity-model"),
            (() => ws.Navigate(ws.SPicksPage), "s-picks"),
            (() => ws.Navigate(ws.PPicksPage), "p-picks"),
            (() => ws.Navigate(ws.WaveformsPage), "waveforms"),
            (() => ws.Navigate(ws.ProcessingPage), "processing"),
            (() => ws.Navigate(ws.EngineeringPage), "engineering"),
            (() => ws.Navigate(ws.ResultsPage), "quality-control"),
            (() => ws.Navigate(ws.GeometryPage), "geometry"),
        ];
        foreach (var (show, name) in pages)
        {
            show();
            await UiHarness.SettleAsync(1200);
            main.Ui.Toasts.Clear();
            UiHarness.Capture(window, name);
        }

        ws.Navigate(ws.ReportPage);
        await ws.ReportPage.PreviewCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(1500);
        main.Ui.Toasts.Clear();
        UiHarness.Capture(window, "report");
        window.Close();
    }
}
