using System.Text;
using System.Text.RegularExpressions;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;

// wgo: أداة سطر أوامر للجهاز المعزول (لا تستخدم الإنترنت إطلاقاً)
//   wgo stats <catalog.wglib>
//   wgo search <catalog.wglib> <كلمات البحث> [--limit N]
//   wgo scan  <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2) return Usage();
return args[0].ToLowerInvariant() switch
{
    "stats" => Stats(args[1]),
    "scan" => Scan(args[1..]),
    "search" => Search(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("Usage:\n  wgo stats <catalog.wglib>\n  wgo search <catalog.wglib> <words> [--limit N]\n  wgo scan <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]");
    return 1;
}

static int Stats(string catalog)
{
    using var db = CatalogStore.Open(catalog);
    long Q(string sql)
    {
        using var c = db.Conn.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar() ?? 0);
    }

    var packages = Q("SELECT COUNT(*) FROM packages");
    Console.WriteLine($"Packages:                    {packages}");
    Console.WriteLine($"Versions:                    {Q("SELECT COUNT(*) FROM versions")}");
    Console.WriteLine($"With ProductCode:            {Q("SELECT COUNT(*) FROM (SELECT pkg_id FROM arp WHERE product_code IS NOT NULL UNION SELECT pkg_id FROM installers WHERE product_code IS NOT NULL)")}");
    Console.WriteLine($"With Apps&Features name:     {Q("SELECT COUNT(DISTINCT pkg_id) FROM arp WHERE display_name IS NOT NULL")}");
    Console.WriteLine($"With Commands (portable...): {Q("SELECT COUNT(DISTINCT pkg_id) FROM installers WHERE commands <> ''")}");
    Console.WriteLine("\nInstaller types (packages):");
    using var cmd = db.Conn.CreateCommand();
    cmd.CommandText = "SELECT type, COUNT(DISTINCT pkg_id) n FROM installers GROUP BY type ORDER BY n DESC LIMIT 12";
    using var r = cmd.ExecuteReader();
    while (r.Read()) Console.WriteLine($"  {(r.IsDBNull(0) || r.GetString(0) == "" ? "(none)" : r.GetString(0)),-14} {r.GetInt64(1)}");
    return 0;
}

static int Search(string[] a)
{
    if (a.Length < 2) return Usage();
    int limit = 15;
    var words = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        if (a[i] == "--limit" && i + 1 < a.Length && int.TryParse(a[i + 1], out var parsed)) { limit = parsed; i++; }
        else words.Add(a[i]);
    }
    var q = string.Join(" ", Regex.Matches(string.Join(" ", words), @"[\p{L}\p{Nd}]+").Select(m => "\"" + m.Value + "\"*"));
    if (q.Length == 0) return Usage();

    using var db = CatalogStore.Open(a[0]);
    using var cmd = db.Conn.CreateCommand();
    cmd.CommandText = "SELECT p.id, p.name, p.publisher, p.latest_version, p.version_count FROM packages_fts " +
                      "JOIN packages p ON p.id = packages_fts.id WHERE packages_fts MATCH $q ORDER BY rank LIMIT $n";
    cmd.Parameters.AddWithValue("$q", q);
    cmd.Parameters.AddWithValue("$n", limit);
    using var r = cmd.ExecuteReader();
    Console.WriteLine($"{Fit("PackageId", 42)} {Fit("Name", 34)} {Fit("Latest", 18)} Versions");
    int count = 0;
    while (r.Read())
    {
        count++;
        Console.WriteLine($"{Fit(r.GetString(0), 42)} {Fit(r.IsDBNull(1) ? "" : r.GetString(1), 34)} {Fit(r.IsDBNull(3) ? "" : r.GetString(3), 18)} {r.GetInt64(4)}");
    }
    if (count == 0) Console.WriteLine("(no results - the package may not exist in winget-pkgs)");
    return 0;
}

static int Scan(string[] a)
{
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("scan requires Windows."); return 2; }

    string catalog = a[0];
    var portableDirs = new List<string>();
    bool wingetPortables = true, showAll = false, onlyUpdates = false;
    string? csv = null;
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--portable" when i + 1 < a.Length: portableDirs.Add(a[++i]); break;
            case "--csv" when i + 1 < a.Length: csv = a[++i]; break;
            case "--no-winget-portables": wingetPortables = false; break;
            case "--all": showAll = true; break;
            case "--updates": onlyUpdates = true; break;
            default: Console.Error.WriteLine($"Unknown option: {a[i]}"); return Usage();
        }
    }

    Console.WriteLine("Loading catalog...");
    using var db = CatalogStore.Open(catalog);
    var matcher = new Matcher(db);
    Console.WriteLine($"Catalog: {matcher.PackageCount} packages");

    var apps = RegistryScanner.Scan();
    if (wingetPortables) apps.AddRange(PortableScanner.ScanWingetPackages());
    foreach (var d in portableDirs) apps.AddRange(PortableScanner.ScanFolder(d));
    Console.WriteLine($"Scanned: {apps.Count} items\n");

    var results = apps.Select(matcher.Match).ToList();
    var visible = showAll ? results : results.Where(r => r.Confidence != Confidence.None);
    if (onlyUpdates) visible = visible.Where(r => r.Status == UpdateStatus.UpdateAvailable);
    var rows = visible.OrderBy(r => r.App.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    Console.WriteLine($"{Fit("Name", 44)} {Fit("Installed", 18)} {Fit("Latest", 18)} {Fit("Status", 10)} {Fit("Conf", 6)} {Fit("PackageId", 38)} Method");
    foreach (var r in rows)
        Console.WriteLine($"{Fit(r.App.DisplayName, 44)} {Fit(r.InstalledVersion, 18)} {Fit(r.LatestVersion, 18)} " +
                          $"{Fit(StatusText(r.Status), 10)} {Fit(r.Confidence.ToString(), 6)} {Fit(r.PackageId, 38)} {r.Method}{(string.IsNullOrEmpty(r.Note) ? "" : " | " + r.Note)}");

    Console.WriteLine($"\nHigh {Count(results, Confidence.High)} | Medium {Count(results, Confidence.Medium)} | " +
                      $"Low {Count(results, Confidence.Low)} | Unmatched {Count(results, Confidence.None)} | " +
                      $"Updates {results.Count(r => r.Status == UpdateStatus.UpdateAvailable)}");

    if (csv != null)
    {
        var sb = new StringBuilder("Name,Publisher,Source,Scope,Installed,Latest,Status,Confidence,PackageId,Method,Note\n");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                r.App.DisplayName, r.App.Publisher, r.App.Source, r.App.Scope, r.InstalledVersion, r.LatestVersion,
                StatusText(r.Status), r.Confidence.ToString(), r.PackageId, r.Method, r.Note
            }.Select(Csv)));
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"CSV written: {Path.GetFullPath(csv)}");
    }
    return 0;
}

static int Count(List<MatchResult> l, Confidence c) => l.Count(r => r.Confidence == c);
static string StatusText(UpdateStatus s) => s switch { UpdateStatus.UpdateAvailable => "UPDATE", UpdateStatus.UpToDate => "ok", _ => "?" };
static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
static string Fit(string? s, int n)
{
    s ??= "";
    return s.Length <= n ? s.PadRight(n) : s[..(n - 2)] + "..";
}
