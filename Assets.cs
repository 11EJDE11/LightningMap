using Microsoft.Win32.SafeHandles;
namespace LightningMap;

sealed class Assets : IDisposable
{
    sealed record Entry(SafeFileHandle Handle, long Offset, int Length);
    readonly Dictionary<uint, Entry> index = new();
    readonly List<SafeFileHandle> handles = new();
    readonly HashSet<(SafeFileHandle, long)> mounted = new();
    readonly string root;
    // Loose files are listed once; a File.Exists per lookup cost more than the archive reads themselves.
    readonly HashSet<string> loose;
    public long BytesRead { get; private set; }
    public int FileCount => index.Count;
    static readonly string[] Nested = ["local.mix", "localmd.mix", "cache.mix", "cachemd.mix", "conquer.mix", "conquermd.mix", "generic.mix", "genericmd.mix", "isogen.mix", "isogenmd.mix", "temperat.mix", "temperatmd.mix", "tem.mix", "snow.mix", "snowmd.mix", "sno.mix", "urban.mix", "urbanmd.mix", "urb.mix", "urbann.mix", "urbannmd.mix", "ubn.mix", "desert.mix", "desertmd.mix", "des.mix", "lunar.mix", "lunarmd.mix", "lun.mix", "isotemp.mix", "isotemmd.mix", "isosnow.mix", "isosnomd.mix", "isourb.mix", "isourbmd.mix", "isoubn.mix", "isoubnmd.mix", "isodes.mix", "isodesmd.mix", "isolun.mix", "isolunmd.mix"];
    public Assets(string root)
    {
        this.root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        loose = new(Directory.EnumerateFiles(root).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var names = new List<string> { "ra2.mix", "ra2md.mix" };
        names.AddRange(Nested);
        names.AddRange(Directory.EnumerateFiles(root, "ecache*.mix").Select(Path.GetFileName)!);
        names.AddRange(Directory.EnumerateFiles(root, "expand*.mix").Order(StringComparer.OrdinalIgnoreCase).Select(Path.GetFileName)!);
        names.Add("cncnet.mix");
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!loose.Contains(name)) continue;
            string path = Path.Combine(root, name);
            var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            handles.Add(h); Mount(new(h, 0, checked((int)RandomAccess.GetLength(h))), name);
        }
    }
    byte[] Read(Entry e, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > e.Length) throw new InvalidDataException("MIX entry exceeds archive bounds.");
        byte[] b = new byte[length]; int n = 0;
        while (n < length) { int got = RandomAccess.Read(e.Handle, b.AsSpan(n), e.Offset + offset + n); if (got == 0) throw new EndOfStreamException(); n += got; }
        BytesRead += length; return b;
    }
    void Mount(Entry archive, string name)
    {
        if (archive.Length < 6 || !mounted.Add((archive.Handle, archive.Offset))) return;
        byte[] first = Read(archive, 0, Math.Min(92, archive.Length));
        int start = Bin.U16(first) == 0 ? 4 : 0;
        bool encrypted = start == 4 && (Bin.U32(first) & 0x20000) != 0;
        byte[] directory; int body;
        if (encrypted)
        {
            var cipher = new MixCipher(first.AsSpan(4, 80));
            var head = first.AsSpan(84, 8).ToArray(); cipher.Decrypt(head);
            int length = (6 + Bin.U16(head) * 12 + 7) & ~7;
            directory = Read(archive, 84, length); cipher.Decrypt(directory); body = 84 + length;
        }
        else
        {
            int length = 6 + Bin.U16(first, start) * 12;
            directory = Read(archive, start, length); body = start + length;
        }
        var local = new Dictionary<uint, Entry>();
        for (int i = 0; i < Bin.U16(directory); i++)
        {
            int p = 6 + i * 12, offset = Bin.I32(directory, p + 4), size = Bin.I32(directory, p + 8);
            if (offset < 0 || size < 0 || (long)body + offset + size > archive.Length) throw new InvalidDataException($"Invalid directory in {name}.");
            local[Bin.U32(directory, p)] = new(archive.Handle, archive.Offset + body + offset, size);
        }
        foreach (var pair in local) index[pair.Key] = pair.Value;
        foreach (string nested in Nested.Concat(new[] { "conqmd.mix", "genermd.mix" })) if (local.TryGetValue(Bin.FileId(nested), out var e)) Mount(e, nested);
    }
    public byte[]? Get(string name)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("Asset names must be filenames.");
        if (loose.Contains(name)) { var b = File.ReadAllBytes(Path.Combine(root, name)); BytesRead += b.Length; return b; }
        return index.TryGetValue(Bin.FileId(name), out var e) ? Read(e, 0, e.Length) : null;
    }
    public bool Exists(string name) => index.ContainsKey(Bin.FileId(name)) || loose.Contains(name);
    public byte[] Require(string name) => Get(name) ?? throw new FileNotFoundException($"Game asset not found: {name}");
    public void Dispose() { foreach (var h in handles) h.Dispose(); }
}
