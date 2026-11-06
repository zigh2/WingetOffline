using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Core.Catalog;

public enum PackageSort { Relevance, Name, Publisher, Versions }

/// <summary>فلاتر استعراض الكتالوج. الفلاتر المتعلقة بالمثبّتات تُطبَّق على أحدث إصدار من كل حزمة.</summary>
public sealed class PackageFilter
{
    public string? Text { get; set; }
    public string? Publisher { get; set; }
    public string? License { get; set; }
    public string? Tag { get; set; }
    public string? InstallerType { get; set; }
    public string? Architecture { get; set; }
    public string? Scope { get; set; }
    public bool SilentOnly { get; set; }
    public bool PortableOnly { get; set; }
    public bool HasProductCode { get; set; }
    public PackageSort Sort { get; set; } = PackageSort.Relevance;
    public int Limit { get; set; } = 1000;
}

public sealed record PackageRow(
    string Id, string Name, string Publisher, string? Moniker, string? Description,
    string? License, string? Homepage, string Tags, string? Latest, int VersionCount);

public sealed record InstallerRow(
    string Version, string Arch, string Type, string Scope, string Url, string Sha256,
    string? Silent, string? ProductCode);

public sealed record PackageDetails(PackageRow Package, List<string> Versions, List<InstallerRow> LatestInstallers, List<string> Dependencies);

public sealed class CatalogQueries
{
    public static readonly string[] InstallerTypes =
        { "appx", "burn", "exe", "inno", "msi", "msix", "nullsoft", "portable", "pwa", "wix", "zip" };
    public static readonly string[] Architectures = { "x64", "x86", "arm64", "arm", "neutral" };
    public static readonly string[] Scopes = { "user", "machine" };

    private const string Cols =
        "p.id, p.name, p.publisher, p.moniker, p.description, p.license, p.homepage, p.tags, p.latest_version, p.version_count";

    private readonly CatalogStore _db;
    public CatalogQueries(CatalogStore db) => _db = db;

    /// <summary>"vlc media" ← "\"vlc\"* \"media\"*" (بحث بادئات آمن لـ FTS5).</summary>
    public static string? BuildFts(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = Regex.Matches(text, @"[\p{L}\p{Nd}]+").Select(m => "\"" + m.Value + "\"*").ToList();
        return tokens.Count == 0 ? null : string.Join(" ", tokens);
    }

    public (List<PackageRow> Rows, int Total) Search(PackageFilter f)
    {
        var where = new List<string>();
        var ps = new List<(string, object)>();
        var from = "packages p";

        var fts = BuildFts(f.Text);
        if (fts != null)
        {
            from = "packages_fts JOIN packages p ON p.id = packages_fts.id";
            where.Add("packages_fts MATCH $fts");
            ps.Add(("$fts", fts));
        }
        if (!string.IsNullOrWhiteSpace(f.Publisher))
        {
            where.Add("p.publisher LIKE $pub ESCAPE '\\'");
            ps.Add(("$pub", "%" + Esc(f.Publisher.Trim()) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(f.License))
        {
            where.Add("p.license LIKE $lic ESCAPE '\\'");
            ps.Add(("$lic", "%" + Esc(f.License.Trim()) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(f.Tag))
        {
            where.Add("(',' || lower(p.tags) || ',') LIKE $tag ESCAPE '\\'");
            ps.Add(("$tag", "%," + Esc(f.Tag.Trim().ToLowerInvariant()) + ",%"));
        }

        var ic = new List<string>();
        if (!string.IsNullOrEmpty(f.InstallerType)) { ic.Add("i.type = $itype"); ps.Add(("$itype", f.InstallerType)); }
        if (!string.IsNullOrEmpty(f.Architecture)) { ic.Add("i.arch = $iarch"); ps.Add(("$iarch", f.Architecture)); }
        if (!string.IsNullOrEmpty(f.Scope)) { ic.Add("i.scope = $iscope"); ps.Add(("$iscope", f.Scope)); }
        if (f.SilentOnly) ic.Add("(i.silent IS NOT NULL AND i.silent <> '')");
        if (f.PortableOnly) ic.Add("(i.type = 'portable' OR i.nested_type = 'portable')");
        if (f.HasProductCode) ic.Add("(i.product_code IS NOT NULL AND i.product_code <> '')");
        if (ic.Count > 0)
            where.Add("EXISTS (SELECT 1 FROM installers i WHERE i.pkg_id = p.id AND i.version = p.latest_version AND " +
                      string.Join(" AND ", ic) + ")");

        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var order = f.Sort switch
        {
            PackageSort.Name => "p.name COLLATE NOCASE",
            PackageSort.Publisher => "p.publisher COLLATE NOCASE, p.name COLLATE NOCASE",
            PackageSort.Versions => "p.version_count DESC, p.name COLLATE NOCASE",
            _ => fts != null ? "rank" : "p.name COLLATE NOCASE",
        };

        lock (_db.Sync)
        {
            int total;
            using (var cmd = _db.Conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {from}{whereSql}";
                foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
                total = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }

            var rows = new List<PackageRow>();
            using (var cmd = _db.Conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {Cols} FROM {from}{whereSql} ORDER BY {order} LIMIT $limit";
                foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
                cmd.Parameters.AddWithValue("$limit", Math.Max(1, f.Limit));
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(ReadRow(r));
            }
            return (rows, total);
        }
    }

    public PackageDetails? GetDetails(string id)
    {
        lock (_db.Sync)
        {
            PackageRow? pkg = null;
            using (var cmd = _db.Conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {Cols} FROM packages p WHERE p.id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (r.Read()) pkg = ReadRow(r);
            }
            if (pkg == null) return null;

            var versions = new List<string>();
            using (var cmd = _db.Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version FROM versions WHERE pkg_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read()) versions.Add(r.GetString(0));
            }
            versions.Sort(WingetVersionComparer.Instance);
            versions.Reverse();

            var installers = new List<InstallerRow>();
            if (pkg.Latest != null)
            {
                using var cmd = _db.Conn.CreateCommand();
                cmd.CommandText = "SELECT version, arch, type, scope, url, sha256, silent, product_code " +
                                  "FROM installers WHERE pkg_id = $id AND version = $v";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$v", pkg.Latest);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    installers.Add(new InstallerRow(r.GetString(0), S(r, 1) ?? "", S(r, 2) ?? "", S(r, 3) ?? "",
                        S(r, 4) ?? "", S(r, 5) ?? "", S(r, 6), S(r, 7)));
            }
            var deps = pkg.Latest != null ? GetDependencies(pkg.Id, pkg.Latest) : new List<string>();
            return new PackageDetails(pkg, versions, installers, deps);
        }
    }

    /// <summary>التبعيات المسجّلة لإصدار حزمة (فارغة في الكتالوجات المبنية قبل 0.0.9).</summary>
    public List<string> GetDependencies(string packageId, string version)
    {
        var deps = new List<string>();
        lock (_db.Sync)
        {
            try
            {
                using var cmd = _db.Conn.CreateCommand();
                cmd.CommandText = "SELECT dep_id FROM deps WHERE pkg_id = $id AND version = $v ORDER BY dep_id";
                cmd.Parameters.AddWithValue("$id", packageId);
                cmd.Parameters.AddWithValue("$v", version);
                using var r = cmd.ExecuteReader();
                while (r.Read()) deps.Add(r.GetString(0));
            }
            catch (SqliteException) { /* كتالوج قديم بلا جدول deps */ }
        }
        return deps;
    }

    private static PackageRow ReadRow(SqliteDataReader r) =>
        new(r.GetString(0), S(r, 1) ?? "", S(r, 2) ?? "", S(r, 3), S(r, 4), S(r, 5), S(r, 6),
            S(r, 7) ?? "", S(r, 8), r.IsDBNull(9) ? 0 : r.GetInt32(9));

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
