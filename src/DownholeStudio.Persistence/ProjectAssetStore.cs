using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace DownholeStudio.Persistence;

/// <summary>
/// Holds a project's binary assets: decoded trace samples, the original imported files and other
/// assets such as a report logo. Assets saved in a project file are read lazily from it; new assets
/// are kept in memory until the next save. Samples are never handed out for modification.
/// </summary>
public sealed class ProjectAssetStore
{
    private readonly ConcurrentDictionary<Guid, double[]> _traces = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly object _packageLock = new();
    private string? _packagePath;
    private ZipArchive? _zip;
    private HashSet<string> _packageEntries = new(StringComparer.Ordinal);

    public static string TraceEntry(Guid id) => $"traces/{id:N}.f64";

    /// <summary>Points the store at a saved package for lazy reads.</summary>
    public void AttachPackage(string path, IEnumerable<string> entryNames)
    {
        lock (_packageLock)
        {
            _zip?.Dispose();
            _zip = null;
            _packagePath = path;
            _packageEntries = new HashSet<string>(entryNames, StringComparer.Ordinal);
        }
    }

    /// <summary>Closes the backing package (required before the file is replaced). Call <see cref="Materialise"/> first.</summary>
    public void DetachPackage()
    {
        lock (_packageLock)
        {
            _zip?.Dispose();
            _zip = null;
            _packagePath = null;
            _packageEntries = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public string? PackagePath
    {
        get
        {
            lock (_packageLock)
            {
                return _packagePath;
            }
        }
    }

    public void AddTrace(Guid id, double[] samples) => _traces[id] = samples;

    public bool HasTrace(Guid id) => _traces.ContainsKey(id) || HasPackageEntry(TraceEntry(id));

    public ReadOnlyMemory<double> GetTrace(Guid id)
    {
        if (_traces.TryGetValue(id, out var samples))
        {
            return samples;
        }

        var bytes = ReadPackageEntry(TraceEntry(id))
            ?? throw new ProjectFileException("Trace data is missing from the project file.", $"Entry {TraceEntry(id)} not found.");
        var decoded = DecodeF64(bytes);
        _traces.TryAdd(id, decoded);
        return decoded;
    }

    public void AddFile(string entryName, byte[] content) => _files[entryName] = content;

    public bool HasFile(string entryName) => _files.ContainsKey(entryName) || HasPackageEntry(entryName);

    public byte[]? GetFile(string entryName) =>
        _files.TryGetValue(entryName, out var bytes) ? bytes : ReadPackageEntry(entryName);

    public void RemoveFile(string entryName) => _files.TryRemove(entryName, out _);

    /// <summary>Every asset entry that must be written to a package (traces + files).</summary>
    public IEnumerable<string> AllEntryNames(IEnumerable<Guid> traceIds, IEnumerable<string> fileEntries) =>
        traceIds.Select(TraceEntry).Concat(fileEntries).Distinct(StringComparer.Ordinal);

    /// <summary>Bytes for an entry, from memory or the attached package.</summary>
    public byte[]? GetEntryBytes(string entryName)
    {
        if (entryName.StartsWith("traces/", StringComparison.Ordinal) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(entryName), "N", out var id)
            && _traces.TryGetValue(id, out var samples))
        {
            return EncodeF64(samples);
        }

        return GetFile(entryName);
    }

    /// <summary>Loads every lazily referenced entry into memory (before the backing file is replaced).</summary>
    public void Materialise(IEnumerable<string> entryNames)
    {
        foreach (var name in entryNames)
        {
            if (name.StartsWith("traces/", StringComparison.Ordinal) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out var id))
            {
                if (!_traces.ContainsKey(id))
                {
                    GetTrace(id);
                }
            }
            else if (!_files.ContainsKey(name) && ReadPackageEntry(name) is { } bytes)
            {
                _files[name] = bytes;
            }
        }
    }

    public static byte[] EncodeF64(ReadOnlySpan<double> samples)
    {
        var bytes = new byte[samples.Length * 8];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8 * i), samples[i]);
        }

        return bytes;
    }

    public static double[] DecodeF64(ReadOnlySpan<byte> bytes)
    {
        var samples = new double[bytes.Length / 8];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(8 * i)..]);
        }

        return samples;
    }

    private bool HasPackageEntry(string name)
    {
        lock (_packageLock)
        {
            return _packagePath is not null && _packageEntries.Contains(name);
        }
    }

    private byte[]? ReadPackageEntry(string name)
    {
        lock (_packageLock)
        {
            if (_packagePath is null || !_packageEntries.Contains(name))
            {
                return null;
            }

            _zip ??= new ZipArchive(new FileStream(_packagePath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
            var entry = _zip.GetEntry(name);
            if (entry is null)
            {
                return null;
            }

            using var s = entry.Open();
            using var ms = new MemoryStream((int)Math.Max(0, entry.Length));
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
