namespace LightningMap;

// Layer 0 is the ground (terrain tiles), layer 1 is ground overlays (ore, rocks, bridges, tracks) and
// layer 2 is everything standing on the ground (trees, buildings, units). Layer 3 is start position
// markers, drawn over everything. The rasterizer gives each
// layer a disjoint depth range so a lower layer can never be drawn over a higher one.
sealed record Draw(Sprite Sprite, int X, int Y, int Depth, uint[] Palette, bool Shadow = false, bool Flat = false, int Layer = 2);
sealed class Scene
{
    enum ArtKind { Terrain, Overlay, Building, Infantry, Unit }
    public List<Draw> Commands { get; } = new();
    public SortedSet<string> Warnings { get; } = new();
    public int Width { get; }
    public int Height { get; }
    readonly Map map;
    readonly Theater theater;
    readonly Ini rules, art;
    readonly Dictionary<(int, int, int, int, string), uint[]> palettes = new();
    readonly List<(int X, int Y, double Radius, double Intensity, double R, double G, double B)> lights = new();
    readonly int originX, originY;
    readonly bool lighting;
    readonly double brightness, ambient, level, ground, red, green, blue;
    // markerRadius is in native pixels; 0 omits start position markers.
    public Scene(Map map, Theater theater, Ini baseRules, Ini art, bool full, bool lighting, double brightness = 1, int markerRadius = 0)
    {
        this.map = map; this.theater = theater; this.art = art; this.lighting = lighting; this.brightness = brightness;
        // Read once: Palette runs for every cell and object.
        ambient = map.Ini.Number("Lighting", "Ambient", 1); level = map.Ini.Number("Lighting", "Level", .032); ground = map.Ini.Number("Lighting", "Ground");
        red = map.Ini.Number("Lighting", "Red", 1); green = map.Ini.Number("Lighting", "Green", 1); blue = map.Ini.Number("Lighting", "Blue", 1);
        rules = new Ini(); rules.Merge(baseRules); rules.Merge(map.Ini);
        int[] rect = full ? [0, 0, map.Width, map.Height] : map.Local;
        Width = rect[2] * 60; Height = rect[3] * 30;
        originX = rect[0] * 60; originY = rect[1] * 30;
        foreach (string v in map.Ini.Section("Structures").Values)
        {
            var f = v.Split(','); if (f.Length < 5) continue;
            string id = f[1]; double intensity = rules.Number(id, "LightIntensity");
            if (intensity == 0) continue;
            lights.Add((int.Parse(f[3]), int.Parse(f[4]), rules.Number(id, "LightVisibility", 5000) / 256, intensity, rules.Number(id, "LightRedTint", 1), rules.Number(id, "LightGreenTint", 1), rules.Number(id, "LightBlueTint", 1)));
        }
        foreach (var cell in map.Cells)
        {
            var tile = theater.GetTile(cell.Tile, cell.Sub, unchecked(cell.X * 73856093 ^ cell.Y * 19349663));
            if (tile == null) { Warnings.Add($"Missing terrain tile {cell.Tile}:{cell.Sub}"); continue; }
            var (x, y) = Position(cell.X, cell.Y, cell.Height);
            if (!Visible(tile.Ground, x - 30, y - 15) && (tile.Extra == null || !Visible(tile.Extra, x - 30 + tile.Extra.X, y - 15 + tile.Extra.Y))) continue;
            var pal = Palette(theater.IsoPalette, cell.X, cell.Y, cell.Height, "", true);
            int depth = (cell.X + cell.Y) * 32;
            Commands.Add(new(tile.Ground, x - 30, y - 15, depth, pal, Flat: true, Layer: 0));
            if (tile.Extra != null) Commands.Add(new(tile.Extra, x - 30 + tile.Extra.X, y - 15 + tile.Extra.Y, depth, pal, Flat: true, Layer: 0));
        }
        // Type numbers are registration order, not the arbitrary INI key labels (stock keys start at 1).
        var overlayNames = rules.Section("OverlayTypes").Values.ToArray();
        foreach (var cell in map.Cells)
        {
            int index = cell.Y * 512 + cell.X;
            if (index >= map.Overlays.Length || map.Overlays[index] == 255) continue;
            int overlay = map.Overlays[index];
            if (overlay >= overlayNames.Length) { Warnings.Add($"Unregistered overlay {overlay}"); continue; }
            string id = overlayNames[overlay];
            if (id.Equals("USELESS", StringComparison.OrdinalIgnoreCase) || id.StartsWith("DUMMY", StringComparison.OrdinalIgnoreCase)) continue;
            string image = rules.Get(id, "Image", id);
            var shp = theater.Shape(image, art.Yes(image, "Theater"), art.Yes(image, "NewTheater"));
            if (shp == null) { Warnings.Add($"Missing overlay {id} ({image})"); continue; }
            int frame = index < map.OverlayFrames.Length ? map.OverlayFrames[index] : 0;
            var (x, y) = Position(cell.X, cell.Y, cell.Height);
            var sprite = shp.Frame(frame);
            if (!Visible(sprite, x + sprite.X, y + sprite.Y)) continue;
            var pal = Palette(ArtPalette(ArtKind.Overlay, id, image), cell.X, cell.Y, cell.Height, "", true);
            try { Add(sprite, x, y, (cell.X + cell.Y) * 32 + 16, pal, layer: 1); }
            catch (InvalidDataException e) { throw new InvalidDataException($"Overlay {id} image {image} frame {frame}: {e.Message}", e); }
        }
        foreach (var kv in map.Ini.Section("Terrain"))
        {
            int coord = int.Parse(kv.Key); int x = coord % 1000, y = coord / 1000;
            Object(kv.Value.Split(',')[0], x, y, "", ArtKind.Terrain, 0, 256);
        }
        foreach (var section in new[] { "Structures", "Infantry", "Units", "Aircraft" })
            foreach (string value in map.Ini.Section(section).Values)
            {
                var f = value.Split(','); if (f.Length < 6) { Warnings.Add($"Malformed {section} record"); continue; }
                int facing = int.TryParse(f[section == "Infantry" ? 7 : 5], out int face) ? face : 0;
                var kind = section switch { "Structures" => ArtKind.Building, "Infantry" => ArtKind.Infantry, _ => ArtKind.Unit };
                Object(f[1], int.Parse(f[3]), int.Parse(f[4]), f[0], kind, facing, int.Parse(f[2]));
            }
        if (markerRadius > 0) StartMarkers(markerRadius);
        Commands.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    }
    // Multiplayer starts are waypoints 0-7, read until the first gap like the client does. Each marker is
    // centred on its cell at that cell's elevation, so starts on raised ground sit on the plateau, and is
    // drawn on a layer above everything so no cliff or building can cover it.
    void StartMarkers(int radius)
    {
        var waypoints = map.Ini.Section("Waypoints");
        int size = radius * 2 + 1, ring = Math.Max(2, radius / 6);
        byte[] pixels = new byte[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double distance = Math.Sqrt((x - radius) * (x - radius) + (y - radius) * (y - radius));
            pixels[y * size + x] = (byte)(distance > radius + .5 ? 0 : distance > radius + .5 - ring ? 1 : distance > radius + .5 - ring * 2 ? 2 : 3);
        }
        var marker = new Sprite(size, size, -radius, -radius, pixels);
        uint[] palette = new uint[256]; palette[1] = 0xff101010; palette[2] = 0xffffffff; palette[3] = 0xffe02020;
        for (int i = 0; i < 8; i++)
        {
            if (!waypoints.TryGetValue(i.ToString(), out string? value)) break;
            if (!int.TryParse(value, out int coord) || coord < 0) { Warnings.Add($"Invalid start waypoint {i}"); continue; }
            int cx = coord % 1000, cy = coord / 1000; var (x, y) = Position(cx, cy, map.Elevation(cx, cy));
            Commands.Add(new(marker, x + marker.X, y + marker.Y, 0, palette, Layer: 3));
        }
    }
    (int X, int Y) Position(int x, int y, int h) => ((x - y + map.Width) * 30 - originX, (x + y - map.Width) * 15 - h * 15 - originY);
    bool Visible(Sprite sprite, int x, int y) => x < Width && y < Height && x + sprite.Width > 0 && y + sprite.Height > 0;
    void Add(Sprite s, int x, int y, int depth, uint[] palette, bool shadow = false, int layer = 2) => Commands.Add(new(s, x + s.X, y + s.Y, depth, palette, shadow, Layer: layer));
    byte[] ArtPalette(ArtKind kind, string id, string image)
    {
        // The theater palette is for resource overlays, not everything related to ore.
        // Other overlays use iso art, except walls/fences which use the unit palette.
        if (kind == ArtKind.Overlay)
            return rules.Yes(id, "Tiberium") ? theater.TheaterPalette
                : rules.Yes(id, "Wall") ? theater.UnitPalette : theater.IsoPalette;

        if (art.Yes(image, "TerrainPalette")) return theater.IsoPalette;

        // Ordinary terrain uses iso art. Ore drills (TIBTRE01/02/03) are terrain objects
        // authored in the unit palette, like buildings and units. Ownership only affects remapping.
        return kind == ArtKind.Terrain && !rules.Yes(id, "SpawnsTiberium")
            ? theater.IsoPalette : theater.UnitPalette;
    }
    void Object(string id, int cx, int cy, string owner, ArtKind kind, int facing, int health)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (rules.Yes(id, "InvisibleInGame")) return;
        string image = rules.Get(id, "Image", id), imageFile = art.Get(image, "Image", image);
        if (art.Yes(image, "Voxel"))
        {
            int elevation = map.Elevation(cx, cy); var pos = Position(cx, cy, elevation);
            var pal = Palette(ArtPalette(kind, id, image), cx, cy, elevation, owner, true);
            var voxel = theater.VoxelShape(imageFile, facing);
            if (voxel == null) { Warnings.Add($"Missing voxel {imageFile}"); return; }
            Add(voxel, pos.X, pos.Y, (cx + cy) * 32 + 24, pal);
            if (rules.Yes(id, "Turret")) foreach (string suffix in new[] { "tur", "barl" })
            { var part = theater.VoxelShape(imageFile + suffix, facing); if (part != null) Add(part, pos.X, pos.Y, (cx + cy) * 32 + 25, pal); }
            return;
        }
        var shp = theater.Shape(imageFile, art.Yes(image, "Theater"), art.Yes(image, "NewTheater"));
        if (shp == null) { Warnings.Add($"Missing object {id} ({imageFile})"); return; }
        int height = map.Elevation(cx, cy); var (x, y) = Position(cx, cy, height);
        int depth = (cx + cy) * 32 + 24, frame = art.Int(image, "Start");
        if (kind == ArtKind.Building)
        {
            string foundation = art.Get(image, "Foundation", "1x1"); var dimensions = foundation.Split('x');
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int fw) && int.TryParse(dimensions[1], out int fh))
            { x += (fw - fh) * 15; y += (fw + fh - 2) * 7; depth += (fw + fh - 2) * 32; }
            if (health < 128 && shp.Count >= 4) frame = 1;
        }
        if (kind == ArtKind.Infantry)
        {
            string seq = art.Get(image, "Sequence"); var ready = art.Get(seq, "Guard", "0,1,1").Split(',');
            if (ready.Length >= 3) frame = int.Parse(ready[0]) + ((facing + 16) / 32 % 8) * int.Parse(ready[2]);
        }
        var palette = Palette(ArtPalette(kind, id, image), cx, cy, height, owner, true);
        if (shp.Count % 2 == 0 && !art.Yes(image, "NoShadow"))
        {
            var shadow = shp.Frame(frame + shp.Count / 2);
            if (shadow.Pixels.All(b => b <= 1)) Add(shadow, x, y, depth - 1, palette, true);
        }
        Add(shp.Frame(frame), x, y, depth, palette);
        if (kind == ArtKind.Building)
        {
            foreach (string key in new[] { "BibShape", "ActiveAnim", "ActiveAnimTwo", "ActiveAnimThree", "ActiveAnimFour", "IdleAnim", "SuperAnim" })
            {
                string anim = art.Get(image, key); if (anim.Length == 0) continue;
                if (health < 128) anim = art.Get(image, key + "Damaged", anim);
                var animation = theater.Shape(art.Get(anim, "Image", anim), art.Yes(anim, "Theater"), art.Yes(anim, "NewTheater", art.Yes(image, "NewTheater")));
                if (animation == null) { Warnings.Add($"Missing building animation {anim}"); continue; }
                int z = key == "BibShape" ? depth - 2 : depth + 1;
                Add(animation.Frame(art.Int(anim, "Start")), x + art.Int(image, key + "X"), y + art.Int(image, key + "Y"), z, palette);
            }
        }
    }
    uint[] Palette(byte[] source, int x, int y, int height, string owner, bool lit)
    {
        double ambient = lighting && lit ? this.ambient + height * level - ground : 1;
        double r = lighting && lit ? red : 1, g = lighting && lit ? green : 1, b = lighting && lit ? blue : 1;
        if (lighting && lit) foreach (var light in lights)
        {
            double distance = Math.Sqrt((x - light.X) * (x - light.X) + (y - light.Y) * (y - light.Y));
            double amount = Math.Max(0, 1 - distance / light.Radius) * light.Intensity;
            r += amount * light.R; g += amount * light.G; b += amount * light.B;
        }
        // Quantization bounds palette-cache growth while retaining smooth lighting at preview sizes.
        int ir = Math.Clamp((int)Math.Round(r * ambient * brightness * 64), 0, 512), ig = Math.Clamp((int)Math.Round(g * ambient * brightness * 64), 0, 512), ib = Math.Clamp((int)Math.Round(b * ambient * brightness * 64), 0, 512);
        string color = owner.Length == 0 ? "" : rules.Get(owner, "Color", "Grey");
        var key = (ReferenceEquals(source, theater.IsoPalette) ? 0 : ReferenceEquals(source, theater.UnitPalette) ? 1 : 2, ir, ig, ib, color);
        if (palettes.TryGetValue(key, out var cached)) return cached;
        uint[] result = new uint[256];
        var remap = rules.Get("Colors", color, "0,0,180").Split(',');
        for (int i = 0; i < 256; i++)
        {
            double red = source[i * 3] * 4, green = source[i * 3 + 1] * 4, blue = source[i * 3 + 2] * 4;
            if (owner.Length != 0 && i is >= 16 and <= 31 && remap.Length == 3)
            {
                double h = int.Parse(remap[0]) / 255.0 * 6, sat = int.Parse(remap[1]) / 255.0, value = int.Parse(remap[2]) * (1.0 - (i - 16) / 20.0);
                double chroma = value * sat, second = chroma * (1 - Math.Abs(h % 2 - 1)), min = value - chroma;
                (red, green, blue) = (int)h switch { 0 => (chroma, second, 0d), 1 => (second, chroma, 0d), 2 => (0d, chroma, second), 3 => (0d, second, chroma), 4 => (second, 0d, chroma), _ => (chroma, 0d, second) };
                red += min; green += min; blue += min;
            }
            result[i] = 0xff000000u | (uint)Math.Clamp((int)(red * ir / 64), 0, 255) << 16 | (uint)Math.Clamp((int)(green * ig / 64), 0, 255) << 8 | (uint)Math.Clamp((int)(blue * ib / 64), 0, 255);
        }
        palettes[key] = result; return result;
    }
}
