using System.Globalization;
using System.Text;
namespace LightningMap;

sealed class Ini
{
    readonly Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Sections => sections.Keys;
    public Ini() { }
    public Ini(byte[] bytes) : this(Encoding.Latin1.GetString(bytes)) { }
    public Ini(string text)
    {
        Dictionary<string, string>? section = null;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var s = line.Trim(); if (s.Length == 0 || s[0] == ';') continue;
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
    public double Number(string s, string k, double fallback = 0) => double.TryParse(Get(s, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public bool Yes(string s, string k, bool fallback = false) => Get(s, k, fallback ? "yes" : "no").ToLowerInvariant() is "yes" or "true" or "1";
    public void Merge(Ini other) { foreach (var s in other.sections) foreach (var kv in s.Value) Section(s.Key)[kv.Key] = kv.Value; }
    public byte[] Pack(string name, bool lzo, int limit) => Packs.Decode(Convert.FromBase64String(string.Concat(Section(name).OrderBy(p => int.Parse(p.Key, CultureInfo.InvariantCulture)).Select(p => p.Value))), lzo, limit);
}
