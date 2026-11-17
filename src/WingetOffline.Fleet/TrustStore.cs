using System.Text.Json;

namespace WingetOffline.Fleet;

/// <summary>قائمة البصمات الموثوقة (SHA-256 كاملة، 64 خانة hex). تُدخل يدوياً بعد مقارنة البصمة من الطرف الآخر؛ لا TOFU صامت.</summary>
public sealed class TrustStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase); // fingerprint -> label

    public TrustStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (d != null) foreach (var kv in d) _items[Normalize(kv.Key)] = kv.Value;
        }
    }

    public static string Normalize(string fp)
    {
        var s = new string(fp.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (s.Length != 64) throw new FormatException("A SHA-256 fingerprint has 64 hex digits.");
        return s;
    }

    public bool IsTrusted(string fingerprint) { lock (_lock) return _items.ContainsKey(fingerprint); }
    public string? LabelOf(string fingerprint) { lock (_lock) return _items.GetValueOrDefault(fingerprint); }
    public IReadOnlyDictionary<string, string> All() { lock (_lock) return new Dictionary<string, string>(_items); }

    public void Add(string fingerprint, string label) { lock (_lock) { _items[Normalize(fingerprint)] = label; Save(); } }
    public bool Remove(string fingerprint) { lock (_lock) { var r = _items.Remove(Normalize(fingerprint)); if (r) Save(); return r; } }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
    }
}
