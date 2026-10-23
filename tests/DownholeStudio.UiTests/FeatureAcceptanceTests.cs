using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using DownholeStudio.App.Services;
using DownholeStudio.App.ViewModels;
using DownholeStudio.App.ViewModels.Pages;
using DownholeStudio.App.Views;
using DownholeStudio.Core;
using DownholeStudio.Domain;
using DownholeStudio.Domain.Units;
using DownholeStudio.Infrastructure;
using DownholeStudio.Licensing;
using DownholeStudio.Synthetics;
using DownholeStudio.Visualization;
using SkiaSharp;

namespace DownholeStudio.UiTests;

/// <summary>
/// Shared set-up for the feature acceptance tests. The UI tests share one application instance, so every
/// test makes sure a licence is active and opens its own project.
/// </summary>
internal static class Fx
{
    public static string Repo { get; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string Dir(string name)
    {
        var d = Path.Combine(UiHarness.Root, "features", name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Default dialog answers: never save unrelated projects, otherwise the primary button.</summary>
    public static string? Answer(DialogViewModel d) =>
        d.Buttons.FirstOrDefault(b => b.Text == "Don't save")?.Text ?? d.Buttons[0].Text;

    public static async Task<MainWindow> StartAsync(MainViewModel main)
    {
        main.Ui.DialogOverride = Answer;
        main.Ui.SaveFileOverride = null;
        main.Ui.OpenFilesOverride = null;
        main.Ui.OpenFolderOverride = null;
        main.Ui.OpenUrlOverride = _ => { };
        var window = new MainWindow { DataContext = main, Width = 1600, Height = 1000 };
        window.Show();
        await UiHarness.SettleAsync(100);
        await EnsureLicensedAsync(main);
        return window;
    }

    public static async Task EnsureLicensedAsync(MainViewModel main)
    {
        if (main.LicenseManager.Status.CanEdit)
        {
            return;
        }

        await UiHarness.ActivateOfflineAsync(main);
    }

    public static async Task<WorkspaceViewModel> OpenSampleCopyAsync(MainViewModel main, string name)
    {
        var path = Path.Combine(Dir("samples"), name + ".dhsx");
        File.Copy(Path.Combine(Repo, "packaging", "samples", "Downhole Studio Sample.dhsx"), path, overwrite: true);
        await main.OpenPathAsync(path);
        var ws = main.Workspace!;
        Assert.Equal(Path.GetFullPath(path), ws.Session.FilePath);
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.Count > 0);
        return ws;
    }

    /// <summary>New project with a synthetic survey imported from SEG-2 files (depths from the file names).</summary>
    public static async Task<(WorkspaceViewModel Ws, SyntheticSurvey Survey)> ImportSyntheticAsync(MainViewModel main, SyntheticDataset dataset, int depths, string name)
    {
        var survey = SyntheticSurveyGenerator.Generate(SyntheticOptions.For(dataset) with { DepthCount = depths });
        var folder = Dir("seg2-" + name);
        foreach (var f in Directory.GetFiles(folder))
        {
            File.Delete(f);
        }

        SyntheticSurveyGenerator.WriteTo(survey, folder);
        await main.NewProjectCommand.ExecuteAsync(null);
        var ws = main.Workspace!;
        ws.ProjectPage.Name = name;
        ws.Navigate(ws.ImportPage);
        await ws.ImportPage.AddPathsAsync([folder]);
        await ws.ImportPage.ImportCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(300);
        ws.GeometryPage.POffset = survey.Truth.POffset;
        ws.GeometryPage.SOffset = survey.Truth.SOffset;
        await UiHarness.SettleAsync(200);
        return (ws, survey);
    }

    public static async Task<string> SavedPathAsync(WorkspaceViewModel ws, string name)
    {
        var path = Path.Combine(Dir("saved"), name + ".dhsx");
        ws.Main.Ui.SaveFileOverride = _ => path;
        Assert.True(await ws.SaveAsAsync());
        return path;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static byte[] Head(string path, int n) => File.ReadAllBytes(path).Take(n).ToArray();

    public static bool IsPdf(string path) => File.Exists(path) && Head(path, 5).SequenceEqual("%PDF-"u8.ToArray());

    public static bool IsPng(string path) => File.Exists(path) && Head(path, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    public static bool IsZip(string path) => File.Exists(path) && Head(path, 4).SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
}

/// <summary>Application shell, project lifecycle and the Project page.</summary>
public sealed class ShellAndProjectFeatureTests
{
    [AvaloniaFact]
    public async Task ProjectLifecycleSnapshotsScenariosAndFiles()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var dir = Fx.Dir("lifecycle");

        // New, edit, undo/redo.
        await main.NewProjectCommand.ExecuteAsync(null);
        var ws = main.Workspace!;
        Assert.Same(ws.ProjectPage, ws.CurrentPage);
        ws.ProjectPage.Name = "Lifecycle";
        ws.ProjectPage.JobNumber = "JOB-7";
        ws.ProjectPage.SurveyDate = new DateTime(2026, 9, 1);
        Assert.Equal("Lifecycle", ws.State.Metadata.Name);
        Assert.Equal(new DateOnly(2026, 9, 1), ws.State.Metadata.SurveyDate);
        main.UndoCommand.Execute(null);
        Assert.Null(ws.State.Metadata.SurveyDate);
        main.RedoCommand.Execute(null);
        Assert.Equal(new DateOnly(2026, 9, 1), ws.State.Metadata.SurveyDate);

        // Save (first save asks where), Save As, recent list.
        var first = Path.Combine(dir, "lifecycle.dhsx");
        var second = Path.Combine(dir, "lifecycle-2.dhsx");
        main.Ui.SaveFileOverride = _ => first;
        await main.SaveCommand.ExecuteAsync(null);
        Assert.True(File.Exists(first));
        Assert.False(ws.Session.IsDirty);
        main.Ui.SaveFileOverride = _ => second;
        await main.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFullPath(second), ws.Session.FilePath);
        Assert.Contains(main.Catalog.RecentProjects(), r => r.Path == Path.GetFullPath(second));
        ws.ProjectPage.Notes = "edited";
        await ws.ProjectPage.SaveCommand.ExecuteAsync(null);
        Assert.Equal("edited", ProjectSession.Open(second, main.Clock, main.Identity).State.Metadata.Notes);

        // Duplicate: an independent project with a new identity; the current one is unchanged.
        var dup = Path.Combine(dir, "duplicate.dhsx");
        main.Ui.SaveFileOverride = _ => dup;
        await ws.ProjectPage.DuplicateCommand.ExecuteAsync(null);
        var copy = ProjectSession.Open(dup, main.Clock, main.Identity).State;
        Assert.NotEqual(ws.State.Metadata.ProjectId, copy.Metadata.ProjectId);
        Assert.Equal("duplicate", copy.Metadata.Name);
        Assert.Equal("Lifecycle", ws.State.Metadata.Name);

        // Archive (twice to the same file: replacing an earlier archive must work).
        var archive = Path.Combine(dir, "archive.zip");
        main.Ui.SaveFileOverride = _ => archive;
        await ws.ProjectPage.ArchiveCommand.ExecuteAsync(null);
        await ws.ProjectPage.ArchiveCommand.ExecuteAsync(null);
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.Contains(zip.Entries, e => e.Name == "lifecycle-2.dhsx");
            Assert.Contains(zip.Entries, e => e.Name == "audit.json");
            Assert.Contains(zip.Entries, e => e.Name == "results.xlsx");
        }

        // Snapshots: create, restore (undoable), delete.
        ws.ProjectPage.NewSnapshotName = "Before rename";
        ws.ProjectPage.CreateSnapshotCommand.Execute(null);
        await UiHarness.SettleAsync(100);
        Assert.Single(ws.ProjectPage.Snapshots);
        // A snapshot holds the interpretation (processing, picks, layers), not the project details.
        var steps = ws.State.ActiveScenario.VerticalPipeline.Steps.Count;
        ws.ProcessingPage.AddStepCommand.Execute("taper");
        ws.ProjectPage.Name = "Renamed";
        await ws.ProjectPage.RestoreSnapshotCommand.ExecuteAsync(ws.ProjectPage.Snapshots[0]);
        Assert.Equal(steps, ws.State.ActiveScenario.VerticalPipeline.Steps.Count);
        Assert.Equal("Renamed", ws.State.Metadata.Name);
        ws.Undo();
        Assert.Equal(steps + 1, ws.State.ActiveScenario.VerticalPipeline.Steps.Count);
        await UiHarness.SettleAsync(100);
        ws.ProjectPage.DeleteSnapshotCommand.Execute(ws.ProjectPage.Snapshots[0]);
        Assert.Empty(ws.State.Snapshots);

        // Scenarios: create (activated), switch, delete; the last one cannot be deleted.
        ws.ProjectPage.NewScenarioName = "Robust";
        ws.ProjectPage.CreateScenarioCommand.Execute(null);
        Assert.Equal(2, ws.State.Scenarios.Count);
        Assert.Equal("Robust", ws.State.ActiveScenario.Name);
        await UiHarness.SettleAsync(100);
        var original = ws.ProjectPage.Scenarios.First(s => s.Name != "Robust");
        ws.ProjectPage.ActivateScenarioCommand.Execute(original);
        Assert.Equal(original.Id, ws.State.ActiveScenario.Id);
        await UiHarness.SettleAsync(100);
        await ws.ProjectPage.DeleteScenarioCommand.ExecuteAsync(ws.ProjectPage.Scenarios.First(s => s.Name == "Robust"));
        Assert.Single(ws.State.Scenarios);
        await UiHarness.SettleAsync(100);
        await ws.ProjectPage.DeleteScenarioCommand.ExecuteAsync(ws.ProjectPage.Scenarios[0]);
        Assert.Single(ws.State.Scenarios);

        // Audit record.
        var audit = Path.Combine(dir, "audit.json");
        main.Ui.SaveFileOverride = _ => audit;
        await ws.ProjectPage.ExportAuditCommand.ExecuteAsync(null);
        using (var doc = JsonDocument.Parse(File.ReadAllText(audit)))
        {
            Assert.Equal("quakelogic.downhole-studio.audit/1", doc.RootElement.GetProperty("format").GetString());
        }

        // Display units are a project setting; values stay SI internally.
        ws.ProjectPage.UseUsUnits = true;
        Assert.Equal(UnitSystem.USCustomary, ws.State.Metadata.DisplayUnits);
        Assert.Equal("ft", ws.ProjectPage.LengthUnit);
        ws.ProjectPage.UseUsUnits = false;

        // Close (unsaved changes are discarded on request), reopen from the recent list and with Open.
        await main.CloseProjectCommand.ExecuteAsync(null);
        Assert.Null(main.Workspace);
        await main.OpenRecentCommand.ExecuteAsync(Path.GetFullPath(second));
        Assert.Equal("edited", main.Workspace!.State.Metadata.Notes);
        main.Ui.OpenFilesOverride = _ => [first];
        await main.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFullPath(first), main.Workspace!.Session.FilePath);

        // A recent entry whose file has gone can be removed from the list.
        var gone = Path.Combine(dir, "gone.dhsx");
        main.Catalog.TouchRecent(gone, "Gone", main.Clock.Now);
        main.RefreshRecent();
        main.Ui.DialogOverride = d => d.Title == "Project not found" ? "Remove from list" : Fx.Answer(d);
        await main.OpenRecentCommand.ExecuteAsync(gone);
        Assert.DoesNotContain(main.Recent, r => r.Path == gone);
        Assert.Equal(Path.GetFullPath(first), main.Workspace!.Session.FilePath);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GlobalCommandsWork()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "global");

        // Navigation shortcuts.
        main.ImportCommand.Execute(null);
        Assert.Same(ws.ImportPage, ws.CurrentPage);
        main.ExportCommand.Execute(null);
        Assert.Same(ws.ReportPage, ws.CurrentPage);

        // Settings, help, palette, mode, theme.
        main.OpenSettingsCommand.Execute("Units");
        Assert.True(main.IsSettingsOpen);
        Assert.True(main.SettingsPage.IsUnits);
        main.CloseSettingsCommand.Execute(null);
        Assert.False(main.IsSettingsOpen);
        main.ToggleHelpCommand.Execute(null);
        Assert.True(main.IsHelpOpen);
        Assert.Equal("report", main.Help.Current!.Key);
        main.ShowHelpCommand.Execute("engineering");
        Assert.Equal("engineering", main.Help.Current!.Key);
        main.ToggleHelpCommand.Execute(null);
        main.OpenPaletteCommand.Execute(null);
        main.Palette.Query = "velocity";
        await UiHarness.SettleAsync(50);
        var go = main.Palette.Results.First(r => r.Title.Contains("Velocity", StringComparison.OrdinalIgnoreCase));
        main.Palette.RunCommand.Execute(go);
        Assert.False(main.Palette.IsOpen);
        Assert.Same(ws.VelocityPage, ws.CurrentPage);

        var expert = main.IsExpert;
        main.ToggleModeCommand.Execute(null);
        Assert.Equal(!expert, main.IsExpert);
        Assert.Equal(!expert ? UserMode.Expert : UserMode.Guided, main.Settings.Current.Mode);
        main.ToggleModeCommand.Execute(null);

        var before = Application.Current!.ActualThemeVariant;
        main.ToggleThemeCommand.Execute(null);
        await UiHarness.SettleAsync(50);
        Assert.NotEqual(before, Application.Current.ActualThemeVariant);
        Assert.Equal(Application.Current.ActualThemeVariant == ThemeVariant.Dark ? ThemePreference.Dark : ThemePreference.Light, main.Settings.Current.Theme);
        main.SettingsPage.SelectThemeCommand.Execute("1");
        Assert.Equal(ThemePreference.Light, main.Settings.Current.Theme);
        await UiHarness.SettleAsync(50);
        Assert.Equal(ThemeVariant.Light, Application.Current.ActualThemeVariant);

        // Updates (the test server reports no update) and the diagnostic package (twice to one file).
        await main.CheckUpdatesCommand.ExecuteAsync(false);
        Assert.Contains("up to date", main.UpdateMessage);
        var diag = Path.Combine(Fx.Dir("global"), "diagnostics.zip");
        main.Ui.SaveFileOverride = _ => diag;
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Include project file") ? "Include project file" : Fx.Answer(d);
        await main.ExportDiagnosticsCommand.ExecuteAsync(null);
        await main.ExportDiagnosticsCommand.ExecuteAsync(null);
        using (var zip = ZipFile.OpenRead(diag))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "system.json");
            Assert.Contains(zip.Entries, e => e.FullName == "projects/global.dhsx");
        }

        // Generate report from the top bar.
        main.Ui.DialogOverride = Fx.Answer;
        var pdf = Path.Combine(Fx.Dir("global"), "report.pdf");
        main.Ui.SaveFileOverride = _ => pdf;
        main.GenerateReportCommand.Execute(null);
        if (ws.ReportPage.GenerateCommand.ExecutionTask is { } generating)
        {
            await generating;
        }

        await UiHarness.SettleAsync(100);

        Assert.Same(ws.ReportPage, ws.CurrentPage);
        Assert.True(Fx.IsPdf(pdf), $"status: {ws.ReportPage.Status}; messages: {string.Join(" | ", main.Ui.Toasts.Select(t => t.Message))}");

        // Import from the start screen: a new project is created and the file picker opens.
        await main.CloseProjectCommand.ExecuteAsync(null);
        var survey = SyntheticSurveyGenerator.Generate(SyntheticOptions.For(SyntheticDataset.Clean) with { DepthCount = 4 });
        var files = SyntheticSurveyGenerator.WriteTo(survey, Fx.Dir("global-seg2"));
        main.Ui.OpenFilesOverride = _ => files.ToList();
        await main.ImportSurveyCommand.ExecuteAsync(null);
        Assert.NotNull(main.Workspace);
        Assert.Same(main.Workspace!.ImportPage, main.Workspace.CurrentPage);
        Assert.Equal(files.Count, main.Workspace.ImportPage.Files.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SampleProjectOpensFromTheStartScreen()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        await main.CloseProjectCommand.ExecuteAsync(null);
        var samples = Path.Combine(AppContext.BaseDirectory, "Samples");
        var bundled = Path.Combine(samples, "Downhole Studio Sample.dhsx");
        if (File.Exists(bundled))
        {
            File.Delete(bundled);
        }

        // Not installed: a clear error, nothing opened.
        string? error = null;
        main.Ui.DialogOverride = d => { error = d.Title; return d.Buttons[0].Text; };
        await main.OpenSampleCommand.ExecuteAsync(null);
        Assert.Equal("Sample project not found", error);
        Assert.Null(main.Workspace);

        // Installed: a copy in the project folder is opened (the bundled file is never modified).
        Directory.CreateDirectory(samples);
        File.Copy(Path.Combine(Fx.Repo, "packaging", "samples", "Downhole Studio Sample.dhsx"), bundled);
        var hash = Fx.Hash(bundled);
        main.Ui.DialogOverride = Fx.Answer;
        var target = Path.Combine(main.Paths.DefaultProjectFolder, "Downhole Studio Sample.dhsx");
        var existed = File.Exists(target);
        await main.OpenSampleCommand.ExecuteAsync(null);
        Assert.Equal(Path.GetFullPath(target), main.Workspace!.Session.FilePath);
        Assert.Equal(hash, Fx.Hash(bundled));
        await main.CloseProjectCommand.ExecuteAsync(null);
        if (!existed && !target.StartsWith(UiHarness.Root, StringComparison.Ordinal))
        {
            File.Delete(target);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsDefaultsApplyToNewProjects()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var s = main.SettingsPage;
        var saved = main.Settings.Current;
        try
        {
            s.PPickerIndex = 1;
            s.SPickerIndex = 2;
            s.A4Paper = true;
            s.UsUnits = true;
            s.AutoSaveMinutes = 0;
            Assert.Equal(1, main.Settings.Current.AutoSaveMinutes);
            s.AutoSaveMinutes = 90;
            Assert.Equal(60, main.Settings.Current.AutoSaveMinutes);
            s.EditorName = "  Field Engineer ";
            Assert.Equal("Field Engineer", main.Settings.Current.EditorName);

            await main.NewProjectCommand.ExecuteAsync(null);
            var ws = main.Workspace!;
            Assert.Equal(PickAlgorithm.StaLta, ws.State.ActiveScenario.PPicker.Algorithm);
            Assert.Equal(PickAlgorithm.EnergyRatio, ws.State.ActiveScenario.SPicker.Algorithm);
            Assert.Equal(UnitSystem.USCustomary, ws.State.Metadata.DisplayUnits);
            Assert.Equal(DownholeStudio.Reporting.Pdf.PaperSize.A4, ws.ReportPage.Paper.Value);
            Assert.False(ws.Session.CanUndo);
        }
        finally
        {
            s.PPickerIndex = (int)saved.DefaultPPicker - 1;
            s.SPickerIndex = (int)saved.DefaultSPicker - 1;
            s.A4Paper = saved.PaperSize == "A4";
            s.UsUnits = saved.Units == UnitSystem.USCustomary;
            s.AutoSaveMinutes = saved.AutoSaveMinutes;
            s.EditorName = saved.EditorName;
            await main.CloseProjectCommand.ExecuteAsync(null);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UnsavedWorkIsRecoveredAfterACrash()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "recovery");
        ws.ProjectPage.Name = "Unsaved rename";
        var original = ws.Session.FilePath!;
        var hash = Fx.Hash(original);

        // The auto-save timer writes a recovery copy; the project file itself is not touched.
        typeof(MainViewModel).GetMethod("AutoSave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(main, null);
        Assert.NotNull(ws.LastAutoSave);
        Assert.Equal(hash, Fx.Hash(original));
        var marker = Path.Combine(main.Recovery.Directory, $"{ws.Session.SessionId:N}.json");
        Assert.True(File.Exists(marker));

        // Simulate the process having died: the copy becomes an orphan and is offered at the next start.
        var info = JsonSerializer.Deserialize<DownholeStudio.Persistence.RecoveryInfo>(File.ReadAllText(marker), DownholeStudio.Persistence.ProjectJson.Options)!;
        File.WriteAllText(marker, JsonSerializer.Serialize(info with { ProcessId = int.MaxValue - 7 }, DownholeStudio.Persistence.ProjectJson.Options));
        main.Workspace = null;
        string? offered = null;
        main.Ui.DialogOverride = d =>
        {
            if (d.Buttons.Any(b => b.Text == "Recover"))
            {
                offered = d.Message;
                return "Recover";
            }

            return Fx.Answer(d);
        };
        await main.StartupAsync(null);
        Assert.NotNull(offered);
        Assert.Contains("Unsaved rename", offered);
        Assert.Equal("Unsaved rename", main.Workspace!.State.Metadata.Name);
        Assert.Equal(original, main.Workspace.RecoveredFrom);
        Assert.Null(main.Workspace.Session.FilePath);
        Assert.False(File.Exists(marker));
        Assert.Equal(hash, Fx.Hash(original));
        main.Shutdown();
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>Trial, activation, verification, deactivation and the read-only mode without a licence.</summary>
public sealed class LicensingFeatureTests
{
    [AvaloniaFact]
    public async Task LicenceLifecycleAndReadOnlyMode()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);

        await main.License.RefreshOnlineCommand.ExecuteAsync(null);
        Assert.True(main.LicenseManager.Status.CanEdit);
        Assert.NotNull(main.LicenseManager.Status.LastVerifiedAt);
        await main.License.CopyInfoCommand.ExecuteAsync(null);
        if (main.Ui.Clipboard is { } clipboard)
        {
            Assert.Contains("Downhole Studio", await clipboard.TryGetTextAsync());
        }

        var ws = await Fx.OpenSampleCopyAsync(main, "readonly");
        var path = ws.Session.FilePath!;
        var hash = Fx.Hash(path);
        var picks = ws.State.ActiveScenario.PPicks;

        // Deactivate (asks first): the project becomes read-only and is never modified.
        var deactivations = UiHarness.Server.Deactivations;
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Deactivate") ? "Deactivate" : Fx.Answer(d);
        await main.License.DeactivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(200);
        Assert.Equal(deactivations + 1, UiHarness.Server.Deactivations);
        Assert.False(main.CanEdit);
        Assert.True(ws.Session.ReadOnly);

        ws.ProjectPage.Name = "Should not change";
        Assert.NotEqual("Should not change", ws.State.Metadata.Name);
        ws.Navigate(ws.PPicksPage);
        ws.PPicksPage.OverwriteManual = true;
        await ws.PPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        Assert.Same(picks, ws.State.ActiveScenario.PPicks);
        Assert.False(ws.UndoCommand.CanExecute(null));
        Assert.False(await ws.SaveAsync());
        var pdf = Path.Combine(Fx.Dir("readonly"), "report.pdf");
        main.Ui.SaveFileOverride = _ => pdf;
        await ws.ReportPage.GenerateCommand.ExecuteAsync(null);
        Assert.False(File.Exists(pdf));
        var dup = Path.Combine(Fx.Dir("readonly"), "dup.dhsx");
        main.Ui.SaveFileOverride = _ => dup;
        await ws.ProjectPage.DuplicateCommand.ExecuteAsync(null);
        Assert.False(File.Exists(dup));
        await main.NewProjectCommand.ExecuteAsync(null);
        Assert.Same(ws, main.Workspace);
        Assert.True(main.License.IsOpen);
        await main.CloseProjectCommand.ExecuteAsync(null);
        Assert.Equal(hash, Fx.Hash(path));

        // Offline activation (how customers activate): the window shows this computer's code; a key for another
        // computer is refused with an explanation; the right key activates.
        Assert.True(main.License.ShowOfflineActivation);
        Assert.Equal(main.LicenseManager.ComputerCode, main.License.ComputerCode);
        Assert.NotNull(ComputerCode.Normalize(main.License.ComputerCode));
        main.License.ActivationKey = UiHarness.OfflineKey(ComputerCode.FromDeviceHash(new string('a', 64)));
        main.License.ActivateOfflineCommand.Execute(null);
        Assert.Contains("another computer", main.License.Error);
        Assert.Contains(main.License.ComputerCode, main.License.Error);
        Assert.False(main.CanEdit);
        main.License.ActivationKey = "dhs1.garbage";
        main.License.ActivateOfflineCommand.Execute(null);
        Assert.Contains("not valid", main.License.Error);

        // The code can be copied or e-mailed to QuakeLogic.
        await main.License.CopyComputerCodeCommand.ExecuteAsync(null);
        string? mail = null;
        main.Ui.OpenUrlOverride = u => mail = u;
        main.License.EmailComputerCodeCommand.Execute(null);
        Assert.StartsWith("mailto:sales@quakelogic.net", mail);
        Assert.Contains(main.License.ComputerCode, Uri.UnescapeDataString(mail!));

        await UiHarness.ActivateOfflineAsync(main);
        Assert.Equal(LicenseState.Licensed, main.LicenseManager.Status.State);
        Assert.True(main.License.IsOfflineActivation);
        Assert.False(main.License.CanVerifyOnline);
        Assert.Equal("Remove Activation from This Computer", main.License.DeactivateLabel);
        Assert.Equal("Example Geotechnical", main.License.LicensedTo);

        // Removing the activation is local; the activation file (.dhslic) from the e-mail activates again.
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Deactivate") ? "Deactivate" : Fx.Answer(d);
        await main.License.DeactivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(100);
        main.Ui.DialogOverride = Fx.Answer;
        Assert.Equal(LicenseState.NotActivated, main.LicenseManager.Status.State);
        var activationFile = Path.Combine(Fx.Dir("activation"), "DownholeStudio-activation.dhslic");
        File.WriteAllText(activationFile, UiHarness.OfflineKey() + Environment.NewLine);
        main.Ui.OpenFilesOverride = _ => [activationFile];
        await main.License.OpenActivationFileCommand.ExecuteAsync(null);
        main.Ui.OpenFilesOverride = null;
        Assert.Equal(LicenseState.Licensed, main.LicenseManager.Status.State);
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Deactivate") ? "Deactivate" : Fx.Answer(d);
        await main.License.DeactivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(100);
        main.Ui.DialogOverride = Fx.Answer;

        // Online activation, available when a licensing server is configured: a wrong key is rejected with an
        // explanation; the right key activates.
        Assert.True(main.License.OnlineAvailable);
        Assert.True(main.License.ShowActivateLink);
        main.License.ShowActivateFormCommand.Execute(null);
        main.License.Email = FakeLicenseServer.Email;
        main.License.LicenseKey = "DHST-0000-0000-0000-0000";
        await main.License.ActivateCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(main.License.Error));
        Assert.False(main.CanEdit);
        main.License.LicenseKey = "not a key";
        await main.License.ActivateCommand.ExecuteAsync(null);
        Assert.Contains("DHST-", main.License.Error);
        main.License.LicenseKey = FakeLicenseServer.ValidKey;
        await main.License.ActivateCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(100);
        Assert.Equal(LicenseState.Licensed, main.LicenseManager.Status.State);
        Assert.False(main.License.IsOfflineActivation);
        Assert.Equal("Example Geotechnical", main.License.LicensedTo);

        // Opened again with a licence the project is editable.
        await main.OpenPathAsync(path);
        Assert.False(main.Workspace!.Session.ReadOnly);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>Import, geometry and channel mapping.</summary>
public sealed class ImportAndGeometryFeatureTests
{
    [AvaloniaFact]
    public async Task ImportChecksFilesAndAppliesOnlyApprovedCorrections()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var survey = SyntheticSurveyGenerator.Generate(SyntheticOptions.For(SyntheticDataset.MissingTraces) with { DepthCount = 8 });
        var folder = Fx.Dir("import-problems");
        foreach (var f in Directory.GetFiles(folder))
        {
            File.Delete(f);
        }

        var written = SyntheticSurveyGenerator.WriteTo(survey, folder);
        File.Copy(written[0], Path.Combine(folder, "ZZ_copy_of_first.sg2"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not seismic data");
        await main.NewProjectCommand.ExecuteAsync(null);
        var page = main.Workspace!.ImportPage;
        var ws = main.Workspace!;

        // Folder: every SEG-2 file is inspected; problems are reported, nothing is imported yet.
        main.Ui.OpenFolderOverride = _ => folder;
        await page.AddFolderCommand.ExecuteAsync(null);
        Assert.Equal(written.Count + 1, page.Files.Count);
        Assert.True(page.HasDuplicates);
        Assert.True(page.WarningCount + page.ErrorCount > 0);
        Assert.Empty(ws.State.Survey.Records);

        // Removing a file takes it out of the import and out of the checks.
        await page.RemoveFileCommand.ExecuteAsync(page.Files.First(f => f.FileName == "ZZ_copy_of_first.sg2"));
        Assert.Equal(written.Count, page.Files.Count);
        Assert.False(page.HasDuplicates);
        page.ClearCommand.Execute(null);
        Assert.Empty(page.Files);
        Assert.False(page.ImportCommand.CanExecute(null));

        // Files: duplicates skipped (approved correction), one file excluded by the user.
        main.Ui.OpenFilesOverride = _ => Directory.GetFiles(folder, "*.sg2").ToList();
        await page.AddFilesCommand.ExecuteAsync(null);
        Assert.True(page.HasDuplicates);
        Assert.True(page.SkipDuplicates);
        var excluded = page.Files.First(f => f.FileName != "ZZ_copy_of_first.sg2" && f.FileName != Path.GetFileName(written[0]));
        excluded.Include = false;
        await page.ImportCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(200);
        Assert.Equal(written.Count - 1, ws.State.Survey.Records.Count);
        Assert.DoesNotContain(ws.State.Survey.Records, r => r.Source.FileName == excluded.FileName);
        Assert.Single(ws.State.Survey.Records, r => r.Source.Sha256 == ws.State.Survey.Records.First(x => x.Source.FileName == Path.GetFileName(written[0])).Source.Sha256);
        Assert.Same(ws.GeometryPage, ws.CurrentPage);

        // Originals are kept byte-for-byte.
        var kept = written.First(w => Path.GetFileName(w) != excluded.FileName && w != written[0]);
        var rec = ws.State.Survey.Records.First(r => r.Source.FileName == Path.GetFileName(kept));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(kept))), rec.Source.Sha256);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task GeometryDepthsAndChannels()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var (ws, survey) = await Fx.ImportSyntheticAsync(main, SyntheticDataset.Clean, 10, "geometry");
        var g = ws.GeometryPage;
        ws.Navigate(g);
        Assert.Equal(10, ws.State.Survey.Stations.Count);
        Assert.Equal(survey.Truth.SOffset, ws.State.Survey.Geometry.SSource.HorizontalOffset);

        g.POffset = 3.5;
        g.PSourceDepth = 0.2;
        g.BoreholeName = "BH-9";
        g.TotalDepth = 12;
        Assert.Equal(3.5, ws.State.Survey.Geometry.PSource.HorizontalOffset);
        Assert.Equal("BH-9", ws.State.Survey.Geometry.Borehole.Name);
        Assert.NotNull(g.Diagram);
        Assert.Equal(10, g.Diagram!.Receivers.Count);

        // Collar reference: depths below ground = measured − collar height.
        g.CollarHeight = 0.5;
        g.DepthReference = GeometryPageViewModel.ReferenceChoices[1];
        var station = ws.State.Survey.StationsByDepth()[3];
        Assert.Equal(station.MeasuredDepth - 0.5, ws.State.Survey.Geometry.DepthBelowGround(station.MeasuredDepth), 9);
        g.DepthReference = GeometryPageViewModel.ReferenceChoices[0];

        // Mirror (wrong direction), undo, assign by pattern, and back to the file suggestions.
        var firstRecord = ws.State.Survey.Records.OrderBy(r => r.Sequence).First();
        double DepthOf(Guid recordId) => ws.State.Survey.FindStation(ws.State.Survey.FindRecord(recordId)!.StationId!.Value)!.MeasuredDepth;
        var d0 = DepthOf(firstRecord.Id);
        g.MirrorDepthsCommand.Execute(null);
        Assert.Equal(ws.State.Survey.Stations.Max(s => s.MeasuredDepth), DepthOf(firstRecord.Id), 9);
        ws.Undo();
        Assert.Equal(d0, DepthOf(firstRecord.Id), 9);
        g.FirstDepth = 2;
        g.Increment = 2;
        g.AssignDirection = GeometryPageViewModel.DirectionChoices[0];
        g.Pattern = GeometryPageViewModel.PatternChoices[0];
        g.AssignByPatternCommand.Execute(null);
        Assert.Equal(2, DepthOf(firstRecord.Id), 9);
        Assert.Equal(20, ws.State.Survey.Stations.Max(s => s.MeasuredDepth), 9);
        Assert.True(g.HasSuggestions);
        g.ApplySuggestionsCommand.Execute(null);
        Assert.Equal(d0, DepthOf(firstRecord.Id), 9);

        // One record by hand, and excluded.
        var row = g.Records.First(r => r.Record.Id == firstRecord.Id);
        g.AssignRecord(row, 25, ShotKind.SNegative);
        Assert.Equal(25, DepthOf(firstRecord.Id), 9);
        Assert.Equal(ShotKind.SNegative, ws.State.Survey.FindRecord(firstRecord.Id)!.Shot);
        g.SetRecordExcluded(g.Records.First(r => r.Record.Id == firstRecord.Id), true);
        Assert.True(ws.State.Survey.FindRecord(firstRecord.Id)!.Excluded);

        // Channel map: change a role, save as a profile, apply the built-in profile.
        var ref4 = g.Channels.First(c => c.Channel == 4);
        ref4.Role = GeometryPageViewModel.RoleChoices[0];
        g.CommitChannels();
        Assert.Equal(ComponentRole.Unassigned, ws.State.Survey.ChannelMap.RoleOf(4));
        g.Channels.First(c => c.Channel == 4).Role = GeometryPageViewModel.RoleChoices.First(r => r.Value == ComponentRole.Reference);
        g.CommitChannels();
        g.NewProfileName = "Test rig " + Guid.NewGuid().ToString("N")[..6];
        var name = g.NewProfileName;
        g.SaveProfileCommand.Execute(null);
        Assert.Contains(g.Profiles, p => p.Profile.Name == name);
        g.SelectedProfile = g.Profiles.First(p => p.Profile.BuiltIn && p.Profile.Name.Contains("QuakeLogic 3C", StringComparison.Ordinal));
        g.ApplyProfileCommand.Execute(null);
        Assert.Equal(g.SelectedProfile?.Profile.Name ?? ws.State.Survey.ChannelMap.ProfileName, ws.State.Survey.ChannelMap.ProfileName);
        Assert.Equal(ComponentRole.Vertical, ws.State.Survey.ChannelMap.RoleOf(1));
        Assert.Equal(ComponentRole.Reference, ws.State.Survey.ChannelMap.RoleOf(4));
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>Waveform viewer, trace adjustments and the processing chain.</summary>
public sealed class WaveformAndProcessingFeatureTests
{
    [AvaloniaFact]
    public async Task WaveformDisplayAndTraceAdjustments()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "waveforms");
        var page = ws.WaveformsPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        Assert.Equal(32, page.Section!.Traces.Count);

        foreach (var mode in WaveformsPageViewModel.ModeOptions)
        {
            page.Mode = mode;
            Assert.Equal(mode.Value, page.Section!.Mode);
        }

        var processed = page.Section!.Traces[5].Samples.ToArray();
        page.ComparisonMode = WaveformsPageViewModel.ComparisonOptions[1];
        var raw = page.Section!.Traces[5].Samples;
        Assert.NotEqual(processed, raw);
        page.ComparisonMode = WaveformsPageViewModel.ComparisonOptions[2];
        Assert.NotNull(page.Section!.Traces[5].Overlay);
        page.ComparisonMode = WaveformsPageViewModel.ComparisonOptions[0];
        page.Overlay = true;
        Assert.True(page.Section!.OverlayMode);
        page.Overlay = false;
        page.DepthScale = true;
        Assert.True(page.Section!.SpaceByPosition);
        page.DepthScale = false;
        page.NormalizeEach = false;
        Assert.False(page.Section!.NormalizeEach);
        page.NormalizeEach = true;
        foreach (var shot in WaveformsPageViewModel.ShotOptions)
        {
            page.Shot = shot;
            Assert.Equal(32, page.Section!.Traces.Count);
        }

        foreach (var component in WaveformsPageViewModel.ComponentOptions)
        {
            page.Component = component;
            Assert.NotEmpty(page.Section!.Traces);
        }

        page.Shot = WaveformsPageViewModel.ShotOptions[0];
        page.Component = WaveformsPageViewModel.ComponentOptions[0];
        page.AdjustGain(2);
        Assert.Equal(2, page.Section!.GlobalGain);
        page.ResetGainCommand.Execute(null);
        Assert.Equal(1, page.Gain);

        // Trace inspector: gain, polarity, static shift and exclusion are recorded per trace and undoable.
        var key = page.Section!.Traces[10].Key;
        page.SelectTrace(key);
        await UiHarness.SettleAsync(100);
        Assert.NotNull(page.SelectedTrace);
        Assert.NotNull(page.Spectrum);
        Assert.Contains("Dominant frequency", page.SpectrumSummary);
        Assert.NotEmpty(page.HoverLines(10, 0.02));
        var traceId = (Guid)key;
        page.SelectedTrace!.Gain = 2.5;
        page.SelectedTrace!.ReversePolarity = true;
        page.SelectedTrace!.TimeShiftMs = 1.5;
        page.SelectedTrace!.Excluded = true;
        var adj = ws.State.ActiveScenario.AdjustmentFor(traceId);
        Assert.Equal(2.5, adj.Gain);
        Assert.True(adj.ReversePolarity);
        Assert.Equal(0.0015, adj.TimeShift, 12);
        Assert.True(adj.Excluded);
        await UiHarness.SettleAsync(100);
        Assert.True(page.Section!.Traces.First(t => Equals(t.Key, key)).Excluded);
        for (var i = 0; i < 4; i++)
        {
            ws.Undo();
        }

        adj = ws.State.ActiveScenario.AdjustmentFor(traceId);
        Assert.Equal(1, adj.Gain);
        Assert.False(adj.ReversePolarity || adj.Excluded);
        Assert.Equal(0, adj.TimeShift);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ProcessingChainEveryStepAndTriggerCorrection()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "processing");
        var page = ws.ProcessingPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        var builder = ws.Analysis.Builder();
        var someTrace = ws.State.Survey.Records.First().Traces[0].Id;
        var rawBefore = builder.Raw(someTrace).Samples.ToArray();

        page.Group = ProcessingPageViewModel.GroupOptions[0];
        var kinds = new (string Kind, Type Step)[]
        {
            ("removeMean", typeof(RemoveMeanStep)), ("detrend", typeof(LinearDetrendStep)), ("taper", typeof(TaperStep)), ("bandPass", typeof(BandPassStep)),
            ("highPass", typeof(HighPassStep)), ("lowPass", typeof(LowPassStep)), ("notch", typeof(NotchStep)), ("normalize", typeof(NormalizeStep)),
            ("balance", typeof(BalanceStep)), ("agc", typeof(AgcStep)), ("polarity", typeof(PolarityStep)), ("timeShift", typeof(TimeShiftStep)), ("resample", typeof(ResampleStep)),
        };
        foreach (var (kind, type) in kinds)
        {
            var count = ws.State.ActiveScenario.VerticalPipeline.Steps.Count;
            page.AddStepCommand.Execute(kind);
            Assert.Equal(count + 1, ws.State.ActiveScenario.VerticalPipeline.Steps.Count);
            Assert.IsType(type, ws.State.ActiveScenario.VerticalPipeline.Steps[^1]);
            Assert.NotNull(page.SelectedStep);
            Assert.NotNull(page.Editor);
            var processed = ws.Analysis.Builder().Process(someTrace);
            Assert.All(processed.Samples, v => Assert.True(double.IsFinite(v), $"{kind} produced a non-finite sample"));
        }

        // Raw data is never modified by processing.
        Assert.Equal(rawBefore, ws.Analysis.Builder().Raw(someTrace).Samples);

        // Edit parameters in the inspector.
        page.SelectedStep = page.Steps.First(s => s.Step is BandPassStep);
        page.Editor!.Low = 15;
        page.Editor!.High = 300;
        page.Editor!.Order = 6;
        page.Editor!.ZeroPhase = false;
        var bp = ws.State.ActiveScenario.VerticalPipeline.Steps.OfType<BandPassStep>().Single();
        Assert.Equal((15d, 300d, 6, false), (bp.LowHz, bp.HighHz, bp.Order, bp.ZeroPhase));
        Assert.NotNull(page.Response);
        page.SelectedStep = page.Steps.First(s => s.Step is NotchStep);
        page.Editor!.Low = 50;
        page.Editor!.Q = 20;
        Assert.Equal(20, ws.State.ActiveScenario.VerticalPipeline.Steps.OfType<NotchStep>().Single().QualityFactor);
        page.SelectedStep = page.Steps.First(s => s.Step is TaperStep);
        page.Editor!.Value = 10;
        Assert.Equal(0.1, ws.State.ActiveScenario.VerticalPipeline.Steps.OfType<TaperStep>().Single().Fraction, 9);
        page.SelectedStep = page.Steps.First(s => s.Step is NormalizeStep);
        page.Editor!.Rms = true;
        Assert.Equal(NormalizationMode.Rms, ws.State.ActiveScenario.VerticalPipeline.Steps.OfType<NormalizeStep>().Single().Mode);

        // Enable/disable, reorder, remove.
        var agc = page.Steps.First(s => s.Step is AgcStep);
        agc.Enabled = false;
        Assert.False(ws.State.ActiveScenario.VerticalPipeline.Steps.OfType<AgcStep>().Single().Enabled);
        var index = ws.State.ActiveScenario.VerticalPipeline.Steps.ToList().FindIndex(s => s is AgcStep);
        page.MoveUpCommand.Execute(page.Steps.First(s => s.Step is AgcStep));
        Assert.Equal(index - 1, ws.State.ActiveScenario.VerticalPipeline.Steps.ToList().FindIndex(s => s is AgcStep));
        page.MoveDownCommand.Execute(page.Steps.First(s => s.Step is AgcStep));
        Assert.Equal(index, ws.State.ActiveScenario.VerticalPipeline.Steps.ToList().FindIndex(s => s is AgcStep));
        foreach (var row in page.Steps.Where(s => s.Step is not RemoveMeanStep).ToList())
        {
            page.RemoveStepCommand.Execute(row);
        }

        Assert.All(ws.State.ActiveScenario.VerticalPipeline.Steps, s => Assert.IsType<RemoveMeanStep>(s));

        // Suggested band-pass is only added on request.
        Assert.True(page.HasSuggestion);
        var before = ws.State.ActiveScenario.VerticalPipeline.Steps.Count;
        page.AddSuggestionCommand.Execute(null);
        Assert.Equal(before + 1, ws.State.ActiveScenario.VerticalPipeline.Steps.Count);
        page.Group = ProcessingPageViewModel.GroupOptions[1];
        Assert.Contains("S-shot", page.Suggestion);
        page.Group = ProcessingPageViewModel.GroupOptions[0];

        // Stacking and trigger correction.
        page.StackRepeated = !ws.State.ActiveScenario.StackRepeatedShots;
        Assert.Equal(page.StackRepeated, ws.State.ActiveScenario.StackRepeatedShots);
        page.SelectedTab = 1;
        await page.AutoPickReferencesCommand.ExecuteAsync(null);
        Assert.Equal(ws.State.Survey.Records.Count, ws.State.ActiveScenario.ReferencePicks.Values.Count(p => p.Time is not null));
        Assert.Contains("reference onset", page.ReferenceSummary);
        page.SelectedReference = page.References[3];
        page.ClearReferencePickCommand.Execute(null);
        Assert.Null(ws.State.ActiveScenario.ReferencePicks.GetValueOrDefault(page.References[3].RecordId)?.Time);
        page.SetReferencePick(0.002);
        Assert.Equal(PickOrigin.Manual, ws.State.ActiveScenario.ReferencePicks[page.References[3].RecordId].Origin);
        foreach (var mode in ProcessingPageViewModel.TriggerModes)
        {
            page.TriggerMode = mode;
            Assert.Equal(mode.Value, ws.State.ActiveScenario.Trigger.Mode);
            bool Applied(Analysis.AnalysisResult r) => r.S.Observations.Where(o => o.Time is not null)
                .All(o => mode.Value == TriggerCorrectionMode.None ? o.TriggerCorrection == 0 && o.Time == o.RawTime : o.TriggerCorrection is not null);
            await UiHarness.WaitForResultAsync(ws, Applied);
            Assert.True(Applied(ws.Result!), mode.Label);
            Assert.All(ws.Result!.S.Observations.Where(o => o.Time is not null), o => Assert.NotNull(o.RawTime));
        }

        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>P and S picking, rotation and correlation.</summary>
public sealed class PickingFeatureTests
{
    [AvaloniaFact]
    public async Task PPickingEveryAlgorithmAndReviewTools()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "ppicks");
        var page = ws.PPicksPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        var stations = ws.State.Survey.StationsByDepth();
        var reviewed = ws.State.ActiveScenario.PPicks;

        foreach (var algorithm in PickingPageViewModel.AlgorithmOptions)
        {
            page.Algorithm = algorithm;
            Assert.Equal(algorithm.Value, ws.State.ActiveScenario.PPicker.Algorithm);
            page.OverwriteManual = true;
            await page.RunAutoPickCommand.ExecuteAsync(null);
            var picks = ws.State.ActiveScenario.PPicks;
            Assert.Equal(32, picks.Values.Count(p => p.Time is not null && p.Origin == PickOrigin.Automatic && p.Algorithm == algorithm.Value));
            // Automatic picks agree with the reviewed sample picks to within a fraction of a millisecond.
            var differences = stations.Select(s => Math.Abs(picks[s.Id].Time!.Value - reviewed[s.Id].Time!.Value)).ToList();
            Assert.True(differences.Average() < 0.0015, $"{algorithm.Value}: mean difference {differences.Average() * 1000:0.000} ms");
        }

        page.Algorithm = PickingPageViewModel.AlgorithmOptions[0];
        page.OverwriteManual = true;
        await page.RunAutoPickCommand.ExecuteAsync(null);

        // Manual pick, snapped to a sample; re-running the picker keeps it and shows the suggestion.
        var st = stations[8];
        page.TraceClicked(st.Id, 0.0151234, selectOnly: false);
        var manual = ws.State.ActiveScenario.PPicks[st.Id];
        Assert.Equal(PickOrigin.Manual, manual.Origin);
        var dt = ws.State.Survey.RecordsAt(st.Id, ShotKind.P).First().Traces[0].SampleInterval;
        Assert.Equal(0, Math.IEEERemainder(manual.Time!.Value - ws.State.Survey.RecordsAt(st.Id, ShotKind.P).First().Traces[0].Delay, dt), 9);
        page.OverwriteManual = false;
        page.Scope = PickingPageViewModel.ScopeOptions[0];
        await page.RunAutoPickCommand.ExecuteAsync(null);
        Assert.Equal(manual.Time, ws.State.ActiveScenario.PPicks[st.Id].Time);
        Assert.NotNull(ws.State.ActiveScenario.PPicks[st.Id].Suggestion);

        // Keyboard nudges, accept suggestion, restore automatic, delete, include/exclude.
        page.Nudge(1);
        Assert.Equal(manual.Time!.Value + dt, ws.State.ActiveScenario.PPicks[st.Id].Time!.Value, 9);
        page.Nudge(-10);
        Assert.Equal(manual.Time!.Value - 9 * dt, ws.State.ActiveScenario.PPicks[st.Id].Time!.Value, 9);
        page.AcceptSuggestion();
        Assert.Equal(PickOrigin.AcceptedSuggestion, ws.State.ActiveScenario.PPicks[st.Id].Origin);
        Assert.Equal(ws.State.ActiveScenario.PPicks[st.Id].Suggestion!.Time, ws.State.ActiveScenario.PPicks[st.Id].Time);
        page.TraceClicked(st.Id, 0.016, selectOnly: false);
        page.PickInspector!.RestoreCommand.Execute(null);
        Assert.Equal(PickOrigin.Automatic, ws.State.ActiveScenario.PPicks[st.Id].Origin);
        page.PickInspector!.ToggleIncludeCommand.Execute(null);
        Assert.False(ws.State.ActiveScenario.PPicks[st.Id].Included);
        await UiHarness.WaitForResultAsync(ws, r => r.P.Observations.First(o => o.StationId == st.Id).Included == false);
        page.PickInspector!.ToggleIncludeCommand.Execute(null);
        page.PickInspector!.DeleteCommand.Execute(null);
        Assert.Null(ws.State.ActiveScenario.PPicks.GetValueOrDefault(st.Id)?.Time);
        page.Scope = PickingPageViewModel.ScopeOptions[1];
        await page.RunAutoPickCommand.ExecuteAsync(null);
        Assert.NotNull(ws.State.ActiveScenario.PPicks[st.Id].Time);
        page.SelectAdjacent(1);
        Assert.Equal(stations[9].Id, ws.SelectedStationId);
        page.Scope = PickingPageViewModel.ScopeOptions[2];
        await page.RunAutoPickCommand.ExecuteAsync(null);
        Assert.Equal(PickOrigin.Automatic, ws.State.ActiveScenario.PPicks[stations[9].Id].Origin);

        // The table accepts a typed time; display rounding never rewrites a pick.
        await UiHarness.SettleAsync(200);
        var row = page.Rows.First(r => r.StationId == stations[2].Id);
        row.PickText = "9.5";
        Assert.Equal(PickOrigin.Manual, ws.State.ActiveScenario.PPicks[stations[2].Id].Origin);
        Assert.Equal(0.0095, ws.State.ActiveScenario.PPicks[stations[2].Id].Time!.Value, 4);

        // Mark all automatic picks reviewed.
        await page.AcceptAllCommand.ExecuteAsync(null);
        Assert.DoesNotContain(ws.State.ActiveScenario.PPicks.Values, p => p.Origin == PickOrigin.Automatic && p.Time is not null);
        page.ToggleAdvancedCommand.Execute(null);
        Assert.True(page.ShowAdvanced);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SWaveRotationCorrelationAndPickTrace()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "spicks");
        var page = ws.SPicksPage;
        var stations = ws.State.Survey.StationsByDepth();
        ws.SelectedStationId = stations[12].Id;
        ws.Navigate(page);
        await UiHarness.SettleAsync(300);
        Assert.NotNull(page.Detail);
        Assert.Equal(3, page.Detail!.Traces.Count);

        page.AnalyzeRotationCommand.Execute(null);
        Assert.Contains("Maximum horizontal energy", page.RotationSummary);
        Assert.NotNull(page.Hodogram);
        page.RotationAngle = 12.5;
        page.ApplyRotationCommand.Execute(null);
        Assert.Equal(12.5, ws.State.ActiveScenario.SWave.StationRotation[stations[12].Id]);
        page.ClearRotationCommand.Execute(null);
        Assert.False(ws.State.ActiveScenario.SWave.StationRotation.ContainsKey(stations[12].Id));
        page.RotationAngle = 5;
        page.ApplyRotationToAllCommand.Execute(null);
        Assert.Equal(SComponentMode.Rotated, ws.State.ActiveScenario.SWave.Component);
        Assert.Equal(32, ws.State.ActiveScenario.SWave.StationRotation.Count);
        ws.Undo();
        Assert.Empty(ws.State.ActiveScenario.SWave.StationRotation);

        // Opposite shots are reversed copies at the shear arrival.
        page.CorrelateOppositeCommand.Execute(null);
        Assert.Contains("reversed copies", page.CorrelationSummary);
        Assert.NotNull(page.CorrelationPlot);

        // Adjacent-depth correlation proposes an arrival close to the reviewed pick.
        var reviewed = ws.State.ActiveScenario.SPicks[stations[12].Id].Time!.Value;
        page.CorrelateAdjacentCommand.Execute(null);
        Assert.Contains("Proposed S arrival", page.CorrelationSummary);
        page.UseCorrelationPickCommand.Execute(null);
        var xc = ws.State.ActiveScenario.SPicks[stations[12].Id];
        Assert.Equal(PickOrigin.CrossCorrelation, xc.Origin);
        Assert.True(Math.Abs(xc.Time!.Value - reviewed) < 0.0025, $"correlation pick {xc.Time * 1000:0.000} ms vs reviewed {reviewed * 1000:0.000} ms");

        // Choosing another depth discards the correlation; it cannot be applied to the wrong depth.
        ws.SelectedStationId = stations[13].Id;
        await UiHarness.SettleAsync(100);
        var other = ws.State.ActiveScenario.SPicks[stations[13].Id];
        page.UseCorrelationPickCommand.Execute(null);
        Assert.Equal(other, ws.State.ActiveScenario.SPicks[stations[13].Id]);

        // Pick trace and every picker on S.
        foreach (var option in SPickingPageViewModel.PickTraceOptions)
        {
            page.PickTrace = option;
            Assert.Equal(option.Value, ws.State.ActiveScenario.SWave.PickTrace);
        }

        page.PickTrace = SPickingPageViewModel.PickTraceOptions[0];
        foreach (var algorithm in PickingPageViewModel.AlgorithmOptions)
        {
            page.Algorithm = algorithm;
            page.OverwriteManual = true;
            page.Scope = PickingPageViewModel.ScopeOptions[0];
            await page.RunAutoPickCommand.ExecuteAsync(null);
            Assert.True(ws.State.ActiveScenario.SPicks.Values.Count(p => p.Time is not null && p.Algorithm == algorithm.Value) >= 30, algorithm.Label);
        }

        page.ComponentMode = SPickingPageViewModel.ComponentOptions[1];
        Assert.Equal(SComponentMode.HorizontalY, ws.State.ActiveScenario.SWave.Component);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>Velocity model, layers, engineering parameters and Vs30.</summary>
public sealed class ModelFeatureTests
{
    [AvaloniaFact]
    public async Task VelocityMethodsAndLayerEditing()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "velocity");
        var page = ws.VelocityPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        const double trueVs30 = 367.6;

        foreach (var method in VelocityPageViewModel.MethodOptions)
        {
            foreach (var regression in VelocityPageViewModel.RegressionOptions)
            {
                page.Method = method;
                page.Regression = regression;
                await UiHarness.WaitForResultAsync(ws, r => r.Method == method.Value && ws.State.ActiveScenario.Analysis.Regression == regression.Value);
                var r = ws.Result!;
                Assert.NotNull(page.TravelTime);
                Assert.NotNull(page.Profile);
                Assert.Equal(5, page.Layers.Count);
                if (method.Value is VelocityMethod.LayerRegression or VelocityMethod.RefractedRay)
                {
                    Assert.All(r.LayerResults, l => Assert.NotNull(l.S?.Velocity));
                    Assert.InRange(r.Vs30.Value!.Value / trueVs30, 0.97, 1.03);
                }
            }
        }

        page.Method = VelocityPageViewModel.MethodOptions[1];
        page.Regression = VelocityPageViewModel.RegressionOptions[0];
        page.HuberK = 2;
        Assert.Equal(2, ws.State.ActiveScenario.Analysis.HuberThreshold);

        // Add, move, split, merge and delete boundaries.
        page.NewBoundary = 11;
        page.AddBoundaryCommand.Execute(null);
        Assert.Equal(6, ws.State.ActiveScenario.Layers.Layers.Count);
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.Count == 6);
        var added = ws.Result!.LayerResults.First(l => Math.Abs(l.Layer.Top - 11) < 1e-9);
        page.SelectLayerCommand.Execute(page.Layers.First(l => l.Id == added.Layer.Id));
        await UiHarness.SettleAsync(100);
        Assert.Equal(added.Layer.Id, ws.SelectedLayerId);
        Assert.NotNull(page.LayerInspector);
        page.LayerInspector!.Top = 12;
        Assert.Contains(ws.State.ActiveScenario.Layers.Layers, l => Math.Abs(l.TopDepth - 12) < 1e-9);
        page.LayerInspector!.DeleteCommand.Execute(null);
        Assert.Equal(5, ws.State.ActiveScenario.Layers.Layers.Count);
        var target = ws.State.ActiveScenario.Layers.Layers[2].Id;
        page.SplitLayer(target);
        Assert.Equal(6, ws.State.ActiveScenario.Layers.Layers.Count);
        page.MergeLayer(ws.State.ActiveScenario.Layers.Layers[3].Id);
        Assert.Equal(5, ws.State.ActiveScenario.Layers.Layers.Count);
        page.AddBoundaryAt(6.04);
        Assert.Contains(ws.State.ActiveScenario.Layers.Layers, l => Math.Abs(l.TopDepth - 6.04) < 1e-9);
        page.MoveBoundary(ws.State.ActiveScenario.Layers.Layers.First(l => Math.Abs(l.TopDepth - 6.04) < 1e-9).Id, 6.5, completed: false);
        page.MoveBoundary(ws.State.ActiveScenario.Layers.Layers.First(l => Math.Abs(l.TopDepth - 6.5) < 1e-9).Id, 6.8, completed: true);
        Assert.Contains(ws.State.ActiveScenario.Layers.Layers, l => Math.Abs(l.TopDepth - 6.8) < 1e-9);
        ws.Undo();
        Assert.DoesNotContain(ws.State.ActiveScenario.Layers.Layers, l => Math.Abs(l.TopDepth - 6.8) < 1e-9 || Math.Abs(l.TopDepth - 6.5) < 1e-9);
        ws.Undo();
        Assert.Equal(5, ws.State.ActiveScenario.Layers.Layers.Count);

        // Suggestions: never applied without the user.
        page.SuggestCommand.Execute(null);
        Assert.True(page.HasSuggestions);
        var layersBefore = ws.State.ActiveScenario.Layers;
        page.DismissSuggestionsCommand.Execute(null);
        Assert.False(page.HasSuggestions);
        Assert.Same(layersBefore, ws.State.ActiveScenario.Layers);
        page.SuggestCommand.Execute(null);
        var first = page.Suggestions[0];
        page.ApplySuggestionCommand.Execute(first);
        Assert.Contains(ws.State.ActiveScenario.Layers.Layers, l => Math.Abs(l.TopDepth - first.Depth) < 1e-9);
        page.ApplyAllSuggestionsCommand.Execute(null);
        Assert.False(page.HasSuggestions);

        // Clicking a point selects its depth and layer; shift-click excludes it from the fit (still visible).
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.Count == ws.State.ActiveScenario.Layers.Layers.Count);
        var st = ws.State.Survey.StationsByDepth()[20];
        page.PlotPointClicked((st.Id, WaveType.S));
        Assert.Equal(st.Id, ws.SelectedStationId);
        Assert.NotNull(ws.SelectedLayerId);
        page.TogglePointExcluded((st.Id, WaveType.S));
        Assert.False(ws.State.ActiveScenario.SPicks[st.Id].Included);
        await UiHarness.WaitForResultAsync(ws, r => r.S.Observations.First(o => o.StationId == st.Id).Included == false);
        Assert.Contains(ws.Result!.S.Observations, o => o.StationId == st.Id);
        page.TogglePointExcluded((st.Id, WaveType.S));
        Assert.True(ws.State.ActiveScenario.SPicks[st.Id].Included);

        foreach (var kind in VelocityPageViewModel.ProfileOptions)
        {
            page.ProfileKind = kind;
            Assert.NotNull(page.Profile);
        }

        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EngineeringDensitySourcesIntervalsAndUnits()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "engineering");
        var page = ws.EngineeringPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        Assert.All(ws.Result!.LayerResults, l => Assert.Equal(DensitySource.Measured, l.Density.Source));
        Assert.All(ws.Result!.LayerResults, l => Assert.NotNull(l.Properties.ShearModulus));

        page.DensitySource = EngineeringPageViewModel.SourceOptions[1];
        page.ConstantDensity = 1950;
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.All(l => l.Density.Source == DensitySource.Constant && l.Density.Value == 1950));
        Assert.All(ws.Result!.LayerResults, l => Assert.False(l.Density.Estimated));
        var layer = ws.Result!.LayerResults[1];
        Assert.Equal(1950 * layer.S!.Velocity!.Value * layer.S.Velocity.Value, layer.Properties.ShearModulus!.Value, 1);

        page.DensitySource = EngineeringPageViewModel.SourceOptions[2];
        foreach (var method in EngineeringPageViewModel.EstimationOptions)
        {
            page.Estimation = method;
            await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.All(l => l.Density.Source == DensitySource.Estimated && l.Density.Method != null && l.Density.Method.Contains(method.Value == DensityEstimationMethod.Gardner1974 ? "Gardner" : "Brocher", StringComparison.OrdinalIgnoreCase)));
            Assert.All(ws.Result!.LayerResults, l => Assert.True(l.Density.Estimated));
            Assert.Contains("ESTIMATED", page.Densities[0].Used);
        }

        page.DensitySource = EngineeringPageViewModel.SourceOptions[0];
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.All(l => l.Density.Source == DensitySource.Measured));

        // A new layer has no measured density; "fill empty layers" copies the first entered value only there.
        ws.VelocityPage.AddBoundaryAt(27);
        await UiHarness.WaitForResultAsync(ws, r => r.LayerResults.Count == 6);
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        var empty = ws.State.ActiveScenario.Layers.Layers.Count(l => l.MeasuredDensity is null);
        if (empty > 0)
        {
            Assert.Contains(ws.Result!.LayerResults, l => l.Density.Value is null && l.Properties.ShearModulus is null);
            page.ApplyDensityToAllCommand.Execute(null);
            Assert.All(ws.State.ActiveScenario.Layers.Layers, l => Assert.NotNull(l.MeasuredDensity));
            Assert.Equal(1800, ws.State.ActiveScenario.Layers.Layers[0].MeasuredDensity);
        }

        page.Densities[0].Density = 1850;
        Assert.Equal(1850, ws.State.ActiveScenario.Layers.Layers[0].MeasuredDensity);

        // Custom time-averaged interval.
        page.IntervalTop = 0;
        page.IntervalBottom = 20;
        page.AddIntervalCommand.Execute(null);
        await UiHarness.WaitForResultAsync(ws, r => r.CustomIntervals.Count == 1);
        Assert.NotNull(ws.Result!.CustomIntervals[0].Value);
        Assert.False(ws.Result!.CustomIntervals[0].Extrapolated);
        Assert.Single(page.Intervals);
        page.RemoveIntervalCommand.Execute(page.Intervals[0]);
        await UiHarness.WaitForResultAsync(ws, r => r.CustomIntervals.Count == 0);

        // US customary display.
        ws.ProjectPage.UseUsUnits = true;
        ws.Navigate(ws.ProjectPage);
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        Assert.Contains("ft/s", page.Vs30Value);
        ws.ProjectPage.UseUsUnits = false;
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Vs30IsNeverExtrapolatedWithoutConsent()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var (ws, _) = await Fx.ImportSyntheticAsync(main, SyntheticDataset.Clean, 20, "shallow");
        ws.Navigate(ws.PPicksPage);
        await ws.PPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        ws.Navigate(ws.SPicksPage);
        await ws.SPicksPage.RunAutoPickCommand.ExecuteAsync(null);
        var page = ws.EngineeringPage;
        ws.Navigate(page);
        await UiHarness.WaitForResultAsync(ws, r => r.S.Observations.Count(o => o.Time is not null) >= 18);
        Assert.Null(ws.Result!.Vs30.Value);
        Assert.True(page.Vs30Missing);
        Assert.Contains("Insufficient measured depth", ws.Result!.Vs30.Reason);

        // Cancelled: nothing changes.
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Allow extrapolation") ? "Cancel" : Fx.Answer(d);
        page.AllowExtrapolation = true;
        await UiHarness.SettleAsync(200);
        Assert.False(page.AllowExtrapolation);
        Assert.False(ws.State.ActiveScenario.Analysis.Vs30.AllowExtrapolation);

        // Allowed: the value is labelled EXTRAPOLATED.
        main.Ui.DialogOverride = d => d.Buttons.Any(b => b.Text == "Allow extrapolation") ? "Allow extrapolation" : Fx.Answer(d);
        page.AllowExtrapolation = true;
        await UiHarness.WaitForResultAsync(ws, r => r.Vs30.Value is not null);
        Assert.True(ws.State.ActiveScenario.Analysis.Vs30.AllowExtrapolation);
        Assert.True(ws.Result!.Vs30.Extrapolated);
        Assert.Contains("EXTRAPOLATED", page.Vs30Status);
        Assert.Contains(ws.Session.History, h => h.Description.Contains("ALLOWED", StringComparison.Ordinal));
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}

/// <summary>Quality control, scenario comparison, report and every export.</summary>
public sealed class ResultsAndReportFeatureTests
{
    [AvaloniaFact]
    public async Task ResultsFlagsAndScenarioComparison()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "results");
        var page = ws.ResultsPage;
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        Assert.NotEmpty(page.Cards);
        Assert.Equal("Good", page.Quality);
        page.ShowNotes = true;
        await UiHarness.SettleAsync(100);
        Assert.NotEmpty(page.History);
        page.HistoryFilter = "pick";
        await UiHarness.SettleAsync(50);
        Assert.All(page.History, h => Assert.True((h.Operation + h.Description + h.Parameters).Contains("pick", StringComparison.OrdinalIgnoreCase)));

        // Make a problem at one depth and jump to it from the findings.
        var st = ws.State.Survey.StationsByDepth()[15];
        ws.PPicksPage.SetPickFromTable(st.Id, ws.State.ActiveScenario.PPicks[st.Id].Time!.Value * 1000 + 4);
        await UiHarness.WaitForResultAsync(ws, r => r.Qc.Flags.Any(f => f.SubjectId == st.Id));
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);
        var flag = page.Flags.First(f => f.Subject == st.Id);
        page.GoToFlagCommand.Execute(flag);
        Assert.Equal(st.Id, ws.SelectedStationId);
        Assert.Same(flag.Wave == WaveType.S ? ws.SPicksPage : ws.PPicksPage, ws.CurrentPage);

        // Scenario comparison.
        ws.ProjectPage.NewScenarioName = "Layer regression";
        ws.ProjectPage.CreateScenarioCommand.Execute(null);
        ws.VelocityPage.Method = VelocityPageViewModel.MethodOptions[0];
        ws.Navigate(page);
        page.LoadComparisonCommand.Execute(null);
        Assert.Equal(2, page.Comparison.Count);
        Assert.Single(page.Comparison, c => c.Active);
        Assert.All(page.Comparison, c => Assert.Contains("m/s", c.Vs30));
        page.ManageScenariosCommand.Execute(null);
        Assert.Same(ws.ProjectPage, ws.CurrentPage);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReportBrandingTemplatesAndEveryExport()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "report");
        var page = ws.ReportPage;
        var dir = Fx.Dir("report");
        ws.Navigate(page);
        await UiHarness.SettleAsync(200);

        // Report fields and sections.
        page.ClientName = "Riverside DOT";
        Assert.Equal("Riverside DOT", ws.State.Report.Client.Name);
        await page.PreviewCommand.ExecuteAsync(null);
        var full = page.PreviewPages.Count;
        Assert.True(full > 5);
        foreach (var toggle in page.Sections.Where(s => s.Section.ToString().StartsWith("Appendix", StringComparison.Ordinal)))
        {
            toggle.IsOn = false;
        }

        Assert.DoesNotContain(ws.State.Report.Sections, s => s.ToString().StartsWith("Appendix", StringComparison.Ordinal));
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.PreviewPages.Count < full);

        // Logo: PNG accepted, non-image rejected, cleared.
        var logo = Path.Combine(dir, "logo.png");
        using (var bmp = new SKBitmap(120, 40))
        {
            bmp.Erase(SKColors.OrangeRed);
            using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(logo, data.ToArray());
        }

        main.Ui.OpenFilesOverride = _ => [logo];
        await page.ChooseLogoCommand.ExecuteAsync(null);
        await UiHarness.SettleAsync(100);
        Assert.True(page.HasLogo);
        Assert.NotNull(ws.State.Report.Branding.LogoAsset);
        var bad = Path.Combine(dir, "logo.png.txt");
        File.WriteAllText(bad, "not an image");
        string? error = null;
        main.Ui.DialogOverride = d => { error = d.Title; return Fx.Answer(d); };
        main.Ui.OpenFilesOverride = _ => [bad];
        var asset = ws.State.Report.Branding.LogoAsset;
        await page.ChooseLogoCommand.ExecuteAsync(null);
        Assert.Equal("Unsupported image", error);
        Assert.Equal(asset, ws.State.Report.Branding.LogoAsset);
        main.Ui.DialogOverride = Fx.Answer;

        // Templates.
        page.Company = "Acme Geophysics";
        page.NewTemplateName = "Acme " + Guid.NewGuid().ToString("N")[..6];
        var template = page.NewTemplateName;
        page.SaveTemplateCommand.Execute(null);
        Assert.Contains(template, page.Templates);
        page.Company = "Someone Else";
        page.ClearLogoCommand.Execute(null);
        await UiHarness.SettleAsync(100);
        Assert.False(page.HasLogo);
        page.SelectedTemplate = template;
        page.ApplyTemplateCommand.Execute(null);
        await UiHarness.SettleAsync(100);
        Assert.Equal("Acme Geophysics", ws.State.Report.Branding.CompanyName);
        Assert.True(page.HasLogo);

        // PDF report (US Letter and A4).
        foreach (var paper in ReportPageViewModel.PaperOptions)
        {
            page.Paper = paper;
            var pdf = Path.Combine(dir, $"report-{paper.Value}.pdf");
            main.Ui.SaveFileOverride = _ => pdf;
            await page.GenerateCommand.ExecuteAsync(null);
            Assert.True(Fx.IsPdf(pdf), pdf);
            Assert.True(new FileInfo(pdf).Length > 50_000);
            Assert.Equal(pdf, page.LastReportPath);
        }

        Assert.True(ws.ReportGenerated);
        Assert.Contains(ws.Session.History, h => h.Operation == "report.generate");
        string? opened = null;
        main.Ui.OpenUrlOverride = u => opened = u;
        page.OpenReportCommand.Execute(null);
        Assert.Equal(page.LastReportPath, opened);

        // Tables.
        var xlsx = Path.Combine(dir, "tables.xlsx");
        main.Ui.SaveFileOverride = _ => xlsx;
        await page.ExportXlsxCommand.ExecuteAsync(null);
        Assert.True(Fx.IsZip(xlsx));
        using (var zip = ZipFile.OpenRead(xlsx))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "xl/workbook.xml");
            Assert.True(zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)) >= 7);
        }

        foreach (var which in new[] { "picks", "layers", "engineering", "intervals", "qc", "history" })
        {
            var csv = Path.Combine(dir, $"{which}.csv");
            main.Ui.SaveFileOverride = _ => csv;
            await page.ExportCsvCommand.ExecuteAsync(which);
            var lines = File.ReadAllLines(csv);
            Assert.True(lines.Length >= 2, which);
            Assert.Contains(",", lines[0]);
        }

        // Every figure in every format; PNG at the requested pixel size, transparent when asked.
        page.FigureWidth = 1200;
        page.FigureHeight = 800;
        foreach (var figure in ReportPageViewModel.FigureOptions)
        {
            page.Figure = figure;
            foreach (var format in ReportPageViewModel.FormatOptions)
            {
                page.FigureFormat = format;
                var file = Path.Combine(dir, $"{figure.Value}.{format.Value.ToString().ToLowerInvariant()}");
                main.Ui.SaveFileOverride = _ => file;
                await page.ExportFigureCommand.ExecuteAsync(null);
                var text = File.ReadAllText(file);
                switch (format.Value)
                {
                    case ExportFormat.Png:
                        Assert.True(Fx.IsPng(file), file);
                        using (var decoded = SKBitmap.Decode(file))
                        {
                            Assert.Equal(1200, decoded.Width);
                            Assert.Equal(800, decoded.Height);
                        }

                        break;
                    case ExportFormat.Svg:
                        Assert.Contains("<svg", text);
                        break;
                    default:
                        Assert.True(Fx.IsPdf(file), file);
                        break;
                }
            }
        }

        page.Figure = ReportPageViewModel.FigureOptions[0];
        page.FigureFormat = ReportPageViewModel.FormatOptions[0];
        page.Transparent = true;
        var transparent = Path.Combine(dir, "transparent.png");
        main.Ui.SaveFileOverride = _ => transparent;
        await page.ExportFigureCommand.ExecuteAsync(null);
        using (var decoded = SKBitmap.Decode(transparent))
        {
            Assert.Equal(0, decoded.GetPixel(2, 2).Alpha);
        }

        page.Transparent = false;
        main.Ui.Toasts.Clear();
        await page.CopyFigureCommand.ExecuteAsync(null);
        Assert.Contains(main.Ui.Toasts, t => t.Message.Contains("clipboard", StringComparison.OrdinalIgnoreCase));
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SaveAndReopenPreservesTheInterpretationExactly()
    {
        var main = UiHarness.Main;
        var window = await Fx.StartAsync(main);
        var ws = await Fx.OpenSampleCopyAsync(main, "roundtrip");
        var st = ws.State.Survey.StationsByDepth()[7];
        ws.Navigate(ws.PPicksPage);
        ws.PPicksPage.TraceClicked(st.Id, 0.0141, selectOnly: false);
        ws.PPicksPage.SetComment(st.Id, "checked twice");
        ws.VelocityPage.AddBoundaryAt(13.37);
        ws.ProcessingPage.Group = ProcessingPageViewModel.GroupOptions[0];
        ws.ProcessingPage.AddStepCommand.Execute("highPass");
        ws.EngineeringPage.IntervalTop = 5;
        ws.EngineeringPage.IntervalBottom = 25;
        ws.EngineeringPage.AddIntervalCommand.Execute(null);
        ws.Navigate(ws.WaveformsPage);
        ws.WaveformsPage.SelectTrace(ws.WaveformsPage.Section!.Traces[3].Key);
        ws.WaveformsPage.SelectedTrace!.Gain = 3;
        await UiHarness.WaitForResultAsync(ws, r => r.CustomIntervals.Count == 1);
        var before = ws.State;
        var resultBefore = ws.Result!;
        var path = await Fx.SavedPathAsync(ws, "roundtrip");
        await main.CloseProjectCommand.ExecuteAsync(null);

        await main.OpenPathAsync(path);
        var after = main.Workspace!.State;
        string Picks(ProjectState s, WaveType w) => string.Join(";", s.ActiveScenario.PicksFor(w).OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}:{kv.Value.Time:R}:{kv.Value.Origin}:{kv.Value.Algorithm}:{kv.Value.Included}:{kv.Value.Comment}"));
        Assert.Equal(Picks(before, WaveType.P), Picks(after, WaveType.P));
        Assert.Equal(Picks(before, WaveType.S), Picks(after, WaveType.S));
        Assert.Equal(before.ActiveScenario.Layers.Layers.Select(l => (l.TopDepth, l.MeasuredDensity, l.Label)), after.ActiveScenario.Layers.Layers.Select(l => (l.TopDepth, l.MeasuredDensity, l.Label)));
        string Chain(ProcessingPipeline p) => string.Join("|", p.Steps.Select(s => s.GetType().Name + string.Join(",", s.DescribeParameters().Select(kv => kv.Key + "=" + kv.Value)) + s.Enabled));
        Assert.Equal(Chain(before.ActiveScenario.VerticalPipeline), Chain(after.ActiveScenario.VerticalPipeline));
        Assert.Equal(Chain(before.ActiveScenario.HorizontalPipeline), Chain(after.ActiveScenario.HorizontalPipeline));
        Assert.Equal(before.ActiveScenario.TraceAdjustments.OrderBy(kv => kv.Key).ToList(), after.ActiveScenario.TraceAdjustments.OrderBy(kv => kv.Key).ToList());
        Assert.Equal(before.ActiveScenario.Analysis.Vs30.CustomIntervals, after.ActiveScenario.Analysis.Vs30.CustomIntervals);
        Assert.Equal(before.ActiveScenario.Trigger, after.ActiveScenario.Trigger);
        await UiHarness.WaitForResultAsync(main.Workspace!, r => r.CustomIntervals.Count == 1);
        Assert.Equal(resultBefore.Vs30.Value, main.Workspace!.Result!.Vs30.Value);
        await main.CloseProjectCommand.ExecuteAsync(null);
        window.Close();
    }
}
