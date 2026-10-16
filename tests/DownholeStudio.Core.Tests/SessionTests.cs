using DownholeStudio.Domain;
using DownholeStudio.Persistence;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Core.Tests;

public sealed class SessionTests
{
    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "dhs-tests", Guid.NewGuid().ToString("N"), name);

    [Fact]
    public async Task SaveLoadRoundTripPreservesEverything()
    {
        var (session, analysis, _, _) = await Pipeline.BuildAsync(new SyntheticOptions { DepthCount = 6 });
        session.CreateSnapshot("After picking");
        session.CreateScenario("Scenario B", "alternative filters");
        session.Execute(Edits.AddStep(session.State, ComponentGroup.Horizontal, new BandPassStep { LowHz = 20, HighHz = 150 }));
        var path = TempFile("survey.dhsx");
        session.Save(path);
        Assert.False(session.IsDirty);

        var loaded = ProjectSession.Open(path, new FixedClock(Pipeline.T0), new AppIdentity("1.0.0-test", "tester"));
        Assert.Equal(ProjectJson.Serialize(session.State), ProjectJson.Serialize(loaded.State));
        Assert.Equal(2, loaded.State.Scenarios.Count);
        Assert.Single(loaded.Document.SnapshotStates);
        Assert.False(loaded.IsDirty);

        // Raw samples and the original files come back byte-identical.
        foreach (var record in session.State.Survey.Records)
        {
            Assert.Equal(session.Document.Assets.GetFile(record.Source.EmbeddedPath), loaded.Document.Assets.GetFile(record.Source.EmbeddedPath));
            foreach (var t in record.Traces)
            {
                Assert.Equal(session.Document.Assets.GetTrace(t.Id).ToArray(), loaded.Document.Assets.GetTrace(t.Id).ToArray());
            }
        }

        Assert.Contains(loaded.History, h => h.Operation == "import");
        Assert.Contains(loaded.History, h => h.Operation == "pick.auto");
        Assert.Equal(loaded.History.Count, loaded.History.Count(h => h.AppVersion == "1.0.0-test"));
        _ = analysis;
    }

    [Fact]
    public async Task ResavingTheSameFileKeepsBackupAndData()
    {
        var (session, _, _, _) = await Pipeline.BuildAsync(new SyntheticOptions { DepthCount = 3 }, autoPick: false);
        var path = TempFile("p.dhsx");
        session.Save(path);
        var reopened = ProjectSession.Open(path, new FixedClock(Pipeline.T0), AppIdentity.Default);
        reopened.Execute(Edits.SetMetadata(reopened.State.Metadata with { Name = "Renamed" }));
        reopened.Save();
        reopened.Execute(Edits.SetMetadata(reopened.State.Metadata with { Name = "Renamed twice" }));
        reopened.Save();
        Assert.True(File.Exists(path + ".bak"));
        var final = ProjectPackage.Load(path);
        Assert.Equal("Renamed twice", final.State.Metadata.Name);
        var trace = final.State.Survey.Records[0].Traces[0].Id;
        Assert.Equal(session.Document.Assets.GetTrace(trace).ToArray(), final.Assets.GetTrace(trace).ToArray());
        Assert.Equal("Renamed", ProjectPackage.Load(path + ".bak").State.Metadata.Name);
    }

    [Fact]
    public void DamagedFileIsReportedNotLoaded()
    {
        var path = TempFile("bad.dhsx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "garbage");
        var ex = Assert.Throws<ProjectFileException>(() => ProjectPackage.Load(path));
        Assert.Contains("not a Downhole Studio project", ex.Message);
    }

    [Fact]
    public void UndoRedoCoversEveryKindOfEdit()
    {
        var session = ProjectSession.CreateNew("u", new FixedClock(Pipeline.T0), AppIdentity.Default);
        var station = new Station { Id = Guid.NewGuid(), MeasuredDepth = 5 };
        session.Execute(Edits.SetStations([station], [], "add station"));
        var s0 = session.State;
        session.Execute(Edits.SetPick(session.State, WaveType.P, station.Id, 0.012, null, Pipeline.T0));
        session.Execute(Edits.AddStep(session.State, ComponentGroup.Vertical, new BandPassStep()));
        session.Execute(Edits.SetGeometry(session.State.Survey.Geometry with { PSource = new SourceGeometry { HorizontalOffset = 3 } }, "offset"));
        session.Execute(Edits.AddBoundary(session.State, 4));
        session.Execute(Edits.SetPickIncluded(session.State, WaveType.P, station.Id, false));
        session.Execute(Edits.SetTraceAdjustment(session.State, Guid.NewGuid(), new TraceAdjustment { ReversePolarity = true }, "polarity"));
        session.Execute(Edits.SetTrigger(new TriggerSettings { Mode = TriggerCorrectionMode.AlignToReferenceMedian }));
        var s7 = session.State;
        for (var i = 0; i < 7; i++)
        {
            Assert.True(session.Undo());
        }

        Assert.Same(s0, session.State);
        for (var i = 0; i < 7; i++)
        {
            Assert.True(session.Redo());
        }

        Assert.Same(s7, session.State);
        Assert.Equal(7, session.History.Count(h => h.Operation == "edit.undo"));
    }

    [Fact]
    public void DragGestureIsOneUndoStep()
    {
        var session = ProjectSession.CreateNew("g", new FixedClock(Pipeline.T0), AppIdentity.Default);
        var station = new Station { Id = Guid.NewGuid(), MeasuredDepth = 5 };
        session.Execute(Edits.SetStations([station], [], "add station"));
        var before = session.State;
        session.BeginGesture();
        foreach (var t in new[] { 0.010, 0.011, 0.012, 0.0125 })
        {
            session.Gesture(Edits.SetPick(session.State, WaveType.P, station.Id, t, null, Pipeline.T0));
        }

        session.EndGesture();
        Assert.Equal(0.0125, session.State.ActiveScenario.PPicks[station.Id].Time);
        session.Undo();
        Assert.Same(before, session.State);
        Assert.Single(session.History, h => h.Operation == "pick.set");
    }

    [Fact]
    public void ManualPickKeepsSuggestionAndCanBeRestored()
    {
        var session = ProjectSession.CreateNew("m", new FixedClock(Pipeline.T0), AppIdentity.Default);
        var id = Guid.NewGuid();
        var suggestion = new PickSuggestion { Time = 0.02, Algorithm = PickAlgorithm.Aic };
        session.Execute(Edits.SetStations([new Station { Id = id, MeasuredDepth = 3 }], [], "s"));
        session.Execute(Edits.ApplyAutoPicks(WaveType.S, [new Analysis.Picking.AutoPickOutcome(id, suggestion, null, null)], PickAlgorithm.Aic, Pipeline.T0, false));
        session.Execute(Edits.SetPick(session.State, WaveType.S, id, 0.021, null, Pipeline.T0));
        var pick = session.State.ActiveScenario.SPicks[id];
        Assert.Equal(PickOrigin.Manual, pick.Origin);
        Assert.Equal(0.02, pick.Suggestion!.Time);
        // Re-running auto picking does not overwrite the manual pick.
        session.Execute(Edits.ApplyAutoPicks(WaveType.S, [new Analysis.Picking.AutoPickOutcome(id, suggestion with { Time = 0.03 }, null, null)], PickAlgorithm.Aic, Pipeline.T0, false));
        Assert.Equal(0.021, session.State.ActiveScenario.SPicks[id].Time);
        session.Execute(Edits.RestoreAutoPick(session.State, WaveType.S, id, Pipeline.T0));
        Assert.Equal(0.03, session.State.ActiveScenario.SPicks[id].Time);
        Assert.Equal(PickOrigin.Automatic, session.State.ActiveScenario.SPicks[id].Origin);
        session.Execute(Edits.DeletePick(session.State, WaveType.S, id));
        Assert.Null(session.State.ActiveScenario.SPicks[id].Time);
        Assert.NotNull(session.State.ActiveScenario.SPicks[id].Suggestion);
    }

    [Fact]
    public async Task ScenariosShareRawDataAndCompare()
    {
        var (session, analysis, _, _) = await Pipeline.BuildAsync(new SyntheticOptions { DepthCount = 12 });
        session.CreateScenario("B — refracted ray");
        session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Method = VelocityMethod.RefractedRay }, "method"));
        Assert.Same(session.State.Scenarios[0].PPicks, session.State.Scenarios[1].PPicks);
        var summaries = ScenarioComparison.Compare(session.State, analysis.Traces, analysis.Cache, Pipeline.T0);
        Assert.Equal(2, summaries.Count);
        Assert.Equal(VelocityMethod.RefractedRay, summaries[1].Method);
    }

    [Fact]
    public void RecoveryCopiesOfDeadSessionsAreOffered()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhs-recovery", Guid.NewGuid().ToString("N"));
        var store = new RecoveryStore(dir);
        var session = ProjectSession.CreateNew("Recover me", new FixedClock(Pipeline.T0), AppIdentity.Default);
        store.Save(session.SessionId, session.Document, null, "1.0.0", Pipeline.T0);
        Assert.Empty(store.FindOrphans()); // our own process is alive
        var marker = Path.Combine(dir, $"{session.SessionId:N}.json");
        File.WriteAllText(marker, File.ReadAllText(marker).Replace($"\"processId\": {Environment.ProcessId}", "\"processId\": 999999"));
        var orphan = Assert.Single(store.FindOrphans());
        Assert.Equal("Recover me", orphan.ProjectName);
        Assert.Equal("Recover me", ProjectPackage.Load(orphan.PackagePath).State.Metadata.Name);
        store.Discard(orphan.SessionId);
        Assert.Empty(store.FindOrphans());
    }

    [Fact]
    public void CatalogStoresRecentProjectsProfilesAndTemplates()
    {
        var catalog = new AppCatalog(Path.Combine(Path.GetTempPath(), "dhs-cat", Guid.NewGuid().ToString("N"), "catalog.db"));
        catalog.TouchRecent("/tmp/a.dhsx", "A", Pipeline.T0);
        catalog.TouchRecent("/tmp/b.dhsx", "B", Pipeline.T0.AddMinutes(1));
        Assert.Equal("B", catalog.RecentProjects()[0].Name);
        catalog.SaveProfile(new InstrumentProfile { Name = "Field kit 2", Channels = [new ChannelAssignment { ChannelNumber = 7, Role = ComponentRole.Vertical }] }, Pipeline.T0);
        Assert.Contains(catalog.Profiles(), p => p.Name == "Field kit 2" && !p.BuiltIn);
        Assert.Contains(catalog.Profiles(), p => p.Name == "QuakeLogic 3C Borehole" && p.BuiltIn);
        Assert.Throws<InvalidOperationException>(() => catalog.SaveProfile(new InstrumentProfile { Name = "QuakeLogic 3C Borehole" }, Pipeline.T0));
        catalog.SaveTemplate(new ReportTemplate("Default", new ReportBranding { CompanyName = "Acme Geo" }, [1, 2, 3], ".png"), Pipeline.T0);
        Assert.Equal("Acme Geo", catalog.Templates().Single().Branding.CompanyName);
    }
}
