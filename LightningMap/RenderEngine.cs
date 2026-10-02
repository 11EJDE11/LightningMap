using System.Diagnostics;
using System.IO.Compression;
namespace LightningMap;

public sealed record RenderOptions(int Width = 0, CompressionLevel PngCompression = CompressionLevel.Fastest, bool FullMap = false, bool Lighting = true, double Brightness = 1, bool StartMarkers = true);
public sealed record RenderResult(int Width, int Height, double PrepareMs, double DrawMs, double EncodeMs, double TotalMs, double PeakWorkingSetMiB, long AssetBytesRead, string[] Warnings);

/// <summary>
/// Reusable rendering context. Construction indexes the game archives and parses rulesmd.ini and artmd.ini
/// once; decoded theater art is kept between renders. Keep one instance for the life of the application and
/// call it from one thread at a time. Dispose to close the game archives.
/// </summary>
public sealed class RenderEngine : IDisposable
{
    /// <summary>
    /// Decoded art grows with the number of distinct tiles and objects seen. Once this many bytes have been
    /// read from the game archives into the caches, they are dropped before the next render.
    /// </summary>
    public long CacheLimitBytes { get; set; } = 32L << 20;
    readonly Assets assets;
    readonly Ini rules, art;
    readonly Dictionary<string, Theater> theaters = new();
    long cacheStart;
    public RenderEngine(string gameDirectory)
    {
        assets = new Assets(gameDirectory);
        try { rules = new Ini(assets.Require("rulesmd.ini")); art = new Ini(assets.Require("artmd.ini")); }
        catch { assets.Dispose(); throw; }
        cacheStart = assets.BytesRead;
    }
    /// <summary>Renders a map to a PNG file. The file is replaced only once the image is complete.</summary>
    public RenderResult Render(string mapPath, string outputPath, RenderOptions? options = null)
    {
        if (!Path.GetExtension(outputPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a .png file.", nameof(outputPath));
        string full = Path.GetFullPath(outputPath);
        if (Path.GetFullPath(mapPath).Equals(full, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output cannot overwrite the input map.");
        string folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.tmp.png");
        try
        {
            RenderResult result;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)) result = Render(mapPath, file, null, options);
#if NET
            File.Move(temporary, full, overwrite: true);
#else
            if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
#endif
            return result;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Renders a map as PNG into a writable stream, which is left open.</summary>
    public RenderResult Render(string mapPath, Stream png, RenderOptions? options = null) => Render(mapPath, png, null, options);
    /// <summary>Renders a map and passes the pixels to a sink, for callers that encode or upload the image themselves.</summary>
    public RenderResult Render(string mapPath, IImageSink sink, RenderOptions? options = null) => Render(mapPath, null, sink, options);
    /// <summary>Releases decoded art. The next render of each theater decodes it again.</summary>
    public void ClearCache() { theaters.Clear(); cacheStart = assets.BytesRead; }
    RenderResult Render(string mapPath, Stream? png, IImageSink? sink, RenderOptions? options)
    {
        options ??= new();
        if (options.Width < 0 || double.IsNaN(options.Brightness) || options.Brightness is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(options));
        var timer = Stopwatch.StartNew();
        if (assets.BytesRead - cacheStart > CacheLimitBytes) ClearCache();
        long read = assets.BytesRead;
        var map = new Map(mapPath);
        if (!theaters.TryGetValue(map.Theater, out var theater)) theaters[map.Theater] = theater = new Theater(assets, map.Theater);
        // Markers are 42 native pixels in radius, or larger so that downscaled output keeps them at least 28 pixels.
        int[] rect = options.FullMap ? [0, 0, map.Width, map.Height] : map.Local;
        int nativeWidth = rect[2] * 60, outputWidth = options.Width == 0 ? nativeWidth : Math.Min(options.Width, nativeWidth);
        int markerRadius = options.StartMarkers ? Math.Max(42, (int)Math.Round(28.0 * nativeWidth / outputWidth)) : 0;
        var scene = new Scene(map, theater, rules, art, options.FullMap, options.Lighting, options.Brightness, markerRadius); double prepare = timer.Elapsed.TotalMilliseconds;
        var stats = Rasterizer.Render(scene, png, sink, options.Width, options.PngCompression);
        return new(stats.Width, stats.Height, prepare, stats.DrawMs, stats.EncodeMs, timer.Elapsed.TotalMilliseconds, Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, assets.BytesRead - read, scene.Warnings.ToArray());
    }
    public void Dispose() => assets.Dispose();
}
