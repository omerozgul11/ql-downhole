using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DownholeStudio.Domain;

namespace DownholeStudio.Formats;

/// <summary>A depth suggested by file content or name. Never applied without user approval.</summary>
public sealed record DepthHint(double Depth, string Source);

/// <summary>One file examined before import.</summary>
public sealed record ImportCandidate
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long Length { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset? LastWriteTime { get; init; }
    public string? ReaderName { get; init; }
    public SeismicFileMetadata? Metadata { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorDetails { get; init; }
    public DepthHint? DepthHint { get; init; }
    public ShotKind ShotHint { get; init; }
    public string? ShotHintSource { get; init; }

    public bool IsReadable => Metadata is not null;
}

public sealed record ImportPreview
{
    public ImmutableList<ImportCandidate> Files { get; init; } = [];
    public ImmutableList<Diagnostic> Diagnostics { get; init; } = [];

    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);

    public int ReadableCount => Files.Count(f => f.IsReadable);
}

/// <summary>
/// Reads headers of candidate files and reports problems (errors, warnings, information). It
/// never repairs data; suggested fixes are offered for the user to approve explicitly.
/// </summary>
public sealed partial class ImportInspector(SeismicFormatRegistry registry)
{
    public async Task<ImportPreview> InspectAsync(IEnumerable<string> paths, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var list = paths.Distinct(StringComparer.Ordinal).ToList();
        var files = new List<ImportCandidate>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            files.Add(await InspectFileAsync(list[i], ct).ConfigureAwait(false));
            progress?.Report((i + 1.0) / list.Count);
        }

        files = OrderFiles(files);
        return new ImportPreview { Files = [.. files], Diagnostics = [.. Validate(files)] };
    }

    /// <summary>Expands folders to the supported files they contain (non-recursive by default).</summary>
    public IReadOnlyList<string> ExpandPaths(IEnumerable<string> paths, bool recursive = false)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                result.AddRange(Directory.EnumerateFiles(p, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(registry.IsSupportedExtension));
            }
            else if (File.Exists(p))
            {
                result.Add(p);
            }
        }

        return result.OrderBy(p => p, NaturalStringComparer.Instance).ToList();
    }

    public async Task<ImportCandidate> InspectFileAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var name = info.Name;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ImportCandidate
            {
                Path = path, FileName = name, ErrorMessage = $"\"{name}\" could not be opened. Check that it exists and is not locked by another program.",
                ErrorDetails = ex.Message,
            };
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var reader = registry.Find(bytes.AsSpan(0, Math.Min(64, bytes.Length)), name);
        var baseCandidate = new ImportCandidate
        {
            Path = path, FileName = name, Length = bytes.Length, Sha256 = sha, LastWriteTime = info.LastWriteTimeUtc,
        };
        if (reader is null)
        {
            return baseCandidate with
            {
                ErrorMessage = $"\"{name}\" is not in a supported format. Downhole Studio reads: {string.Join(", ", registry.Readers.Select(r => r.Name))}.",
                ErrorDetails = "No reader recognised the file signature.",
            };
        }

        try
        {
            await using var ms = new MemoryStream(bytes, writable: false);
            var metadata = await reader.ReadMetadataAsync(ms, name, ct).ConfigureAwait(false);
            var (shot, shotSource) = GuessShot(name, metadata);
            return baseCandidate with
            {
                ReaderName = reader.Name, Metadata = metadata, DepthHint = GuessDepth(name, metadata), ShotHint = shot, ShotHintSource = shotSource,
            };
        }
        catch (SeismicFormatException ex)
        {
            return baseCandidate with { ReaderName = reader.Name, ErrorMessage = ex.Message, ErrorDetails = ex.TechnicalDetails };
        }
    }

    private static List<ImportCandidate> OrderFiles(List<ImportCandidate> files)
    {
        // Natural file-name order is the acquisition order most systems produce (file 1, 2, …, 10).
        return files.OrderBy(f => f.FileName, NaturalStringComparer.Instance).ToList();
    }

    internal static IEnumerable<Diagnostic> Validate(IReadOnlyList<ImportCandidate> files)
    {
        foreach (var f in files.Where(f => !f.IsReadable))
        {
            yield return Diagnostic.Error("import.unreadable", f.ErrorMessage ?? $"\"{f.FileName}\" could not be read.", f.ErrorDetails);
        }

        var readable = files.Where(f => f.IsReadable).ToList();
        if (readable.Count == 0)
        {
            if (files.Count > 0)
            {
                yield return Diagnostic.Error("import.nothing", "None of the selected files can be imported.");
            }

            yield break;
        }

        foreach (var group in readable.GroupBy(f => f.Sha256).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ", group.Select(f => f.FileName));
            yield return WithFix(Diagnostic.Error("import.duplicate", $"These files are identical copies of the same record: {names}.",
                $"SHA-256 {group.Key}"), "Import only the first copy");
        }

        // Sample interval consistency.
        var intervals = readable.SelectMany(f => f.Metadata!.Traces.Select(t => (f, t.SampleInterval))).ToList();
        var commonInterval = Mode(intervals.Select(x => x.SampleInterval));
        foreach (var (f, dt) in intervals.Where(x => !Same(x.SampleInterval, commonInterval)).DistinctBy(x => x.f.FileName))
        {
            yield return Diagnostic.Warning("import.sampleRate",
                $"\"{f.FileName}\" uses a different sample rate ({Rate(dt)}) from the other measurements ({Rate(commonInterval)}). Review the file before continuing.",
                $"Sample interval {dt.ToString("R", CultureInfo.InvariantCulture)} s vs {commonInterval.ToString("R", CultureInfo.InvariantCulture)} s.");
        }

        var counts = readable.SelectMany(f => f.Metadata!.Traces.Select(t => (f, t.SampleCount))).ToList();
        var commonCount = (int)Mode(counts.Select(x => (double)x.SampleCount));
        foreach (var (f, n) in counts.Where(x => x.SampleCount != commonCount).DistinctBy(x => x.f.FileName))
        {
            yield return Diagnostic.Warning("import.sampleCount",
                $"\"{f.FileName}\" has traces with {n} samples while most records have {commonCount}. Record lengths differ.",
                null);
        }

        var commonTraceCount = (int)Mode(readable.Select(f => (double)f.Metadata!.Traces.Count));
        var commonChannels = readable.GroupBy(f => string.Join(",", f.Metadata!.Traces.Select(t => t.ChannelNumber).OrderBy(c => c)))
            .OrderByDescending(g => g.Count()).First().First().Metadata!.Traces.Select(t => t.ChannelNumber).ToHashSet();
        foreach (var f in readable)
        {
            var channels = f.Metadata!.Traces.Select(t => t.ChannelNumber).ToHashSet();
            if (f.Metadata.Traces.Count < commonTraceCount)
            {
                yield return Diagnostic.Warning("import.missingTraces",
                    $"\"{f.FileName}\" contains {f.Metadata.Traces.Count} traces; other records contain {commonTraceCount}. Traces may be missing.");
            }

            var missing = commonChannels.Except(channels).OrderBy(c => c).ToList();
            if (missing.Count > 0)
            {
                yield return Diagnostic.Warning("import.missingChannels",
                    $"\"{f.FileName}\" has no channel {string.Join(", ", missing)}, which the other records contain.");
            }

            if (channels.Count != f.Metadata.Traces.Count)
            {
                yield return Diagnostic.Warning("import.duplicateChannel", $"\"{f.FileName}\" uses the same channel number for more than one trace.");
            }

            foreach (var note in f.Metadata.ReaderNotes)
            {
                yield return Diagnostic.Info("import.readerNote", $"\"{f.FileName}\": {note}");
            }

        }

        foreach (var g in readable.Where(f => f.Metadata!.Traces.Any(t => Math.Abs(t.Delay) > 0))
                     .GroupBy(f => Math.Round(f.Metadata!.Traces.First(t => Math.Abs(t.Delay) > 0).Delay, 9)))
        {
            var what = g.Key < 0 ? $"start {-g.Key * 1000:0.###} ms before the trigger (pre-trigger samples)" : $"start {g.Key * 1000:0.###} ms after the trigger";
            yield return Diagnostic.Info("import.delay",
                $"{g.Count()} record(s) {what}. Times are measured from the trigger, so this is already part of the time axis.",
                string.Join(", ", g.Take(10).Select(f => f.FileName)) + (g.Count() > 10 ? $" and {g.Count() - 10} more" : ""));
        }

        // Depth hints: duplicates and ordering.
        var hinted = readable.Where(f => f.DepthHint is not null).ToList();
        if (hinted.Count > 0 && hinted.Count < readable.Count)
        {
            yield return Diagnostic.Info("import.depthHintsPartial",
                $"Depths could be suggested for {hinted.Count} of {readable.Count} records. Assign the remaining depths on the Geometry page.");
        }

        foreach (var g in hinted.GroupBy(f => (Math.Round(f.DepthHint!.Depth, 3), f.ShotHint)).Where(g => g.Count() > 1))
        {
            var shot = g.Key.ShotHint == ShotKind.Unknown ? "" : $" ({ShotName(g.Key.ShotHint)})";
            yield return Diagnostic.Warning("import.duplicateDepth",
                $"{g.Count()} records suggest the same depth {g.Key.Item1:0.###} m{shot}: {string.Join(", ", g.Select(f => f.FileName))}. They may be repeated shots or a depth-entry mistake.");
        }

        if (hinted.Count >= 3)
        {
            var depths = hinted.Select(f => f.DepthHint!.Depth).ToList();
            var steps = depths.Zip(depths.Skip(1), (a, b) => Math.Sign(b - a)).Where(s => s != 0).ToList();
            if (steps.Count > 0 && steps.Any(s => s > 0) && steps.Any(s => s < 0))
            {
                yield return Diagnostic.Warning("import.depthSequence",
                    "The suggested depths do not increase or decrease steadily in file order. Check the depth sequence before assigning depths.",
                    string.Join(", ", hinted.Select(f => $"{f.FileName}: {f.DepthHint!.Depth:0.###} m")));
            }
        }

        var timed = readable.Where(f => f.Metadata!.AcquiredAt is not null).ToList();
        if (timed.Count >= 2)
        {
            var outOfOrder = timed.Zip(timed.Skip(1)).Where(p => p.Second.Metadata!.AcquiredAt < p.First.Metadata!.AcquiredAt).ToList();
            if (outOfOrder.Count > 0)
            {
                yield return WithFix(Diagnostic.Warning("import.acquisitionOrder",
                    $"File order and recording time disagree for {outOfOrder.Count} pair(s) of records, e.g. \"{outOfOrder[0].Second.FileName}\" was recorded before \"{outOfOrder[0].First.FileName}\"."),
                    "Order records by recording time");
            }
        }

        yield return Diagnostic.Info("import.summary",
            $"{readable.Count} record(s), {readable.Sum(f => f.Metadata!.Traces.Count)} traces, {Rate(commonInterval)} sampling, {commonCount} samples per trace ({commonCount * commonInterval * 1000:0.#} ms).");
    }

    private static string ShotName(ShotKind s) => s switch
    {
        ShotKind.P => "P",
        ShotKind.SPositive => "S+",
        ShotKind.SNegative => "S−",
        _ => "?",
    };

    private static Diagnostic WithFix(Diagnostic d, string? fix) => fix is null ? d : d with { SuggestedFix = fix };

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));

    private static double Mode(IEnumerable<double> values) =>
        values.GroupBy(v => Math.Round(v, 12)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();

    private static string Rate(double dt)
    {
        var hz = 1.0 / dt;
        return hz >= 1000 ? $"{hz / 1000:0.###} kHz" : $"{hz:0.###} Hz";
    }

    /// <summary>Suggests a receiver depth from SEG-2 keywords or the file name.</summary>
    public static DepthHint? GuessDepth(string fileName, SeismicFileMetadata metadata)
    {
        foreach (var (key, value) in metadata.Keywords.Concat(metadata.Traces.FirstOrDefault()?.Keywords ?? ImmutableDictionary<string, string>.Empty))
        {
            var m = DepthKeyword().Match(key + " " + value);
            if (m.Success && double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return new DepthHint(d, $"{key} keyword");
            }
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var fm = FileDepth().Match(stem);
        if (fm.Success && double.TryParse(fm.Groups["v"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var fd))
        {
            return new DepthHint(fd, "file name");
        }

        return null;
    }

    /// <summary>Suggests the shot type from keywords or file name tokens (P, S+, S−, left/right…).</summary>
    public static (ShotKind Shot, string? Source) GuessShot(string fileName, SeismicFileMetadata metadata)
    {
        var text = (metadata.Keywords.TryGetValue("NOTE", out var note) ? note : "") + " " +
                   (metadata.Keywords.TryGetValue("SOURCE", out var src) ? src : "");
        var fromNote = ShotFromText(text);
        if (fromNote != ShotKind.Unknown)
        {
            return (fromNote, "NOTE/SOURCE keyword");
        }

        var fromName = ShotFromText(Path.GetFileNameWithoutExtension(fileName));
        return fromName == ShotKind.Unknown ? (ShotKind.Unknown, null) : (fromName, "file name");
    }

    private static ShotKind ShotFromText(string text)
    {
        var t = " " + text.ToUpperInvariant().Replace('_', ' ').Replace('-', ' ') + " ";
        if (Regex.IsMatch(t, @"(S\+|SH?L\b|\bS ?LEFT|\bLEFT\b|\bSPOS|\bS ?POS)"))
        {
            return ShotKind.SPositive;
        }

        if (Regex.IsMatch(t, @"(S−|S\s?NEG|SH?R\b|\bS ?RIGHT|\bRIGHT\b|\bSNEG)"))
        {
            return ShotKind.SNegative;
        }

        if (Regex.IsMatch(t, @"(\bP ?WAVE\b|\bP\d|\bP\b|\bPW\b)"))
        {
            return ShotKind.P;
        }

        return ShotKind.Unknown;
    }

    [GeneratedRegex(@"\bDEPTH\b\s*[=:]?\s*(?<v>-?\d+(\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DepthKeyword();

    [GeneratedRegex(@"(?<![A-Za-z])(?:D|Z|DEPTH)?[ _-]?(?<v>\d+(?:[.,]\d+)?)\s*(?:M|m)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex FileDepth();
}

/// <summary>Orders "file2" before "file10".</summary>
public sealed partial class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return string.Compare(x, y, StringComparison.Ordinal);
        }

        var a = Chunks().Matches(x);
        var b = Chunks().Matches(y);
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var ca = a[i].Value;
            var cb = b[i].Value;
            int c;
            if (char.IsDigit(ca[0]) && char.IsDigit(cb[0]) && decimal.TryParse(ca, NumberStyles.Integer, CultureInfo.InvariantCulture, out var da)
                && decimal.TryParse(cb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var db))
            {
                c = da.CompareTo(db);
            }
            else
            {
                c = string.Compare(ca, cb, StringComparison.OrdinalIgnoreCase);
            }

            if (c != 0)
            {
                return c;
            }
        }

        return a.Count.CompareTo(b.Count);
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Chunks();
}
