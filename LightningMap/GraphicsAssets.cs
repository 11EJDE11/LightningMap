namespace LightningMap;

sealed record Sprite(int Width, int Height, int X, int Y, byte[] Pixels, byte[]? Depth = null, byte[]? Shade = null);
sealed class Shp(byte[] bytes)
{
    public int Count { get; } = Bin.U16(bytes, 6);
    public int Width { get; } = Bin.U16(bytes, 2);
    public int Height { get; } = Bin.U16(bytes, 4);
    readonly Dictionary<int, Sprite> frames = new();
    [MethodImpl(Bin.Hot)]
    public Sprite Frame(int frame)
    {
        frame = Math.Clamp(frame, 0, Count - 1);
        if (frames.TryGetValue(frame, out var cached)) return cached;
        int p = 8 + frame * 24, w = Bin.U16(bytes, p + 4), h = Bin.U16(bytes, p + 6), offset = Bin.I32(bytes, p + 20);
        if ((long)w * h > 16_000_000) throw new InvalidDataException("SHP frame too large.");
        byte[] pixels = new byte[w * h];
        if (offset > 0 && pixels.Length > 0)
        {
            if ((bytes[p + 8] & 3) == 3)
            {
                for (int y = 0; y < h; y++)
                {
                    int end = offset + Bin.U16(bytes, offset); offset += 2; int x = 0;
                    if (end < offset || end > bytes.Length) throw new InvalidDataException("Bad SHP row length.");
                    while (offset < end)
                    {
                        byte b = bytes[offset++];
                        if (b == 0) { if (offset >= end) throw new InvalidDataException("Truncated SHP run."); x += bytes[offset++]; }
                        else { if (x >= w) throw new InvalidDataException("SHP row overflow."); pixels[y * w + x++] = b; }
                    }
                    // Original art can end a scanline with transparent alignment padding.
                }
            }
            else if ((bytes[p + 8] & 3) == 2)
            {
                for (int y = 0; y < h; y++) { int length = Bin.U16(bytes, offset); if (length < 2 || length - 2 > w) throw new InvalidDataException("Invalid SHP literal row."); bytes.AsSpan(offset + 2, length - 2).CopyTo(pixels.AsSpan(y * w)); offset += length; }
            }
            else bytes.AsSpan(offset, pixels.Length).CopyTo(pixels);
        }
        return frames[frame] = new(w, h, Bin.U16(bytes, p) - Width / 2, Bin.U16(bytes, p + 2) - Height / 2, pixels);
    }
}

sealed record Tile(Sprite Ground, Sprite? Extra, int Height, int Ramp);
static class Tmp
{
    [MethodImpl(Bin.Hot)]
    public static Tile?[] Decode(byte[] b)
    {
        int count = checked(Bin.I32(b) * Bin.I32(b, 4));
        int w = Bin.I32(b, 8), h = Bin.I32(b, 12);
        if (count < 1 || count > 100 || w != 60 || h != 30) throw new InvalidDataException("Invalid RA2 TMP header.");
        var tiles = new Tile?[count];
        for (int i = 0; i < count; i++)
        {
            int p = Bin.I32(b, 16 + i * 4); if (p == 0) continue;
            byte[] pixels = new byte[w * h], depth = new byte[w * h];
            int source = p + 52, z = p + Bin.I32(b, p + 12), n = 0;
            bool hasZ = (b[p + 36] & 2) != 0 && z > p;
            for (int y = 0; y < h; y++)
            {
                int row = y < 15 ? (y + 1) * 4 : (29 - y) * 4;
                int dest = y * w + (w - row) / 2;
                b.AsSpan(source + n, row).CopyTo(pixels.AsSpan(dest));
                if (hasZ) b.AsSpan(z + n, row).CopyTo(depth.AsSpan(dest)); n += row;
            }
            Sprite? extra = null;
            int ew = Bin.I32(b, p + 28), eh = Bin.I32(b, p + 32);
            if ((b[p + 36] & 1) != 0 && ew > 0 && eh > 0)
            {
                if ((long)ew * eh > 1_000_000) throw new InvalidDataException("TMP extra image too large.");
                int ep = p + Bin.I32(b, p + 8), ez = p + Bin.I32(b, p + 16);
                var data = b.AsSpan(ep, ew * eh).ToArray();
                extra = new(ew, eh, Bin.I32(b, p + 20) - Bin.I32(b, p), Bin.I32(b, p + 24) - Bin.I32(b, p + 4), data, hasZ && ez > p ? b.AsSpan(ez, ew * eh).ToArray() : null);
            }
            tiles[i] = new(new(w, h, 0, 0, pixels, depth), extra, b[p + 40], b[p + 42]);
        }
        return tiles;
    }
}

sealed class Theater
{
    public string Extension { get; }
    public string Letter { get; }
    public string PaletteSuffix { get; }
    public byte[] IsoPalette { get; }
    public byte[] UnitPalette { get; }
    public byte[] TheaterPalette { get; }
    readonly Assets assets;
    readonly List<string> tiles = new();
    readonly Dictionary<int, Tile?[][]> cache = new();
    readonly Dictionary<string, Shp?> shapes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, int), Sprite?> voxels = new();
    public Sprite? VoxelShape(string image, int facing)
    {
        var key = (image.ToUpperInvariant(), facing);
        if (voxels.TryGetValue(key, out var cached)) return cached;
        var file = assets.Get(image + ".vxl");
        return voxels[key] = file == null ? null : Voxel.Render(file, assets.Get(image + ".hva"), facing);
    }
    public Theater(Assets assets, string name)
    {
        this.assets = assets;
        (string ini, Extension, Letter, PaletteSuffix) = name switch {
            "TEMPERATE" => ("temperatmd.ini", "tem", "T", "tem"),
            "SNOW" => ("snowmd.ini", "sno", "A", "sno"),
            "URBAN" => ("urbanmd.ini", "urb", "U", "urb"),
            "NEWURBAN" => ("urbannmd.ini", "ubn", "N", "ubn"),
            "DESERT" => ("desertmd.ini", "des", "D", "des"),
            "LUNAR" => ("lunarmd.ini", "lun", "L", "lun"),
            _ => throw new NotSupportedException($"Unsupported theater: {name}")
        };
        IsoPalette = assets.Require($"iso{PaletteSuffix}.pal");
        UnitPalette = assets.Require($"unit{PaletteSuffix}.pal");
        TheaterPalette = assets.Require(ini.Replace("md.ini", ".pal"));
        var control = new Ini(assets.Require(ini));
        foreach (string section in control.Sections.Where(s => s.StartsWith("TileSet", StringComparison.OrdinalIgnoreCase)).OrderBy(s => int.Parse(s[7..])))
            for (int i = 1; i <= control.Int(section, "TilesInSet"); i++) tiles.Add(control.Get(section, "FileName") + i.ToString("D2"));
    }
    public Tile? GetTile(int id, int sub, int seed)
    {
        if (id is -1 or 65535) id = 0;
        if ((uint)id >= tiles.Count) return null;
        if (!cache.TryGetValue(id, out var variants))
        {
            var list = new List<Tile?[]>();
            byte[]? main = assets.Get($"{tiles[id]}.{Extension}");
            if (main == null) { cache[id] = []; return null; }
            list.Add(Tmp.Decode(main));
            // Alternate art is randomized deterministically; damaged bridge art is not a variant.
            int first = Bin.I32(main, 16);
            if (first != 0 && (main[first + 36] & 4) == 0)
                for (char c = 'a'; c <= 'g'; c++) { var b = assets.Get($"{tiles[id]}{c}.{Extension}"); if (b == null) break; list.Add(Tmp.Decode(b)); }
            cache[id] = variants = list.ToArray();
        }
        if (variants.Length == 0) return null;
        var chosen = variants[(uint)seed % variants.Length];
        return (uint)sub < chosen.Length ? chosen[sub] : null;
    }
    public Shp? Shape(string image, bool theater, bool newTheater)
    {
        string file = image + (theater ? "." + Extension : ".shp");
        if (newTheater && image.Length >= 2)
        {
            string candidate = image[..1] + Letter + image[2..] + ".shp";
            if (assets.Exists(candidate)) file = candidate;
            else { candidate = image[..1] + "G" + image[2..] + ".shp"; if (assets.Exists(candidate)) file = candidate; }
        }
        if (shapes.TryGetValue(file, out var s)) return s;
        byte[]? b = assets.Get(file); if (b == null && theater) b = assets.Get(image + ".shp");
        return shapes[file] = b == null ? null : new Shp(b);
    }
}
