using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WingetOffline.Fleet;

/// <summary>سجل تدقيق JSON-lines مترابط بالتجزئة: كل سطر يحمل SHA-256 للسطر السابق، فأي تعديل أو حذف في الوسط يُكتشف.</summary>
public sealed class AuditLog
{
    private readonly string _path;
    private readonly object _lock = new();
    private string _last;

    private sealed class Entry
    {
        public DateTime Utc { get; set; }
        public string Actor { get; set; } = "";
        public string Action { get; set; } = "";
        public string Result { get; set; } = "";
        public string Prev { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    public AuditLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _last = new string('0', 64);
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
            if (lines.Length > 0) _last = JsonSerializer.Deserialize<Entry>(lines[^1])!.Hash;
        }
    }

    public void Append(string actor, string action, string result)
    {
        lock (_lock)
        {
            var e = new Entry { Utc = DateTime.UtcNow, Actor = actor, Action = action, Result = result, Prev = _last };
            e.Hash = Compute(e);
            File.AppendAllText(_path, JsonSerializer.Serialize(e) + "\n", new UTF8Encoding(false));
            _last = e.Hash;
        }
    }

    /// <summary>true إن كانت السلسلة سليمة. يعيد رقم أول سطر مشبوه (1-based) في badLine.</summary>
    public static bool Verify(string path, out int badLine)
    {
        badLine = 0;
        if (!File.Exists(path)) return true;
        var prev = new string('0', 64);
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            n++;
            Entry? e;
            try { e = JsonSerializer.Deserialize<Entry>(line); } catch { e = null; }
            if (e == null || e.Prev != prev || e.Hash != Compute(e)) { badLine = n; return false; }
            prev = e.Hash;
        }
        return true;
    }

    private static string Compute(Entry e)
    {
        var s = $"{e.Utc:O}|{e.Actor}|{e.Action}|{e.Result}|{e.Prev}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }
}
