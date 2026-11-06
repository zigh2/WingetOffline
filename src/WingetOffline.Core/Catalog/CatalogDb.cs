using Microsoft.Data.Sqlite;
using WingetOffline.Core.Models;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Core.Catalog;

/// <summary>قاعدة الكتالوج (SQLite + FTS5): إنشاء المخطط والكتابة.</summary>
public sealed class CatalogDb : IDisposable
{
    private readonly SqliteConnection _c;
    public CatalogDb(string path)
    {
        _c = new SqliteConnection($"Data Source={path};Pooling=False");
        _c.Open();
        Exec("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(Schema);
    }

    private const string Schema = @"
CREATE TABLE IF NOT EXISTS packages(
  id TEXT PRIMARY KEY, name TEXT, publisher TEXT, moniker TEXT, description TEXT,
  license TEXT, homepage TEXT, tags TEXT, latest_version TEXT, version_count INTEGER);
CREATE TABLE IF NOT EXISTS versions(
  pkg_id TEXT, version TEXT, release_date TEXT, min_os TEXT, locale TEXT,
  PRIMARY KEY(pkg_id, version));
CREATE TABLE IF NOT EXISTS installers(
  pkg_id TEXT, version TEXT, arch TEXT, type TEXT, nested_type TEXT, scope TEXT,
  url TEXT, sha256 TEXT, silent TEXT, silent_progress TEXT, custom TEXT,
  product_code TEXT, upgrade_code TEXT, portable_alias TEXT, commands TEXT, nested_files TEXT);
CREATE TABLE IF NOT EXISTS arp(
  pkg_id TEXT, version TEXT, display_name TEXT, publisher TEXT, display_version TEXT,
  product_code TEXT, upgrade_code TEXT, installer_type TEXT);
CREATE TABLE IF NOT EXISTS deps(pkg_id TEXT, version TEXT, dep_id TEXT);
CREATE INDEX IF NOT EXISTS ix_deps_pkg ON deps(pkg_id, version);
CREATE INDEX IF NOT EXISTS ix_inst_pkg ON installers(pkg_id, version);
CREATE INDEX IF NOT EXISTS ix_inst_pc  ON installers(product_code);
CREATE INDEX IF NOT EXISTS ix_inst_uc  ON installers(upgrade_code);
CREATE INDEX IF NOT EXISTS ix_inst_sha ON installers(sha256);
CREATE INDEX IF NOT EXISTS ix_arp_pc   ON arp(product_code);
CREATE INDEX IF NOT EXISTS ix_arp_name ON arp(display_name);
CREATE VIRTUAL TABLE IF NOT EXISTS packages_fts USING fts5(
  id, name, publisher, moniker, description, tags, tokenize='unicode61');
";

    private void Exec(string sql) { using var cmd = _c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    public void BeginBatch() => Exec("BEGIN");
    public void Commit() => Exec("COMMIT");

    public void AddVersion(PackageVersion p)
    {
        Run(@"INSERT OR REPLACE INTO versions VALUES($i,$v,$d,$m,$l)",
            ("$i", p.Id), ("$v", p.Version), ("$d", p.ReleaseDate), ("$m", p.MinOsVersion), ("$l", p.Locale));

        foreach (var i in p.Installers)
        {
            Run(@"INSERT INTO installers VALUES($p,$v,$a,$t,$nt,$s,$u,$h,$si,$sp,$c,$pc,$uc,$pa,$cm,$nf)",
                ("$p", p.Id), ("$v", p.Version), ("$a", i.Architecture), ("$t", i.InstallerType),
                ("$nt", i.NestedInstallerType), ("$s", i.Scope), ("$u", i.Url), ("$h", i.Sha256),
                ("$si", i.Silent), ("$sp", i.SilentWithProgress), ("$c", i.Custom),
                ("$pc", i.ProductCode), ("$uc", i.UpgradeCode), ("$pa", i.PortableAlias),
                ("$cm", string.Join(";", i.Commands)), ("$nf", string.Join(";", i.NestedFiles)));

            foreach (var a in i.AppsAndFeatures)
                Run(@"INSERT INTO arp VALUES($p,$v,$n,$pub,$dv,$pc,$uc,$t)",
                    ("$p", p.Id), ("$v", p.Version), ("$n", a.DisplayName), ("$pub", a.Publisher),
                    ("$dv", a.DisplayVersion), ("$pc", a.ProductCode), ("$uc", a.UpgradeCode), ("$t", a.InstallerType));
        }

        foreach (var dep in p.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase))
            Run("INSERT INTO deps VALUES($p,$v,$d)", ("$p", p.Id), ("$v", p.Version), ("$d", dep));
    }

    /// <summary>يُستدعى مرة واحدة لكل حزمة بعد جمع كل إصداراتها (المانيفست الأحدث يمنح الوصف).</summary>
    public void UpsertPackage(PackageVersion latestMeta, IEnumerable<string> allVersions)
    {
        var versions = allVersions.ToList();
        versions.Sort(WingetVersionComparer.Instance);
        var latest = versions[^1];
        var tags = string.Join(",", latestMeta.Tags);

        Run(@"INSERT OR REPLACE INTO packages VALUES($i,$n,$p,$m,$d,$l,$h,$t,$lv,$vc)",
            ("$i", latestMeta.Id), ("$n", latestMeta.Name), ("$p", latestMeta.Publisher),
            ("$m", latestMeta.Moniker), ("$d", latestMeta.Description), ("$l", latestMeta.License),
            ("$h", latestMeta.Homepage), ("$t", tags), ("$lv", latest), ("$vc", versions.Count));
        Run(@"INSERT INTO packages_fts VALUES($i,$n,$p,$m,$d,$t)",
            ("$i", latestMeta.Id), ("$n", latestMeta.Name), ("$p", latestMeta.Publisher),
            ("$m", latestMeta.Moniker), ("$d", latestMeta.Description), ("$t", tags));
    }

    private void Run(string sql, params (string, object?)[] ps)
    {
        using var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, (object?)v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Optimize() { Exec("INSERT INTO packages_fts(packages_fts) VALUES('optimize'); VACUUM;"); }
    public void Dispose()
    {
        _c.Dispose();
        SqliteConnection.ClearAllPools(); // يحرّر ملف catalog.db ليُضغط بعد الإغلاق
    }
}
