using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LightningMap;

// Parameter names are the JSON property names. Serialization is source-generated for native AOT.
sealed record ReportRecord(string map, string? output = null, int? iteration = null, double? initializationMs = null, RenderResult? result = null, string? error = null);
sealed record Report(double initializationMs, double elapsedMs, int failures, List<ReportRecord> records);
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Report))]
sealed partial class ReportJson : JsonSerializerContext;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test") { SelfTests.Run(); return 0; }
            if (args.Length >= 3 && args[0] == "extract") { using var a = new Assets(args[1]); File.WriteAllBytes(args.Length > 3 ? args[3] : args[2], a.Require(args[2])); Console.WriteLine($"{a.FileCount} indexed assets; {a.BytesRead:n0} bytes read"); return 0; }
            if (args.Length >= 2 && args[0] == "inspect") { var m = new Map(args[1]); Console.WriteLine($"{m.Width}x{m.Height} {m.Theater}: {m.Cells.Length} cells, tile max {m.Cells.Max(c => c.Tile)}, height max {m.Cells.Max(c => c.Height)}"); return 0; }
            if (args.Length == 0 || args.Contains("--help"))
            {
                Console.WriteLine("""
                    LightningMap <map-or-directory> --game <game-directory> [options]
                      --output <file-or-directory>  PNG or JPEG; default ./previews/<map>.png
                      --width <pixels>              Downscale to width; default 0 = native
                      --quality <1..100>            JPEG quality (default 90)
                      --compression fast|normal|small   PNG compression (default fast)
                      --full-map                    Include area outside LocalSize
                      --no-lighting                 Use unmodified palette colors
                      --no-markers                  Omit start position markers
                      --brightness <0.1..4>         Exposure multiplier (default 1)
                      --format png|jpg              Format for directory input (default png)
                      --repeat <N>                  Repeat in one process for warm-cache timings
                      --report <json-file>          Save measurements, warnings and failures
                      --strict                      Fail if any asset warnings occur
                      --self-test                   Run codec, resampling and output checks
                    Native dimensions: LocalSize width * 60 by LocalSize height * 30.
                    """); return 0;
            }
            var valued = new HashSet<string> { "--game", "--output", "--width", "--quality", "--compression", "--format", "--repeat", "--report", "--brightness" };
            var flags = new HashSet<string> { "--full-map", "--no-lighting", "--no-markers", "--strict" };
            var options = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (valued.Contains(args[i])) { if (i + 1 == args.Length) throw new ArgumentException($"Missing value for {args[i]}"); options.Add(args[i], args[++i]); }
                else if (flags.Contains(args[i])) options.Add(args[i], "true");
                else throw new ArgumentException($"Unknown option: {args[i]}");
            }
            string Option(string name, string fallback = "") => options.GetValueOrDefault(name, fallback);
            string game = Option("--game"), output = Option("--output", "previews"), format = Option("--format", "png");
            if (game.Length == 0) throw new ArgumentException("--game is required.");
            if (format is not ("png" or "jpg")) throw new ArgumentException("--format must be png or jpg.");
            int width = int.Parse(Option("--width", "0")), quality = int.Parse(Option("--quality", "90")), repeat = int.Parse(Option("--repeat", "1"));
            double brightness = double.Parse(Option("--brightness", "1"), System.Globalization.CultureInfo.InvariantCulture);
            if (width < 0 || quality is < 1 or > 100 || repeat is < 1 or > 10000 || !double.IsFinite(brightness) || brightness is < .1 or > 4) throw new ArgumentException("Invalid width, quality, repeat count or brightness.");
            var compression = Option("--compression", "fast") switch { "fast" => CompressionLevel.Fastest, "normal" => CompressionLevel.Optimal, "small" => CompressionLevel.SmallestSize, _ => throw new ArgumentException("Unknown compression mode.") };
            bool directory = Directory.Exists(args[0]);
            var files = directory ? Directory.GetFiles(args[0]).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".map" or ".mpr" or ".yrm").Order(StringComparer.OrdinalIgnoreCase).ToArray() : new[] { args[0] };
            if (files.Length == 0) throw new ArgumentException("No map files found.");
            bool outputDirectory = directory || !options.ContainsKey("--output") || Directory.Exists(output);
            var total = Stopwatch.StartNew(); using var engine = new RenderEngine(game);
            double init = total.Elapsed.TotalMilliseconds;
            var records = new List<ReportRecord>(); int failures = 0;
            foreach (string file in files) for (int iteration = 0; iteration < repeat; iteration++)
            {
                string path = outputDirectory ? Path.Combine(output, Path.GetFileNameWithoutExtension(file) + "." + format) : output;
                try
                {
                    var result = engine.Render(file, path, new(width, quality, compression, options.ContainsKey("--full-map"), !options.ContainsKey("--no-lighting"), brightness, !options.ContainsKey("--no-markers")));
                    records.Add(new(file, path, iteration, init, result));
                    Console.WriteLine($"{Path.GetFileName(file)} -> {path} ({result.Width}x{result.Height}) prepare={result.PrepareMs:F1}ms draw={result.DrawMs:F1}ms resize/encode={result.EncodeMs:F1}ms total={result.TotalMs:F1}ms init={init:F1}ms peak={result.PeakWorkingSetMiB:F1}MiB");
                    if (iteration == 0) foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {Path.GetFileName(file)}: {warning}");
                    if (options.ContainsKey("--strict") && result.Warnings.Length > 0) failures++;
                }
                catch (Exception e) when (directory) { failures++; records.Add(new(file, error: e.Message)); Console.Error.WriteLine($"error: {file}: {e.Message}"); }
            }
            if (Option("--report") is { Length: > 0 } report)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, JsonSerializer.Serialize(new Report(init, total.Elapsed.TotalMilliseconds, failures, records), ReportJson.Default.Report));
            }
            return failures == 0 ? 0 : 2;
        }
        catch (Exception e) { Console.Error.WriteLine($"error: {e.Message}"); if (Environment.GetEnvironmentVariable("LIGHTNINGMAP_DEBUG") == "1") Console.Error.WriteLine(e); return 1; }
    }
}
