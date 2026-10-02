using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LightningMap;

static class Bin
{
    // Hot loops start fully optimised instead of in unoptimised tier-0 code, which can be several times slower
    // for the first renders. AggressiveOptimization exists only on .NET Core; 0 means no flags.
#if NET
    public const MethodImplOptions Hot = MethodImplOptions.AggressiveOptimization;
#else
    public const MethodImplOptions Hot = 0;
#endif
    public static ushort U16(ReadOnlySpan<byte> b, int p = 0) => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    public static int I32(ReadOnlySpan<byte> b, int p = 0) => BinaryPrimitives.ReadInt32LittleEndian(b[p..]);
    public static uint U32(ReadOnlySpan<byte> b, int p = 0) => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i => {
        uint v = (uint)i; for (int j = 0; j < 8; j++) v = (v >> 1) ^ ((v & 1) != 0 ? 0xedb88320u : 0); return v;
    }).ToArray();
    [MethodImpl(Bin.Hot)]
    public static uint Crc(ReadOnlySpan<byte> bytes, uint crc = 0)
    { crc = ~crc; foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8); return ~crc; }
    public static uint FileId(string name)
    {
        var b = Encoding.ASCII.GetBytes(name.ToUpperInvariant());
        int r = b.Length & 3;
        if (r == 0) return Crc(b);
        int n = b.Length; Array.Resize(ref b, (n + 3) & ~3); b[n] = (byte)r;
        for (int i = n + 1; i < b.Length; i++) b[i] = b[n & ~3];
        return Crc(b);
    }
}

// Standard Blowfish, initialized from hexadecimal digits of pi. Used only for MIX directories.
sealed class MixCipher
{
    readonly uint[] s = (uint[])BlowfishConstants.Words.Clone();
    uint F(uint x) => ((s[18 + (x >> 24)] + s[274 + ((x >> 16) & 255)]) ^ s[530 + ((x >> 8) & 255)]) + s[786 + (x & 255)];
    void Encrypt(ref uint a, ref uint b)
    {
        for (int i = 0; i < 16; i++) { a ^= s[i]; b ^= F(a); (a, b) = (b, a); }
        (a, b) = (b, a); b ^= s[16]; a ^= s[17];
    }
    public MixCipher(ReadOnlySpan<byte> source)
    {
        byte[] der = Convert.FromBase64String("AihRvNoIbTn85FZRYNZRcT+i6KpU+maCsEqr3Q5q+LDB5tH7Tz2qQ38V");
        byte[] bigEndian = der.AsSpan(2).ToArray(); Array.Reverse(bigEndian);
        var modulus = Unsigned(bigEndian);
        Span<byte> key = stackalloc byte[78]; key.Clear();
        for (int i = 0; i < 2; i++)
        {
            var n = Unsigned(source.Slice(i * 40, 40).ToArray());
            var decoded = BigInteger.ModPow(n, 65537, modulus).ToByteArray();
            decoded.AsSpan(0, Math.Min(decoded.Length, 39)).CopyTo(key[(i * 39)..]);
        }
        int k = 0;
        for (int i = 0; i < 18; i++) { uint v = 0; for (int j = 0; j < 4; j++) { v = (v << 8) | key[k]; k = (k + 1) % 56; } s[i] ^= v; }
        uint a = 0, b = 0;
        for (int i = 0; i < s.Length; i += 2) { Encrypt(ref a, ref b); s[i] = a; s[i + 1] = b; }
    }
    // A little-endian unsigned integer; the extra zero byte keeps BigInteger from reading it as negative.
    static BigInteger Unsigned(byte[] littleEndian) => new([.. littleEndian, 0]);
    public void Decrypt(Span<byte> data)
    {
        for (int p = 0; p < data.Length; p += 8)
        {
            uint a = BinaryPrimitives.ReadUInt32BigEndian(data[p..]), b = BinaryPrimitives.ReadUInt32BigEndian(data[(p + 4)..]);
            for (int i = 17; i > 1; i--) { a ^= s[i]; b ^= F(a); (a, b) = (b, a); }
            (a, b) = (b, a); b ^= s[1]; a ^= s[0];
            BinaryPrimitives.WriteUInt32BigEndian(data[p..], a); BinaryPrimitives.WriteUInt32BigEndian(data[(p + 4)..], b);
        }
    }
}
