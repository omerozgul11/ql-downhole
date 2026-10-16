using System.Buffers.Binary;
using System.Text;
using DownholeStudio.Domain;
using DownholeStudio.Formats;
using DownholeStudio.Synthetics;

namespace DownholeStudio.Formats.Tests;

public sealed class Seg2Tests
{
    private static readonly string GoldenDir = Path.Combine(AppContext.BaseDirectory, "golden");

    private static Dictionary<string, string> TraceKeys(int channel) => new() { ["CHANNEL_NUMBER"] = channel.ToString(), ["DELAY"] = "0.001", ["RECEIVER"] = "Z" };

    [Theory]
    [InlineData(Seg2SampleFormat.Int16, true)]
    [InlineData(Seg2SampleFormat.Int16, false)]
    [InlineData(Seg2SampleFormat.Int32, true)]
    [InlineData(Seg2SampleFormat.Int32, false)]
    [InlineData(Seg2SampleFormat.Float32, true)]
    [InlineData(Seg2SampleFormat.Float32, false)]
    [InlineData(Seg2SampleFormat.Float64, true)]
    [InlineData(Seg2SampleFormat.Float64, false)]
    public void RoundTripsEveryEncodingAndByteOrder(Seg2SampleFormat format, bool little)
    {
        double[] a = [0, 1, -2, 300, -32768, 32767, 12.5];
        double[] b = [5, -5, 7, -7, 0, 1, 2];
        var bytes = Seg2Writer.Write([new(a, 0.000125, TraceKeys(1)), new(b, 0.000125, TraceKeys(2))],
            new Dictionary<string, string> { ["ACQUISITION_DATE"] = "08/FEB/2024", ["ACQUISITION_TIME"] = "13:45:10", ["NOTE"] = "DEPTH 12.5\nline two" }, format, little);
        var data = new Seg2Reader().Parse(bytes, "x.sg2");
        Assert.Equal(little ? "little-endian" : "big-endian", data.Metadata.ByteOrder);
        Assert.Equal(2, data.Metadata.Traces.Count);
        Assert.Equal(0.000125, data.Metadata.Traces[0].SampleInterval);
        Assert.Equal(0.001, data.Metadata.Traces[1].Delay);
        Assert.Equal(2, data.Metadata.Traces[1].ChannelNumber);
        Assert.Equal(new DateTimeOffset(2024, 2, 8, 13, 45, 10, TimeSpan.Zero), data.Metadata.AcquiredAt);
        Assert.Equal("DEPTH 12.5\nline two", data.Metadata.Keywords["NOTE"]);
        // Integer encodings round to nearest even (12.5 → 12), as Math.Round does.
        var expected = format is Seg2SampleFormat.Int16 or Seg2SampleFormat.Int32 ? a.Select(v => Math.Round(v)).ToArray() : a;

        Assert.Equal(expected, data.Samples[0]);
        Assert.Equal(b, data.Samples[1]);
    }

    [Fact]
    public void GoldenFilesDecodeToReferenceValues()
    {
        // The golden files are committed binary fixtures; regenerate only deliberately (see GoldenFileGenerator).
        foreach (var (file, format) in GoldenFileGenerator.Files)
        {
            var path = Path.Combine(GoldenDir, file);
            Assert.True(File.Exists(path), $"Missing golden file {file}");
            var data = new Seg2Reader().Parse(File.ReadAllBytes(path), file);
            Assert.Equal(GoldenFileGenerator.Reference, data.Samples[0]);
            Assert.Equal(3, data.Metadata.Traces[0].ChannelNumber);
            Assert.Equal(0.00025, data.Metadata.Traces[0].SampleInterval);
            Assert.Equal(format, data.Metadata.Traces[0].SampleFormat);
            Assert.Equal("GOLDEN", data.Metadata.Keywords["COMPANY"]);
        }
    }

    [Fact]
    public void RejectsNonSeg2File()
    {
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(Encoding.ASCII.GetBytes("This is a text file, not seismic data at all."), "notes.txt"));
        Assert.Contains("not a SEG-2 file", ex.Message);
        Assert.False(new Seg2Reader().CanRead(Encoding.ASCII.GetBytes("hello"), "a.sg2"));
    }

    [Fact]
    public void RejectsTruncatedFile()
    {
        var bytes = Valid();
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes.AsSpan(0, bytes.Length - 10), "cut.sg2"));
        Assert.Contains("truncated", ex.Message);
        Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes.AsSpan(0, 20), "tiny.sg2"));
    }

    [Fact]
    public void RejectsMissingSampleInterval()
    {
        // Rename SAMPLE_INTERVAL to SAMPLE_INTERVAX inside the trace descriptor.
        var bytes = Valid();
        var text = Encoding.Latin1.GetString(bytes);
        var idx = text.IndexOf("SAMPLE_INTERVAL", StringComparison.Ordinal);
        bytes[idx + 14] = (byte)'X';
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "nointerval.sg2"));
        Assert.Contains("sample interval", ex.Message);
    }

    [Fact]
    public void RejectsMissingTraceDescriptor()
    {
        var bytes = Valid();
        var pointer = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        bytes[pointer] = 0; // destroy the 0x4422 block id
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "bad.sg2"));
        Assert.Contains("trace descriptor", ex.Message);
    }

    [Fact]
    public void RejectsPointerOutsideFile()
    {
        var bytes = Valid();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 999_999);
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "bad.sg2"));
        Assert.Contains("outside the file", ex.Message);
    }

    [Fact]
    public void Rejects20BitPackedFormatWithExplanation()
    {
        var bytes = Valid();
        var pointer = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        bytes[pointer + 12] = 3;
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "packed.sg2"));
        Assert.Contains("20-bit", ex.Message);
        Assert.NotNull(ex.TechnicalDetails);
    }

    [Fact]
    public void RejectsUnknownEncoding()
    {
        var bytes = Valid();
        var pointer = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        bytes[pointer + 12] = 9;
        var ex = Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "odd.sg2"));
        Assert.Contains("unknown sample encoding", ex.Message);
    }

    [Fact]
    public void InvalidTerminatorIsReported()
    {
        var bytes = Valid();
        bytes[8] = 5;
        Assert.Throws<SeismicFormatException>(() => new Seg2Reader().Parse(bytes, "term.sg2"));
    }

    [Theory]
    [InlineData("26/FEB/1990", 1990, 2, 26)]
    [InlineData("26-Feb-1990", 1990, 2, 26)]
    [InlineData("2024/03/15", 2024, 3, 15)]
    [InlineData("15/03/2024", 2024, 3, 15)]
    public void ParsesDateForms(string text, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), Seg2Reader.ParseDate(text));

    [Fact]
    public void AmbiguousNumericDateIsNotGuessed() => Assert.Null(Seg2Reader.ParseDate("03/04/2024"));

    private static byte[] Valid() => Seg2Writer.Write([new([1, 2, 3, 4], 0.001, TraceKeys(1))], new Dictionary<string, string> { ["NOTE"] = "x" });
}

/// <summary>Defines and writes the committed golden SEG-2 fixtures.</summary>
public static class GoldenFileGenerator
{
    public static readonly double[] Reference = [0, 1, -1, 2, -2, 100, -100, 1234, -1234, 32000, -32000, 7];

    public static IReadOnlyList<(string File, string Format)> Files { get; } =
    [
        ("golden_int16_le.sg2", "int16"), ("golden_int16_be.sg2", "int16"), ("golden_int32_le.sg2", "int32"), ("golden_int32_be.sg2", "int32"),
        ("golden_float32_le.sg2", "float32"), ("golden_float32_be.sg2", "float32"), ("golden_float64_le.sg2", "float64"), ("golden_float64_be.sg2", "float64"),
    ];

    public static void WriteAll(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (file, _) in Files)
        {
            var format = file.Contains("int16") ? Seg2SampleFormat.Int16 : file.Contains("int32") ? Seg2SampleFormat.Int32 : file.Contains("float32") ? Seg2SampleFormat.Float32 : Seg2SampleFormat.Float64;
            var bytes = Seg2Writer.Write([new(Reference, 0.00025, new Dictionary<string, string> { ["CHANNEL_NUMBER"] = "3", ["RECEIVER"] = "Y" })],
                new Dictionary<string, string> { ["COMPANY"] = "GOLDEN", ["ACQUISITION_DATE"] = "01/JAN/2026", ["ACQUISITION_TIME"] = "00:00:00" },
                format, littleEndian: file.EndsWith("_le.sg2", StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(folder, file), bytes);
        }
    }
}

public sealed class ImportInspectorTests
{
    [Fact]
    public async Task ReportsInconsistentSampleRateInPlainLanguage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dhs-fmt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, "a_1.0m_P.sg2"), 0.00025);
        Write(Path.Combine(dir, "b_2.0m_P.sg2"), 0.00025);
        Write(Path.Combine(dir, "c_3.0m_P.sg2"), 0.0005);
        File.Copy(Path.Combine(dir, "a_1.0m_P.sg2"), Path.Combine(dir, "d_copy.sg2"));
        File.WriteAllText(Path.Combine(dir, "notes.sg2"), "not seismic");
        var inspector = new ImportInspector(SeismicFormatRegistry.CreateDefault());
        var preview = await inspector.InspectAsync(inspector.ExpandPaths([dir]));
        var rate = Assert.Single(preview.Diagnostics, d => d.Code == "import.sampleRate");
        Assert.Contains("different sample rate (2 kHz) from the other measurements (4 kHz)", rate.Message);
        Assert.Contains(preview.Diagnostics, d => d.Code == "import.duplicate" && d.SuggestedFix is not null);
        Assert.Contains(preview.Diagnostics, d => d.Code == "import.unreadable" && d.Severity == Severity.Error);
        Assert.Equal(1.0, preview.Files.First(f => f.FileName.StartsWith("a_", StringComparison.Ordinal)).DepthHint!.Depth);
    }

    [Theory]
    [InlineData("DH_012.0m_P.sg2", 12.0, ShotKind.P)]
    [InlineData("DH_012.0m_SL.sg2", 12.0, ShotKind.SPositive)]
    [InlineData("DH_012.0m_SR.sg2", 12.0, ShotKind.SNegative)]
    [InlineData("depth 7.5 m left.sg2", 7.5, ShotKind.SPositive)]
    public void GuessesDepthAndShotFromFileName(string name, double depth, ShotKind shot)
    {
        var meta = new SeismicFileMetadata { FormatName = "SEG-2", FileName = name };
        Assert.Equal(depth, ImportInspector.GuessDepth(name, meta)!.Depth);
        Assert.Equal(shot, ImportInspector.GuessShot(name, meta).Shot);
    }

    [Fact]
    public void NaturalOrdering()
    {
        var names = new[] { "f10.sg2", "f2.sg2", "f1.sg2" }.OrderBy(x => x, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(["f1.sg2", "f2.sg2", "f10.sg2"], names);
    }

    [Fact]
    public void SyntheticSurveyIsValidSeg2()
    {
        var survey = SyntheticSurveyGenerator.Generate(new SyntheticOptions { DepthCount = 2, BigEndian = true, SampleFormat = Seg2SampleFormat.Int32 });
        foreach (var f in survey.Files)
        {
            var data = new Seg2Reader().Parse(f.Content, f.FileName);
            Assert.Equal(4, data.Metadata.Traces.Count);
            Assert.Equal("big-endian", data.Metadata.ByteOrder);
        }
    }

    private static void Write(string path, double dt) =>
        File.WriteAllBytes(path, Seg2Writer.Write([new(new double[100], dt, new Dictionary<string, string> { ["CHANNEL_NUMBER"] = "1" })], new Dictionary<string, string>()));
}
