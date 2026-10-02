#if !NET
using System.IO.Compression;
using System.Runtime.InteropServices;
namespace LightningMap;

// .NET Framework has no ZLibStream, and its DeflateStream.Flush writes nothing, but parallel PNG compression
// needs zlib's sync flush. The framework's DeflateStream is built on zlib in clrcompression.dll in the runtime
// directory, so call that directly.
sealed unsafe class ZLibStream : Stream
{
    // z_stream; uLong is 32 bits on Windows.
    [StructLayout(LayoutKind.Sequential)]
    struct ZStream
    {
        public byte* NextIn; public uint AvailIn; public uint TotalIn;
        public byte* NextOut; public uint AvailOut; public uint TotalOut;
        public IntPtr Msg, State, Alloc, Free, Opaque;
        public int DataType; public uint Adler, Reserved;
    }
    const string Zlib = "clrcompression.dll";
    const int NoFlush = 0, SyncFlush = 2, Finish = 4, StreamEnd = 1, BufferError = -5;
    // Declared like the framework's own imports of these functions.
    [DllImport(Zlib, ExactSpelling = true, CharSet = CharSet.Ansi)] static extern int deflateInit2_(ZStream* stream, int level, int method, int windowBits, int memLevel, int strategy, string version, int streamSize);
    [DllImport(Zlib, ExactSpelling = true)] static extern int deflate(ZStream* stream, int flush);
    [DllImport(Zlib, ExactSpelling = true)] static extern int deflateEnd(ZStream* stream);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    // Loading by full path first makes the imports above bind to the framework's copy.
    static readonly bool loaded = LoadLibraryW(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), Zlib)) != IntPtr.Zero;

    readonly Stream output;
    readonly bool leaveOpen;
    readonly byte[] buffer = new byte[1 << 16];
    // zlib keeps a pointer back to its z_stream, so the struct lives in unmanaged memory where it cannot move.
    ZStream* z;
    public ZLibStream(Stream output, CompressionLevel compression, bool leaveOpen)
    {
        if (!loaded) throw new PlatformNotSupportedException($"{Zlib} was not found in the .NET Framework directory.");
        this.output = output; this.leaveOpen = leaveOpen;
        var stream = (ZStream*)Marshal.AllocHGlobal(sizeof(ZStream)); *stream = default;
        int level = compression switch { CompressionLevel.Fastest => 1, CompressionLevel.NoCompression => 0, _ => 6 };
        int result = deflateInit2_(stream, level, 8, 15, 8, 0, "1.2.11", sizeof(ZStream));
        if (result != 0) { Marshal.FreeHGlobal((IntPtr)stream); throw new IOException($"zlib initialisation failed ({result})."); }
        z = stream;
    }
    public override void Write(byte[] data, int offset, int count)
    {
        if (z == null) throw new ObjectDisposedException(nameof(ZLibStream));
        fixed (byte* p = data) { z->NextIn = p + offset; z->AvailIn = (uint)count; Deflate(NoFlush); z->NextIn = null; }
    }
    // Runs deflate until it has consumed all input and, for a flush, written everything out.
    void Deflate(int flush)
    {
        fixed (byte* p = buffer)
            while (true)
            {
                z->NextOut = p; z->AvailOut = (uint)buffer.Length;
                int result = deflate(z, flush);
                if (result < 0 && (result != BufferError || flush == Finish)) throw new IOException($"zlib compression failed ({result}).");
                int produced = buffer.Length - (int)z->AvailOut;
                if (produced > 0) output.Write(buffer, 0, produced);
                if (flush == Finish ? result == StreamEnd : z->AvailIn == 0 && z->AvailOut != 0) return;
            }
    }
    public override void Flush() { if (z != null) Deflate(SyncFlush); }
    protected override void Dispose(bool disposing)
    {
        if (z != null)
        {
            try { if (disposing) Deflate(Finish); }
            finally { deflateEnd(z); Marshal.FreeHGlobal((IntPtr)z); z = null; }
            if (disposing && !leaveOpen) output.Dispose();
        }
        base.Dispose(disposing);
    }
    ~ZLibStream() => Dispose(false);
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
}
#endif
