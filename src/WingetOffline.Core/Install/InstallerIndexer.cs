using System.Security.Cryptography;
using System.Text.Json;
using WingetOffline.Core.Catalog;

namespace WingetOffline.Core.Install;

/// <summary>صف مثبّت من الكتالوج (المانيفست) تطابقت بصمته SHA256 مع ملف على القرص.</summary>
public sealed record CatalogInstaller(
    string PackageId, string Version, string Arch, string Type, string? NestedType, string Scope,
    string Url, string Sha256, string? Silent, string? SilentProgress, string? Custom,
    string? ProductCode, string? PortableAlias, string? NestedFiles);

/// <summary>ملف مثبّت موجود في مجلد المثبّتات مع نتيجة مطابقته للكتالوج.</summary>
public sealed class IndexedInstaller
{
    public string FilePath { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public List<CatalogInstaller> Matches { get; } = new();

    public bool IsMatched => Matches.Count > 0;

    /// <summary>أفضل تطابق: نفضّل x64 ثم neutral ثم x86.</summary>
    public CatalogInstaller? Best => Matches.OrderBy(m => ArchRank(m.Arch)).FirstOrDefault();

    private static int ArchRank(string arch) => arch.ToLowerInvariant() switch
    {
        "x64" => 0, "neutral" => 1, "x86" => 2, "arm64" => 3, _ => 4
    };
}

/// <summary>
/// يتعرّف على ملفات المثبّتات بحساب SHA256 ومطابقته مع الكتالوج (لا يحتاج ملف بيانات إضافياً).
/// </summary>
public static class InstallerIndexer
{
    public static readonly string[] Extensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip" };

    public static List<IndexedInstaller> IndexFolder(CatalogStore db, string folder, IProgress<string>? progress = null,
                                                     CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(folder, "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        var cache = HashCache.Load();
        var items = new List<IndexedInstaller>();
        int i = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(f);
            progress?.Report($"Hashing {++i}/{files.Count}: {info.Name}");
            items.Add(new IndexedInstaller { FilePath = f, Size = info.Length, Sha256 = cache.GetOrCompute(info) });
        }
        cache.Save();

        progress?.Report("Matching against the catalog...");
        Match(db, items);
        return items;
    }

    public static IndexedInstaller IndexFile(CatalogStore db, string file)
    {
        var info = new FileInfo(file);
        var cache = HashCache.Load();
        var item = new IndexedInstaller { FilePath = info.FullName, Size = info.Length, Sha256 = cache.GetOrCompute(info) };
        cache.Save();
        Match(db, new List<IndexedInstaller> { item });
        return item;
    }

    private static void Match(CatalogStore db, List<IndexedInstaller> items)
    {
        var byHash = items.GroupBy(x => x.Sha256.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        var hashes = byHash.Keys.ToList();

        lock (db.Sync)
        {
            for (int offset = 0; offset < hashes.Count; offset += 300)
            {
                var batch = hashes.Skip(offset).Take(300).ToList();
                // بعض المانيفستات تكتب البصمة بأحرف صغيرة: نبحث بالصيغتين
                var variants = batch.Concat(batch.Select(h => h.ToLowerInvariant())).ToList();
                var ph = string.Join(",", variants.Select((_, i) => "$h" + i));

                using var cmd = db.Conn.CreateCommand();
                cmd.CommandText =
                    "SELECT pkg_id, version, arch, type, nested_type, scope, url, sha256, silent, silent_progress, " +
                    "custom, product_code, portable_alias, nested_files FROM installers WHERE sha256 IN (" + ph + ")";
                for (int i = 0; i < variants.Count; i++) cmd.Parameters.AddWithValue("$h" + i, variants[i]);

                var seen = new HashSet<string>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var ci = new CatalogInstaller(
                        r.GetString(0), r.GetString(1), S(r, 2) ?? "", S(r, 3) ?? "", S(r, 4), S(r, 5) ?? "",
                        S(r, 6) ?? "", r.GetString(7), S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), S(r, 13));
                    if (!seen.Add($"{ci.PackageId}|{ci.Version}|{ci.Arch}|{ci.Type}|{ci.Scope}|{ci.Url}")) continue;
                    if (byHash.TryGetValue(ci.Sha256.ToUpperInvariant(), out var list))
                        foreach (var item in list) item.Matches.Add(ci);
                }
            }
        }
    }

    private static string? S(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

/// <summary>كاش لبصمات الملفات (المسار+الحجم+تاريخ التعديل) لتفادي إعادة حساب SHA256 لملفات كبيرة.</summary>
internal sealed class HashCache
{
    private readonly Dictionary<string, string> _map;
    private bool _dirty;

    private HashCache(Dictionary<string, string> map) => _map = map;

    private static string FilePath => AppPaths.Combine("hashcache.json");

    public static HashCache Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new HashCache(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch { /* كاش تالف: نبدأ من جديد */ }
        return new HashCache(new Dictionary<string, string>());
    }

    public string GetOrCompute(FileInfo f)
    {
        var key = $"{f.FullName}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";
        if (_map.TryGetValue(key, out var h)) return h;
        using var fs = f.OpenRead();
        h = Convert.ToHexString(SHA256.HashData(fs));
        _map[key] = h;
        _dirty = true;
        return h;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map));
        }
        catch { /* فشل الحفظ غير حرج */ }
    }
}
