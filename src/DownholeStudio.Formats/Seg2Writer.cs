using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DownholeStudio.Formats;

public enum Seg2SampleFormat : byte
{
    Int16 = 1,
    Int32 = 2,
    Float32 = 4,
    Float64 = 5,
}

/// <summary>One trace to write: samples plus "KEYWORD value" strings (SAMPLE_INTERVAL is added automatically).</summary>
public sealed record Seg2TraceToWrite(double[] Samples, double SampleInterval, IReadOnlyDictionary<string, string> Keywords);

/// <summary>Writes SEG-2 revision 1 files (used for synthetic surveys, golden test files and exports).</summary>
public static class Seg2Writer
{
    public static byte[] Write(IReadOnlyList<Seg2TraceToWrite> traces, IReadOnlyDictionary<string, string> fileKeywords,
        Seg2SampleFormat format = Seg2SampleFormat.Float32, bool littleEndian = true)
    {
        if (traces.Count is 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(traces), "SEG-2 files hold between 1 and 65535 traces.");
        }

        var w = new Writer(littleEndian);
        var pointerBlock = Align4(4 * traces.Count);
        var fileStrings = EncodeStrings(fileKeywords, littleEndian);
        var header = new byte[Align4(32 + pointerBlock + fileStrings.Length)];
        w.U16(header, 0, 0x3A55);
        w.U16(header, 2, 1);
        w.U16(header, 4, (ushort)pointerBlock);
        w.U16(header, 6, (ushort)traces.Count);
        header[8] = 1; // string terminator: one byte, NUL
        header[9] = 0;
        header[11] = 1; // line terminator: one byte, LF
        header[12] = 0x0A;
        fileStrings.CopyTo(header, 32 + pointerBlock);

        var blocks = new List<byte[]>();
        var offset = header.Length;
        for (var t = 0; t < traces.Count; t++)
        {
            var trace = traces[t];
            var keys = new Dictionary<string, string>(trace.Keywords, StringComparer.OrdinalIgnoreCase)
            {
                ["SAMPLE_INTERVAL"] = trace.SampleInterval.ToString("R", CultureInfo.InvariantCulture),
            };
            var strings = EncodeStrings(keys, littleEndian);
            var descriptorSize = Align4(32 + strings.Length);
            var bytesPerSample = format switch
            {
                Seg2SampleFormat.Int16 => 2,
                Seg2SampleFormat.Int32 => 4,
                Seg2SampleFormat.Float32 => 4,
                _ => 8,
            };
            var dataSize = trace.Samples.Length * bytesPerSample;
            var block = new byte[descriptorSize + dataSize];
            w.U16(block, 0, 0x4422);
            w.U16(block, 2, (ushort)descriptorSize);
            w.U32(block, 4, (uint)dataSize);
            w.U32(block, 8, (uint)trace.Samples.Length);
            block[12] = (byte)format;
            strings.CopyTo(block, 32);
            for (var i = 0; i < trace.Samples.Length; i++)
            {
                var o = descriptorSize + i * bytesPerSample;
                var v = trace.Samples[i];
                switch (format)
                {
                    case Seg2SampleFormat.Int16:
                        w.I16(block, o, (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue));
                        break;
                    case Seg2SampleFormat.Int32:
                        w.I32(block, o, (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue));
                        break;
                    case Seg2SampleFormat.Float32:
                        w.F32(block, o, (float)v);
                        break;
                    default:
                        w.F64(block, o, v);
                        break;
                }
            }

            w.U32(header, 32 + 4 * t, (uint)offset);
            offset += block.Length;
            blocks.Add(block);
        }

        using var ms = new MemoryStream(offset);
        ms.Write(header);
        foreach (var b in blocks)
        {
            ms.Write(b);
        }

        return ms.ToArray();
    }

    private static byte[] EncodeStrings(IReadOnlyDictionary<string, string> keywords, bool littleEndian)
    {
        using var ms = new MemoryStream();
        var w = new Writer(littleEndian);
        foreach (var (key, value) in keywords)
        {
            var text = Encoding.Latin1.GetBytes(key.ToUpperInvariant() + " " + value.Replace("\r\n", "\n"));
            var length = 2 + text.Length + 1;
            var entry = new byte[length];
            w.U16(entry, 0, (ushort)length);
            text.CopyTo(entry, 2);
            ms.Write(entry);
        }

        ms.Write(new byte[2]); // zero offset terminates the list
        return ms.ToArray();
    }

    private static int Align4(int n) => (n + 3) & ~3;

    private readonly struct Writer(bool little)
    {
        public void U16(byte[] b, int o, ushort v) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v); }
        public void U32(byte[] b, int o, uint v) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v); }
        public void I16(byte[] b, int o, short v) { if (little) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(o), v); }
        public void I32(byte[] b, int o, int v) { if (little) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v); }
        public void F32(byte[] b, int o, float v) { if (little) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(o), v); }
        public void F64(byte[] b, int o, double v) { if (little) BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(o), v); }
    }
}
