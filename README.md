# LightningMap

A fast CPU renderer for **Red Alert 2: Yuri's Revenge** maps. It reads a map and your installed game art and writes a PNG or JPEG preview, with start positions marked. It does not run the game or use the map's embedded preview.

It comes as two parts:

- **`LightningMap`**, a .NET library (`net48` and `net8.0`, no platform-specific dependencies) for applications such as the CnCNet client.
- **`LightningMap.Cli`**, a command-line tool. The release `LightningMap.Cli.exe` is a single native Windows x64 executable; no .NET runtime is needed.

No game assets are included: point it at your Yuri's Revenge directory.

## Usage

```powershell
LightningMap.Cli <map-or-directory> --game <game-directory> [options]
```

```powershell
# Full-resolution PNG of one map
LightningMap.Cli.exe "C:\Games\YR\Maps\Yuri's Revenge\2_across_the_frost.map" --game "C:\Games\YR" --output frost.png

# 1024-pixel-wide JPEG preview
LightningMap.Cli.exe example.map --game "C:\Games\YR" --output preview.jpg --width 1024 --quality 90

# Every map in a directory (.map, .mpr, .yrm), with a JSON timing report
LightningMap.Cli.exe "C:\Games\YR\Maps" --game "C:\Games\YR" --output previews --width 1024 --report batch.json
```

| Option | Description |
|---|---|
| `--game <dir>` | Yuri's Revenge directory (required) |
| `--output <file-or-dir>` | `.png` or `.jpg` file, or a directory; default `./previews/<map>.png` |
| `--width <pixels>` | Downscale to this width; `0` (default) is native resolution |
| `--quality <1-100>` | JPEG quality (default 90) |
| `--compression fast\|normal\|small` | PNG compression (default `fast`) |
| `--format png\|jpg` | Output format for directory input (default `png`) |
| `--full-map` | Render the whole `Size` rectangle instead of the playable `LocalSize` |
| `--no-lighting` | Use unmodified palette colours |
| `--brightness <0.1-4>` | Exposure multiplier (default 1) |
| `--no-markers` | Omit start position markers |
| `--repeat <N>` | Render N times in one process (warm-cache timing) |
| `--report <file>` | Save timings, warnings and failures as JSON |
| `--strict` | Exit with code 2 if any asset warnings occur |
| `--self-test` | Run the built-in codec and output checks |

Native resolution is 60 pixels per map column and 30 per row, so `LocalSize=2,4,84,105` renders at 5040×3150. Start positions (waypoints 0–7) are drawn at their cell's elevation, so starts on cliffs and plateaus are placed correctly. Missing assets are reported as warnings and skipped. Exit codes: 0 success, 1 error, 2 failed maps in a directory run or `--strict` warnings.

## Performance

Whole-process time including startup (median of 5 runs), Ryzen 5 7600X (6 cores/12 threads), Windows 11, game files in the OS cache.

| Map | Native size | Native PNG | PNG size | 1024-wide PNG | Peak memory |
|---|---|---|---|---|---|
| SinkSwim | 1560×1050 (1.6 MP) | 47 ms | 2.1 MB | 50 ms | 37 MiB |
| xmp31s2 | 3960×2040 (8.1 MP) | 69 ms | 12.3 MB | 56 ms | 52 MiB |
| 2_across_the_frost | 5040×3150 (15.9 MP) | 91 ms | 23.0 MB | 68 ms | 63 MiB |
| 4_frostborne_isles | 6780×3480 (23.6 MP) | 103 ms | 27.7 MB | 75 ms | 65 MiB |
| mag | 8640×4860 (42.0 MP) | 142 ms | 40.8 MB | 94 ms | 78 MiB |

The image is drawn in 32-row bands on all cores. Each band is downscaled (when a width is given) and PNG-compressed on the same core, and the bands are joined into one zlib stream, so memory grows with map width rather than area. Directory runs reuse the archive index and decoded art between maps.

## Use from C#

Reference the `LightningMap` project. It targets `net48` and `net8.0` and depends on nothing beyond the runtime (plus `System.Memory` on `net48`); the `net8.0` build is platform-independent.

```csharp
using LightningMap;

// Once, e.g. on a background thread at startup: indexes the game archives and parses rulesmd.ini/artmd.ini.
var renderer = new RenderEngine(gameDirectory);

// Then per map. Each call reuses the archive index, the rules and recently decoded art.
RenderResult result = renderer.Render(mapPath, "preview.png", new RenderOptions(Width: 1024));
renderer.Render(mapPath, stream, options);   // PNG into any writable stream
renderer.Render(mapPath, sink, options);     // raw 0xAARRGGBB rows through IImageSink, e.g. straight into a texture
```

- Keep one `RenderEngine` for the life of the application and call it from one thread at a time. Dispose it to close the game archives.
- Decoded art is cached between renders and dropped once `CacheLimitBytes` (default 32 MiB) of new art has been read; `ClearCache()` drops it immediately. Renders without a warm cache are only a few milliseconds slower.
- On `net48`, PNG compression calls the zlib that ships with .NET Framework (`clrcompression.dll`) directly, because `DeflateStream` there cannot sync-flush, which parallel compression needs.
- 32-bit .NET Framework computes some lighting and voxel maths at a different floating-point precision, so a few hundred pixels per map can differ from 64-bit output.

Times for `2_across_the_frost` through the library, from one engine in a client-like process (default JIT settings, PNG written to disk; Ryzen 5 7600X, 6 cores/12 threads):

| | `net48` | `net8.0` |
|---|---|---|
| `new RenderEngine` | 44–54 ms | 25–30 ms |
| First render, native 5040×3150 | 195–210 ms | 180–195 ms |
| Later renders, native | 110–120 ms | 105–120 ms |
| First render, 5000 wide | 300 ms | 240–250 ms |
| Later renders, 5000 wide | 200–215 ms | 165–170 ms |

The first render includes JIT compilation and loading the theater.

## Build

```powershell
# Library
dotnet build LightningMap/LightningMap.csproj -c Release

# Command-line tool, as a native executable in publish/
dotnet publish LightningMap.Cli/LightningMap.Cli.csproj -c Release -r win-x64 -o publish
```

Requires the .NET 10 SDK. Publishing the tool also needs the MSVC linker (Visual Studio "Desktop development with C++") for Native AOT; `dotnet build` produces an ordinary JIT build. JPEG output exists only in the tool, through `System.Drawing`.

## Limitations

This is a static preview, not a reproduction of every game drawing rule. Voxel lighting is approximate, voxel shadows and animation poses are not drawn, tile variants are chosen deterministically, and Ares/Phobos extensions and mod-specific archive layouts are not supported.

## Format references

[MIX](https://xhp.xwis.net/documents/MIX_Format.html) · [TMP](https://modenc.renegadeprojects.com/TMP) · [TMP (XCC)](https://xhp.xwis.net/documents/TMP_TS_Format.html) · [SHP](https://moddingwiki.shikadi.net/wiki/Westwood_SHP_Format_(TS)) · [LZO](https://www.kernel.org/doc/Documentation/lzo.txt) · [Scenario terrain](https://opents-developers.github.io/OpenTS/formats/scenario-terrain/) · [VXL/HVA](https://github.com/sh4faq/Red-Alert-2--Modding-Guide/blob/master/01-VXL-HVA-Format.md)
