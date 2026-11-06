using Microsoft.Data.Sqlite;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;

namespace WingetOffline.Core.Packing;

/// <summary>طلب تضمين حزمة في حزمة المثبّتات (الإصدار اختياري؛ الافتراضي الأحدث في الكتالوج).</summary>
public sealed record PackRequest(string PackageId, string? Version = null);

/// <summary>مثبّت واحد مختار للتنزيل، مع سبب إدراجه ("requested" أو "dependency of X").</summary>
public sealed record PackItem(string PackageId, string Version, string Reason, CatalogInstaller Installer)
{
    public bool IsDependency => Reason != "requested";
}

public sealed class PackOptions
{
    /// <summary>ترتيب تفضيل المعماريات.</summary>
    public string[] Architectures { get; set; } = { "x64", "neutral", "x86" };
    /// <summary>"machine" أو "user" لتفضيل نطاق؛ null بلا تفضيل صريح.</summary>
    public string? Scope { get; set; }
    public bool IncludeDependencies { get; set; } = true;
    public int MaxDependencyDepth { get; set; } = 3;
}

public sealed record PackPlan(List<PackItem> Items, List<string> Warnings);

/// <summary>
/// يحوّل قائمة حزم مطلوبة إلى قائمة مثبّتات محدّدة (مثبّت واحد لكل حزمة) مع تبعياتها.
/// منطق بحت على الكتالوج المحلي: لا يتصل بأي شبكة (التنزيل في أداة Builder فقط).
/// </summary>
public static class PackPlanner
{
    public static PackPlan Plan(CatalogStore db, IEnumerable<PackRequest> requests, PackOptions? options = null)
    {
        var opt = options ?? new PackOptions();
        var queries = new CatalogQueries(db);
        var items = new List<PackItem>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var queue = new Queue<(string Id, string? Version, int Depth, string Reason)>();
        foreach (var r in requests) queue.Enqueue((r.PackageId.Trim(), r.Version, 0, "requested"));

        while (queue.Count > 0)
        {
            var (id, wanted, depth, reason) = queue.Dequeue();
            if (id.Length == 0 || !seen.Add(id)) continue;

            var resolved = ResolvePackage(db, id);
            if (resolved == null) { warnings.Add($"{id}: not in the catalog."); continue; }
            var (pkgId, latest) = resolved.Value;

            var version = wanted ?? latest;
            if (version != null && wanted != null && !VersionExists(db, pkgId, wanted))
            {
                warnings.Add($"{pkgId}: version {wanted} is not in the catalog; using the latest ({latest}).");
                version = latest;
            }
            if (version == null) { warnings.Add($"{pkgId}: the catalog has no version for it."); continue; }

            var chosen = ChooseInstaller(LoadInstallers(db, pkgId, version), opt);
            if (chosen == null)
            {
                warnings.Add($"{pkgId} {version}: no usable installer ({string.Join("/", opt.Architectures)}) with a download URL and SHA256.");
                continue;
            }
            items.Add(new PackItem(pkgId, version, reason, chosen));

            if (opt.IncludeDependencies && depth < opt.MaxDependencyDepth)
                foreach (var dep in queries.GetDependencies(pkgId, version))
                    queue.Enqueue((dep, null, depth + 1, $"dependency of {pkgId}"));
        }
        return new PackPlan(items, warnings);
    }

    private static (string Id, string? Latest)? ResolvePackage(CatalogStore db, string id)
    {
        lock (db.Sync)
        {
            using var cmd = db.Conn.CreateCommand();
            cmd.CommandText = "SELECT id, latest_version FROM packages WHERE id = $id COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
        }
    }

    private static bool VersionExists(CatalogStore db, string pkgId, string version)
    {
        lock (db.Sync)
        {
            using var cmd = db.Conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM versions WHERE pkg_id = $id AND version = $v LIMIT 1";
            cmd.Parameters.AddWithValue("$id", pkgId);
            cmd.Parameters.AddWithValue("$v", version);
            return cmd.ExecuteScalar() != null;
        }
    }

    private static List<CatalogInstaller> LoadInstallers(CatalogStore db, string pkgId, string version)
    {
        var list = new List<CatalogInstaller>();
        lock (db.Sync)
        {
            using var cmd = db.Conn.CreateCommand();
            cmd.CommandText =
                "SELECT pkg_id, version, arch, type, nested_type, scope, url, sha256, silent, silent_progress, " +
                "custom, product_code, portable_alias, nested_files FROM installers WHERE pkg_id = $id AND version = $v";
            cmd.Parameters.AddWithValue("$id", pkgId);
            cmd.Parameters.AddWithValue("$v", version);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new CatalogInstaller(
                    r.GetString(0), r.GetString(1), S(r, 2) ?? "", S(r, 3) ?? "", S(r, 4), S(r, 5) ?? "",
                    S(r, 6) ?? "", S(r, 7) ?? "", S(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), S(r, 13)));
        }
        return list;
    }

    internal static CatalogInstaller? ChooseInstaller(IEnumerable<CatalogInstaller> rows, PackOptions opt) =>
        rows.Where(i => !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.Sha256))
            .Where(i => !i.Type.Equals("pwa", StringComparison.OrdinalIgnoreCase))
            .Where(i => ArchRank(i, opt) < 99)
            .OrderBy(i => (ArchRank(i, opt), ScopeRank(i, opt), TypeRank(i)))
            .FirstOrDefault();

    private static int ArchRank(CatalogInstaller i, PackOptions o)
    {
        int idx = Array.FindIndex(o.Architectures, a => a.Equals(i.Arch, StringComparison.OrdinalIgnoreCase));
        return idx < 0 ? 99 : idx;
    }

    private static int ScopeRank(CatalogInstaller i, PackOptions o) =>
        o.Scope == null
            ? (i.Scope.Equals("user", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            : (i.Scope.Equals(o.Scope, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    /// <summary>نفضّل مثبّتات Windows Installer ثم المثبّتات الصامتة المعروفة، وآخراً المحمول.</summary>
    private static int TypeRank(CatalogInstaller i) => i.Type.ToLowerInvariant() switch
    {
        "msi" or "wix" => 0,
        "inno" or "nullsoft" or "burn" => 1,
        "exe" => 2,
        "msix" or "appx" => 3,
        "zip" or "portable" => 4,
        _ => 5,
    };

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

public static class PackNaming
{
    private static readonly string[] KnownExtensions = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".zip" };

    /// <summary>اسم ملف ثابت: المعرّف_الإصدار_المعمارية.امتداد (من الرابط، وإلا من نوع المثبّت).</summary>
    public static string FileNameFor(PackItem item)
    {
        var inst = item.Installer;
        string ext = "";
        if (Uri.TryCreate(inst.Url, UriKind.Absolute, out var uri)) ext = Path.GetExtension(uri.AbsolutePath);
        if (!KnownExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            ext = inst.Type.ToLowerInvariant() switch
            {
                "msi" or "wix" => ".msi",
                "msix" => ".msix",
                "appx" => ".appx",
                "zip" => ".zip",
                _ => ".exe",
            };
        }
        var name = $"{item.PackageId}_{item.Version}_{inst.Arch}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + ext.ToLowerInvariant();
    }
}
