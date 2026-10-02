using System.Numerics;
using System.Text;
namespace LightningMap;

// A small static voxel rasterizer. Models are projected once per facing and cached as sprites.
// Lighting comes from exposed voxel faces rather than Westwood's proprietary normal lookup table.
static class Voxel
{
    readonly record struct Point(float X, float Y, float Z, byte Color, byte Shade);
    static float F(byte[] b, int p) => BitConverter.Int32BitsToSingle(Bin.I32(b, p));
    static string Name(byte[] b, int p) => Encoding.ASCII.GetString(b, p, 16).Split('\0')[0];
    [MethodImpl(Bin.Hot)]
    public static Sprite Render(byte[] file, byte[]? hva, int facing)
    {
        if (!file.AsSpan(0, 15).SequenceEqual("Voxel Animation"u8)) throw new InvalidDataException("Invalid VXL signature.");
        int count = Bin.I32(file, 20), bodyLength = Bin.I32(file, 28), body = 802 + count * 28, tails = body + bodyLength;
        if (count is < 1 or > 128 || tails < body || (long)tails + count * 92 > file.Length) throw new InvalidDataException("Invalid VXL directory.");
        var transforms = new Dictionary<string, float[]>();
        if (hva != null)
        {
            int sections = Bin.I32(hva, 20);
            if (sections is < 1 or > 128 || Bin.I32(hva, 16) < 1) throw new InvalidDataException("Invalid HVA directory.");
            for (int i = 0; i < sections; i++) transforms[Name(hva, 24 + i * 16)] = Enumerable.Range(0, 12).Select(k => F(hva, 24 + sections * 16 + i * 48 + k * 4)).ToArray();
        }
        var points = new List<Point>(); double angle = facing * Math.PI * 2 / 256 + Math.PI / 2;
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        for (int section = 0; section < count; section++)
        {
            int p = tails + section * 92, nx = file[p + 88], ny = file[p + 89], nz = file[p + 90];
            if (nx == 0 || ny == 0 || nz == 0) throw new InvalidDataException("Empty VXL dimensions.");
            int start = body + Bin.I32(file, p), data = body + Bin.I32(file, p + 8);
            byte[] grid = new byte[nx * ny * nz];
            for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            {
                int offset = Bin.I32(file, start + (x + y * nx) * 4); if (offset == -1) continue;
                int cursor = data + offset, z = 0;
                while (z < nz)
                {
                    z += file[cursor++]; int run = file[cursor++];
                    if (z + run > nz) throw new InvalidDataException("VXL span exceeds dimensions.");
                    for (int i = 0; i < run; i++) { grid[(z++ * ny + y) * nx + x] = file[cursor]; cursor += 2; }
                    if (file[cursor++] != run) throw new InvalidDataException("Invalid VXL run trailer.");
                    if (run == 0) break;
                }
            }
            float minX = F(file, p + 64), minY = F(file, p + 68), minZ = F(file, p + 72);
            float dx = (F(file, p + 76) - minX) / nx, dy = (F(file, p + 80) - minY) / ny, dz = (F(file, p + 84) - minZ) / nz;
            transforms.TryGetValue(Name(file, 802 + section * 28), out var matrix);
            bool Empty(int x, int y, int z) => x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz || grid[(z * ny + y) * nx + x] == 0;
            for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            {
                byte color = grid[(z * ny + y) * nx + x]; if (color == 0) continue;
                float vx = minX + (x + .5f) * dx, vy = minY + (y + .5f) * dy, vz = minZ + (z + .5f) * dz;
                if (matrix != null)
                {
                    float hx = matrix[0] * vx + matrix[1] * vy + matrix[2] * vz + matrix[3];
                    float hy = matrix[4] * vx + matrix[5] * vy + matrix[6] * vz + matrix[7];
                    vz = matrix[8] * vx + matrix[9] * vy + matrix[10] * vz + matrix[11]; vx = hx; vy = hy;
                }
                float rx = vx * cos - vy * sin, ry = vx * sin + vy * cos;
                float normalX = (Empty(x + 1, y, z) ? 1 : 0) - (Empty(x - 1, y, z) ? 1 : 0);
                float normalY = (Empty(x, y + 1, z) ? 1 : 0) - (Empty(x, y - 1, z) ? 1 : 0);
                float normalZ = (Empty(x, y, z + 1) ? 1 : 0) - (Empty(x, y, z - 1) ? 1 : 0);
                float shade = Math.Clamp(.78f + normalZ * .18f - (normalX * cos - normalY * sin) * .10f - (normalX * sin + normalY * cos) * .06f, .45f, 1);
                points.Add(new((rx - ry) * .7071068f, (rx + ry) * .3535534f - vz * .8660254f, rx + ry + vz * .4f, color, (byte)(shade * 255)));
            }
        }
        if (points.Count == 0) return new(0, 0, 0, 0, []);
        int left = (int)Math.Floor(points.Min(p => p.X)) - 1, top = (int)Math.Floor(points.Min(p => p.Y)) - 1;
        int width = (int)Math.Ceiling(points.Max(p => p.X)) - left + 2, height = (int)Math.Ceiling(points.Max(p => p.Y)) - top + 2;
        if (width > 2048 || height > 2048) throw new InvalidDataException("VXL projected bounds too large.");
        byte[] pixels = new byte[width * height], shadeMap = new byte[pixels.Length]; float[] depths = new float[pixels.Length]; depths.AsSpan().Fill(float.NegativeInfinity);
        foreach (var v in points)
        {
            int x = (int)Math.Round(v.X) - left, y = (int)Math.Round(v.Y) - top;
            for (int yy = y; yy <= y + 1; yy++) for (int xx = x; xx <= x + 1; xx++)
            { int dest = yy * width + xx; if (v.Z < depths[dest]) continue; pixels[dest] = v.Color; shadeMap[dest] = v.Shade; depths[dest] = v.Z; }
        }
        return new(width, height, left, top, pixels, Shade: shadeMap);
    }
}
