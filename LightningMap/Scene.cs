namespace LightningMap;

// Layer 0 is the ground (terrain tiles), layer 1 is ground overlays (ore, rocks, bridges, tracks) and
// layer 2 is everything standing on the ground (trees, buildings, units). Layer 3 is start position
// markers, drawn over everything. The rasterizer gives each
// layer a disjoint depth range so a lower layer can never be drawn over a higher one.
sealed record Draw(Sprite Sprite, int X, int Y, int Depth, uint[] Palette, bool Shadow = false, bool Flat = false, int Layer = 2);

// How the game lights a drawn object (gamemd; see ccmaps-net's LightingType for the per-class research):
//   Full     the cell's brightness and colour tint, including lamp tints: terrain tiles, ordinary overlays, terrain objects
//   Ambient  brightness only, lamps add brightness but not colour: buildings, units, infantry, aircraft, walls, ore drills
//   None     drawn as authored: ore and gems, anim-palette art
enum Light { None, Ambient, Full }

sealed class Scene
{
    enum ArtKind { Terrain, Overlay, Building, Infantry, Unit, Aircraft }
    public List<Draw> Commands { get; } = new();
    public SortedSet<string> Warnings { get; } = new();
    public int Width { get; }
    public int Height { get; }
    readonly Map map;
    readonly Theater theater;
    readonly Ini rules, art;
    readonly Dictionary<(int, int, int, int, string), uint[]> palettes = new();
    readonly List<Lamp> lamps = new();
    readonly int originX, originY;
    readonly bool lighting;
    readonly double brightness, ambient, level, ground, red, green, blue;
    readonly double extraUnitLight, extraInfantryLight, extraAircraftLight;

    // A lamp lights from its building's centre: the cell plus (foundation - 1) half cells on each axis
    // (BuildingClass::GetCoords). Visibility is in leptons, 256 to a cell.
    readonly record struct Lamp(double X, double Y, double Visibility, double Intensity, double Red, double Green, double Blue);

    // Ore and gems: the map stores one overlay id per cell, but the game only uses it to find the
    // tiberium type and draws from that type's pool of images instead (CellClass::DrawOverlay):
    // flat cells use pool[(x * y) % 12]; cells on a full slope use the 8 slope pieces after the 12 flat
    // ones, 2 per slope direction. Ranges are OverlayTypes registration indices (RA2/YR).
    static readonly (int Min, int Max)[] TiberiumPools = [(102, 121), (27, 38), (127, 146), (147, 166)];

    // markerRadius is in native pixels; 0 omits start position markers.
    [MethodImpl(Bin.Hot)]
    public Scene(Map map, Theater theater, Ini baseRules, Ini art, bool full, bool lighting, double brightness = 1, int markerRadius = 0)
    {
        this.map = map; this.theater = theater; this.art = art; this.lighting = lighting; this.brightness = brightness;
        // Read once: Palette runs for every cell and object.
        ambient = map.Ini.Number("Lighting", "Ambient", 1); level = map.Ini.Number("Lighting", "Level", .032); ground = map.Ini.Number("Lighting", "Ground");
        red = map.Ini.Number("Lighting", "Red", 1); green = map.Ini.Number("Lighting", "Green", 1); blue = map.Ini.Number("Lighting", "Blue", 1);
        rules = Ini.Layered(baseRules, map.Ini);
        // Units, infantry and aircraft draw a shade lighter than the ground under them (Draw_It adds these).
        extraUnitLight = rules.Number("AudioVisual", "ExtraUnitLight"); extraInfantryLight = rules.Number("AudioVisual", "ExtraInfantryLight");
        extraAircraftLight = rules.Number("AudioVisual", "ExtraAircraftLight");
        int[] rect = full ? [0, 0, map.Width, map.Height] : map.Local;
        Width = rect[2] * 60; Height = rect[3] * 30;
        originX = rect[0] * 60; originY = rect[1] * 30;
        if (!full)
        {
            // The visible area in game reaches above and below LocalSize: from 2.5 rows above its top to 2
            // rows below its bottom, but never past the last fully tiled row of the map (ccmaps-net
            // GetLocalSizePixels, which matches in-game screenshots). Without this, cliffs and lakes on the
            // top and bottom edges are cut off.
            int top = Math.Max(rect[1] - 3, 0) * 30 + 15;
            int bottom = Math.Min(2 * (rect[1] - 3 + rect[3] + 5), LastFullRow() * 2 + 1) * 15;
            if (bottom > top) { originY = top; Height = bottom - top; }
        }
        LoadLamps();
        foreach (var cell in map.Cells)
        {
            var tile = theater.GetTile(cell.Tile, cell.Sub, unchecked(cell.X * 73856093 ^ cell.Y * 19349663));
            if (tile == null) { Warnings.Add($"Missing terrain tile {cell.Tile}:{cell.Sub}"); continue; }
            var (x, y) = Position(cell.X, cell.Y, cell.Height);
            if (!Visible(tile.Ground, x - 30, y - 15) && (tile.Extra == null || !Visible(tile.Extra, x - 30 + tile.Extra.X, y - 15 + tile.Extra.Y))) continue;
            var pal = Palette(theater.IsoPalette, cell.X, cell.Y, cell.Height, "", Light.Full);
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
            bool tiberium = rules.Yes(id, "Tiberium"), wall = rules.Yes(id, "Wall");
            string storedImage = rules.Get(id, "Image", id);
            if (tiberium)
            {
                int pooled = PooledTiberium(overlay, cell);
                if (pooled != overlay && pooled < overlayNames.Length) id = overlayNames[pooled];
            }
            string image = rules.Get(id, "Image", id);
            var shp = theater.Shape(image, art.Yes(image, "Theater"), art.Yes(image, "NewTheater"));
            if (shp == null) { Warnings.Add($"Missing overlay {id} ({image})"); continue; }
            int frame = index < map.OverlayFrames.Length ? map.OverlayFrames[index] : 0;
            var (x, y) = Position(cell.X, cell.Y, cell.Height);
            // Overlays are anchored on the cell's top corner, half a cell above its centre. Tiberium is drawn in
            // its own pass 3 pixels lower; RA2 walls also sit 3 lower. Rock and road overlays (Land=Rock/Road)
            // drop half a cell, railroads sit 14 pixels below the corner.
            string land = rules.Get(id, "Land");
            y += land.Equals("Railroad", StringComparison.OrdinalIgnoreCase) ? -1
                : land.Equals("Rock", StringComparison.OrdinalIgnoreCase) || land.Equals("Road", StringComparison.OrdinalIgnoreCase) ? 0 : -15;
            if (tiberium || wall) y += 3;
            x += art.Int(image, "XDrawOffset"); y += art.Int(image, "YDrawOffset");
            var sprite = shp.Frame(frame);
            if (!Visible(sprite, x + sprite.X, y + sprite.Y)) continue;
            var (source, light) = tiberium ? (theater.TheaterPalette, Light.None) : wall ? (theater.UnitPalette, Light.Ambient) : (theater.IsoPalette, Light.Full);
            var pal = Palette(source, cell.X, cell.Y, cell.Height, "", light);
            // Overlays cast shadows (the second half of their frames). CellClass::Draw_Overlay_Shadow reads the
            // overlay id the map stored, so pooled tiberium casts the stored image's shadow, not its pooled body's.
            if (!art.Yes(storedImage, "NoShadow") && !rules.Yes(id, "NoShadow"))
            {
                var stored = storedImage.Equals(image, StringComparison.OrdinalIgnoreCase) ? shp : theater.Shape(storedImage, art.Yes(storedImage, "Theater"), art.Yes(storedImage, "NewTheater"));
                if (stored != null && stored.Count >= 2 && stored.Count % 2 == 0 && frame < stored.Count / 2)
                {
                    var shadow = stored.Frame(frame + stored.Count / 2);
                    if (shadow.Pixels.Length > 0 && shadow.Pixels.All(b => b <= 1)) Add(shadow, x, y, (cell.X + cell.Y) * 32 + 15, pal, true, layer: 1);
                }
            }
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
                var kind = section switch { "Structures" => ArtKind.Building, "Infantry" => ArtKind.Infantry, "Aircraft" => ArtKind.Aircraft, _ => ArtKind.Unit };
                int subCell = kind == ArtKind.Infantry && int.TryParse(f[5], out int sc) ? sc : 0;
                Object(f[1], int.Parse(f[3]), int.Parse(f[4]), f[0], kind, facing, int.Parse(f[2]), subCell);
            }
        if (markerRadius > 0) StartMarkers(markerRadius);
        Commands.Sort((a, b) => a.Depth.CompareTo(b.Depth));
    }

    // The game makes a light source for every building type with LightVisibility and a non-zero
    // LightIntensity. Absent tints default to 1000 (the type's per-mille field initialised to 1,000,000),
    // and when the map has its own section for the type, keys it omits are re-read as whole numbers
    // (BuildingTypeClass::Read_INI divides the per-mille default by 1000 in integer maths).
    void LoadLamps()
    {
        foreach (string v in map.Ini.Section("Structures").Values)
        {
            var f = v.Split(','); if (f.Length < 5) continue;
            string id = f[1];
            if (!rules.Has(id, "LightVisibility")) continue;
            bool mapSection = map.Ini.Sections.Contains(id, StringComparer.OrdinalIgnoreCase);
            double Key(string key, double fallback)
            {
                double value = rules.Number(id, key, fallback);
                return mapSection && !map.Ini.Has(id, key) ? Math.Truncate(value) : value;
            }
            double intensity = Key("LightIntensity", 0);
            if (Math.Abs(intensity) < .001) continue;
            if (!int.TryParse(f[3], out int cx) || !int.TryParse(f[4], out int cy)) continue;
            string image = rules.Get(id, "Image", id);
            var foundation = art.Get(image, "Foundation", "1x1").Split('x');
            int fw = foundation.Length == 2 && int.TryParse(foundation[0], out int w) ? w : 1, fh = foundation.Length == 2 && int.TryParse(foundation[1], out int h) ? h : 1;
            lamps.Add(new(cx + (fw - 1) * .5, cy + (fh - 1) * .5, rules.Number(id, "LightVisibility", 5000), intensity,
                Key("LightRedTint", 1000), Key("LightGreenTint", 1000), Key("LightBlueTint", 1000)));
        }
    }

    // Rows are 30-pixel screen rows (dy / 2). Searching up from the bottom of the map, the first row whose
    // cells are all present is the lowest the image may reach; below it the map edge shows black.
    int LastFullRow()
    {
        int columns = map.Width * 2, rows = map.Height + 1;
        var touched = new bool[columns * rows];
        foreach (var c in map.Cells)
        {
            int dx = c.X - c.Y + map.Width - 1, row = (c.X + c.Y - map.Width - 1) / 2;
            if ((uint)dx < columns && (uint)row < rows) touched[row * columns + dx] = true;
        }
        for (int y = map.Height - 1; y > map.Height - 10 && y >= 0; y--)
        {
            bool full = true;
            for (int x = 1; x < columns - 3 && full; x++) full = touched[y * columns + x];
            if (full) return y;
        }
        return map.Height - 1;
    }

    int PooledTiberium(int overlay, Cell cell)
    {
        foreach (var (min, max) in TiberiumPools)
        {
            if (overlay < min || overlay > max) continue;
            int images = Math.Min(12, max - min + 1), slopes = max - min + 1 - images, product = cell.X * cell.Y;
            int ramp = theater.GetTile(cell.Tile, cell.Sub, 0)?.Ramp ?? 0;
            return ramp is >= 1 and <= 4 && slopes >= 8 ? min + images + product % 2 + (ramp - 1) * 2 : min + product % images;
        }
        return overlay;
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

    (byte[] Source, Light Light) ArtPalette(ArtKind kind, string id, string image)
    {
        if (art.Yes(image, "AnimPalette")) return (theater.AnimPalette, Light.None);
        if (art.Yes(image, "TerrainPalette")) return (theater.IsoPalette, kind == ArtKind.Terrain ? Light.Full : Light.Ambient);
        // Ordinary terrain uses iso art. Ore drills (TIBTRE01/02/03) are terrain objects drawn through the
        // unit palette and lit by brightness only, like buildings and units. Ownership only affects remapping.
        if (kind == ArtKind.Terrain) return rules.Yes(id, "SpawnsTiberium") ? (theater.UnitPalette, Light.Ambient) : (theater.IsoPalette, Light.Full);
        return (theater.UnitPalette, Light.Ambient);
    }
    double ExtraLight(ArtKind kind) => kind switch { ArtKind.Unit => extraUnitLight, ArtKind.Infantry => extraInfantryLight, ArtKind.Aircraft => extraAircraftLight, _ => 0 };

    void Object(string id, int cx, int cy, string owner, ArtKind kind, int facing, int health, int subCell = 0)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (rules.Yes(id, "InvisibleInGame")) return;
        string image = rules.Get(id, "Image", id), imageFile = art.Get(image, "Image", image);
        var (source, light) = ArtPalette(kind, id, image);
        if (art.Yes(image, "Voxel"))
        {
            int elevation = map.Elevation(cx, cy); var pos = Position(cx, cy, elevation);
            var pal = Palette(source, cx, cy, elevation, owner, light, ExtraLight(kind));
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
        // Buildings are drawn centred on their entry cell's top corner; the art is authored for that, so the
        // foundation only matters for draw order. Terrain objects, units and infantry centre on the cell.
        if (kind == ArtKind.Building) y -= 15;
        // Ore drills are raised 16 pixels (TerrainClass::Draw_It via TiberiumDrawer).
        if (kind == ArtKind.Terrain && rules.Yes(id, "SpawnsTiberium")) y -= 16;
        // Infantry stand on one of the engine's sub-cell spots, a quarter cell off the centre (RA2/YR: 2 up-right, 3 down-left, 4 down).
        if (kind == ArtKind.Infantry) (x, y) = subCell switch { 2 => (x + 15, y), 3 => (x - 15, y), 4 => (x, y + 7), _ => (x, y) };
        x += art.Int(image, "XDrawOffset"); y += art.Int(image, "YDrawOffset");
        if (kind == ArtKind.Building)
        {
            string foundation = art.Get(image, "Foundation", "1x1"); var dimensions = foundation.Split('x');
            if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int fw) && int.TryParse(dimensions[1], out int fh))
            depth += (fw + fh - 2) * 32;
            if (Damaged(id, health) && shp.Count >= 4) frame = 1;
        }
        if (kind == ArtKind.Infantry)
        {
            string seq = art.Get(image, "Sequence"); var ready = art.Get(seq, "Guard", "0,1,1").Split(',');
            if (ready.Length >= 3) frame = int.Parse(ready[0]) + ((facing + 16) / 32 % 8) * int.Parse(ready[2]);
        }
        var palette = Palette(source, cx, cy, height, owner, light, ExtraLight(kind));
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
                if (Damaged(id, health)) anim = art.Get(image, key + "Damaged", anim);
                var animation = theater.Shape(art.Get(anim, "Image", anim), art.Yes(anim, "Theater"), art.Yes(anim, "NewTheater", art.Yes(image, "NewTheater")));
                if (animation == null) { Warnings.Add($"Missing building animation {anim}"); continue; }
                int z = key == "BibShape" ? depth - 2 : depth + 1;
                // Animations with AnimPalette=yes (glows, screens) are drawn through anim.pal, unlit.
                var animPalette = art.Yes(anim, "AnimPalette") ? Palette(theater.AnimPalette, cx, cy, height, "", Light.None) : palette;
                Add(animation.Frame(art.Int(anim, "Start")), x + art.Int(image, key + "X"), y + art.Int(image, key + "Y"), z, animPalette);
            }
        }
    }

    // A building shows its damaged art at or below ConditionYellow (50%), except occupiable civilian buildings,
    // which stay intact until ConditionRed (25%) (BuildingClass draw state; ccmaps-net BuildingDrawable).
    bool Damaged(string id, int health)
    {
        int yellow = (int)(256 * Percent("ConditionYellow", 50) / 100), red = (int)(256 * Percent("ConditionRed", 25) / 100);
        if (health > yellow) return false;
        return !(health > red && rules.Yes(id, "CanBeOccupied") && rules.Int(id, "TechLevel") < 1);
    }
    double Percent(string key, double fallback) => rules.Has("AudioVisual", key) ? rules.Number("AudioVisual", key, fallback) : fallback;

    // The engine draws through 63 intensity steps from black to double brightness (LightConvertClass):
    // a cell's brightness lands on a multiple of 1/31. AlphaLightingRemapClass::Get_Table picks the row
    // with (261 * brightness) >> 11 and the entry is (127 * shade * 62) / 32258.
    static double QuantizeIntensity(double intensity)
    {
        int brightness = Math.Clamp((int)(intensity * 1000), 0, 2000);
        int shade = Math.Min(254, (261 * brightness) >> 11);
        return Math.Min(62, 127 * shade * 62 / 32258) / 31.0;
    }

    // Per-cell lighting as the game computes it (CellClass::ComputeLighting and the normalisation after it):
    // brightness = Ambient - Ground + Level * height, plus each lamp's intensity scaled by distance; the colour
    // tint starts at the map's Red/Green/Blue and lamps add their tints. Brightness and each tint channel clamp
    // to [0, 2]; the tint is normalised so its largest channel is 1 and that channel moves into the brightness,
    // which clamps to 2 again and is quantised to the engine's 63 steps.
    [MethodImpl(Bin.Hot)]
    uint[] Palette(byte[] source, int x, int y, int height, string owner, Light light, double extra = 0)
    {
        double rm = 1, gm = 1, bm = 1;
        if (lighting && light != Light.None)
        {
            double amb = ambient - ground + level * height + extra, tr = 1, tg = 1, tb = 1;
            if (light == Light.Full) { tr = red; tg = green; tb = blue; }
            foreach (var lamp in lamps)
            {
                double dx = lamp.X - x, dy = lamp.Y - y, leptons = 256 * Math.Sqrt(dx * dx + dy * dy);
                // LightSourceClass::Process truncates the distance and lights while it is within visibility.
                if (lamp.Visibility <= 0 || Math.Floor(leptons) > lamp.Visibility) continue;
                double effect = (lamp.Visibility - leptons) / lamp.Visibility;
                amb += effect * lamp.Intensity;
                if (light == Light.Full) { tr += effect * lamp.Red; tg += effect * lamp.Green; tb += effect * lamp.Blue; }
            }
            amb = Math.Clamp(amb, 0, 2); tr = Math.Clamp(tr, 0, 2); tg = Math.Clamp(tg, 0, 2); tb = Math.Clamp(tb, 0, 2);
            double m = Math.Max(tr, Math.Max(tg, tb));
            if (m < .001) rm = gm = bm = 0;
            else
            {
                double intensity = QuantizeIntensity(Math.Min(amb * m, 2));
                rm = intensity * tr / m; gm = intensity * tg / m; bm = intensity * tb / m;
            }
        }
        // 1/256 steps keep the palette cache small while staying below what an 8-bit channel can show.
        int ir = (int)Math.Round(rm * brightness * 256), ig = (int)Math.Round(gm * brightness * 256), ib = (int)Math.Round(bm * brightness * 256);
        string color = owner.Length == 0 ? "" : rules.Get(owner, "Color", "Grey");
        int sourceId = ReferenceEquals(source, theater.IsoPalette) ? 0 : ReferenceEquals(source, theater.UnitPalette) ? 1 : ReferenceEquals(source, theater.TheaterPalette) ? 2 : 3;
        var key = (sourceId, ir, ig, ib, color);
        if (palettes.TryGetValue(key, out var cached)) return cached;
        byte[] colors = source;
        if (owner.Length != 0) { colors = (byte[])source.Clone(); RemapHouse(colors, rules.Get("Colors", color, "0,0,180")); }
        // Unit-palette colours 240-254 are never lit (lights, glows and other self-illuminated details).
        bool objectPalette = sourceId == 1;
        uint[] result = new uint[256];
        double unlit = brightness * 256;
        for (int i = 0; i < 256; i++)
        {
            bool fixedColour = objectPalette && i is >= 240 and <= 254;
            double r = fixedColour ? unlit : ir, g = fixedColour ? unlit : ig, b = fixedColour ? unlit : ib;
            // Palettes are 6-bit: 63 is full intensity.
            result[i] = 0xff000000u | (uint)Math.Clamp((int)(colors[i * 3] * r / 256 / 63 * 255), 0, 255) << 16
                | (uint)Math.Clamp((int)(colors[i * 3 + 1] * g / 256 / 63 * 255), 0, 255) << 8
                | (uint)Math.Clamp((int)(colors[i * 3 + 2] * b / 256 / 63 * 255), 0, 255);
        }
        palettes[key] = result; return result;
    }

    // The 16 house-colour shades in palette indices 16-31, built the way gamemd does (0x0068C3B0): the
    // colour's hue is kept, saturation is swept up a sine and value down a cosine, so the darkest shade is
    // black; the conversion is the engine's integer HSV (0x00517440). Colour is "hue,saturation,value", 0-255.
    static void RemapHouse(byte[] colors, string hsv)
    {
        var parts = hsv.Split(',');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int hue) || !int.TryParse(parts[1], out int sat) || !int.TryParse(parts[2], out int val)) return;
        for (int i = 0; i < 16; i++)
        {
            double value = i == 0 ? 0.19634954084936207 : i * 0.08144869842640204 + 0.3490658503988659;
            double saturation = i * 0.046542113386515455 + 0.8726646259971648;
            var (r, g, b) = EngineHsv(hue, (int)(Math.Sin(saturation) * sat), (int)(Math.Cos(value) * val));
            colors[(16 + i) * 3] = (byte)(r * 63 / 255); colors[(16 + i) * 3 + 1] = (byte)(g * 63 / 255); colors[(16 + i) * 3 + 2] = (byte)(b * 63 / 255);
        }
    }
    static (int R, int G, int B) EngineHsv(int h, int s, int v)
    {
        int sector = h * 6 / 255, frac = h * 6 % 255;
        int p = (255 - s) * v / 255, q = (255 - frac * s / 255) * v / 255, t = (255 - (255 - frac) * s / 255) * v / 255;
        return sector switch { 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), 5 => (v, p, q), _ => (v, t, p) };
    }
}
