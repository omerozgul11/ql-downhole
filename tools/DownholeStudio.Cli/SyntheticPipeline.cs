using DownholeStudio.Core;
using DownholeStudio.Core.Import;
using DownholeStudio.Domain;
using DownholeStudio.Formats;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Cli;

/// <summary>Builds a project from a synthetic survey with the same steps a user takes in the application.</summary>
internal static class SyntheticPipeline
{
    public static async Task<(ProjectSession Session, AnalysisService Analysis, SyntheticSurvey Survey)> BuildAsync(SyntheticOptions options, string name, AppIdentity identity,
        bool triggerCorrection, string? folder = null)
    {
        var survey = SyntheticSurveyGenerator.Generate(options);
        folder ??= Path.Combine(Path.GetTempPath(), "dhs-cli", Guid.NewGuid().ToString("N"));
        SyntheticSurveyGenerator.WriteTo(survey, folder);
        var clock = new SystemClock();
        var session = ProjectSession.CreateNew(name, clock, identity);
        var import = new ImportService(SeismicFormatRegistry.CreateDefault(), clock);
        var preview = await import.PreviewAsync([folder]);
        await import.ImportAsync(session, preview, new ImportOptions { ApplySuggestedDepths = true });
        var g = session.State.Survey.Geometry;
        session.Execute(Edits.SetGeometry(g with
        {
            PSource = g.PSource with { HorizontalOffset = options.POffset }, SSource = g.SSource with { HorizontalOffset = options.SOffset },
            Direction = options.BottomUp ? AcquisitionDirection.BottomUp : AcquisitionDirection.TopDown,
        }, "Source offsets"));
        var analysis = new AnalysisService(session, new ProcessedTraceCache());
        await analysis.AutoPickReferencesAsync(false);
        if (triggerCorrection)
        {
            session.Execute(Edits.SetTrigger(new TriggerSettings { Mode = TriggerCorrectionMode.SubtractReferenceOnset }));
        }

        await analysis.AutoPickAsync(WaveType.P, null, false);
        await analysis.AutoPickAsync(WaveType.S, null, false);
        return (session, analysis, survey);
    }

    public static void UseLayers(ProjectSession session, IEnumerable<double> boundaries, VelocityMethod method)
    {
        session.Execute(Edits.SetLayerModel(Analysis.Velocity.LayerEdits.FromBoundaries(boundaries), "Layer boundaries"));
        session.Execute(Edits.SetAnalysisSettings(session.State.ActiveScenario.Analysis with { Method = method }, $"Method {method}"));
    }
}
