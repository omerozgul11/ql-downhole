using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DownholeStudio.Domain;

namespace DownholeStudio.Persistence;

/// <summary>Everything that is saved in a .dhsx file.</summary>
public sealed class ProjectDocument
{
    public required ProjectState State { get; set; }
    public ImmutableList<HistoryEntry> History { get; set; } = [];
    public ImmutableDictionary<Guid, ProjectState> SnapshotStates { get; set; } = ImmutableDictionary<Guid, ProjectState>.Empty;
    public ProjectAssetStore Assets { get; init; } = new();
}

public sealed record PackageManifest
{
    public string Format { get; init; } = ProjectPackage.FormatId;
    public int SchemaVersion { get; init; } = ProjectState.CurrentSchemaVersion;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public Guid ProjectId { get; init; }
    public string ProjectName { get; init; } = "";
    /// <summary>SHA-256 of every other entry, for integrity checking.</summary>
    public ImmutableSortedDictionary<string, string> Entries { get; init; } = ImmutableSortedDictionary<string, string>.Empty;
}

/// <summary>Reads and writes the .dhsx project container (a ZIP archive, see ARCHITECTURE.md §7).</summary>
public static class ProjectPackage
{
    public const string FormatId = "quakelogic.downhole-studio.project";
    public const string Extension = ".dhsx";

    /// <summary>
    /// Saves atomically: the package is written to a temporary file next to the target, re-opened
    /// and verified, and only then replaces the target. The previous file is kept as
    /// "&lt;name&gt;.dhsx.bak". A failure at any point leaves the existing file untouched.
    /// </summary>
    public static void Save(ProjectDocument document, string path, string appVersion, DateTimeOffset now)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ProjectFileException("The project location is not valid.");
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        var needed = RequiredAssetEntries(document).ToList();

        // Pull every lazily referenced asset into memory: the backing file may be the one replaced.
        document.Assets.Materialise(needed);
        try
        {
            WritePackage(document, temp, needed, appVersion, now);
            Verify(temp);
            var reattach = string.Equals(document.Assets.PackagePath, full, StringComparison.OrdinalIgnoreCase);
            if (reattach)
            {
                document.Assets.DetachPackage();
            }

            if (File.Exists(full))
            {
                File.Replace(temp, full, full + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(temp);
            throw new ProjectFileException($"The project could not be saved to \"{full}\". Your previous file has not been changed.", ex.Message, ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        using var zip = ZipFile.OpenRead(full);
        document.Assets.AttachPackage(full, zip.Entries.Select(e => e.FullName).ToList());
    }

    /// <summary>Writes a package to an arbitrary stream-backed file without touching the document's backing file.</summary>
    public static void WriteCopy(ProjectDocument document, string path, string appVersion, DateTimeOffset now)
    {
        var needed = RequiredAssetEntries(document).ToList();
        WritePackage(document, path, needed, appVersion, now);
    }

    public static ProjectDocument Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new ProjectFileException($"The project file \"{full}\" does not exist.");
        }

        try
        {
            using var zip = ZipFile.OpenRead(full);
            var manifest = ReadManifest(zip);
            var integrity = new List<string>();
            foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json"))
            {
                if (manifest.Entries.TryGetValue(entry.FullName, out var expected) && entry.FullName.EndsWith(".json", StringComparison.Ordinal)
                    && Hash(ReadEntry(entry)) != expected)
                {
                    integrity.Add(entry.FullName);
                }
            }

            if (integrity.Count > 0)
            {
                throw new ProjectFileException("The project file is damaged: some of its content does not match its checksums.",
                    "Mismatched entries: " + string.Join(", ", integrity));
            }

            var state = ReadState(zip, "project.json");
            var history = zip.GetEntry("history.json") is { } h
                ? JsonSerializer.Deserialize<ImmutableList<HistoryEntry>>(ReadEntry(h), ProjectJson.Options) ?? []
                : [];
            var snapshots = ImmutableDictionary.CreateBuilder<Guid, ProjectState>();
            foreach (var info in state.Snapshots)
            {
                if (zip.GetEntry($"snapshots/{info.Id:N}.json") is not null)
                {
                    snapshots[info.Id] = ReadState(zip, $"snapshots/{info.Id:N}.json");
                }
            }

            var assets = new ProjectAssetStore();
            assets.AttachPackage(full, zip.Entries.Select(e => e.FullName).ToList());
            return new ProjectDocument { State = state, History = history, SnapshotStates = snapshots.ToImmutable(), Assets = assets };
        }
        catch (InvalidDataException ex)
        {
            throw new ProjectFileException("This file is not a Downhole Studio project or it is damaged.", ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new ProjectFileException("The project file could not be read: its content is not valid.", ex.Message, ex);
        }
    }

    /// <summary>Reads only the manifest (for recent-project lists and recovery prompts).</summary>
    public static PackageManifest ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return ReadManifest(zip);
    }

    public static IEnumerable<string> RequiredAssetEntries(ProjectDocument document)
    {
        var states = new[] { document.State }.Concat(document.SnapshotStates.Values).ToList();
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in states)
        {
            foreach (var record in s.Survey.Records)
            {
                entries.Add(record.Source.EmbeddedPath);
                foreach (var t in record.Traces)
                {
                    entries.Add(ProjectAssetStore.TraceEntry(t.Id));
                }
            }

            if (s.Report.Branding.LogoAsset is { } logo)
            {
                entries.Add(logo);
            }
        }

        return entries;
    }

    private static void WritePackage(ProjectDocument document, string path, IReadOnlyList<string> assetEntries, string appVersion, DateTimeOffset now)
    {
        var hashes = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Put(string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = now;
                using var s = entry.Open();
                s.Write(bytes);
                hashes[name] = Hash(bytes);
            }

            Put("project.json", Encoding.UTF8.GetBytes(ProjectJson.Serialize(document.State with { SchemaVersion = ProjectState.CurrentSchemaVersion })));
            Put("history.json", Encoding.UTF8.GetBytes(ProjectJson.Serialize(document.History)));
            foreach (var (id, snapshot) in document.SnapshotStates)
            {
                Put($"snapshots/{id:N}.json", Encoding.UTF8.GetBytes(ProjectJson.Serialize(snapshot)));
            }

            foreach (var name in assetEntries)
            {
                var bytes = document.Assets.GetEntryBytes(name)
                    ?? throw new ProjectFileException("Some imported data could not be found while saving; the project was not saved.", $"Missing asset {name}.");
                Put(name, bytes, name.StartsWith("raw/", StringComparison.Ordinal) ? CompressionLevel.Optimal : CompressionLevel.Fastest);
            }

            var manifest = new PackageManifest
            {
                AppVersion = appVersion, SavedAt = now, ProjectId = document.State.Metadata.ProjectId, ProjectName = document.State.Metadata.Name,
                Entries = hashes.ToImmutable(),
            };
            var m = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using var ms = m.Open();
            ms.Write(Encoding.UTF8.GetBytes(ProjectJson.Serialize(manifest)));
        }
    }

    private static void Verify(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadManifest(zip);
        foreach (var (name, hash) in manifest.Entries)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Entry {name} missing after write.");
            if (Hash(ReadEntry(entry)) != hash)
            {
                throw new InvalidDataException($"Entry {name} failed verification after write.");
            }
        }
    }

    private static PackageManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new ProjectFileException("This file is not a Downhole Studio project (no manifest).");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(ReadEntry(entry), ProjectJson.Options)
            ?? throw new ProjectFileException("The project manifest is empty.");
        if (manifest.Format != FormatId)
        {
            throw new ProjectFileException("This file is not a Downhole Studio project.", $"Format id '{manifest.Format}'.");
        }

        return manifest;
    }

    private static ProjectState ReadState(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new ProjectFileException("The project file is incomplete.", $"{name} missing.");
        var node = JsonNode.Parse(ReadEntry(entry)) as JsonObject ?? throw new ProjectFileException("The project data is not valid.", $"{name} is not an object.");
        ProjectMigrations.Migrate(node);
        return node.Deserialize<ProjectState>(ProjectJson.Options) ?? throw new ProjectFileException("The project data is empty.");
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
