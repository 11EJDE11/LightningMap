using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
namespace LightningMap;

/// <summary>Receives a rendered image from top to bottom, one row of 0xAARRGGBB pixels at a time.</summary>
public interface IImageSink
{
    void Start(int width, int height);
    /// <summary>The span is only valid during the call.</summary>
    void WriteRow(ReadOnlySpan<uint> row);
    /// <summary>Called after the last row; not called if rendering fails.</summary>
    void Finish();
}

static class Png
{
    public static void Header(Stream file, int width, int height)
    {
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13]; header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header[4..], height); header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR"u8, header);
    }
    // Sub filter: each RGB byte minus the same channel of the pixel to its left.
    [MethodImpl(Bin.Hot)]
    public static unsafe void Filter(ReadOnlySpan<uint> row, Span<byte> scanline)
    {
        fixed (uint* source = row) fixed (byte* line = scanline)
        {
            line[0] = 1; uint previous = 0; byte* p = line + 1;
            for (int x = 0; x < row.Length; x++, p += 3)
            {
                uint pixel = source[x];
                p[0] = (byte)((pixel >> 16) - (previous >> 16)); p[1] = (byte)((pixel >> 8) - (previous >> 8)); p[2] = (byte)(pixel - previous); previous = pixel;
            }
        }
    }
    public static void Chunk(Stream file, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); file.Write(length); file.Write(type); file.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(length, Bin.Crc(data, Bin.Crc(type))); file.Write(length);
    }
}

// PNG is compressed in parallel. Each band becomes a sync-flushed, byte-aligned run of non-final deflate
// blocks in its own IDAT chunk; PngBands joins them into one zlib stream.
sealed class PngBandEncoder(int width, CompressionLevel compression)
{
    readonly MemoryStream chunk = new();
    readonly byte[] scanline = new byte[1 + width * 3];
    public uint Adler { get; private set; }
    public long Length { get; private set; }
    public ReadOnlySpan<byte> Chunk => chunk.GetBuffer().AsSpan(0, (int)chunk.Length);
    public void Encode(ReadOnlySpan<uint> pixels, int rows)
    {
        // Six placeholder bytes put zlib's two-byte header where the chunk type goes, so it is overwritten.
        chunk.SetLength(0); chunk.Write([0, 0, 0, 0, 0, 0]);
        long end;
        using (var zlib = new ZLibStream(chunk, compression, leaveOpen: true))
        {
            for (int row = 0; row < rows; row++) { Png.Filter(pixels.Slice(row * width, width), scanline); zlib.Write(scanline); }
            zlib.Flush(); end = chunk.Length;
        }
        // Closing the stream appended the final block and the band's Adler-32 (computed by zlib); keep only the checksum.
        var buffer = chunk.GetBuffer().AsSpan();
        Adler = BinaryPrimitives.ReadUInt32BigEndian(buffer[(int)(chunk.Length - 4)..]); Length = (long)rows * scanline.Length;
        BinaryPrimitives.WriteInt32BigEndian(buffer, checked((int)end - 8)); "IDAT"u8.CopyTo(buffer[4..]);
        chunk.SetLength(end); Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Bin.Crc(chunk.GetBuffer().AsSpan(4, (int)end - 4))); chunk.Write(crc);
    }
}

sealed class PngBands
{
    readonly Stream file;
    uint adler = 1;
    public PngBands(Stream file, int width, int height, CompressionLevel compression)
    {
        this.file = file; Png.Header(file, width, height);
        Png.Chunk(file, "IDAT"u8, [0x78, compression switch { CompressionLevel.Fastest => 0x01, (CompressionLevel)3 /* SmallestSize */ => 0xda, _ => 0x9c }]);
    }
    public void Append(PngBandEncoder band) { file.Write(band.Chunk); adler = Combine(adler, band.Adler, band.Length); }
    public void Finish()
    {
        // An empty final fixed-Huffman block, then the Adler-32 of all scanlines.
        Span<byte> tail = [0x03, 0x00, 0, 0, 0, 0]; BinaryPrimitives.WriteUInt32BigEndian(tail[2..], adler);
        Png.Chunk(file, "IDAT"u8, tail); Png.Chunk(file, "IEND"u8, []);
    }
    // zlib's adler32_combine: the checksum of A followed by B, given both checksums and B's length.
    public static uint Combine(uint a, uint b, long lengthB)
    {
        const uint Base = 65521; uint remainder = (uint)(lengthB % Base);
        uint sum1 = a & 0xffff, sum2 = remainder * sum1 % Base;
        sum1 += (b & 0xffff) + Base - 1; sum2 += (a >> 16) + (b >> 16) + Base - remainder;
        if (sum1 >= Base) sum1 -= Base; if (sum1 >= Base) sum1 -= Base;
        if (sum2 >= Base << 1) sum2 -= Base << 1; if (sum2 >= Base) sum2 -= Base;
        return sum1 | sum2 << 16;
    }
}

// Exact box-area downscaling, done band by band on the workers. Each output row sums its source rows with
// the weights, and in the order, of a single top-to-bottom pass, so the result does not depend on the banding.
sealed class Resampler
{
    readonly int sw, dw, dh;
    readonly double scaleX, scaleY;
    // Output row y adds up contributions start[y] .. start[y + 1] - 1: a source row and its weight.
    readonly int[] start, sourceRow;
    readonly double[] weight;
    // Horizontal footprint of each output column: first and last source pixel and their partial weights.
    readonly int[] first, last;
    readonly double[] firstWeight, lastWeight;
    // One horizontally filtered source row and the running sums of one output row, RGB interleaved.
    public sealed class Scratch(int width) { public readonly double[] Row = new double[width * 3], Sum = new double[width * 3]; }
    public Resampler(int sw, int sh, int dw, int dh)
    {
        this.sw = sw; this.dw = dw; this.dh = dh; scaleX = (double)sw / dw; scaleY = (double)sh / dh;
        var rows = new List<int>(); var weights = new List<double>(); start = new int[dh + 1];
        int dy = 0;
        for (int sy = 0; sy < sh && dy < dh; sy++)
        {
            double top = sy, bottom = sy + 1;
            while (top < bottom - 1e-9 && dy < dh)
            {
                double end = Math.Min(bottom, (dy + 1) * scaleY);
                rows.Add(sy); weights.Add(end - top); top = end;
                if (end >= (dy + 1) * scaleY - 1e-9) start[++dy] = rows.Count;
            }
        }
        if (dy != dh) throw new InvalidOperationException("Downscaling did not cover every output row.");
        sourceRow = rows.ToArray(); weight = weights.ToArray();
        first = new int[dw]; last = new int[dw]; firstWeight = new double[dw]; lastWeight = new double[dw];
        for (int x = 0; x < dw; x++)
        {
            double left = x * scaleX, right = (x + 1) * scaleX;
            first[x] = (int)left; last[x] = Math.Min(sw - 1, (int)right);
            firstWeight[x] = Math.Min(first[x] + 1, right) - left; lastWeight[x] = last[x] == first[x] ? 0 : right - last[x];
        }
    }
    public Scratch CreateScratch() => new(dw);
    // Groups output rows into bands that each need at most sourceRows source rows (or one output row, if it needs more).
    public Band[] Bands(int sourceRows)
    {
        var bands = new List<Band>();
        for (int y = 0; y < dh;)
        {
            int top = sourceRow[start[y]], end = y + 1;
            while (end < dh && sourceRow[start[end + 1] - 1] - top < sourceRows) end++;
            bands.Add(new(top, sourceRow[start[end] - 1] - top + 1, y, end - y)); y = end;
        }
        return bands.ToArray();
    }
    // source holds the band's source rows; output receives its output rows. Each source row is filtered when
    // first needed, into a buffer small enough to stay in cache; an edge row shared by two output rows is kept.
    [MethodImpl(Bin.Hot)]
    public unsafe void Run(Band band, uint[] source, Scratch s, uint[] output)
    {
        int filtered = -1, n = dw * 3;
        fixed (uint* src = source, dst = output) fixed (double* row = s.Row, sum = s.Sum)
            for (int y = 0; y < band.OutputRows; y++)
            {
                int dy = band.OutputY + y;
                new Span<double>(sum, n).Clear();
                for (int c = start[dy]; c < start[dy + 1]; c++)
                {
                    int r = sourceRow[c] - band.SourceY;
                    if (r != filtered) { Filter(src + r * sw, row); filtered = r; }
                    double wt = weight[c]; int i = 0;
                    // A multiply then an add in each lane: the same IEEE operations as the scalar loop.
                    if (Vector.IsHardwareAccelerated)
                        for (var w = new Vector<double>(wt); i <= n - Vector<double>.Count; i += Vector<double>.Count)
                            *(Vector<double>*)(sum + i) += *(Vector<double>*)(row + i) * w;
                    for (; i < n; i++) sum[i] += row[i] * wt;
                }
                uint* o = dst + y * dw;
                for (int x = 0; x < dw; x++)
                    o[x] = 0xff000000u | (uint)Math.Clamp((int)Math.Round(sum[x * 3] / scaleY), 0, 255) << 16 | (uint)Math.Clamp((int)Math.Round(sum[x * 3 + 1] / scaleY), 0, 255) << 8 | (uint)Math.Clamp((int)Math.Round(sum[x * 3 + 2] / scaleY), 0, 255);
            }
    }
    [MethodImpl(Bin.Hot)]
    unsafe void Filter(uint* source, double* row)
    {
        fixed (int* fx = first, lx = last) fixed (double* fw = firstWeight, lw = lastWeight)
            for (int x = 0; x < dw; x++, row += 3)
            {
                int a = fx[x], b = lx[x];
                uint startPixel = source[a], endPixel = source[b];
                double rr = ((startPixel >> 16) & 255) * fw[x] + ((endPixel >> 16) & 255) * lw[x];
                double gg = ((startPixel >> 8) & 255) * fw[x] + ((endPixel >> 8) & 255) * lw[x];
                double bb = (startPixel & 255) * fw[x] + (endPixel & 255) * lw[x];
                uint ri = 0, gi = 0, bi = 0;
                for (int sx = a + 1; sx < b; sx++) { uint c = source[sx]; ri += (c >> 16) & 255; gi += (c >> 8) & 255; bi += c & 255; }
                row[0] = (rr + ri) / scaleX; row[1] = (gg + gi) / scaleX; row[2] = (bb + bi) / scaleX;
            }
    }
}
