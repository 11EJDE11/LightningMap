namespace LightningMap;

static class Packs
{
    public static byte[] Decode(byte[] input, bool lzo, int limit)
    {
        if (input.Length == 0) return [];
        int total = 0;
        for (int p = 0; p < input.Length;)
        {
            if (input.Length - p < 4) throw new InvalidDataException("Truncated pack header.");
            int size = Bin.U16(input, p), output = Bin.U16(input, p + 2); p += 4;
            if (size == 0 || output == 0 || output > 8192 || size > input.Length - p || total > limit - output) throw new InvalidDataException("Invalid map pack block.");
            total += output; p += size;
        }
        byte[] result = new byte[total]; int dest = 0;
        for (int p = 0; p < input.Length;)
        {
            int size = Bin.U16(input, p), output = Bin.U16(input, p + 2); p += 4;
            try { if (lzo) Lzo(input.AsSpan(p, size), result.AsSpan(dest, output)); else Lcw(input.AsSpan(p, size), result.AsSpan(dest, output)); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { throw new InvalidDataException("Malformed compressed map block.", ex); }
            p += size; dest += output;
        }
        return result;
    }
    static int Length(ReadOnlySpan<byte> src, ref int p, int n, int mask)
    { if (n != 0) return n; n = mask; while (src[p] == 0) { n += 255; p++; } return n + src[p++]; }
    static void Copy(Span<byte> output, ref int dest, int from, int length)
    {
        if (from < 0 || from >= dest || length > output.Length - dest) throw new InvalidDataException("Invalid compression back-reference.");
        for (int i = 0; i < length; i++) output[dest++] = output[from++];
    }
    [MethodImpl(Bin.Hot)]
    public static void Lzo(ReadOnlySpan<byte> src, Span<byte> output)
    {
        int p = 0, d = 0, state = 0;
        if (src[0] > 17) { int n = src[p++] - 17; src.Slice(p, n).CopyTo(output); p += n; d += n; state = Math.Min(n, 4); }
        while (p < src.Length)
        {
            int op = src[p++], length, distance, tail;
            if (op < 16 && state == 0)
            {
                length = Length(src, ref p, op, 15) + 3;
                src.Slice(p, length).CopyTo(output[d..]); p += length; d += length; state = 4; continue;
            }
            if (op < 16) { length = state == 4 ? 3 : 2; distance = (src[p++] << 2) + (op >> 2) + (state == 4 ? 2049 : 1); tail = op & 3; }
            else if (op < 32)
            {
                length = Length(src, ref p, op & 7, 7) + 2; int operand = Bin.U16(src, p); p += 2;
                distance = 16384 + ((op & 8) << 11) + (operand >> 2); tail = operand & 3;
                if (distance == 16384) { if (d != output.Length || p != src.Length) throw new InvalidDataException("LZO length mismatch."); return; }
            }
            else if (op < 64) { length = Length(src, ref p, op & 31, 31) + 2; int operand = Bin.U16(src, p); p += 2; distance = (operand >> 2) + 1; tail = operand & 3; }
            else { length = (op >> 5) + 1; distance = (src[p++] << 3) + ((op >> 2) & 7) + 1; tail = op & 3; }
            Copy(output, ref d, d - distance, length);
            src.Slice(p, tail).CopyTo(output[d..]); p += tail; d += tail; state = tail;
        }
        throw new InvalidDataException("Missing LZO end marker.");
    }
    [MethodImpl(Bin.Hot)]
    public static void Lcw(ReadOnlySpan<byte> src, Span<byte> output)
    {
        int p = 0, d = 0; bool relative = src[0] == 0; if (relative) p++;
        while (p < src.Length)
        {
            int op = src[p++];
            if (op == 128) { if (d != output.Length) throw new InvalidDataException("LCW length mismatch."); return; }
            if (op < 128) { int n = (op >> 4) + 3, distance = ((op & 15) << 8) | src[p++]; Copy(output, ref d, d - distance, n); }
            else if (op < 192) { int n = op & 63; src.Slice(p, n).CopyTo(output[d..]); p += n; d += n; }
            else if (op == 254) { int n = Bin.U16(src, p); p += 2; output.Slice(d, n).Fill(src[p++]); d += n; }
            else
            {
                int n = (op & 63) + 3; if (op == 255) { n = Bin.U16(src, p); p += 2; }
                int from = Bin.U16(src, p); p += 2; Copy(output, ref d, relative ? d - from : from, n);
            }
        }
        if (d != output.Length) throw new InvalidDataException("Truncated LCW stream.");
    }
}
