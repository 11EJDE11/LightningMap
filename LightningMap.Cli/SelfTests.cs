using System.Drawing;
using System.IO.Compression;
namespace LightningMap;

static class SelfTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception("Self-test failed: " + message); }
    static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Self-test accepted invalid data: " + message); }
    public static void Run()
    {
        Check(Bin.Crc("123456789"u8) == 0xcbf43926, "CRC-32 known vector");
        byte[] lzo = new byte[7]; Packs.Lzo([21, 65, 66, 67, 68, 76, 0, 17, 0, 0], lzo);
        Check(lzo.SequenceEqual("ABCDABC"u8.ToArray()), "LZO literal and back-reference");
        byte[] repeated = new byte[8]; Packs.Lzo([18, 65, 192, 0, 17, 0, 0], repeated);
        Check(repeated.All(b => b == 65), "LZO overlapping back-reference");
        Reject(() => Packs.Lzo([17, 0, 0], new byte[1]), "LZO wrong output length");
        byte[] lcw = new byte[6]; Packs.Lcw([0x83, 65, 66, 67, 0, 3, 0x80], lcw);
        Check(lcw.SequenceEqual("ABCABC"u8.ToArray()), "LCW short match");
        Packs.Lcw([0, 0x83, 65, 66, 67, 0xc0, 3, 0, 0x80], lcw);
        Check(lcw.SequenceEqual("ABCABC"u8.ToArray()), "LCW relative long match");
        Packs.Lcw([0xfe, 6, 0, 42, 0x80], lcw); Check(lcw.All(b => b == 42), "LCW fill");
        Reject(() => Packs.Lcw([0, 0x80], new byte[1]), "LCW wrong output length");
        Reject(() => Packs.Decode([255, 255, 1, 0], true, 8192), "truncated pack");
        Reject(() => Packs.Decode([1, 0, 0, 32, 0], true, 2), "pack output limit");
        var ini = new Ini("[S]\r\nKey = 12 ; comment\r\n[S]\nOther=yes\n");
        Check(ini.Int("s", "KEY") == 12 && ini.Yes("S", "Other"), "INI case and repeated section");
        var random = new Random(71);
        foreach (var (sw, sh, dw, dh) in new[] { (37, 17, 13, 7), (16, 8, 4, 2), (5, 3, 1, 1), (7, 9, 6, 8), (8, 8, 8, 8) })
        {
            uint[] pixels = Enumerable.Range(0, sw * sh).Select(_ => 0xff000000u | (uint)random.Next(1 << 24)).ToArray();
            // Two-source-row bands, so the test also covers rows shared between bands.
            var resampler = new Resampler(sw, sh, dw, dh); var bands = resampler.Bands(2); var scratch = resampler.CreateScratch();
            uint[] output = new uint[dw * dh];
            foreach (var band in bands)
            {
                uint[] rows = new uint[dw * band.OutputRows]; resampler.Run(band, pixels.AsSpan(band.SourceY * sw, band.SourceRows * sw).ToArray(), scratch, rows);
                rows.CopyTo(output, band.OutputY * dw);
            }
            Check(bands.Sum(b => b.OutputRows) == dh, "resize height");
            for (int y = 0; y < dh; y++) for (int x = 0; x < dw; x++)
            {
                double l = (double)x * sw / dw, r = (double)(x + 1) * sw / dw, t = (double)y * sh / dh, b = (double)(y + 1) * sh / dh;
                for (int shift = 0; shift <= 16; shift += 8)
                {
                    double sum = 0;
                    for (int sy = (int)t; sy < Math.Ceiling(b); sy++) for (int sx = (int)l; sx < Math.Ceiling(r); sx++)
                        sum += ((pixels[sy * sw + sx] >> shift) & 255) * (Math.Min(sx + 1, r) - Math.Max(sx, l)) * (Math.Min(sy + 1, b) - Math.Max(sy, t));
                    int expected = (int)Math.Round(sum / ((r - l) * (b - t)));
                    Check(Math.Abs(expected - ((output[y * dw + x] >> shift) & 255)) <= 1, "area-filter pixel");
                }
            }
        }
        string temp = Path.Combine(Path.GetTempPath(), "lightningmap-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = File.Create(temp + ".png"))
            {
                // One band per row, so the joined zlib stream and combined Adler-32 are exercised.
                var png = new PngBands(file, 3, 2, CompressionLevel.Fastest); var encoder = new PngBandEncoder(3, CompressionLevel.Fastest);
                encoder.Encode([0xffff0000, 0xff00ff00, 0xff0000ff], 1); png.Append(encoder); encoder.Encode([0xffffffff, 0xff000000, 0xff123456], 1); png.Append(encoder); png.Finish();
            }
            using (var image = new Bitmap(temp + ".png")) { Check(image.Width == 3 && image.Height == 2, "PNG dimensions"); Check(image.GetPixel(2, 1).ToArgb() == unchecked((int)0xff123456), "PNG decoded pixels"); }
            using (var jpg = new JpegSink(temp + ".jpg", 95)) { jpg.Start(8, 8); for (int y = 0; y < 8; y++) jpg.WriteRow(Enumerable.Repeat(0xff808080u, 8).ToArray()); jpg.Finish(); }
            using (var image = new Bitmap(temp + ".jpg")) { Check(image.Width == 8 && Math.Abs(image.GetPixel(4, 4).R - 128) < 3, "JPEG encoding"); }
        }
        finally { foreach (string ext in new[] { ".png", ".jpg" }) if (File.Exists(temp + ext)) File.Delete(temp + ext); }
        Console.WriteLine("PASS: codecs, malformed pack rejection, INI, exact area downsampling, PNG pixels, JPEG.");
    }
}
