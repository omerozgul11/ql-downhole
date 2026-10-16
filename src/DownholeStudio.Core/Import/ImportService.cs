using System.Collections.Immutable;
using System.Security.Cryptography;
using DownholeStudio.Domain;
using DownholeStudio.Formats;
using DownholeStudio.Persistence;

namespace DownholeStudio.Core.Import;

/// <summary>Choices the user made in the import preview. Corrections are only applied when approved here.</summary>
public sealed record ImportOptions
{
    /// <summary>Files the user unticked.</summary>
    public IReadOnlySet<string> ExcludedPaths { get; init; } = new HashSet<string>();
    /// <summary>Approved fix: import only the first of several identical files.</summary>
    public bool SkipDuplicates { get; init; } = true;
    /// <summary>Approved fix: order records by their recorded acquisition time instead of file name.</summary>
    public bool OrderByRecordingTime { get; init; }
    /// <summary>Approved: create depths from the suggested depths and shot kinds.</summary>
    public bool ApplySuggestedDepths { get; init; }
    /// <summary>Channel map to use when the project has none yet.</summary>
    public ChannelMap? ChannelMap { get; init; }
}

public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Messages);

/// <summary>Reads approved files into the project. Raw bytes are embedded unchanged; decoded samples are stored once.</summary>
public sealed class ImportService(SeismicFormatRegistry registry, IClock clock)
{
    public ImportInspector Inspector { get; } = new(registry);

    public Task<ImportPreview> PreviewAsync(IEnumerable<string> paths, IProgress<double>? progress = null, CancellationToken ct = default) =>
        Inspector.InspectAsync(Inspector.ExpandPaths(paths), progress, ct);

    public async Task<ImportResult> ImportAsync(ProjectSession session, ImportPreview preview, ImportOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var messages = new List<string>();
        var candidates = preview.Files.Where(f => f.IsReadable && !options.ExcludedPaths.Contains(f.Path)).ToList();
        var existingHashes = session.State.Survey.Records.Select(r => r.Source.Sha256).ToHashSet(StringComparer.Ordinal);
        if (options.SkipDuplicates)
        {
            var seen = new HashSet<string>(existingHashes, StringComparer.Ordinal);
            var kept = new List<ImportCandidate>();
            foreach (var c in candidates)
            {
                if (seen.Add(c.Sha256))
                {
                    kept.Add(c);
                }
                else
                {
                    messages.Add($"\"{c.FileName}\" was not imported: it is identical to a record already in the project or in this import.");
                }
            }

            candidates = kept;
        }

        if (options.OrderByRecordingTime)
        {
            candidates = candidates.OrderBy(c => c.Metadata!.AcquiredAt ?? DateTimeOffset.MaxValue).ThenBy(c => c.FileName, NaturalStringComparer.Instance).ToList();
        }

        var now = clock.Now;
        var assets = session.Document.Assets;
        var nextSequence = session.State.Survey.Records.Count == 0 ? 0 : session.State.Survey.Records.Max(r => r.Sequence) + 1;
        var records = new List<SeismicRecord>();
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(c.Path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"\"{c.FileName}\" could not be read again during import: {ex.Message}");
                continue;
            }

            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != c.Sha256)
            {
                messages.Add($"\"{c.FileName}\" changed after the preview and was not imported. Preview the files again.");
                continue;
            }

            var reader = registry.Readers.First(r => r.Name == c.ReaderName);
            SeismicFileData data;
            await using (var ms = new MemoryStream(bytes, writable: false))
            {
                data = await reader.ReadAsync(ms, c.FileName, ct).ConfigureAwait(false);
            }

            var embedded = $"raw/{c.Sha256}/{SafeName(c.FileName)}";
            assets.AddFile(embedded, bytes);
            var traces = ImmutableList.CreateBuilder<TraceInfo>();
            for (var t = 0; t < data.Metadata.Traces.Count; t++)
            {
                var h = data.Metadata.Traces[t];
                var id = Guid.NewGuid();
                assets.AddTrace(id, data.Samples[t]);
                traces.Add(new TraceInfo
                {
                    Id = id, Index = h.Index, ChannelNumber = h.ChannelNumber, SampleCount = h.SampleCount, SampleInterval = h.SampleInterval, Delay = h.Delay,
                    Label = h.Label, ReceiverLocation = h.ReceiverLocation, SourceLocation = h.SourceLocation, Header = h.Keywords,
                });
            }

            records.Add(new SeismicRecord
            {
                Id = Guid.NewGuid(), FormatName = data.Metadata.FormatName, Sequence = nextSequence++, AcquiredAt = data.Metadata.AcquiredAt,
                Label = Path.GetFileNameWithoutExtension(c.FileName), Traces = traces.ToImmutable(), Header = data.Metadata.Keywords,
                SuggestedDepth = c.DepthHint?.Depth, SuggestedShot = c.ShotHint, SuggestionSource = c.DepthHint?.Source ?? c.ShotHintSource,
                Source = new SourceFileInfo
                {
                    FileName = c.FileName, OriginalPath = c.Path, Length = bytes.Length, Sha256 = c.Sha256, LastWriteTime = c.LastWriteTime,
                    EmbeddedPath = embedded, ImportedAt = now,
                },
            });
            progress?.Report((i + 1.0) / candidates.Count);
        }

        if (records.Count == 0)
        {
            return new ImportResult(0, preview.Files.Count, messages);
        }

        var map = session.State.Survey.ChannelMap.Channels.Count > 0 ? null
            : options.ChannelMap ?? ChannelMapSuggester.Suggest(records, AppCatalog.BuiltInProfiles).Map;
        var names = string.Join(", ", records.Take(3).Select(r => r.Source.FileName)) + (records.Count > 3 ? $" and {records.Count - 3} more" : "");
        var edit = new ProjectEdit("import", $"Imported {records.Count} record(s): {names}", s =>
        {
            var survey = s.Survey with { Records = s.Survey.Records.AddRange(records) };
            if (map is not null)
            {
                survey = survey with { ChannelMap = map };
            }

            if (options.ApplySuggestedDepths)
            {
                var (stations, recs) = DepthAssigner.FromSuggestions(survey);
                survey = survey with { Stations = stations, Records = recs };
            }

            return s with { Survey = survey };
        }).With("files", records.Count).With("sha256", string.Join(";", records.Select(r => r.Source.FileName + "=" + r.Source.Sha256)))
          .With("suggestedDepthsApplied", options.ApplySuggestedDepths).With("orderedByTime", options.OrderByRecordingTime);
        session.Execute(edit);
        return new ImportResult(records.Count, preview.Files.Count - records.Count, messages);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToHashSet();
        return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}

/// <summary>Suggests a channel map from trace labels or a matching instrument profile. Always overridable.</summary>
public static class ChannelMapSuggester
{
    public static (ChannelMap Map, string Reason) Suggest(IReadOnlyList<SeismicRecord> records, IEnumerable<InstrumentProfile> profiles)
    {
        var channels = records.SelectMany(r => r.Traces).GroupBy(t => t.ChannelNumber).OrderBy(g => g.Key).ToList();
        var fromLabels = new List<ChannelAssignment>();
        foreach (var g in channels)
        {
            var label = g.Select(t => t.Label?.Trim().ToUpperInvariant()).FirstOrDefault(l => !string.IsNullOrEmpty(l));
            var role = label switch
            {
                "Z" or "V" or "VERT" or "VERTICAL" or "UP" => ComponentRole.Vertical,
                "X" or "H1" or "N" or "R" or "RADIAL" => ComponentRole.HorizontalX,
                "Y" or "H2" or "E" or "T" or "TRANSVERSE" => ComponentRole.HorizontalY,
                "REF" or "TRIGGER" or "TRIG" or "SPY" or "REFERENCE" => ComponentRole.Reference,
                _ => ComponentRole.Unassigned,
            };
            fromLabels.Add(new ChannelAssignment { ChannelNumber = g.Key, Role = role, Label = label });
        }

        if (fromLabels.Count > 0 && fromLabels.All(a => a.Role != ComponentRole.Unassigned))
        {
            return (new ChannelMap { Channels = [.. fromLabels], ProfileName = "From trace labels" }, "Suggested from the trace labels stored in the files.");
        }

        var set = channels.Select(g => g.Key).ToHashSet();
        var profile = profiles.FirstOrDefault(p => p.Channels.Select(c => c.ChannelNumber).ToHashSet().SetEquals(set));
        if (profile is not null)
        {
            return (new ChannelMap { Channels = profile.Channels, ProfileName = profile.Name }, $"Suggested profile \"{profile.Name}\" because it matches the channel numbers.");
        }

        return (new ChannelMap { Channels = [.. fromLabels] }, "No profile matches; assign each channel's role on the Geometry page.");
    }
}

/// <summary>Creates measurement depths and links records to them. Every result is applied through an undoable edit.</summary>
public static class DepthAssigner
{
    /// <summary>
    /// Assigns depths by acquisition order: records (excluding excluded ones) are grouped in blocks of
    /// <paramref name="pattern"/>.Count; block k gets depth first ± k·increment (− for bottom-up).
    /// </summary>
    public static (ImmutableList<Station> Stations, ImmutableList<SeismicRecord> Records) ByPattern(Survey survey, double firstDepth, double increment,
        AcquisitionDirection direction, IReadOnlyList<ShotKind> pattern)
    {
        if (pattern.Count == 0)
        {
            throw new ArgumentException("The shot pattern must contain at least one shot.", nameof(pattern));
        }

        var ordered = survey.Records.Where(r => !r.Excluded).OrderBy(r => r.Sequence).ToList();
        var stations = new List<Station>();
        var updates = new Dictionary<Guid, SeismicRecord>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var level = i / pattern.Count;
            var depth = direction == AcquisitionDirection.BottomUp ? firstDepth - level * increment : firstDepth + level * increment;
            if (level >= stations.Count)
            {
                stations.Add(new Station { Id = Guid.NewGuid(), MeasuredDepth = Math.Round(depth, 6) });
            }

            updates[ordered[i].Id] = ordered[i] with { StationId = stations[level].Id, Shot = pattern[i % pattern.Count] };
        }

        return ([.. stations], survey.Records.Select(r => updates.TryGetValue(r.Id, out var u) ? u : r with { StationId = r.Excluded ? r.StationId : null }).ToImmutableList());
    }

    /// <summary>Creates one depth per distinct suggested depth and uses suggested shot kinds.</summary>
    public static (ImmutableList<Station> Stations, ImmutableList<SeismicRecord> Records) FromSuggestions(Survey survey)
    {
        var stations = survey.Stations.ToList();
        var records = new List<SeismicRecord>();
        foreach (var r in survey.Records)
        {
            if (r.SuggestedDepth is not { } d || r.StationId is not null)
            {
                records.Add(r);
                continue;
            }

            var station = stations.FirstOrDefault(s => Math.Abs(s.MeasuredDepth - d) < 1e-6);
            if (station is null)
            {
                station = new Station { Id = Guid.NewGuid(), MeasuredDepth = d };
                stations.Add(station);
            }

            records.Add(r with { StationId = station.Id, Shot = r.SuggestedShot != ShotKind.Unknown ? r.SuggestedShot : r.Shot });
        }

        return ([.. stations.OrderBy(s => s.MeasuredDepth)], [.. records]);
    }

    /// <summary>Mirrors depths about the middle of the surveyed interval (fixes a reversed acquisition direction).</summary>
    public static ImmutableList<Station> Mirror(IReadOnlyList<Station> stations)
    {
        if (stations.Count == 0)
        {
            return [];
        }

        var sum = stations.Min(s => s.MeasuredDepth) + stations.Max(s => s.MeasuredDepth);
        return stations.Select(s => s with { MeasuredDepth = Math.Round(sum - s.MeasuredDepth, 6) }).ToImmutableList();
    }

    /// <summary>Removes depths that no longer have records.</summary>
    public static ImmutableList<Station> Prune(Survey survey) =>
        survey.Stations.Where(s => survey.Records.Any(r => r.StationId == s.Id)).ToImmutableList();
}
