namespace LightningMap;

readonly record struct Cell(short X, short Y, int Tile, byte Sub, byte Height);
sealed class Map
{
    public Ini Ini { get; }
    public int Width { get; }
    public int Height { get; }
    public int[] Local { get; }
    public string Theater { get; }
    public Cell[] Cells { get; }
    public byte[] Overlays { get; }
    public byte[] OverlayFrames { get; }
    readonly byte[] heights = new byte[512 * 512];
    public int Elevation(int x, int y) => (uint)x < 512 && (uint)y < 512 ? heights[y * 512 + x] : 0;
    public Map(string path)
    {
        Ini = new Ini(File.ReadAllBytes(path));
        var size = Ini.Get("Map", "Size").Split(',').Select(int.Parse).ToArray();
        if (size.Length != 4 || size[2] <= 0 || size[3] <= 0 || size[2] + size[3] > 510) throw new InvalidDataException("Invalid map dimensions.");
        Width = size[2]; Height = size[3];
        // Some maps' LocalSize runs past Size (the editor doesn't stop it, the game clamps); clamp it the same way,
        // and fall back to the whole map only when nothing usable is left.
        var local = Ini.Get("Map", "LocalSize", $"0,0,{Width},{Height}").Split(',').Select(v => int.TryParse(v.Trim(), out int n) ? n : -1).ToArray();
        if (local.Length != 4 || local.Any(n => n < 0)) local = [0, 0, Width, Height];
        int lx = Math.Min(local[0], Width - 1), ly = Math.Min(local[1], Height - 1);
        int lw = Math.Min(local[2], Width - lx), lh = Math.Min(local[3], Height - ly);
        Local = lw > 0 && lh > 0 ? [lx, ly, lw, lh] : [0, 0, Width, Height];
        Theater = Ini.Get("Map", "Theater", "TEMPERATE").ToUpperInvariant();
        var packed = Ini.Pack("IsoMapPack5", true, 512 * 512 * 11 + 4);
        if (packed.Length < 11) throw new InvalidDataException("Map has no IsoMapPack5 terrain.");
        var cells = new List<Cell>(packed.Length / 11);
        for (int p = 0; p + 11 <= packed.Length; p += 11)
        {
            short x = (short)Bin.U16(packed, p), y = (short)Bin.U16(packed, p + 2);
            if (x == 0 && y == 0) break;
            if ((uint)x >= 512 || (uint)y >= 512 || packed[p + 9] > 30) throw new InvalidDataException("Terrain cell outside supported bounds.");
            cells.Add(new(x, y, Bin.I32(packed, p + 4), packed[p + 8], packed[p + 9])); heights[y * 512 + x] = packed[p + 9];
        }
        Cells = cells.ToArray();
        Overlays = Ini.Pack("OverlayPack", false, 512 * 512);
        OverlayFrames = Ini.Pack("OverlayDataPack", false, 512 * 512);
        if (Overlays.Length != 0 && Overlays.Length != 512 * 512 || OverlayFrames.Length != 0 && OverlayFrames.Length != 512 * 512) throw new InvalidDataException("Invalid overlay grid length.");
    }
}
