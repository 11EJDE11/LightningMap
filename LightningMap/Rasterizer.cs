using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
namespace LightningMap;

sealed record RenderStats(int Width, int Height, double DrawMs, double EncodeMs);
// A horizontal strip of the image: source rows [SourceY, SourceY + SourceRows) make output rows [OutputY, OutputY + OutputRows).
readonly record struct Band(int SourceY, int SourceRows, int OutputY, int OutputRows);
static class Rasterizer
{
    const int BandHeight = 32;
    // Ground (layer 0) and everything on it (layer 1) use disjoint depth ranges so a terrain tile can
    // never carve into an overlay, tree, building or unit that stands on or near it. Terrain depth is at
    // most a few hundred thousand, so one bit well above that is enough to keep the layers apart.
    const int LayerBias = 1 << 20;
    // Each band is drawn, downscaled and, for PNG, filtered and compressed on a worker thread, then handed to
    // the output strictly in order. Output is a PNG written to png, or rows handed to sink. Draw and encode
    // times are summed across workers.
    public static RenderStats Render(Scene scene, Stream? png, IImageSink? sink, int width, CompressionLevel compression)
    {
        int sw = scene.Width, sh = scene.Height;
        int w = width == 0 ? sw : Math.Min(width, sw), h = Math.Max(1, (int)Math.Round((double)sh * w / sw));
        var resampler = w == sw && h == sh ? null : new Resampler(sw, sh, w, h);
        Band[] bands = resampler?.Bands(BandHeight)
            ?? Enumerable.Range(0, (sh + BandHeight - 1) / BandHeight).Select(i => { int y = i * BandHeight, n = Math.Min(BandHeight, sh - y); return new Band(y, n, y, n); }).ToArray();
        // Downscaled bands can share a source row at their edges; a command is drawn into every band it touches.
        var commands = new List<Draw>?[bands.Length];
        foreach (var c in scene.Commands)
        {
            if (c.X >= sw || c.X + c.Sprite.Width <= 0 || c.Y >= sh || c.Y + c.Sprite.Height <= 0) continue;
            int lo = 0, hi = bands.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (bands[mid].SourceY + bands[mid].SourceRows <= c.Y) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < bands.Length && bands[i].SourceY < c.Y + c.Sprite.Height; i++) (commands[i] ??= new()).Add(c);
        }
        int sourceRows = bands.Max(b => b.SourceRows), outputRows = bands.Max(b => b.OutputRows);
        var pngOut = png != null ? new PngBands(png, w, h, compression) : null;
        sink?.Start(w, h);
        int next = -1, turn = 0; bool failed = false; long drawTicks = 0, encodeTicks = 0; var gate = new object();
        void Work()
        {
            uint[] pixels = new uint[sw * sourceRows]; int[] depth = new int[pixels.Length];
            uint[] rows = resampler == null ? pixels : new uint[w * outputRows];
            var scratch = resampler?.CreateScratch();
            var encoder = pngOut != null ? new PngBandEncoder(w, compression) : null;
            for (int index; (index = Interlocked.Increment(ref next)) < bands.Length;)
            {
                var band = bands[index];
                long start = Stopwatch.GetTimestamp();
                pixels.AsSpan(0, sw * band.SourceRows).Fill(0xff000000u); depth.AsSpan(0, sw * band.SourceRows).Fill(int.MinValue);
                if (commands[index] is { } list) foreach (var command in list) Blit(command, pixels, depth, sw, band.SourceY, band.SourceRows);
                long drawn = Stopwatch.GetTimestamp(); Interlocked.Add(ref drawTicks, drawn - start);
                resampler?.Run(band, pixels, scratch!, rows);
                encoder?.Encode(rows, band.OutputRows);
                long encoding = Stopwatch.GetTimestamp() - drawn;
                lock (gate)
                {
                    while (turn != index && !failed) Monitor.Wait(gate);
                    if (failed) return;
                    long writing = Stopwatch.GetTimestamp();
                    if (pngOut != null) pngOut.Append(encoder!);
                    else for (int row = 0; row < band.OutputRows; row++) sink!.WriteRow(rows.AsSpan(row * w, w));
                    turn++; Monitor.PulseAll(gate);
                    encoding += Stopwatch.GetTimestamp() - writing;
                }
                Interlocked.Add(ref encodeTicks, encoding);
            }
        }
        int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, bands.Length));
        try { Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ => { try { Work(); } catch { lock (gate) { failed = true; Monitor.PulseAll(gate); } throw; } }); }
        catch (AggregateException e) { ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw(); }
        long finish = Stopwatch.GetTimestamp();
        if (pngOut != null) pngOut.Finish(); else sink!.Finish();
        return new(w, h, Ms(drawTicks), Ms(encodeTicks) + Ms(Stopwatch.GetTimestamp() - finish));
    }
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    [MethodImpl(Bin.Hot)]
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
