using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace DownholeStudio.Formats;

/// <summary>
/// Reader for SEG-2 (SEG Engineering and Groundwater Geophysics Committee, 1990), revision 1.
/// Supports both byte orders and sample formats 1 (int16), 2 (int32), 4 (float32) and 5 (float64).
/// Format 3 (20-bit packed) is rejected with an explanatory message because its bit layout could
/// not be verified against the standard (see SEG2-PARSER.md).
/// </summary>
public sealed class Seg2Reader : ISeismicDataReader
{
    private const ushort FileBlockId = 0x3A55;
    private const ushort TraceBlockId = 0x4422;

    public string Name => "SEG-2";

    public IReadOnlyList<string> SupportedExtensions { get; } = [".sg2", ".seg2", ".dat"];

    public bool CanRead(ReadOnlySpan<byte> header, string fileName)
    {
        if (header.Length < 2)
        {
            return false;
        }

        // 0x3A55 stored little-endian is 55 3A; big-endian is 3A 55.
        return (header[0] == 0x55 && header[1] == 0x3A) || (header[0] == 0x3A && header[1] == 0x55);
    }

    public async Task<SeismicFileMetadata> ReadMetadataAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, fileName, decodeSamples: false).Metadata;
    }

    public async Task<SeismicFileData> ReadAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, fileName, decodeSamples: true);
    }

    /// <summary>Parses a complete SEG-2 file held in memory.</summary>
    public SeismicFileData Parse(ReadOnlySpan<byte> data, string fileName, bool decodeSamples = true)
    {
        if (data.Length < 32)
        {
            throw new SeismicFormatException(
                $"\"{fileName}\" is too short to be a SEG-2 file ({data.Length} bytes; the file header alone is 32 bytes).",
                "File descriptor block truncated.");
        }

        bool littleEndian;
        if (data[0] == 0x55 && data[1] == 0x3A)
        {
            littleEndian = true;
        }
        else if (data[0] == 0x3A && data[1] == 0x55)
        {
            littleEndian = false;
        }
        else
        {
            throw new SeismicFormatException(
                $"\"{fileName}\" is not a SEG-2 file: it does not start with the SEG-2 identifier.",
                $"Expected block id 0x3A55, found bytes 0x{data[0]:X2} 0x{data[1]:X2}.");
        }

        var r = new Endian(littleEndian);
        var notes = ImmutableList.CreateBuilder<string>();
        var revision = r.U16(data, 2);
        if (revision != 1)
        {
            notes.Add($"SEG-2 revision {revision} (the reader implements revision 1; fields are read as revision 1).");
        }

        int pointerBlockSize = r.U16(data, 4);
        int traceCount = r.U16(data, 6);
        if (traceCount == 0)
        {
            throw new SeismicFormatException($"\"{fileName}\" contains no traces.", "Number of traces in file descriptor is 0.");
        }

        if (pointerBlockSize < traceCount * 4)
        {
            throw new SeismicFormatException(
                $"\"{fileName}\" has a damaged header: the trace table is too small for {traceCount} traces.",
                $"Trace pointer sub-block size {pointerBlockSize} < 4 × {traceCount}.");
        }

        if (32 + pointerBlockSize > data.Length)
        {
            throw new SeismicFormatException($"\"{fileName}\" is truncated inside the trace table.", "Trace pointer sub-block extends past end of file.");
        }

        var stringTerm = ReadTerminator(data, 8, fileName, "string");
        var lineTerm = ReadTerminator(data, 11, fileName, "line");

        var pointers = new uint[traceCount];
        for (var i = 0; i < traceCount; i++)
        {
            pointers[i] = r.U32(data, 32 + 4 * i);
            if (pointers[i] < 32 + (uint)pointerBlockSize || pointers[i] + 32 > data.Length)
            {
                throw new SeismicFormatException(
                    $"\"{fileName}\" is damaged: trace {i + 1} points outside the file.",
                    $"Trace pointer {i} = {pointers[i]} (file length {data.Length}).");
            }
        }

        var firstTrace = (int)pointers.Min();
        var fileKeywords = ParseStrings(data[(32 + pointerBlockSize)..firstTrace], r, stringTerm, lineTerm, notes, "file descriptor");
        var acquiredAt = ParseAcquisitionTime(fileKeywords, notes);

        var traces = ImmutableList.CreateBuilder<SeismicTraceHeader>();
        var samples = new List<double[]>(traceCount);
        for (var i = 0; i < traceCount; i++)
        {
            var p = (int)pointers[i];
            var id = r.U16(data, p);
            if (id != TraceBlockId)
            {
                throw new SeismicFormatException(
                    $"\"{fileName}\" is damaged: trace {i + 1} does not start with a SEG-2 trace descriptor.",
                    $"Expected 0x4422 at offset {p}, found 0x{id:X4}.");
            }

            int blockSize = r.U16(data, p + 2);
            var dataSize = r.U32(data, p + 4);
            var sampleCount = r.U32(data, p + 8);
            var formatCode = data[p + 12];
            if (blockSize < 32 || p + blockSize > data.Length)
            {
                throw new SeismicFormatException($"\"{fileName}\" is damaged: trace {i + 1} header has an invalid size.",
                    $"Trace descriptor block size {blockSize} at offset {p}.");
            }

            var keywords = ParseStrings(data[(p + 32)..(p + blockSize)], r, stringTerm, lineTerm, notes, $"trace {i + 1}");
            var (bytesPerSample, formatName) = formatCode switch
            {
                1 => (2, "int16"),
                2 => (4, "int32"),
                3 => (0, "20-bit packed"),
                4 => (4, "float32"),
                5 => (8, "float64"),
                _ => (-1, $"code {formatCode}"),
            };
            if (bytesPerSample == 0)
            {
                throw new SeismicFormatException(
                    $"\"{fileName}\" stores samples in the 20-bit packed SEG-2 format (code 3), which Downhole Studio does not decode. Export the record as 16-bit, 32-bit or floating-point SEG-2 from the acquisition software.",
                    $"Trace {i + 1}: data format code 3. The exact bit layout of this format could not be verified against the SEG-2 standard, so it is rejected rather than decoded by assumption.");
            }

            if (bytesPerSample < 0)
            {
                throw new SeismicFormatException($"\"{fileName}\" uses an unknown sample encoding in trace {i + 1}.",
                    $"Data format code {formatCode} is not defined by SEG-2 revision 1.");
            }

            var start = p + blockSize;
            var needed = (long)sampleCount * bytesPerSample;
            if (start + needed > data.Length)
            {
                throw new SeismicFormatException(
                    $"\"{fileName}\" is truncated: trace {i + 1} should hold {sampleCount} samples but the file ends early.",
                    $"Need {needed} bytes from offset {start}, file length {data.Length}.");
            }

            if (dataSize != 0 && dataSize != needed)
            {
                notes.Add($"Trace {i + 1}: data block size field ({dataSize} bytes) differs from samples × size ({needed} bytes); the sample count was used.");
            }

            if (!keywords.TryGetValue("SAMPLE_INTERVAL", out var siText) || !TryDouble(siText, out var sampleInterval) || !(sampleInterval > 0))
            {
                throw new SeismicFormatException(
                    $"\"{fileName}\" does not state a valid sample interval for trace {i + 1}, so its time axis is unknown.",
                    $"SAMPLE_INTERVAL keyword missing or invalid ('{(keywords.TryGetValue("SAMPLE_INTERVAL", out var v) ? v : "<absent>")}').");
            }

            var delay = keywords.TryGetValue("DELAY", out var d) && TryDouble(d, out var dv) ? dv : 0.0;
            var channel = keywords.TryGetValue("CHANNEL_NUMBER", out var c) && int.TryParse(c.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cv) ? cv : i + 1;
            traces.Add(new SeismicTraceHeader
            {
                Index = i,
                ChannelNumber = channel,
                SampleCount = (int)sampleCount,
                SampleInterval = sampleInterval,
                Delay = delay,
                SampleFormat = formatName,
                DescalingFactor = keywords.TryGetValue("DESCALING_FACTOR", out var df) && TryDouble(df, out var dfv) ? dfv : null,
                ReceiverLocation = FirstNumber(keywords, "RECEIVER_LOCATION"),
                SourceLocation = FirstNumber(keywords, "SOURCE_LOCATION"),
                Label = keywords.TryGetValue("RECEIVER", out var rec) ? rec : null,
                Keywords = keywords,
            });

            if (decodeSamples)
            {
                samples.Add(Decode(data.Slice(start, (int)needed), (int)sampleCount, formatCode, r));
            }
        }

        var metadata = new SeismicFileMetadata
        {
            FormatName = Name,
            FileName = fileName,
            ByteOrder = littleEndian ? "little-endian" : "big-endian",
            Revision = revision,
            AcquiredAt = acquiredAt,
            Keywords = fileKeywords,
            Traces = traces.ToImmutable(),
            ReaderNotes = notes.ToImmutable(),
        };
        return new SeismicFileData(metadata, samples);
    }

    private static double[] Decode(ReadOnlySpan<byte> raw, int count, byte code, Endian r)
    {
        var y = new double[count];
        switch (code)
        {
            case 1:
                for (var i = 0; i < count; i++)
                {
                    y[i] = r.I16(raw, 2 * i);
                }

                break;
            case 2:
                for (var i = 0; i < count; i++)
                {
                    y[i] = r.I32(raw, 4 * i);
                }

                break;
            case 4:
                for (var i = 0; i < count; i++)
                {
                    y[i] = r.F32(raw, 4 * i);
                }

                break;
            case 5:
                for (var i = 0; i < count; i++)
                {
                    y[i] = r.F64(raw, 8 * i);
                }

                break;
        }

        return y;
    }

    private static byte[] ReadTerminator(ReadOnlySpan<byte> data, int offset, string fileName, string kind)
    {
        int size = data[offset];
        if (size is < 1 or > 2)
        {
            throw new SeismicFormatException($"\"{fileName}\" has a damaged header (invalid {kind} terminator).",
                $"{kind} terminator size {size} at offset {offset}; SEG-2 allows 1 or 2.");
        }

        return data.Slice(offset + 1, size).ToArray();
    }

    /// <summary>
    /// Parses the free-format string section: each entry is a 2-byte offset to the next entry
    /// (counted from the start of the offset field), followed by "KEYWORD value" and the string
    /// terminator. An offset of zero ends the list.
    /// </summary>
    private static ImmutableDictionary<string, string> ParseStrings(ReadOnlySpan<byte> block, Endian r, byte[] stringTerm, byte[] lineTerm,
        ImmutableList<string>.Builder notes, string where)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        while (pos + 2 <= block.Length)
        {
            int next = r.U16(block, pos);
            if (next == 0)
            {
                break;
            }

            if (next < 2 || pos + next > block.Length)
            {
                notes.Add($"{where}: string list ends unexpectedly; remaining header text was ignored.");
                break;
            }

            var text = block.Slice(pos + 2, next - 2);
            var end = text.IndexOf(stringTerm);
            if (end >= 0)
            {
                text = text[..end];
            }

            var s = Encoding.Latin1.GetString(text).TrimEnd('\0', ' ');
            if (s.Length > 0)
            {
                var split = s.IndexOfAny([' ', '\t']);
                var key = (split < 0 ? s : s[..split]).Trim().ToUpperInvariant();
                var value = split < 0 ? "" : s[(split + 1)..].Trim();
                if (key == "NOTE")
                {
                    value = value.Replace(Encoding.Latin1.GetString(lineTerm), "\n");
                }

                if (key.Length > 0)
                {
                    result[key] = result.TryGetValue(key, out var existing) ? existing + "\n" + value : value;
                }
            }

            pos += next;
        }

        return result.ToImmutable();
    }

    private static DateTimeOffset? ParseAcquisitionTime(ImmutableDictionary<string, string> keywords, ImmutableList<string>.Builder notes)
    {
        if (!keywords.TryGetValue("ACQUISITION_DATE", out var dateText))
        {
            return null;
        }

        var date = ParseDate(dateText);
        if (date is null)
        {
            notes.Add($"ACQUISITION_DATE '{dateText}' is not in a recognised form; the record date is left blank.");
            return null;
        }

        var time = TimeSpan.Zero;
        if (keywords.TryGetValue("ACQUISITION_TIME", out var timeText))
        {
            var parts = timeText.Trim().Split(':');
            if (parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            {
                double sec = 0;
                if (parts.Length >= 3 && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
                {
                    sec = 0;
                }

                time = new TimeSpan(0, h, m, 0).Add(TimeSpan.FromSeconds(sec));
            }
            else
            {
                notes.Add($"ACQUISITION_TIME '{timeText}' is not in HH:MM:SS form; only the date is used.");
            }
        }

        // SEG-2 does not record a time zone; times are kept as written (offset 0 = "as recorded").
        return new DateTimeOffset(date.Value.ToDateTime(TimeOnly.MinValue).Add(time), TimeSpan.Zero);
    }

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    /// <summary>Parses SEG-2 ACQUISITION_DATE forms; returns null rather than guessing ambiguous numeric dates.</summary>
    public static DateOnly? ParseDate(string text)
    {
        var parts = text.Trim().Split(['/', '-', '.', ' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        int Month(string s)
        {
            var idx = Array.FindIndex(Months, m => s.StartsWith(m, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                return idx + 1;
            }

            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
        }

        static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

        int day, month, year;
        if (parts[0].Length == 4)
        {
            (year, month, day) = (Int(parts[0]), Month(parts[1]), Int(parts[2]));
        }
        else
        {
            // Standard form is DD/MMM/YYYY. Numeric months are only accepted when unambiguous.
            (day, month, year) = (Int(parts[0]), Month(parts[1]), Int(parts[2]));
            if (char.IsDigit(parts[1][0]) && day <= 12 && month <= 12 && day != month)
            {
                return null;
            }
        }

        if (year is >= 0 and < 100)
        {
            year += year < 70 ? 2000 : 1900;
        }

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static double? FirstNumber(ImmutableDictionary<string, string> keywords, string key)
    {
        if (!keywords.TryGetValue(key, out var text))
        {
            return null;
        }

        var token = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null && TryDouble(token, out var v) ? v : null;
    }

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    private readonly struct Endian(bool little)
    {
        public ushort U16(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b[o..]) : BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
        public uint U32(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b[o..]) : BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
        public short I16(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadInt16LittleEndian(b[o..]) : BinaryPrimitives.ReadInt16BigEndian(b[o..]);
        public int I32(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadInt32LittleEndian(b[o..]) : BinaryPrimitives.ReadInt32BigEndian(b[o..]);
        public float F32(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadSingleLittleEndian(b[o..]) : BinaryPrimitives.ReadSingleBigEndian(b[o..]);
        public double F64(ReadOnlySpan<byte> b, int o) => little ? BinaryPrimitives.ReadDoubleLittleEndian(b[o..]) : BinaryPrimitives.ReadDoubleBigEndian(b[o..]);
    }
}
