using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WingetOffline.Core.Catalog;

/// <summary>فتح الكتالوج للقراءة فقط. يقبل .wglib (يفك ويتحقق من SHA256 ويخزّن في كاش) أو catalog.db مباشرة.</summary>
public sealed class CatalogStore : IDisposable
{
    public string DbPath { get; }
    public SqliteConnection Conn { get; }

    /// <summary>اتصال SQLite واحد مشترك: كل استخدام له يجب أن يكون داخل lock(Sync).</summary>
    public object Sync { get; } = new();

    public DateTime? CreatedUtc { get; private set; }
    public int MetaPackages { get; private set; }
    public int MetaVersions { get; private set; }

    private CatalogStore(string dbPath)
    {
        DbPath = dbPath;
        Conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        Conn.Open();
    }

    public static CatalogStore Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Catalog not found", path);
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return new CatalogStore(path);

        using var zip = ZipFile.OpenRead(path);
        var metaEntry = zip.GetEntry("meta.json") ?? throw new InvalidDataException("meta.json missing in .wglib");
        string expected;
        DateTime? created = null;
        int packages = 0, versions = 0;
        using (var s = metaEntry.Open())
        using (var doc = JsonDocument.Parse(s))
        {
            var root = doc.RootElement;
            expected = root.GetProperty("db_sha256").GetString() ?? "";
            if (root.TryGetProperty("created_utc", out var c) && c.TryGetDateTime(out var dt)) created = dt;
            if (root.TryGetProperty("packages", out var p) && p.TryGetInt32(out var pn)) packages = pn;
            if (root.TryGetProperty("versions", out var v) && v.TryGetInt32(out var vn)) versions = vn;
        }
        if (!Regex.IsMatch(expected, "^[0-9A-Fa-f]{64}$"))
            throw new InvalidDataException("Invalid db_sha256 in meta.json");

        var cacheDir = AppPaths.Combine("cache", expected.ToLowerInvariant());
        var db = Path.Combine(cacheDir, "catalog.db");

        if (!File.Exists(db))
        {
            Directory.CreateDirectory(cacheDir);
            var entry = zip.GetEntry("catalog.db") ?? throw new InvalidDataException("catalog.db missing in .wglib");
            var tmp = db + ".tmp";
            entry.ExtractToFile(tmp, true);
            string actual;
            using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(SHA256.HashData(fs));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("Checksum mismatch: the .wglib file is corrupted.");
            }
            File.Move(tmp, db, true);
        }
        PruneOldCaches(expected);
        return new CatalogStore(db) { CreatedUtc = created, MetaPackages = packages, MetaVersions = versions };
    }

    /// <summary>يحذف الكتالوجات المفكوكة القديمة (165 MB لكل منها) بعد تحميل كتالوج أحدث.</summary>
    private static void PruneOldCaches(string currentSha)
    {
        try
        {
            var root = AppPaths.Combine("cache");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(dir), currentSha, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); } catch { /* مفتوح من نسخة أخرى: نتركه */ }
            }
        }
        catch { /* التنظيف اختياري */ }
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
