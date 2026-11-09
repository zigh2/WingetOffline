using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Install;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Packing;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

// wgo: أداة سطر أوامر للجهاز المعزول (لا تستخدم الإنترنت إطلاقاً)
//   wgo stats <catalog.wglib>
//   wgo search <catalog.wglib> <كلمات البحث> [--limit N]
//   wgo index <catalog.wglib> <installers-folder>
//   wgo install <catalog.wglib> <installer-file> [--run] [--yes] [--interactive] [--no-elevate] [--type T --args "..."]
//   wgo audit-versions <catalog.wglib> [--limit N]
//   wgo script <catalog.wglib> <installers-folder> [--out install-offline.ps1] [--only id1,id2]
//   wgo uninstall <program-name> [--run] [--yes] [--interactive] [--no-elevate]
//   wgo scan  <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]
//   wgo library scan [<folder>...] [--all-drives [--removable]] [--exclude <rule>]... [--exclusions <file>] [--no-default-exclusions]
//                    [--rescan] [--hash] [--catalog <catalog.wglib>] [--export <file.xlsx|csv|json>]
//   wgo library list [--text word] [--type exe,msi,...] [--export <file>] [--json]
//   wgo library match <catalog.wglib> [--newer] [--export <file>] [--json]

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
    "audit-versions" => AuditVersionsCmd(args[1..]),
    "library" => LibraryCmd(args[1..]),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("Usage:\n  wgo index <catalog.wglib> <installers-folder>\n  wgo install <catalog.wglib> <file> [--run] [--yes] [--interactive] [--no-elevate] [--type T --args \"...\"]\n  wgo uninstall <program-name> [--run] [--yes] [--interactive] [--no-elevate]\n  wgo script <catalog.wglib> <folder> [--out install-offline.ps1] [--only id1,id2]\n  wgo audit-versions <catalog.wglib> [--limit N] [--csv file]\n  (scan also accepts --export-updates <file.json> to save the updates you need)\n  (scan and index also accept --json)\n  wgo stats <catalog.wglib>\n  wgo search <catalog.wglib> <words> [--limit N]\n  wgo scan <catalog.wglib> [--portable <dir>]... [--no-winget-portables] [--csv out.csv] [--all] [--updates]\n  wgo library scan [<folder>...] [--all-drives [--removable]] [--exclude <rule>]... [--exclusions <file>] [--no-default-exclusions]\n                   [--rescan] [--hash] [--catalog <catalog.wglib>] [--export <file.xlsx|csv|json>]\n  (exclusion rules: C:\\Windows\\*  .tmp  setup*.log  node_modules  re:<regex>; defaults skip C:\\Windows, C:\\Program Files...)\n  wgo library list [--text word] [--type exe,msi,...] [--export <file>] [--json]\n  wgo library match <catalog.wglib> [--newer] [--export <file>] [--json]\n  (the library is the same one the app shows on its Local page)");
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

static int AuditVersionsCmd(string[] a)
{
    if (a.Length < 1) return Usage();
    int limit = 25;
    string? csv = null;
    for (int i = 1; i < a.Length; i++)
    {
        if (a[i] == "--limit" && i + 1 < a.Length && int.TryParse(a[i + 1], out var parsed)) { limit = parsed; i++; }
        else if (a[i] == "--csv" && i + 1 < a.Length) csv = a[++i];
    }

    using var db = CatalogStore.Open(a[0]);
    var report = VersionAudit.Run(db, limit);
    double pct = report.PackagesChecked == 0 ? 0 : 100.0 * report.PackagesWithViolations / report.PackagesChecked;
    Console.WriteLine($"Packages with at least two dated versions: {report.PackagesChecked}");
    Console.WriteLine($"Packages where the version order disagrees with release dates: {report.PackagesWithViolations} ({pct:0.0}%)");
    Console.WriteLine($"  by date gap:  up to 30 days: {report.GapUpTo30Days}   31-365 days: {report.Gap31To365Days}   over a year: {report.GapOver365Days}\n");
    if (report.Samples.Count > 0)
    {
        Console.WriteLine($"{Fit("Package", 42)} {Fit("Older release", 26)} {Fit("Newer release", 26)} Gap");
        foreach (var v in report.Samples)
            Console.WriteLine($"{Fit(v.PackageId, 42)} {Fit(v.OlderByDate + " (" + v.OlderDate + ")", 26)} {Fit(v.NewerByDate + " (" + v.NewerDate + ")", 26)} {v.GapDays}d");
        Console.WriteLine("\nEach row: the release dated EARLIER has the HIGHER version according to the comparer.");
        Console.WriteLine("Occasional rows are legitimate (patches to old branches, wrong dates); repeated patterns mean a comparison rule is wrong.");
    }
    if (csv != null)
    {
        var sb = new StringBuilder("PackageId,OlderVersion,OlderDate,NewerVersion,NewerDate,GapDays\n");
        foreach (var v in report.All)
            sb.AppendLine(string.Join(",", new[] { v.PackageId, v.OlderByDate, v.OlderDate, v.NewerByDate, v.NewerDate, v.GapDays.ToString() }.Select(Csv)));
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"All {report.All.Count} rows written: {Path.GetFullPath(csv)}");
    }
    return 0;
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
    string? exportUpdates = null;
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
            case "--export-updates" when i + 1 < a.Length: exportUpdates = a[++i]; break;
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

    if (exportUpdates != null)
    {
        var needed = results
            .Where(r => r.Status == UpdateStatus.UpdateAvailable && r.Confidence >= Confidence.Medium && !string.IsNullOrEmpty(r.PackageId))
            .Select(r => new UpdateEntry(r.PackageId!, r.LatestVersion, r.InstalledVersion, r.App.DisplayName))
            .ToList();
        UpdateList.Write(exportUpdates, needed);
        Info($"Update list written: {Path.GetFullPath(exportUpdates)} ({needed.Count} update(s))");
    }
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

// ---------------------------------------------------------------- المكتبة المحلية

static int LibraryCmd(string[] a)
{
    if (a.Length == 0) return Usage();
    var sub = a[0].ToLowerInvariant();

    var positional = new List<string>();
    string? export = null, catalogPath = null, text = null, types = null;
    bool rescan = false, hash = false, json = false, onlyNewer = false;
    bool allDrives = false, removable = false, noDefaultExclusions = false;
    string? exclusionsFile = null;
    var excludes = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--rescan": rescan = true; break;
            case "--hash": hash = true; break;
            case "--json": json = true; break;
            case "--newer": onlyNewer = true; break;
            case "--all-drives": allDrives = true; break;
            case "--removable": removable = true; break;
            case "--no-default-exclusions": noDefaultExclusions = true; break;
            case "--exclude" when i + 1 < a.Length: excludes.Add(a[++i]); break;
            case "--exclusions" when i + 1 < a.Length: exclusionsFile = a[++i]; break;
            case "--export" when i + 1 < a.Length: export = a[++i]; break;
            case "--catalog" when i + 1 < a.Length: catalogPath = a[++i]; break;
            case "--text" when i + 1 < a.Length: text = a[++i]; break;
            case "--type" when i + 1 < a.Length: types = a[++i]; break;
            default:
                if (a[i].StartsWith("--")) { Console.Error.WriteLine("Unknown option: " + a[i]); return 1; }
                positional.Add(a[i]);
                break;
        }
    }

    var store = LibraryStore.Load();

    switch (sub)
    {
        case "scan":
        {
            var roots = LibraryRoots.Compute(allDrives, removable, positional);
            if (roots.Count == 0) { Console.Error.WriteLine("Give at least one folder, or use --all-drives."); return 1; }

            // الاستثناءات: الافتراضية (إلا بطلب) + --exclude + ملف قواعد
            var ruleText = new StringBuilder();
            if (!noDefaultExclusions) ruleText.AppendLine(LibraryExclusions.DefaultText);
            foreach (var e in excludes) ruleText.AppendLine(e);
            if (exclusionsFile != null)
            {
                if (!File.Exists(exclusionsFile)) { Console.Error.WriteLine("Exclusions file not found: " + exclusionsFile); return 1; }
                ruleText.AppendLine(File.ReadAllText(exclusionsFile));
            }
            var exclusions = LibraryExclusions.Parse(ruleText.ToString());
            foreach (var err in exclusions.Errors) Console.Error.WriteLine("Exclusion rule ignored: " + err);

            var progress = new InlineProgress<LibraryProgress>(p =>
            {
                if (json) return;
                if (p.Total > 0) Console.Error.Write($"\r{p.Phase}: {p.Done}/{p.Total}   ");
                else Console.Error.Write($"\r{p.Phase}   ");
            });
            var r = store.Sync(roots, new LibrarySyncOptions
            {
                Rescan = rescan, ComputeHashes = hash, Exclusions = exclusions,
                KeepEntriesOutsideRoots = !allDrives, KeepEntriesOnMissingDrives = allDrives,
            }, progress);
            if (!json) Console.Error.WriteLine();

            int linked = 0;
            if (catalogPath != null)
            {
                using var db = CatalogStore.Open(catalogPath);
                linked = LibraryCatalogLinker.Link(store.Entries, db);
                store.Save();
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    added = r.Added, updated = r.Updated, unchanged = r.Unchanged, removed = r.Removed, hashed = r.Hashed,
                    errors = r.Errors, excludedDirectories = r.ExcludedDirectories, excludedFiles = r.ExcludedFiles,
                    skippedOnlineOnly = r.SkippedOnlineOnly, offlineRoots = r.OfflineRoots, seconds = r.Duration.TotalSeconds, linkedToCatalog = linked,
                    total = store.Entries.Count,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine($"Library: {store.Entries.Count} files ({r.Added} new, {r.Updated} changed, {r.Unchanged} unchanged, {r.Removed} removed)"
                    + (r.Hashed > 0 ? $", {r.Hashed} hashed" : "") + (linked > 0 ? $", {linked} matched to winget" : "")
                    + (r.Errors > 0 ? $", {r.Errors} read errors" : "") + $" in {r.Duration.TotalSeconds:0.#}s");
                if (r.ExcludedDirectories + r.ExcludedFiles > 0)
                    Console.WriteLine($"Skipped by exclusions: {r.ExcludedDirectories} folder(s), {r.ExcludedFiles} file(s)");
                if (r.SkippedOnlineOnly > 0) Console.WriteLine($"{r.SkippedOnlineOnly} online-only cloud file(s) left unread");
                foreach (var o in r.OfflineRoots) Console.WriteLine("Not reachable (kept as is): " + o);
            }
            return ExportEntries(store.Entries, null, export);
        }

        case "list":
        {
            var q = FilterEntries(store.Entries, text, types);
            if (json) Console.WriteLine(JsonSerializer.Serialize(q, new JsonSerializerOptions { WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            else
            {
                Console.WriteLine($"{"Type",-6} {"Name",-34} {"Version",-14} {"Arch",-6} {"Size",9}  {"Identified",-15} File");
                foreach (var e in q)
                    Console.WriteLine($"{e.KindText(),-6} {Trunc(e.DisplayName, 34),-34} {Trunc(e.Version ?? "", 14),-14} {e.Architecture,-6} {LibraryPresentation.FormatSize(e.Size),9}  {e.IdentifiedText(),-15} {e.FileName}");
                Console.WriteLine($"\n{q.Count} of {store.Entries.Count} files");
            }
            return ExportEntries(q, null, export);
        }

        case "match":
        {
            if (positional.Count == 0) { Console.Error.WriteLine("Give the catalog file: wgo library match <catalog.wglib>"); return 1; }
            using var db = CatalogStore.Open(positional[0]);
            var matcher = new Matcher(db);
            List<MatchResult> results;
            lock (db.Sync) results = RegistryScanner.Scan().Select(matcher.Match).ToList();

            var links = new LibraryMatcher(store.Entries).Link(results.Select(r => (r.App, (string?)(r.InstalledVersion ?? r.App.DisplayVersion))));
            var rows = results.Where(r => links.ByApp.ContainsKey(r.App))
                .Select(r => (R: r, M: links.ByApp[r.App]))
                .Where(x => !onlyNewer || x.M.Relation == LibraryRelation.LibraryNewer)
                .OrderBy(x => x.R.App.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(rows.Select(x => new
                {
                    name = x.R.App.DisplayName, installed = x.R.InstalledVersion ?? x.R.App.DisplayVersion,
                    libraryVersion = x.M.Best.Version, relation = x.M.Relation.ToString(), file = x.M.Best.Path,
                    files = x.M.Candidates.Count, confidence = x.M.Confidence.ToString(), method = x.M.Method,
                }), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            else
            {
                Console.WriteLine($"{"Installed program",-40} {"Installed",-14} {"Library",-22} File");
                foreach (var (r, m) in rows)
                    Console.WriteLine($"{Trunc(r.App.DisplayName, 40),-40} {Trunc(r.InstalledVersion ?? r.App.DisplayVersion ?? "", 14),-14} {m.ShortText,-22} {m.Best.FileName}");
                Console.WriteLine($"\n{rows.Count} installed program(s) found in the library ({results.Count} scanned, {store.Entries.Count} library files)");
            }
            return ExportEntries(store.Entries, links.ByPath, export);
        }

        default:
            return Usage();
    }
}

static List<LibraryEntry> FilterEntries(IEnumerable<LibraryEntry> entries, string? text, string? types)
{
    IEnumerable<LibraryEntry> q = entries;
    if (!string.IsNullOrWhiteSpace(text))
        q = q.Where(e => e.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                      || (e.Publisher?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                      || e.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                      || e.Folder.Contains(text, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(types))
    {
        var set = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant()).ToHashSet();
        q = q.Where(e => set.Contains(e.KindText()));
    }
    return q.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Version, StringComparer.OrdinalIgnoreCase).ToList();
}

static int ExportEntries(IEnumerable<LibraryEntry> entries, IReadOnlyDictionary<string, InstalledLink>? links, string? file)
{
    if (file == null) return 0;
    if (TableExporter.FormatFromPath(file) == null)
    {
        Console.Error.WriteLine("Export file must end with .xlsx, .csv or .json");
        return 1;
    }
    try { TableExporter.Write(file, LibraryExport.ToTable(entries, links)); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine("Could not write the export: " + ex.Message);
        return 1;
    }
    Console.WriteLine("Exported to " + file);
    return 0;
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

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

sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public InlineProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
