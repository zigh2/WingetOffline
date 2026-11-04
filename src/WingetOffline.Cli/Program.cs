using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

// wgo: أداة سطر أوامر للجهاز المعزول (لا تستخدم الإنترنت إطلاقاً)
//   wgo stats <catalog.wglib>
//   wgo search <catalog.wglib> <كلمات البحث> [--limit N]
//   wgo index <catalog.wglib> <installers-folder>
//   wgo install <catalog.wglib> <installer-file> [--run] [--yes] [--interactive] [--no-elevate] [--type T --args "..."]
//   wgo script <catalog.wglib> <installers-folder> [--out install-offline.ps1] [--only id1,id2]
//   wgo uninstall <program-name> [--run] [--yes] [--interactive] [--no-elevate]
//   wgo scan  <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2) return Usage();
return args[0].ToLowerInvariant() switch
{
    "stats" => Stats(args[1]),
    "scan" => Scan(args[1..]),
    "search" => Search(args[1..]),
    "index" => IndexCmd(args[1..]),
    "install" => InstallCmd(args[1..]),
    "uninstall" => UninstallCmd(args[1..]),
    "script" => ScriptCmd(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("Usage:\n  wgo index <catalog.wglib> <installers-folder>\n  wgo install <catalog.wglib> <file> [--run] [--yes] [--interactive] [--no-elevate] [--type T --args \"...\"]\n  wgo uninstall <program-name> [--run] [--yes] [--interactive] [--no-elevate]\n  wgo script <catalog.wglib> <folder> [--out install-offline.ps1] [--only id1,id2]\n  (scan and index also accept --json)\n  wgo stats <catalog.wglib>\n  wgo search <catalog.wglib> <words> [--limit N]\n  wgo scan <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]");
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

static int IndexCmd(string[] a)
{
    if (a.Length < 2) return Usage();
    if (!Directory.Exists(a[1])) { Console.Error.WriteLine("Folder not found: " + a[1]); return 2; }

    bool json = a.Skip(2).Contains("--json");
    using var db = CatalogStore.Open(a[0]);
    var items = InstallerIndexer.IndexFolder(db, a[1], json ? null : new ConsoleProgress());
    if (json)
    {
        var payload = items.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase).Select(i => new
        {
            file = i.FilePath, sizeBytes = i.Size, sha256 = i.Sha256, recognised = i.IsMatched,
            package = i.Best?.PackageId, version = i.Best?.Version, type = i.Best?.Type, arch = i.Best?.Arch,
            catalogMatches = i.Matches.Count,
        });
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
        return 0;
    }
    Console.WriteLine();
    Console.WriteLine($"{Fit("File", 46)} {Fit("Size", 9)} {Fit("Status", 10)} {Fit("Package", 34)} {Fit("Version", 16)} {Fit("Type", 9)} Arch");
    foreach (var it in items.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase))
    {
        var b = it.Best;
        Console.WriteLine($"{Fit(Path.GetFileName(it.FilePath), 46)} {Fit((it.Size / 1048576.0).ToString("0.0") + " MB", 9)} " +
                          $"{Fit(it.IsMatched ? "Matched" : "Unknown", 10)} {Fit(b?.PackageId, 34)} {Fit(b?.Version, 16)} {Fit(b?.Type, 9)} {b?.Arch}");
    }
    int matched = items.Count(i => i.IsMatched);
    Console.WriteLine($"\n{items.Count} installer file(s): {matched} matched, {items.Count - matched} unknown (hash not in catalog).");
    return 0;
}

static int InstallCmd(string[] a)
{
    if (a.Length < 2) return Usage();
    string catalog = a[0], file = a[1];
    bool run = false, yes = false, interactive = false, noElevate = false;
    string? type = null, extraArgs = null;
    for (int i = 2; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--run": run = true; break;
            case "--yes": yes = true; break;
            case "--interactive": interactive = true; break;
            case "--no-elevate": noElevate = true; break;
            case "--type" when i + 1 < a.Length: type = a[++i]; break;
            case "--args" when i + 1 < a.Length: extraArgs = a[++i]; break;
            default: Console.Error.WriteLine($"Unknown option: {a[i]}"); return Usage();
        }
    }
    if (!File.Exists(file)) { Console.Error.WriteLine("File not found: " + file); return 2; }

    using var db = CatalogStore.Open(catalog);
    var item = InstallerIndexer.IndexFile(db, file);
    Console.WriteLine($"File:    {item.FilePath}\nSHA256:  {item.Sha256}");

    var inst = item.Best;
    if (type != null)
        inst = new CatalogInstaller("(manual)", "", "", type, null, "", "", item.Sha256, extraArgs, null, null, null, null, null);
    if (inst == null)
    {
        Console.Error.WriteLine("\nThis file's SHA256 is not in the catalog (different version, or not from winget-pkgs).");
        Console.Error.WriteLine("You can still plan it manually: --type msi|inno|nullsoft|burn|exe|msix|zip|portable [--args \"<silent switches>\"]");
        return 3;
    }
    if (item.Matches.Count > 1)
        Console.WriteLine($"Note:    {item.Matches.Count} catalog entries share this hash; using the best one.");
    Console.WriteLine($"Package: {inst.PackageId} {inst.Version} ({inst.Type}, {inst.Arch}, scope: {(inst.Scope.Length == 0 ? "unspecified" : inst.Scope)})");

    Directory.CreateDirectory(PlanRunner.LogDir);
    var opt = new InstallOptions
    {
        Interactive = interactive,
        MsiLogFile = Path.Combine(PlanRunner.LogDir, $"msi-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
    };
    InstallPlan plan;
    try { plan = InstallPlanner.Plan(Path.GetFullPath(file), inst, opt); }
    catch (NotSupportedException ex) { Console.Error.WriteLine(ex.Message); return 3; }

    return ExecutePlan(plan, inst.PackageId, run, yes, noElevate);
}

static int ScriptCmd(string[] a)
{
    if (a.Length < 2) return Usage();
    string catalog = a[0], folder = a[1];
    string? outFile = null;
    HashSet<string>? only = null;
    for (int i = 2; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--out" when i + 1 < a.Length: outFile = a[++i]; break;
            case "--only" when i + 1 < a.Length:
                only = new HashSet<string>(a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                           StringComparer.OrdinalIgnoreCase);
                break;
            default: Console.Error.WriteLine($"Unknown option: {a[i]}"); return Usage();
        }
    }
    if (!Directory.Exists(folder)) { Console.Error.WriteLine("Folder not found: " + folder); return 2; }
    outFile ??= Path.Combine(folder, "install-offline.ps1");

    using var db = CatalogStore.Open(catalog);
    var items = InstallerIndexer.IndexFolder(db, folder, new ConsoleProgress());
    Console.WriteLine();

    // نثبّت أعلى إصدار متاح لكل حزمة
    var entries = items
        .Where(i => i.IsMatched)
        .Select(i => (Item: i, Info: i.Best!))
        .Where(e => only == null || only.Contains(e.Info.PackageId))
        .GroupBy(e => e.Info.PackageId, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(e => e.Info.Version, WingetVersionComparer.Instance).First())
        .ToList();

    if (entries.Count == 0) { Console.Error.WriteLine("No recognised installers to script."); return 3; }

    var script = InstallScriptBuilder.Build(entries, Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, script, new UTF8Encoding(true));
    Console.WriteLine($"Script written: {Path.GetFullPath(outFile)}  ({entries.Count} package(s))");
    Console.WriteLine("Run it on the offline machine:  powershell -ExecutionPolicy Bypass -File .\\" + Path.GetFileName(outFile));
    return 0;
}

static int UninstallCmd(string[] a)
{
    if (a.Length < 1) return Usage();
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("uninstall requires Windows."); return 2; }

    string query = a[0];
    bool run = false, yes = false, interactive = false, noElevate = false;
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--run": run = true; break;
            case "--yes": yes = true; break;
            case "--interactive": interactive = true; break;
            case "--no-elevate": noElevate = true; break;
            default: Console.Error.WriteLine($"Unknown option: {a[i]}"); return Usage();
        }
    }

    var apps = RegistryScanner.Scan();
    apps.AddRange(PortableScanner.ScanWingetPackages());
    var hits = apps.Where(x => x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    if (hits.Count == 0) { Console.Error.WriteLine($"No installed program matches '{query}'."); return 3; }
    if (hits.Count > 1)
    {
        Console.Error.WriteLine($"'{query}' matches {hits.Count} programs; be more specific:");
        foreach (var h in hits.OrderBy(x => x.DisplayName)) Console.Error.WriteLine($"  {h.DisplayName}  [{h.DisplayVersion}]");
        return 3;
    }

    var app = hits[0];
    Console.WriteLine($"Program: {app.DisplayName} {app.DisplayVersion}  (scope: {app.Scope}, source: {app.Source})");
    var plan = UninstallPlanner.Plan(app, interactive);
    if (plan == null) { Console.Error.WriteLine("No uninstall command was found for this program."); return 3; }
    return ExecutePlan(plan, "uninstall-" + app.DisplayName, run, yes, noElevate);
}

static int ExecutePlan(InstallPlan plan, string label, bool run, bool yes, bool noElevate)
{
    Console.WriteLine($"\nPlan:      {plan.Description}");
    Console.WriteLine($"Command:   {plan.CommandLine}");
    Console.WriteLine($"Elevation: {(plan.NeedsElevation ? (PlanRunner.IsElevated() ? "needed (already elevated)" : "needed (UAC prompt)") : "not needed")}");
    Console.WriteLine($"Mode:      {(plan.Interactive ? "interactive (the program's own UI may appear)" : "silent")}");
    if (plan.Warning != null) Console.WriteLine($"Warning:   {plan.Warning}");

    if (!run)
    {
        Console.WriteLine("\nDRY RUN - nothing was executed. Add --run to execute.");
        return 0;
    }
    if (!yes)
    {
        Console.Write("\nExecute this now? (y/N) ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Cancelled.");
            return 0;
        }
    }

    Console.WriteLine("Running... (this can take a while)");
    var res = PlanRunner.Run(plan, label, !noElevate);
    Console.WriteLine($"\nExit code: {res.ExitCode} | Success: {res.Success}{(res.RebootRequired ? " | reboot required" : "")}");
    Console.WriteLine(res.Message);
    Console.WriteLine("Log: " + res.LogPath);
    return res.Success ? 0 : 4;
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
    bool wingetPortables = true, showAll = false, onlyUpdates = false, json = false;
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
            case "--json": json = true; break;
            default: Console.Error.WriteLine($"Unknown option: {a[i]}"); return Usage();
        }
    }

    void Info(string text) { if (json) Console.Error.WriteLine(text); else Console.WriteLine(text); }
    Info("Loading catalog...");
    using var db = CatalogStore.Open(catalog);
    var matcher = new Matcher(db);
    Info($"Catalog: {matcher.PackageCount} packages");

    var apps = RegistryScanner.Scan();
    if (wingetPortables) apps.AddRange(PortableScanner.ScanWingetPackages());
    foreach (var d in portableDirs) apps.AddRange(PortableScanner.ScanFolder(d));
    Info($"Scanned: {apps.Count} items\n");

    var results = apps.Select(matcher.Match).ToList();
    var visible = showAll ? results : results.Where(r => r.Confidence != Confidence.None);
    if (onlyUpdates) visible = visible.Where(r => r.Status == UpdateStatus.UpdateAvailable);
    var rows = visible.OrderBy(r => r.App.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    if (json)
    {
        var payload = rows.Select(r => new
        {
            name = r.App.DisplayName, publisher = r.App.Publisher, source = r.App.Source, scope = r.App.Scope,
            installed = r.InstalledVersion, latest = r.LatestVersion, status = StatusText(r.Status),
            confidence = r.Confidence.ToString(), packageId = r.PackageId, method = r.Method, note = r.Note,
        });
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOpts));
        return 0;
    }

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

sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.Write("\r" + (value.Length > 78 ? value[..78] : value.PadRight(78)));
}

partial class Program
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
