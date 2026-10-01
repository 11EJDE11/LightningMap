# LightningMap

A fast CPU renderer for **Red Alert 2: Yuri's Revenge** maps. It reads a map and your installed game art and writes a PNG or JPEG preview, with start positions marked. It does not run the game or use the map's embedded preview.

Windows x64. The release `LightningMap.exe` is a single native executable; no .NET runtime is needed. No game assets are included: point it at your Yuri's Revenge directory.

## Usage

```powershell
LightningMap <map-or-directory> --game <game-directory> [options]
```

```powershell
# Full-resolution PNG of one map
LightningMap.exe "C:\Games\YR\Maps\Yuri's Revenge\2_across_the_frost.map" --game "C:\Games\YR" --output frost.png

# 1024-pixel-wide JPEG preview
LightningMap.exe example.map --game "C:\Games\YR" --output preview.jpg --width 1024 --quality 90

# Every map in a directory (.map, .mpr, .yrm), with a JSON timing report
LightningMap.exe "C:\Games\YR\Maps" --game "C:\Games\YR" --output previews --width 1024 --report batch.json
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

Whole-process time including startup (median of 5 runs), Intel i7-8550U laptop (4 cores/8 threads), Windows 10, game files in the OS cache.

| Map | Native size | Native PNG | PNG size | 1024-wide PNG | Peak memory |
|---|---|---|---|---|---|
| SinkSwim | 1560×1050 (1.6 MP) | 90 ms | 2.1 MB | 172 ms | 34 MiB |
| xmp31s2 | 3960×2040 (8.1 MP) | 179 ms | 12.3 MB | 210 ms | 43 MiB |
| 2_across_the_frost | 5040×3150 (15.9 MP) | 294 ms | 23.0 MB | 277 ms | 50 MiB |
| 4_frostborne_isles | 6780×3480 (23.6 MP) | 332 ms | 27.7 MB | 319 ms | 53 MiB |
| mag | 8640×4860 (42.0 MP) | 487 ms | 40.8 MB | 416 ms | 62 MiB |

The image is drawn in 32-row bands on all cores, and full-resolution PNG bands are compressed in parallel, so memory grows with map width rather than area. Directory runs reuse the archive index and decoded art between maps.

## Use from C#

```csharp
using LightningMap;

using var renderer = new RenderEngine(gameDirectory);
RenderResult result = renderer.Render(mapPath, "preview.png", new RenderOptions(Width: 1024));
```

Call a `RenderEngine` from one thread at a time; dispose it to close the game archives.

## Build

```powershell
dotnet publish LightningMap.csproj -c Release -r win-x64 -o publish
```

Requires the .NET 10 SDK and the MSVC linker (Visual Studio "Desktop development with C++") for Native AOT. `dotnet build` produces an ordinary JIT build.

## Limitations

This is a static preview, not a reproduction of every game drawing rule. Voxel lighting is approximate, voxel shadows and animation poses are not drawn, tile variants are chosen deterministically, and Ares/Phobos extensions and mod-specific archive layouts are not supported.

## Format references

[MIX](https://xhp.xwis.net/documents/MIX_Format.html) · [TMP](https://modenc.renegadeprojects.com/TMP) · [TMP (XCC)](https://xhp.xwis.net/documents/TMP_TS_Format.html) · [SHP](https://moddingwiki.shikadi.net/wiki/Westwood_SHP_Format_(TS)) · [LZO](https://www.kernel.org/doc/Documentation/lzo.txt) · [Scenario terrain](https://opents-developers.github.io/OpenTS/formats/scenario-terrain/) · [VXL/HVA](https://github.com/sh4faq/Red-Alert-2--Modding-Guide/blob/master/01-VXL-HVA-Format.md)
