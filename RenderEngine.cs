using System.Diagnostics;
using System.IO.Compression;
namespace LightningMap;

public sealed record RenderOptions(int Width = 0, int JpegQuality = 90, CompressionLevel PngCompression = CompressionLevel.Fastest, bool FullMap = false, bool Lighting = true, double Brightness = 1, bool StartMarkers = true);
public sealed record RenderResult(int Width, int Height, double PrepareMs, double DrawMs, double EncodeMs, double TotalMs, double PeakWorkingSetMiB, long AssetBytesRead, string[] Warnings);

/// <summary>Reusable, single-threaded rendering context. Dispose to close game archive handles.</summary>
public sealed class RenderEngine : IDisposable
{
    readonly Assets assets;
    readonly Ini rules, art;
    Theater? theater;
    string? theaterName;
    int sinceReset;
    public RenderEngine(string gameDirectory)
    {
        assets = new Assets(gameDirectory);
        try { rules = new Ini(assets.Require("rulesmd.ini")); art = new Ini(assets.Require("artmd.ini")); }
        catch { assets.Dispose(); throw; }
    }
    public RenderResult Render(string mapPath, string outputPath, RenderOptions? options = null)
    {
        options ??= new();
        if (options.Width < 0 || options.JpegQuality is < 1 or > 100 || !double.IsFinite(options.Brightness) || options.Brightness is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(options));
        string ext = Path.GetExtension(outputPath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) throw new ArgumentException("Output must be PNG or JPEG.");
        if (Path.GetFullPath(mapPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output cannot overwrite the input map.");
        var timer = Stopwatch.StartNew(); long read = assets.BytesRead;
        var map = new Map(mapPath);
        // Bound retained asset memory during very long batch jobs.
        if (theaterName != map.Theater || sinceReset >= 32) { theater = new Theater(assets, map.Theater); theaterName = map.Theater; sinceReset = 0; }
        sinceReset++;
        // Markers are 42 native pixels in radius, or larger so that downscaled output keeps them at least 28 pixels.
        int[] rect = options.FullMap ? [0, 0, map.Width, map.Height] : map.Local;
        int nativeWidth = rect[2] * 60, outputWidth = options.Width == 0 ? nativeWidth : Math.Min(options.Width, nativeWidth);
        int markerRadius = options.StartMarkers ? Math.Max(42, (int)Math.Round(28.0 * nativeWidth / outputWidth)) : 0;
        var scene = new Scene(map, theater!, rules, art, options.FullMap, options.Lighting, options.Brightness, markerRadius); double prepare = timer.Elapsed.TotalMilliseconds;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp{ext}");
        RenderStats stats;
        try { stats = Rasterizer.Render(scene, temporary, options.Width, options.JpegQuality, options.PngCompression); File.Move(temporary, outputPath, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(stats.Width, stats.Height, prepare, stats.DrawMs, stats.EncodeMs, timer.Elapsed.TotalMilliseconds, Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, assets.BytesRead - read, scene.Warnings.ToArray());
    }
    public void Dispose() => assets.Dispose();
}
