using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
namespace LightningMap;

sealed record RenderStats(int Width, int Height, double DrawMs, double EncodeMs);
static class Rasterizer
{
    const int BandHeight = 32;
    // Ground (layer 0) and everything on it (layer 1) use disjoint depth ranges so a terrain tile can
    // never carve into an overlay, tree, building or unit that stands on or near it. Terrain depth is at
    // most a few hundred thousand, so one bit well above that is enough to keep the layers apart.
    const int LayerBias = 1 << 20;
    // Bands are drawn on worker threads and handed to the output strictly in order. Native-size PNG bands
    // are also filtered and compressed on the workers. Draw and encode times are summed across workers.
    public static RenderStats Render(Scene scene, string path, int width, int quality, CompressionLevel compression)
    {
        int w = width == 0 ? scene.Width : Math.Min(width, scene.Width), h = Math.Max(1, (int)Math.Round((double)scene.Height * w / scene.Width));
        var bands = new List<Draw>[(scene.Height + BandHeight - 1) / BandHeight];
        foreach (var c in scene.Commands)
        {
            if (c.X >= scene.Width || c.X + c.Sprite.Width <= 0 || c.Y >= scene.Height || c.Y + c.Sprite.Height <= 0) continue;
            int first = Math.Max(0, c.Y / BandHeight), last = Math.Min(bands.Length - 1, (c.Y + c.Sprite.Height - 1) / BandHeight);
            for (int i = first; i <= last; i++) (bands[i] ??= new()).Add(c);
        }
        bool jpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg", direct = !jpeg && w == scene.Width;
        PngBands? png = direct ? new PngBands(path, w, h, compression) : null;
        IRows? output = direct ? null : jpeg ? new JpegRows(path, w, h, quality) : new PngRows(path, w, h, compression);
        var resize = output == null ? null : new Downsample(output, scene.Width, scene.Height, w, h);
        int next = -1, turn = 0; bool failed = false; long drawTicks = 0, encodeTicks = 0; var gate = new object();
        void Work()
        {
            uint[] pixels = new uint[scene.Width * BandHeight]; int[] depth = new int[pixels.Length];
            var encoder = direct ? new PngBandEncoder(scene.Width, compression) : null;
            for (int band; (band = Interlocked.Increment(ref next)) < bands.Length;)
            {
                long start = Stopwatch.GetTimestamp(); Array.Fill(pixels, 0xff000000u); Array.Fill(depth, int.MinValue);
                int y = band * BandHeight, height = Math.Min(BandHeight, scene.Height - y);
                if (bands[band] is { } commands) foreach (var command in commands) Blit(command, pixels, depth, scene.Width, y, height);
                long drawn = Stopwatch.GetTimestamp(); Interlocked.Add(ref drawTicks, drawn - start);
                encoder?.Encode(pixels, height);
                long encoding = Stopwatch.GetTimestamp() - drawn;
                lock (gate)
                {
                    while (turn != band && !failed) Monitor.Wait(gate);
                    if (failed) return;
                    long writing = Stopwatch.GetTimestamp();
                    if (png != null) png.Append(encoder!);
                    else for (int row = 0; row < height; row++) resize!.Write(pixels.AsSpan(row * scene.Width, scene.Width));
                    turn++; Monitor.PulseAll(gate);
                    encoding += Stopwatch.GetTimestamp() - writing;
                }
                Interlocked.Add(ref encodeTicks, encoding);
            }
        }
        long finish;
        try
        {
            int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, bands.Length));
            try { Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ => { try { Work(); } catch { lock (gate) { failed = true; Monitor.PulseAll(gate); } throw; } }); }
            catch (AggregateException e) { ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw(); }
        }
        finally { finish = Stopwatch.GetTimestamp(); if (png != null) png.Dispose(); else output!.Dispose(); }
        return new(w, h, Stopwatch.GetElapsedTime(0, drawTicks).TotalMilliseconds, Stopwatch.GetElapsedTime(0, encodeTicks).TotalMilliseconds + Stopwatch.GetElapsedTime(finish).TotalMilliseconds);
    }
    static unsafe void Blit(Draw c, uint[] pixels, int[] depths, int width, int bandY, int bandHeight)
    {
        Sprite s = c.Sprite;
        int left = Math.Max(0, c.X), right = Math.Min(width, c.X + s.Width);
        int top = Math.Max(bandY, c.Y), bottom = Math.Min(bandY + bandHeight, c.Y + s.Height);
        if (left >= right || top >= bottom) return;
        int z0 = c.Depth + c.Layer * LayerBias, span = right - left;
        bool shadow = c.Shadow, flatDepth = c.Flat && s.Depth != null;
        fixed (byte* src = s.Pixels, srcZ = s.Depth, srcShade = s.Shade)
        fixed (uint* dst = pixels, palette = c.Palette)
        fixed (int* depth = depths)
        for (int y = top; y < bottom; y++)
        {
            int source = (y - c.Y) * s.Width + left - c.X, dest = (y - bandY) * width + left;
            byte* index = src + source; uint* color = dst + dest; int* z = depth + dest;
            // One loop per draw kind keeps the per-pixel work to a test, a lookup and two stores.
            if (shadow)
            {
                for (int x = 0; x < span; x++)
                    if (index[x] != 0 && z0 >= z[x]) color[x] = 0xff000000u | ((color[x] & 0xfefefe) >> 1);
            }
            else if (flatDepth)
            {
                byte* offset = srcZ + source;
                for (int x = 0; x < span; x++)
                {
                    byte i = index[x]; int value = z0 - offset[x];
                    if (i != 0 && value >= z[x]) { color[x] = palette[i]; z[x] = value; }
                }
            }
            else if (srcShade != null)
            {
                byte* shading = srcShade + source;
                for (int x = 0; x < span; x++)
                {
                    byte i = index[x]; if (i == 0 || z0 < z[x]) continue;
                    uint pixel = palette[i], shade = shading[x];
                    color[x] = 0xff000000u | ((((pixel >> 16) & 255) * shade / 255) << 16) | ((((pixel >> 8) & 255) * shade / 255) << 8) | ((pixel & 255) * shade / 255); z[x] = z0;
                }
            }
            else
            {
                for (int x = 0; x < span; x++)
                {
                    byte i = index[x];
                    if (i != 0 && z0 >= z[x]) { color[x] = palette[i]; z[x] = z0; }
                }
            }
        }
    }
}
