using System.Collections.Immutable;

namespace DownholeStudio.Domain;

/// <summary>Identity of an imported source file. The original bytes are embedded in the project.</summary>
public sealed record SourceFileInfo
{
    public required string FileName { get; init; }
    public string? OriginalPath { get; init; }
    public long Length { get; init; }
    /// <summary>Lower-case hexadecimal SHA-256 of the original file.</summary>
    public required string Sha256 { get; init; }
    public DateTimeOffset? LastWriteTime { get; init; }
    /// <summary>Path of the byte-identical copy inside the project container.</summary>
    public required string EmbeddedPath { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
}

/// <summary>Metadata of one recorded trace. Samples live in the trace store.</summary>
public sealed record TraceInfo
{
    public required Guid Id { get; init; }
    /// <summary>Zero-based position of the trace inside its record.</summary>
    public int Index { get; init; }
    public int ChannelNumber { get; init; }
    public int SampleCount { get; init; }
    /// <summary>Sample interval in seconds.</summary>
    public double SampleInterval { get; init; }
    /// <summary>Time of the first sample relative to the trigger, in seconds (SEG-2 DELAY).</summary>
    public double Delay { get; init; }
    public string? Label { get; init; }
    public double? ReceiverLocation { get; init; }
    public double? SourceLocation { get; init; }
    public ImmutableDictionary<string, string> Header { get; init; } = ImmutableDictionary<string, string>.Empty;

    public double Duration => SampleCount <= 1 ? 0 : (SampleCount - 1) * SampleInterval;
}

/// <summary>One imported shot record (normally one file).</summary>
public sealed record SeismicRecord
{
    public required Guid Id { get; init; }
    public required SourceFileInfo Source { get; init; }
    public required string FormatName { get; init; }
    /// <summary>Order of acquisition (as imported, after optional user re-ordering).</summary>
    public int Sequence { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public string? Label { get; init; }
    public ImmutableList<TraceInfo> Traces { get; init; } = [];
    public ImmutableDictionary<string, string> Header { get; init; } = ImmutableDictionary<string, string>.Empty;
    public Guid? StationId { get; init; }
    public ShotKind Shot { get; init; } = ShotKind.Unknown;
    public bool Excluded { get; init; }
    public string? Comment { get; init; }
    /// <summary>Depth suggested by the file (keyword or name). Only a suggestion; never applied automatically.</summary>
    public double? SuggestedDepth { get; init; }
    /// <summary>Shot kind suggested by the file.</summary>
    public ShotKind SuggestedShot { get; init; }
    public string? SuggestionSource { get; init; }
}

/// <summary>A measurement level (receiver position) in the borehole.</summary>
public sealed record Station
{
    public required Guid Id { get; init; }
    /// <summary>Depth measured from the depth reference (ground surface or collar), metres.</summary>
    public double MeasuredDepth { get; init; }
    public bool Excluded { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Assignment of one recorder channel to a component.</summary>
public sealed record ChannelAssignment
{
    public int ChannelNumber { get; init; }
    public ComponentRole Role { get; init; }
    /// <summary>Inverts the polarity of this channel for every record (sensor wiring convention).</summary>
    public bool ReversePolarity { get; init; }
    public string? Label { get; init; }
}

/// <summary>Channel map shared by all records of a survey.</summary>
public sealed record ChannelMap
{
    public string? ProfileName { get; init; }
    public ImmutableList<ChannelAssignment> Channels { get; init; } = [];

    public ChannelAssignment? For(int channelNumber) => Channels.FirstOrDefault(c => c.ChannelNumber == channelNumber);

    public ComponentRole RoleOf(int channelNumber) => For(channelNumber)?.Role ?? ComponentRole.Unassigned;

    public static ComponentGroup? GroupOf(ComponentRole role) => role switch
    {
        ComponentRole.Vertical => ComponentGroup.Vertical,
        ComponentRole.HorizontalX or ComponentRole.HorizontalY => ComponentGroup.Horizontal,
        ComponentRole.Reference => ComponentGroup.Reference,
        _ => null,
    };
}

/// <summary>Reusable instrument/channel profile (e.g. "QuakeLogic 3C Borehole").</summary>
public sealed record InstrumentProfile
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public ImmutableList<ChannelAssignment> Channels { get; init; } = [];
    public bool BuiltIn { get; init; }
}

public sealed record BoreholeInfo
{
    public string Name { get; init; } = "BH-1";
    public double? Easting { get; init; }
    public double? Northing { get; init; }
    public string? CoordinateSystem { get; init; }
    /// <summary>Ground elevation at the borehole, metres above datum.</summary>
    public double? GroundElevation { get; init; }
    /// <summary>Height of the collar (top of casing) above the ground surface, metres.</summary>
    public double CollarHeight { get; init; }
    public double? TotalDepth { get; init; }
}

/// <summary>Source position for one wave type. Offsets are horizontal distances from the borehole axis.</summary>
public sealed record SourceGeometry
{
    /// <summary>Horizontal distance from the borehole axis to the source, metres.</summary>
    public double HorizontalOffset { get; init; } = 1.5;
    /// <summary>Depth of the source below the ground surface (negative when above ground), metres.</summary>
    public double SourceDepth { get; init; }
    public double? Azimuth { get; init; }
    public double? Easting { get; init; }
    public double? Northing { get; init; }
    public SourceOrientation Orientation { get; init; } = SourceOrientation.Vertical;
    public string? Description { get; init; }
}

public sealed record SurveyGeometry
{
    public BoreholeInfo Borehole { get; init; } = new();
    public SourceGeometry PSource { get; init; } = new() { Orientation = SourceOrientation.Vertical, Description = "Plate struck vertically" };
    public SourceGeometry SSource { get; init; } = new() { Orientation = SourceOrientation.HorizontalCrossline, Description = "Beam struck horizontally at both ends" };
    public AcquisitionDirection Direction { get; init; } = AcquisitionDirection.Unknown;
    public DepthReference DepthReference { get; init; } = DepthReference.GroundSurface;
    public double DepthIncrement { get; init; } = 1.0;
    /// <summary>Clockwise angle from north to the sensor X axis, degrees, when known.</summary>
    public double? SensorAzimuth { get; init; }
    public string? SensorOrientationNote { get; init; }

    /// <summary>Converts a measured depth (from the selected reference) to depth below ground surface.</summary>
    public double DepthBelowGround(double measuredDepth) =>
        DepthReference == DepthReference.Collar ? measuredDepth - Borehole.CollarHeight : measuredDepth;

    public SourceGeometry SourceFor(WaveType wave) => wave == WaveType.P ? PSource : SSource;
}

public sealed record Survey
{
    public ImmutableList<SeismicRecord> Records { get; init; } = [];
    public ImmutableList<Station> Stations { get; init; } = [];
    public SurveyGeometry Geometry { get; init; } = new();
    public ChannelMap ChannelMap { get; init; } = new();

    public SeismicRecord? FindRecord(Guid id) => Records.FirstOrDefault(r => r.Id == id);

    public Station? FindStation(Guid id) => Stations.FirstOrDefault(s => s.Id == id);

    public IEnumerable<SeismicRecord> RecordsAt(Guid stationId, ShotKind shot) =>
        Records.Where(r => r.StationId == stationId && r.Shot == shot && !r.Excluded).OrderBy(r => r.Sequence);

    public (SeismicRecord Record, TraceInfo Trace)? FindTrace(Guid traceId)
    {
        foreach (var record in Records)
        {
            foreach (var trace in record.Traces)
            {
                if (trace.Id == traceId)
                {
                    return (record, trace);
                }
            }
        }

        return null;
    }

    /// <summary>Stations ordered by depth below ground.</summary>
    public IReadOnlyList<Station> StationsByDepth() =>
        Stations.OrderBy(s => Geometry.DepthBelowGround(s.MeasuredDepth)).ToList();
}
