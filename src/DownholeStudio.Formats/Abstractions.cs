using System.Collections.Immutable;

namespace DownholeStudio.Formats;

/// <summary>Metadata of one trace as read from a file.</summary>
public sealed record SeismicTraceHeader
{
    public int Index { get; init; }
    public int ChannelNumber { get; init; }
    public int SampleCount { get; init; }
    /// <summary>Seconds.</summary>
    public double SampleInterval { get; init; }
    /// <summary>Seconds from trigger to first sample.</summary>
    public double Delay { get; init; }
    public string SampleFormat { get; init; } = "";
    public double? DescalingFactor { get; init; }
    public double? ReceiverLocation { get; init; }
    public double? SourceLocation { get; init; }
    public string? Label { get; init; }
    public ImmutableDictionary<string, string> Keywords { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>File-level metadata (no samples).</summary>
public sealed record SeismicFileMetadata
{
    public required string FormatName { get; init; }
    public required string FileName { get; init; }
    public string ByteOrder { get; init; } = "";
    public int Revision { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public ImmutableDictionary<string, string> Keywords { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableList<SeismicTraceHeader> Traces { get; init; } = [];
    /// <summary>Non-fatal observations made while reading.</summary>
    public ImmutableList<string> ReaderNotes { get; init; } = [];
}

/// <summary>A fully decoded file: metadata plus samples (as recorded, before any descaling).</summary>
public sealed record SeismicFileData(SeismicFileMetadata Metadata, IReadOnlyList<double[]> Samples);

/// <summary>
/// Plug-in contract for seismic file formats. Implementations must not touch UI components and
/// must throw <see cref="SeismicFormatException"/> with a plain-language message on bad input.
/// </summary>
public interface ISeismicDataReader
{
    string Name { get; }

    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Quick check based on the first bytes of the file (at least 32 bytes when available).</summary>
    bool CanRead(ReadOnlySpan<byte> header, string fileName);

    Task<SeismicFileMetadata> ReadMetadataAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);

    Task<SeismicFileData> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>A file could not be read. <see cref="Exception.Message"/> is written for users.</summary>
public sealed class SeismicFormatException : Exception
{
    public SeismicFormatException(string message, string? technicalDetails = null, Exception? inner = null)
        : base(message, inner) => TechnicalDetails = technicalDetails;

    public string? TechnicalDetails { get; }
}

/// <summary>Registry of available readers; new formats are added by registering another reader.</summary>
public sealed class SeismicFormatRegistry
{
    private readonly List<ISeismicDataReader> _readers;

    public SeismicFormatRegistry(IEnumerable<ISeismicDataReader> readers) => _readers = readers.ToList();

    public static SeismicFormatRegistry CreateDefault() => new([new Seg2Reader()]);

    public IReadOnlyList<ISeismicDataReader> Readers => _readers;

    public IEnumerable<string> SupportedExtensions => _readers.SelectMany(r => r.SupportedExtensions).Distinct(StringComparer.OrdinalIgnoreCase);

    public bool IsSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a reader by content first, then by extension.</summary>
    public ISeismicDataReader? Find(ReadOnlySpan<byte> header, string fileName)
    {
        foreach (var r in _readers)
        {
            if (r.CanRead(header, fileName))
            {
                return r;
            }
        }

        return null;
    }

    public async Task<ISeismicDataReader?> FindAsync(string path, CancellationToken ct = default)
    {
        var buffer = new byte[64];
        await using var fs = File.OpenRead(path);
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return Find(buffer.AsSpan(0, read), Path.GetFileName(path));
    }
}
