using System.Text.Json;
using System.Text.Json.Serialization;
using DownholeStudio.Analysis;
using DownholeStudio.Domain;

namespace DownholeStudio.Reporting;

/// <summary>
/// Machine-readable audit record: which raw files were imported (with SHA-256), what processing was
/// applied, which picks are manual or automatic, what was excluded, which methods were used and
/// which software version produced the results.
/// </summary>
public static class AuditExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Create(ProjectState project, AnalysisResult result, IEnumerable<HistoryEntry> history, string appVersion, DateTimeOffset now)
    {
        var sc = project.ActiveScenario;
        var survey = project.Survey;
        var depthOf = survey.Stations.ToDictionary(s => s.Id, s => survey.Geometry.DepthBelowGround(s.MeasuredDepth));
        object Pick(Guid station, ArrivalPick p) => new
        {
            depthMetres = depthOf.GetValueOrDefault(station), timeSeconds = p.Time, origin = p.Origin, algorithm = p.Algorithm, included = p.Included,
            confidence = p.Confidence is { } c ? new { c.Score, c.Level, factors = c.Factors.Select(f => new { f.Name, f.Value, f.Score, f.Explanation }) } : null,
            snr = p.Snr, comment = p.Comment, suggestion = p.Suggestion is { } s ? new { timeSeconds = s.Time, s.Algorithm } : null, modified = p.ModifiedAt,
        };

        var audit = new
        {
            format = "quakelogic.downhole-studio.audit/1",
            generatedAt = now,
            software = new { product = "Downhole Studio", version = appVersion },
            project = new { project.Metadata.ProjectId, project.Metadata.Name, project.Metadata.JobNumber, createdWith = project.Metadata.CreatedWithVersion, scenario = sc.Name },
            rawFiles = survey.Records.Select(r => new
            {
                r.Source.FileName, r.Source.Sha256, bytes = r.Source.Length, r.Source.ImportedAt, r.Source.OriginalPath, format = r.FormatName,
                depthMetres = r.StationId is { } id ? depthOf.GetValueOrDefault(id) : (double?)null, shot = r.Shot, excluded = r.Excluded,
                traces = r.Traces.Select(t => new { t.ChannelNumber, t.SampleCount, sampleIntervalSeconds = t.SampleInterval, delaySeconds = t.Delay }),
            }),
            channelMap = survey.ChannelMap,
            geometry = survey.Geometry,
            excludedDepths = survey.Stations.Where(s => s.Excluded).Select(s => depthOf[s.Id]),
            processing = new
            {
                vertical = sc.VerticalPipeline.Steps.Select(Step), horizontal = sc.HorizontalPipeline.Steps.Select(Step), reference = sc.ReferencePipeline.Steps.Select(Step),
                traceAdjustments = sc.TraceAdjustments.Select(kv => new { traceId = kv.Key, kv.Value.ReversePolarity, timeShiftSeconds = kv.Value.TimeShift, kv.Value.Excluded, kv.Value.Gain }),
                stackRepeatedShots = sc.StackRepeatedShots, sWave = new { sc.SWave.Component, sc.SWave.PickTrace, rotationDegrees = sc.SWave.StationRotation.Select(kv => new { depthMetres = depthOf.GetValueOrDefault(kv.Key), degrees = kv.Value }) },
            },
            trigger = new { sc.Trigger.Mode, expectedReferenceTimeSeconds = sc.Trigger.ExpectedReferenceTime, referencePicks = sc.ReferencePicks.Select(kv => new { record = survey.FindRecord(kv.Key)?.Source.FileName, timeSeconds = kv.Value.Time, kv.Value.Origin, kv.Value.Included }) },
            pickers = new { p = sc.PPicker, s = sc.SPicker, reference = sc.ReferencePicker },
            picks = new { p = sc.PPicks.Select(kv => Pick(kv.Key, kv.Value)), s = sc.SPicks.Select(kv => Pick(kv.Key, kv.Value)) },
            analysis = new
            {
                method = result.Method, description = result.MethodDescription, regression = sc.Analysis.Regression, huberThreshold = sc.Analysis.HuberThreshold, vs30Settings = sc.Analysis.Vs30, density = sc.Density,
                layers = result.LayerResults.Select(l => new
                {
                    topMetres = l.Layer.Top, bottomMetres = l.Layer.Bottom, vp = l.P?.Velocity, vs = l.S?.Velocity, vpStdError = l.P?.VelocityStandardError, vsStdError = l.S?.VelocityStandardError,
                    vpPoints = l.P?.PointCount, vsPoints = l.S?.PointCount, densityKgM3 = l.Density.Value, densityEstimated = l.Density.Estimated, densityMethod = l.Density.Method,
                    poisson = l.Properties.PoissonRatio, shearPa = l.Properties.ShearModulus, youngPa = l.Properties.YoungModulus, bulkPa = l.Properties.BulkModulus,
                }),
                vs30 = new { value = result.Vs30.Value, result.Vs30.Extrapolated, result.Vs30.ExtrapolatedFrom, result.Vs30.Reason },
                quality = result.Qc.Quality, qualityReasons = result.Qc.QualityReasons,
                flags = result.Qc.Flags.Select(f => new { f.Severity, f.Code, f.Message, f.Depth }),
            },
            history = history.Select(h => new { h.Timestamp, h.User, h.AppVersion, h.Scenario, h.Operation, h.Description, h.Parameters }),
        };
        return JsonSerializer.Serialize(audit, Json);
    }

    private static object Step(ProcessingStep s) => new { name = s.DisplayName, kind = s.GetType().Name, s.Enabled, parameters = s.DescribeParameters() };
}
