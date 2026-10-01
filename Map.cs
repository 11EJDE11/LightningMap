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
        Local = Ini.Get("Map", "LocalSize", $"0,0,{Width},{Height}").Split(',').Select(int.Parse).ToArray();
        if (Local.Length != 4 || Local[0] < 0 || Local[1] < 0 || Local[2] <= 0 || Local[3] <= 0 || Local[0] + Local[2] > Width || Local[1] + Local[3] > Height) throw new InvalidDataException("Invalid LocalSize.");
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
