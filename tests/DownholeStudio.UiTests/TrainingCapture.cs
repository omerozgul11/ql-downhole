using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DownholeStudio.App.ViewModels.Pages;
using DownholeStudio.App.Views;
using DownholeStudio.Domain;
using DownholeStudio.Licensing;
using DownholeStudio.Synthetics;

namespace DownholeStudio.UiTests;

/// <summary>
/// Captures the frames of the training video (marketing/training) from the real application: one frame per
/// storyboard shot, the screen point the user clicks to reach it, and optional zoom regions. Explicit:
/// <c>DHS_TRAINING_OUT=$PWD/artifacts/training-video-1.0.0 dotnet test tests/DownholeStudio.UiTests --filter FullyQualifiedName~TrainingCapture -- xUnit.Explicit=only</c>.
/// </summary>
public sealed class TrainingCapture
{
    private const int Width = 1920, Height = 960;
    private readonly List<object> _shots = [];
    private string _out = "";
    private Window _window = null!;

    [AvaloniaFact(Explicit = true)]
    public async Task CaptureTrainingFrames()
    {
        _out = Environment.GetEnvironmentVariable("DHS_TRAINING_OUT") ?? Path.Combine(UiHarness.Root, "training");
        Directory.CreateDirectory(Path.Combine(_out, "frames"));
        var main = UiHarness.Main;
        main.Ui.DialogOverride = d => d.Buttons[0].Text;
        _window = new MainWindow { DataContext = main, Width = Width, Height = Height };
        _window.Show();
        await UiHarness.SettleAsync(400);

        // 1 · Getting started
        Assert.Equal(LicenseState.NotActivated, main.LicenseManager.Status.State);
        await Shot("welcome");
        var p = At("Start 14-Day Free Trial");
        await main.License.StartTrialCommand.ExecuteAsync(null);
        await Shot("trial-started", p);

        // 2 · Project and import
        p = At("New Project");
        await main.NewProjectAsync();
        var ws = main.Workspace!;
        await Shot("new-project", p);
        ws.ProjectPage.Name = "Riverside Bridge BH-2";
        ws.ProjectPage.JobNumber = "QL-26-118";
        ws.ProjectPage.SurveyDate = new DateTime(2026, 9, 14);
        await Shot("project-named", At("Project name"));
        p = At("Import");
        ws.Navigate(ws.ImportPage);
        await Shot("import-empty", p);
        var survey = SyntheticSurveyGenerator.Generate(SyntheticOptions.For(SyntheticDataset.TriggerErrors));
        var folder = Path.Combine(UiHarness.Root, "Riverside BH-2 field records");
        SyntheticSurveyGenerator.WriteTo(survey, folder);
        p = At("Add folder…");
        await ws.ImportPage.AddPathsAsync([folder]);
        await Shot("import-preview", p);
        await Shot("import-checks", null, RegionOf("Checks", 0.5));
        p = AtCommand(ws.ImportPage.ImportCommand);
        await ws.ImportPage.ImportCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(800);
        await Shot("imported", p);

        // 3 · Geometry
        p = At("Geometry");
        ws.Navigate(ws.GeometryPage);
        await Shot("geometry", p);
        ws.GeometryPage.BoreholeName = "BH-2";
        ws.GeometryPage.POffset = 2;
        ws.GeometryPage.SOffset = 2;
        await Shot("geometry-offsets", At("Horizontal offset from borehole (m)"));
        ws.GeometryPage.Pattern = GeometryPageViewModel.PatternChoices[0];
        p = At("Assign by acquisition order");
        ws.GeometryPage.AssignByPatternCommand.Execute(null);
        await Shot("geometry-depths", p);

        // 4 · Inspect the data
        p = At("Waveforms");
        ws.Navigate(ws.WaveformsPage);
        await Shot("waveforms", p);
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[11].Id;
        await Shot("waveform-selected", CenterOf<App.Controls.WaveformView>());
        await Shot("waveform-spectrum", null, RegionOfVisual(_window.GetVisualDescendants().OfType<App.Controls.PlotView>().FirstOrDefault(v => v.IsEffectivelyVisible && v.Model == ws.WaveformsPage.Spectrum), 0.5));

        // 5 · Processing and trigger correction
        p = At("Processing");
        ws.Navigate(ws.ProcessingPage);
        await Shot("processing", p);
        p = AtCombo(ProcessingPageViewModel.GroupOptions[0]);
        ws.ProcessingPage.Group = ProcessingPageViewModel.GroupOptions[1];
        await Shot("processing-horizontal", p);
        p = At("Add processing step");
        ws.ProcessingPage.AddStepCommand.Execute("bandPass");
        await Shot("processing-bandpass", p);
        p = At("Reference & trigger correction");
        ws.ProcessingPage.SelectedTab = 1;
        await Shot("reference", p);
        p = AtCommand(ws.ProcessingPage.AutoPickReferencesCommand);
        await ws.ProcessingPage.AutoPickReferencesCommand.ExecuteAsync(null);
        await Shot("reference-picked", p);
        p = AtCombo(ProcessingPageViewModel.TriggerModes[0]);
        ws.ProcessingPage.TriggerMode = ProcessingPageViewModel.TriggerModes[1];
        await UiHarness.SettleAsync(400);
        await Shot("trigger-corrected", p);
        Assert.Equal(TriggerCorrectionMode.SubtractReferenceOnset, ws.State.ActiveScenario.Trigger.Mode);

        // 6 · P-wave picks
        p = At("P Picks");
        ws.Navigate(ws.PPicksPage);
        await Shot("ppicks", p);
        p = At("Run automatic picking");
        await ws.PPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(600);
        await Shot("ppicks-auto", p);
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[11].Id;
        await Shot("ppick-confidence", null, RegionOf("CONFIDENCE", 0.6));
        var station = ws.State.Survey.StationsByDepth()[11];
        var pick = ws.State.ActiveScenario.PPicks[station.Id].Time!.Value;
        ws.PPicksPage.TraceClicked(station.Id, pick + 0.0002, selectOnly: false);
        await Shot("ppick-manual", CenterOf<App.Controls.WaveformView>());
        p = At("Mark automatic picks reviewed");
        ws.PPicksPage.AcceptAllCommand.Execute(null);
        await Shot("ppicks-reviewed", p);

        // 7 · S-wave picks
        p = At("S Picks");
        ws.Navigate(ws.SPicksPage);
        await UiHarness.SettleAsync(300);
        await ws.SPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(600);
        await Shot("spicks-auto", p);
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[12].Id;
        await Shot("spicks-opposite", null, RegionOf("Opposite shots", 0.5));
        p = At("Rotation");
        ws.SPicksPage.DetailTab = 1;
        await UiHarness.SettleAsync(300);
        ws.SPicksPage.AnalyzeRotationCommand.Execute(null);
        await Shot("spicks-rotation", p);

        // 8 · Velocity model
        p = At("Velocity Model");
        ws.Navigate(ws.VelocityPage);
        await Shot("velocity", p);
        p = At("Suggest boundaries");
        ws.VelocityPage.SuggestCommand.Execute(null);
        await Shot("velocity-suggestions", p);
        p = At("Apply all");
        ws.VelocityPage.ApplyAllSuggestionsCommand.Execute(null);
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.Count == ws.State.ActiveScenario.Layers.Layers.Count);
        await Shot("velocity-layers", p);
        p = At(VelocityPageViewModel.MethodOptions[0].Label);
        ws.VelocityPage.Method = VelocityPageViewModel.MethodOptions[1];
        await UiHarness.WaitForResultAsync(ws, r => r.Method == VelocityMethod.RefractedRay);
        await Shot("velocity-refracted", p);

        // 9 · Engineering parameters and Vs30
        p = At("Engineering");
        ws.Navigate(ws.EngineeringPage);
        await Shot("engineering", p);
        p = At(EngineeringPageViewModel.SourceOptions[0].Label);
        ws.EngineeringPage.DensitySource = EngineeringPageViewModel.SourceOptions[1];
        ws.EngineeringPage.ConstantDensity = 1900;
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.All(l => l.Density.Source == DensitySource.Constant));
        await Shot("engineering-density", p);

        // 10 · Quality control
        p = At("Results");
        ws.Navigate(ws.ResultsPage);
        await Shot("results", p);

        // 11 · Report and exports
        p = At("Report");
        ws.Navigate(ws.ReportPage);
        ws.ReportPage.ClientName = "Riverside County DOT";
        ws.ReportPage.SiteName = "Riverside Bridge, pier 3";
        ws.ReportPage.Company = "Example Geotechnical Engineers";
        ws.ReportPage.Engineer = "A. Engineer";
        await Shot("report", p);
        p = At("Preview");
        await ws.ReportPage.PreviewCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(1000);
        await Shot("report-preview", p);
        var pdf = Path.Combine(UiHarness.Root, "Riverside BH-2 report.pdf");
        main.Ui.SaveFileOverride = _ => pdf;
        p = At("Generate PDF…");
        await ws.ReportPage.GenerateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(600);
        await Shot("report-generated", p);
        Assert.True(File.Exists(pdf));
        File.Copy(pdf, Path.Combine(_out, "training-report.pdf"), overwrite: true);

        await File.WriteAllTextAsync(Path.Combine(_out, "capture.json"),
            JsonSerializer.Serialize(new { status = "passed", width = Width, height = Height, shots = _shots }, new JsonSerializerOptions { WriteIndented = true }));
        main.Ui.SaveFileOverride = null;
        _window.Close();
    }

    private async Task Shot(string name, (double X, double Y)? pointer = null, (double X, double Y, double W, double H)? zoom = null)
    {
        await UiHarness.SettleAsync(500);
        UiHarness.Main.Ui.Toasts.Clear();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var file = $"{_shots.Count:00}-{name}.png";
#pragma warning disable CS0618 // The PNG overload is sufficient here.
        _window.CaptureRenderedFrame()!.Save(Path.Combine(_out, "frames", file));
#pragma warning restore CS0618
        _shots.Add(new
        {
            id = name, file,
            pointer = pointer is { } q ? new[] { Math.Round(q.X), Math.Round(q.Y) } : null,
            zoom = zoom is { } z ? new[] { Math.Round(z.X), Math.Round(z.Y), Math.Round(z.W), Math.Round(z.H) } : null,
        });
    }

    /// <summary>Centre of the visible control showing exactly this text (its button when it is a button label).</summary>
    private (double X, double Y)? At(string text)
    {
        var label = _window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == text);
        if (label is null)
        {
            return null;
        }

        Visual target = (Visual?)label.FindAncestorOfType<Button>() ?? (Visual?)label.FindAncestorOfType<ComboBox>() ?? (Visual?)label.FindAncestorOfType<TabItem>() ?? label;
        return Center(target);
    }

    private (double X, double Y)? AtCommand(System.Windows.Input.ICommand command) =>
        _window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, command)) is { } b ? Center(b) : null;

    private (double X, double Y)? AtCombo(object selected) =>
        _window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsEffectivelyVisible && Equals(c.SelectedItem, selected)) is { } c ? Center(c) : null;

    private (double X, double Y)? CenterOf<T>() where T : Visual =>
        _window.GetVisualDescendants().OfType<T>().FirstOrDefault(v => v.IsEffectivelyVisible) is { } v ? Center(v) : null;

    private (double X, double Y)? Center(Visual v) =>
        v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), _window) is { } c ? (c.X, c.Y) : null;

    private (double X, double Y, double W, double H)? RegionOfVisual(Visual? visual, double fraction)
    {
        if (visual is null)
        {
            return null;
        }

        var card = visual.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Bounds.Width > visual.Bounds.Width) ?? (Visual)visual;
        return Fit(card, fraction);
    }

    /// <summary>A zoom region around the card containing this text, at least `fraction` of the screen wide, in screen proportions.</summary>
    private (double X, double Y, double W, double H)? RegionOf(string text, double fraction)
    {
        var label = _window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && string.Equals(t.Text, text, StringComparison.OrdinalIgnoreCase));
        if (label is null)
        {
            return null;
        }

        Visual card = label;
        foreach (var ancestor in label.GetVisualAncestors().OfType<Border>())
        {
            card = ancestor;
            if (ancestor.Bounds.Width > 200 && ancestor.Bounds.Height > 120)
            {
                break;
            }
        }

        return Fit(card, fraction);
    }

    private (double X, double Y, double W, double H) Fit(Visual card, double fraction)
    {
        var tl = card.TranslatePoint(new Point(0, 0), _window)!.Value;
        var w = Math.Max(card.Bounds.Width + 40, Width * fraction);
        var h = w * Height / Width;
        if (h < card.Bounds.Height + 40)
        {
            h = card.Bounds.Height + 40;
            w = h * Width / Height;
        }

        w = Math.Min(w, Width);
        h = Math.Min(h, Height);
        var cx = tl.X + card.Bounds.Width / 2;
        var cy = tl.Y + card.Bounds.Height / 2;
        var x = Math.Clamp(cx - w / 2, 0, Width - w);
        var y = Math.Clamp(cy - h / 2, 0, Height - h);
        return (x, y, w, h);
    }
}
