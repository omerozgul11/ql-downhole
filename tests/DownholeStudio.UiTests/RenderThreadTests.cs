using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DownholeStudio.App.Controls;
using DownholeStudio.App.Views;
using DownholeStudio.Licensing;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.UiTests;

/// <summary>
/// On desktop platforms Avalonia draws on a separate render thread, while the headless test platform draws on
/// the UI thread. Every chart is therefore painted here on a background thread, which fails if a painter
/// touches control properties or view models (the cause of a blank window on Windows and Linux).
/// </summary>
public sealed class RenderThreadTests
{
    private static readonly MethodInfo CreatePainter = typeof(SkiaView).GetMethod("CreatePainter", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [AvaloniaFact]
    public async Task EveryChartPaintsOffTheUiThread()
    {
        var main = UiHarness.Main;
        main.Ui.DialogOverride = d => d.Buttons[0].Text;
        var window = new MainWindow { DataContext = main, Width = 1600, Height = 1000 };
        window.Show();
        if (!main.LicenseManager.Status.CanEdit)
        {
            await UiHarness.ActivateOfflineAsync(main);
        }

        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sample = Path.Combine(UiHarness.Root, "render-thread.dhsx");
        Directory.CreateDirectory(UiHarness.Root);
        File.Copy(Path.Combine(repo, "packaging", "samples", "Downhole Studio Sample.dhsx"), sample, overwrite: true);
        await main.OpenPathAsync(sample);
        await UiHarness.SettleAsync(1000);
        var ws = main.Workspace!;
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[10].Id;

        var painted = new HashSet<string>();
        foreach (var page in ws.Pages)
        {
            ws.Navigate(page);
            await UiHarness.SettleAsync(800);
            foreach (var view in window.GetVisualDescendants().OfType<SkiaView>().Where(v => v.IsEffectivelyVisible && v.Bounds.Width > 0))
            {
                var paint = (Action<SKCanvas, SKRect, PlotTheme>)CreatePainter.Invoke(view, null)!;
                var size = view.Bounds.Size;
                var error = await Task.Run(() =>
                {
                    try
                    {
                        using var surface = SKSurface.Create(new SKImageInfo(Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height)));
                        paint(surface.Canvas, new SKRect(0, 0, (float)size.Width, (float)size.Height), PlotTheme.Light);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                });
                Assert.True(error is null, $"{view.GetType().Name} on {page.NavTitle} touched UI state while painting: {error}");
                painted.Add(view.GetType().Name);
            }
        }

        Assert.Contains(nameof(WaveformView), painted);
        Assert.Contains(nameof(PlotView), painted);
        Assert.Contains(nameof(BoreholeView), painted);
        window.Close();
    }
}
