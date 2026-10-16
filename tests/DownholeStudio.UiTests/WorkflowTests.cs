using Avalonia.Headless.XUnit;
using DownholeStudio.App.ViewModels;
using DownholeStudio.App.Views;
using DownholeStudio.Domain;
using DownholeStudio.Licensing;
using DownholeStudio.Synthetics;

namespace DownholeStudio.UiTests;

/// <summary>Critical user workflows driven through the real views and view models.</summary>
public sealed class WorkflowTests
{
    private static MainWindow Open(MainViewModel main)
    {
        var window = new MainWindow { DataContext = main, Width = 1600, Height = 1000 };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public async Task CompleteSurveyFromTrialToReport()
    {
        var main = UiHarness.Main;
        main.Ui.DialogOverride = d => d.Buttons[0].Text;
        var window = Open(main);
        await UiHarness.SettleAsync(200);

        // Activate trial.
        Assert.Equal(LicenseState.NotActivated, main.LicenseManager.Status.State);
        Assert.True(main.License.IsOpen);
        UiHarness.Capture(window, "00-welcome-license");
        await main.License.StartTrialCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync();
        Assert.Equal(LicenseState.TrialActive, main.LicenseManager.Status.State);
        Assert.False(main.License.IsOpen);
        Assert.True(main.CanEdit);
        UiHarness.Capture(window, "01-start-screen");

        // Create project.
        await main.NewProjectAsync();
        var ws = main.Workspace!;
        ws.ProjectPage.Name = "Riverside Bridge BH-2";
        ws.ProjectPage.JobNumber = "QL-26-118";
        await UiHarness.SettleAsync();
        Assert.Equal("Riverside Bridge BH-2", ws.State.Metadata.Name);
        UiHarness.Capture(window, "02-project");

        // Import SEG-2.
        var survey = SyntheticSurveyGenerator.Generate(SyntheticOptions.For(SyntheticDataset.TriggerErrors) with { DepthCount = 30 });
        var folder = Path.Combine(UiHarness.Root, "survey");
        SyntheticSurveyGenerator.WriteTo(survey, folder);
        ws.Navigate(ws.ImportPage);
        await ws.ImportPage.AddPathsAsync([folder]);
        await UiHarness.SettleAsync();
        Assert.Equal(90, ws.ImportPage.Files.Count);
        UiHarness.Capture(window, "03-import-preview");
        await ws.ImportPage.ImportCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(800);
        Assert.Equal(90, ws.State.Survey.Records.Count);
        Assert.Equal(30, ws.State.Survey.Stations.Count);

        // Geometry.
        ws.Navigate(ws.GeometryPage);
        ws.GeometryPage.POffset = 2;
        ws.GeometryPage.SOffset = 2;
        ws.GeometryPage.BoreholeName = "BH-2";
        await UiHarness.SettleAsync();
        Assert.Equal(2, ws.State.Survey.Geometry.SSource.HorizontalOffset);
        UiHarness.Capture(window, "04-geometry");

        // Waveforms.
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[10].Id;
        ws.Navigate(ws.WaveformsPage);
        await UiHarness.SettleAsync();
        Assert.NotNull(ws.WaveformsPage.Section);
        Assert.Equal(30, ws.WaveformsPage.Section!.Traces.Count);
        UiHarness.Capture(window, "05-waveforms");

        // Processing: band-pass on horizontals, then trigger correction.
        ws.Navigate(ws.ProcessingPage);
        ws.ProcessingPage.Group = App.ViewModels.Pages.ProcessingPageViewModel.GroupOptions[1];
        ws.ProcessingPage.AddStepCommand.Execute("bandPass");
        await UiHarness.SettleAsync();
        Assert.Contains(ws.State.ActiveScenario.HorizontalPipeline.Steps, s => s is BandPassStep);
        UiHarness.Capture(window, "06-processing");
        ws.ProcessingPage.SelectedTab = 1;
        await ws.ProcessingPage.AutoPickReferencesCommand.ExecuteAsync(null);
        ws.ProcessingPage.TriggerMode = App.ViewModels.Pages.ProcessingPageViewModel.TriggerModes[1];
        await UiHarness.SettleAsync();
        Assert.Equal(TriggerCorrectionMode.SubtractReferenceOnset, ws.State.ActiveScenario.Trigger.Mode);
        UiHarness.Capture(window, "07-trigger");

        // P picks: automatic, then a manual pick and undo.
        ws.Navigate(ws.PPicksPage);
        await ws.PPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(600);
        Assert.Equal(30, ws.State.ActiveScenario.PPicks.Values.Count(p => p.HasTime));
        var station = ws.State.Survey.StationsByDepth()[5];
        var before = ws.State.ActiveScenario.PPicks[station.Id].Time;
        ws.PPicksPage.TraceClicked(station.Id, 0.0123, selectOnly: false);
        await UiHarness.SettleAsync();
        Assert.Equal(PickOrigin.Manual, ws.State.ActiveScenario.PPicks[station.Id].Origin);
        Assert.Equal(0.0123, ws.State.ActiveScenario.PPicks[station.Id].Time!.Value, 4);
        // Displaying the pick table must never write rounded values back into the picks.
        Assert.Single(ws.Session.History.TakeLast(3), h => h.Operation == "pick.set");
        ws.Undo();
        await UiHarness.SettleAsync();
        Assert.Equal(before, ws.State.ActiveScenario.PPicks[station.Id].Time);
        UiHarness.Capture(window, "08-p-picks");

        // S picks.
        ws.Navigate(ws.SPicksPage);
        await ws.SPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(600);
        Assert.True(ws.State.ActiveScenario.SPicks.Values.Count(p => p.HasTime) >= 28);
        ws.SelectedStationId = ws.State.Survey.StationsByDepth()[12].Id;
        await UiHarness.SettleAsync();
        ws.SPicksPage.AnalyzeRotationCommand.Execute(null);
        UiHarness.Capture(window, "09-s-picks");

        // Layers and velocity.
        ws.Navigate(ws.VelocityPage);
        ws.VelocityPage.SuggestCommand.Execute(null);
        await UiHarness.SettleAsync();
        Assert.True(ws.VelocityPage.Suggestions.Count >= 2);
        ws.VelocityPage.ApplyAllSuggestionsCommand.Execute(null);
        ws.VelocityPage.Method = App.ViewModels.Pages.VelocityPageViewModel.MethodOptions[1];
        await UiHarness.SettleAsync(600);
        Assert.True(ws.State.ActiveScenario.Layers.Layers.Count >= 3);
        Assert.All(ws.Result!.S.Layers, l => Assert.NotNull(l.Velocity));
        UiHarness.Capture(window, "10-velocity");

        // Engineering and Vs30 (survey reaches 30 m).
        ws.Navigate(ws.EngineeringPage);
        ws.EngineeringPage.DensitySource = App.ViewModels.Pages.EngineeringPageViewModel.SourceOptions[1];
        ws.EngineeringPage.ConstantDensity = 1900;
        await UiHarness.SettleAsync(600);
        Assert.NotNull(ws.Result!.Vs30.Value);
        Assert.InRange(ws.Result.Vs30.Value!.Value / survey.Truth.Vs30!.Value, 0.95, 1.05);
        Assert.All(ws.Result.LayerResults, l => Assert.NotNull(l.Properties.ShearModulus));
        UiHarness.Capture(window, "11-engineering");

        // Review.
        ws.Navigate(ws.ResultsPage);
        await UiHarness.SettleAsync();
        Assert.NotEmpty(ws.ResultsPage.Cards);
        UiHarness.Capture(window, "12-results");

        // Report.
        var pdf = Path.Combine(UiHarness.Root, "report.pdf");
        main.Ui.SaveFileOverride = _ => pdf;
        ws.Navigate(ws.ReportPage);
        ws.ReportPage.ClientName = "Riverside County DOT";
        await ws.ReportPage.PreviewCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync();
        UiHarness.Capture(window, "13-report");
        await ws.ReportPage.GenerateCommand.ExecuteAsync(null);
        Assert.True(File.Exists(pdf));
        Assert.True(new FileInfo(pdf).Length > 50_000);
        Assert.True(ws.ReportGenerated);

        // Save and reopen.
        var project = Path.Combine(UiHarness.Root, "riverside.dhsx");
        main.Ui.SaveFileOverride = _ => project;
        Assert.True(await ws.SaveAsAsync());
        Assert.False(ws.Session.IsDirty);
        await main.OpenPathAsync(project);
        Assert.Equal("Riverside Bridge BH-2", main.Workspace!.ProjectName);
        main.Ui.SaveFileOverride = null;
        window.Close();
    }

    [AvaloniaFact]
    public async Task ActivatesPaidLicenseAndRejectsBadKey()
    {
        var main = UiHarness.Main;
        main.Ui.DialogOverride = d => d.Buttons[0].Text;
        var window = Open(main);
        main.License.IsOpen = true;
        main.License.ShowActivateFormCommand.Execute(null);
        main.License.Email = FakeLicenseServer.Email;
        main.License.LicenseKey = "DHST-0000-0000-0000-0000";
        await main.License.ActivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync();
        Assert.NotNull(main.License.Error);
        UiHarness.Capture(window, "14-activation-error");

        main.License.LicenseKey = FakeLicenseServer.ValidKey.ToLowerInvariant();
        await main.License.ActivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync();
        Assert.Equal(LicenseState.Licensed, main.LicenseManager.Status.State);
        Assert.Equal("Example Geotechnical", main.License.LicensedTo);
        main.OpenSettingsCommand.Execute("License");
        await UiHarness.SettleAsync();
        UiHarness.Capture(window, "15-settings-license");
        main.CloseSettingsCommand.Execute(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CommandPaletteFindsCommands()
    {
        var main = UiHarness.Main;
        var window = Open(main);
        main.OpenPaletteCommand.Execute(null);
        main.Palette.Query = "report";
        await UiHarness.SettleAsync(100);
        Assert.Contains(main.Palette.Results, r => r.Title == "Generate report");
        UiHarness.Capture(window, "16-command-palette");
        main.Palette.CloseCommand.Execute(null);
        main.ShowHelpCommand.Execute("s-picks");
        await UiHarness.SettleAsync(100);
        UiHarness.Capture(window, "17-help");
        main.ToggleHelpCommand.Execute(null);
        window.Close();
    }
}
