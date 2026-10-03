using System.Globalization;
using System.Text;
namespace LightningMap;

sealed class Ini
{
    readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Sections => sections.Keys;
    public Ini() { }
    public Ini(byte[] bytes) : this(Encoding.Latin1.GetString(bytes)) { }
    [MethodImpl(Bin.Hot)]
    public Ini(string text)
    {
        Dictionary<string, string>? section = null;
        // The line breaks recognised by EnumerateLines, found in one pass: System.Memory's IndexOfAny on
        // .NET Framework searches for each separator in turn, which is quadratic on LF-only files.
        for (int from = 0, to = 0; from <= text.Length; from = to + 1)
        {
            for (to = from; to < text.Length && text[to] is not ('\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029'); to++) { }
            var s = text.AsSpan(from, to - from).Trim();
            if (to + 1 < text.Length && text[to] == '\r' && text[to + 1] == '\n') to++;
            if (s.Length == 0 || s[0] == ';') continue;
            if (s[0] == '[') { int end = s.IndexOf(']'); if (end > 0) section = Section(s[1..end].ToString()); continue; }
            int eq = s.IndexOf('='); if (section == null || eq < 0) continue;
            var value = s[(eq + 1)..]; int comment = value.IndexOf(';'); if (comment >= 0) value = value[..comment];
            section[s[..eq].Trim().ToString()] = value.Trim().ToString();
        }
    }
    public Dictionary<string, string> Section(string name)
    {
        if (!sections.TryGetValue(name, out var s)) sections[name] = s = new(StringComparer.OrdinalIgnoreCase);
        return s;
    }
    public string Get(string s, string k, string fallback = "") => sections.TryGetValue(s, out var v) && v.TryGetValue(k, out var r) ? r : fallback;
    public int Int(string s, string k, int fallback = 0) => int.TryParse(Get(s, k), out int n) ? n : fallback;
    // The game reads numbers with atof: the longest numeric prefix counts and the rest is ignored, so
    // "0,01" (a typo in the stock INBLULMP) is 0, not a parse failure. An absent or empty key keeps the fallback.
    public double Number(string s, string k, double fallback = 0)
    {
        string v = Get(s, k).Trim();
        if (v.Length == 0) return fallback;
        int end = 0;
        if (end < v.Length && v[end] is '+' or '-') end++;
        while (end < v.Length && IsDigit(v[end])) end++;
        if (end < v.Length && v[end] == '.') { end++; while (end < v.Length && IsDigit(v[end])) end++; }
        if (end < v.Length && v[end] is 'e' or 'E')
        {
            int e = end + 1;
            if (e < v.Length && v[e] is '+' or '-') e++;
            if (e < v.Length && IsDigit(v[e])) { while (e < v.Length && IsDigit(v[e])) e++; end = e; }
        }
        return double.TryParse(v.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
    static bool IsDigit(char c) => c is >= '0' and <= '9';
    public bool Has(string s, string k) => sections.TryGetValue(s, out var v) && v.ContainsKey(k);
    public bool Yes(string s, string k, bool fallback = false) => Get(s, k, fallback ? "yes" : "no").ToLowerInvariant() is "yes" or "true" or "1";
    public void Merge(Ini other) { foreach (var s in other.sections) foreach (var kv in s.Value) Section(s.Key)[kv.Key] = kv.Value; }
    // The same as merging lower and then upper into a new Ini, but sections only lower has are shared rather
    // than copied, so neither input may be modified afterwards.
    public static Ini Layered(Ini lower, Ini upper)
    {
        var result = new Ini();
        foreach (var s in lower.sections) result.sections[s.Key] = s.Value;
        foreach (var s in upper.sections)
        {
            var merged = lower.sections.TryGetValue(s.Key, out var below) ? new Dictionary<string, string>(below, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.Value) merged[kv.Key] = kv.Value;
            result.sections[s.Key] = merged;
        }
        return result;
    }
    public byte[] Pack(string name, bool lzo, int limit) => Packs.Decode(Convert.FromBase64String(string.Concat(Section(name).OrderBy(p => int.Parse(p.Key, CultureInfo.InvariantCulture)).Select(p => p.Value))), lzo, limit);
}
