using System.Drawing;
using System.Drawing.Imaging;
namespace LightningMap;

// JPEG through GDI+, so it stays out of the cross-platform library. The file is written once all rows have arrived.
sealed class JpegSink(string path, int quality) : IImageSink, IDisposable
{
    Bitmap? bitmap;
    BitmapData? data;
    int row;
    public void Start(int width, int height)
    {
        bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
    }
    public unsafe void WriteRow(ReadOnlySpan<uint> pixels)
    {
        if (bitmap == null || data == null || row >= bitmap.Height || pixels.Length != bitmap.Width) throw new InvalidOperationException("Wrong JPEG row dimensions.");
        pixels.CopyTo(new Span<uint>((void*)(data.Scan0 + data.Stride * row++), bitmap.Width));
    }
    public void Finish()
    {
        bitmap!.UnlockBits(data!); data = null;
        string full = Path.GetFullPath(path), temporary = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            using var options = new EncoderParameters(1); options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bitmap.Save(temporary, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), options);
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { if (data != null) bitmap!.UnlockBits(data); bitmap?.Dispose(); }
}
