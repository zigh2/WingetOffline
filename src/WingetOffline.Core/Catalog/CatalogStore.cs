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
        using (var s = metaEntry.Open())
        using (var doc = JsonDocument.Parse(s))
            expected = doc.RootElement.GetProperty("db_sha256").GetString() ?? "";
        if (!Regex.IsMatch(expected, "^[0-9A-Fa-f]{64}$"))
            throw new InvalidDataException("Invalid db_sha256 in meta.json");

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WingetOffline", "cache", expected.ToLowerInvariant());
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
        return new CatalogStore(db);
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
