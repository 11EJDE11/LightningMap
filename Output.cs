using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
namespace LightningMap;

interface IRows : IDisposable { void Write(ReadOnlySpan<uint> row); }

sealed class PngRows : IRows
{
    readonly FileStream file;
    readonly ZLibStream zlib;
    readonly IdatStream idat;
    readonly byte[] scanline;
    readonly int height;
    int rows;
    public PngRows(string path, int width, int height, CompressionLevel compression)
    {
        this.height = height;
        file = File.Create(path); Header(file, width, height);
        idat = new(file); zlib = new(idat, compression, leaveOpen: true); scanline = new byte[1 + width * 3];
    }
    public static void Header(Stream file, int width, int height)
    {
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13]; header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header[4..], height); header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR"u8, header);
    }
    public void Write(ReadOnlySpan<uint> row)
    {
        if (rows >= height || row.Length * 3 + 1 != scanline.Length) throw new InvalidOperationException("Wrong PNG row dimensions.");
        Filter(row, scanline); zlib.Write(scanline); rows++;
    }
    // Sub filter: each RGB byte minus the same channel of the pixel to its left.
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
    public void Dispose() { zlib.Dispose(); idat.Flush(); Chunk(file, "IEND"u8, []); file.Dispose(); }
    sealed class IdatStream(Stream file) : Stream
    {
        readonly byte[] buffer = new byte[65536]; int count;
        public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o, n));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty) { int n = Math.Min(data.Length, buffer.Length - count); data[..n].CopyTo(buffer.AsSpan(count)); count += n; data = data[n..]; if (count == buffer.Length) Flush(); }
        }
        public override void Flush() { if (count > 0) { Chunk(file, "IDAT"u8, buffer.AsSpan(0, count)); count = 0; } }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    }
}

// Native-size PNG is compressed in parallel. Each band becomes a sync-flushed, byte-aligned run of
// non-final deflate blocks in its own IDAT chunk; PngBands joins them into one zlib stream.
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
            for (int row = 0; row < rows; row++) { PngRows.Filter(pixels.Slice(row * width, width), scanline); zlib.Write(scanline); }
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

sealed class PngBands : IDisposable
{
    readonly FileStream file;
    uint adler = 1;
    public PngBands(string path, int width, int height, CompressionLevel compression)
    {
        file = File.Create(path); PngRows.Header(file, width, height);
        PngRows.Chunk(file, "IDAT"u8, [0x78, compression switch { CompressionLevel.Fastest => 0x01, CompressionLevel.SmallestSize => 0xda, _ => 0x9c }]);
    }
    public void Append(PngBandEncoder band) { file.Write(band.Chunk); adler = Combine(adler, band.Adler, band.Length); }
    public void Dispose()
    {
        // An empty final fixed-Huffman block, then the Adler-32 of all scanlines.
        Span<byte> tail = [0x03, 0x00, 0, 0, 0, 0]; BinaryPrimitives.WriteUInt32BigEndian(tail[2..], adler);
        PngRows.Chunk(file, "IDAT"u8, tail); PngRows.Chunk(file, "IEND"u8, []); file.Dispose();
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

sealed class JpegRows : IRows
{
    readonly Bitmap bitmap;
    readonly BitmapData data;
    readonly string path;
    readonly int quality;
    int row;
    public JpegRows(string path, int width, int height, int quality)
    {
        this.path = path; this.quality = quality;
        bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
    }
    public unsafe void Write(ReadOnlySpan<uint> pixels)
    {
        if (row >= bitmap.Height || pixels.Length != bitmap.Width) throw new InvalidOperationException("Wrong JPEG row dimensions.");
        pixels.CopyTo(new Span<uint>((void*)(data.Scan0 + data.Stride * row++), bitmap.Width));
    }
    public void Dispose()
    {
        bitmap.UnlockBits(data);
        try
        {
            using var options = new EncoderParameters(1); options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(path, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), options);
        }
        finally { bitmap.Dispose(); }
    }
}

// Exact box-area integration, streamed a source scanline at a time.
sealed class Downsample(IRows output, int sw, int sh, int dw, int dh)
{
    readonly double[] sumR = new double[dw], sumG = new double[dw], sumB = new double[dw];
    readonly uint[] row = new uint[dw];
    readonly double[] r = new double[dw], g = new double[dw], b = new double[dw];
    int sy, dy;
    public void Write(ReadOnlySpan<uint> source)
    {
        if (sw == dw && sh == dh) { output.Write(source); return; }
        double scaleX = (double)sw / dw, scaleY = (double)sh / dh;
        for (int x = 0; x < dw; x++)
        {
            double left = x * scaleX, right = (x + 1) * scaleX, rr = 0, gg = 0, bb = 0;
            int first = (int)left, last = Math.Min(sw - 1, (int)right);
            uint startPixel = source[first], endPixel = source[last];
            double firstWeight = Math.Min(first + 1, right) - left, lastWeight = last == first ? 0 : right - last;
            rr = ((startPixel >> 16) & 255) * firstWeight + ((endPixel >> 16) & 255) * lastWeight;
            gg = ((startPixel >> 8) & 255) * firstWeight + ((endPixel >> 8) & 255) * lastWeight;
            bb = (startPixel & 255) * firstWeight + (endPixel & 255) * lastWeight;
            uint ri = 0, gi = 0, bi = 0;
            for (int sx = first + 1; sx < last; sx++)
            {
                uint c = source[sx]; ri += (c >> 16) & 255; gi += (c >> 8) & 255; bi += c & 255;
            }
            r[x] = (rr + ri) / scaleX; g[x] = (gg + gi) / scaleX; b[x] = (bb + bi) / scaleX;
        }
        double top = sy, bottom = ++sy;
        while (top < bottom - 1e-9 && dy < dh)
        {
            double end = Math.Min(bottom, (dy + 1) * scaleY), weight = end - top;
            for (int x = 0; x < dw; x++) { sumR[x] += r[x] * weight; sumG[x] += g[x] * weight; sumB[x] += b[x] * weight; }
            top = end;
            if (end >= (dy + 1) * scaleY - 1e-9)
            {
                for (int x = 0; x < dw; x++) { row[x] = 0xff000000u | (uint)Math.Clamp((int)Math.Round(sumR[x] / scaleY), 0, 255) << 16 | (uint)Math.Clamp((int)Math.Round(sumG[x] / scaleY), 0, 255) << 8 | (uint)Math.Clamp((int)Math.Round(sumB[x] / scaleY), 0, 255); sumR[x] = sumG[x] = sumB[x] = 0; }
                output.Write(row); dy++;
            }
        }
    }
}
