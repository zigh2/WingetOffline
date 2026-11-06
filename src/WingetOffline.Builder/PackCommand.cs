using System.Text;
using System.Text.Json;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Packing;

/// <summary>
/// WingetOffline.Builder pack: يجمع مثبّتات حزم (مع تبعياتها) في مجلد، ويتحقق من SHA256، ويولّد
/// pack-manifest.json وسكربت install-offline.ps1. ينقل المجلد بالفلاش إلى الجهاز المعزول.
/// </summary>
internal static class PackCommand
{
    private const string Usage =
        "Usage: WingetOffline.Builder pack --catalog <catalog.wglib> --out <folder> (--ids a,b | --from-scan updates.json | --list file.txt)\n" +
        "       [--version <v> (single id only)] [--arch x64,x86] [--scope machine|user] [--no-deps] [--dry-run] [--include-low]";

    public static async Task<int> RunAsync(string[] a)
    {
        string? catalog = null, outDir = null, ids = null, fromScan = null, listFile = null, scope = null, version = null;
        string[]? arch = null;
        bool deps = true, dry = false, includeLow = false;

        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--catalog" when i + 1 < a.Length: catalog = a[++i]; break;
                case "--out" when i + 1 < a.Length: outDir = a[++i]; break;
                case "--ids" when i + 1 < a.Length: ids = a[++i]; break;
                case "--from-scan" when i + 1 < a.Length: fromScan = a[++i]; break;
                case "--list" when i + 1 < a.Length: listFile = a[++i]; break;
                case "--version" when i + 1 < a.Length: version = a[++i]; break;
                case "--scope" when i + 1 < a.Length: scope = a[++i]; break;
                case "--arch" when i + 1 < a.Length:
                    arch = a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--no-deps": deps = false; break;
                case "--dry-run": dry = true; break;
                case "--include-low": includeLow = true; break;
                default: Console.Error.WriteLine($"Unknown option: {a[i]}\n{Usage}"); return 1;
            }
        }
        if (catalog == null || outDir == null || (ids == null && fromScan == null && listFile == null))
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }
        if (!File.Exists(catalog)) { Console.Error.WriteLine("Catalog not found: " + catalog); return 2; }

        // ---- الطلبات ----
        var requests = new List<PackRequest>();
        if (ids != null)
        {
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var id in list) requests.Add(new PackRequest(id, list.Length == 1 ? version : null));
        }
        if (listFile != null)
        {
            foreach (var raw in File.ReadAllLines(listFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var at = line.IndexOf('@');
                requests.Add(at > 0 ? new PackRequest(line[..at].Trim(), line[(at + 1)..].Trim()) : new PackRequest(line));
            }
        }
        if (fromScan != null)
        {
            foreach (var e in UpdateList.Read(fromScan, includeLow)) requests.Add(new PackRequest(e.PackageId, e.Version));
        }
        if (requests.Count == 0) { Console.Error.WriteLine("No packages were requested."); return 1; }

        // ---- الخطة ----
        using var db = CatalogStore.Open(catalog);
        var options = new PackOptions { IncludeDependencies = deps, Scope = scope };
        if (arch != null && arch.Length > 0) options.Architectures = arch;
        var plan = PackPlanner.Plan(db, requests, options);

        Console.WriteLine($"{Fit("Package", 42)} {Fit("Version", 16)} {Fit("Type", 9)} {Fit("Arch", 7)} Reason");
        foreach (var it in plan.Items)
            Console.WriteLine($"{Fit(it.PackageId, 42)} {Fit(it.Version, 16)} {Fit(it.Installer.Type, 9)} {Fit(it.Installer.Arch, 7)} {it.Reason}");
        foreach (var w in plan.Warnings) Console.WriteLine("WARNING: " + w);
        Console.WriteLine($"\n{plan.Items.Count} installer(s) planned ({plan.Items.Count(i => i.IsDependency)} as dependencies), {plan.Warnings.Count} warning(s).");

        if (plan.Items.Count == 0) return 3;
        if (dry) { Console.WriteLine("DRY RUN - nothing was downloaded."); return plan.Warnings.Count == 0 ? 0 : 3; }

        // ---- التنزيل ----
        Directory.CreateDirectory(outDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WingetOffline-PackBuilder/1.0");

        var results = new List<DownloadResult>();
        int index = 0;
        foreach (var item in plan.Items)
        {
            index++;
            Console.Write($"\r[{index}/{plan.Items.Count}] {Fit(item.PackageId, 40)}".PadRight(78));
            var res = await PackDownloader.DownloadAsync(http, item, outDir,
                msg => Console.Write($"\r[{index}/{plan.Items.Count}] {msg}".PadRight(78)));
            results.Add(res);
            Console.WriteLine($"\r[{index}/{plan.Items.Count}] {(res.Success ? "OK  " : "FAIL")} {item.PackageId} {item.Version} - {res.Message}".PadRight(78));
        }

        // ---- المخرجات: سجل + سكربت تثبيت (التبعيات أولاً) ----
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(outDir, "pack-manifest.json"), JsonSerializer.Serialize(new
        {
            createdUtc = DateTime.UtcNow,
            catalogBuiltUtc = db.CreatedUtc,
            items = results.Select(r => new
            {
                packageId = r.Item.PackageId, version = r.Item.Version, reason = r.Item.Reason,
                type = r.Item.Installer.Type, arch = r.Item.Installer.Arch, scope = r.Item.Installer.Scope,
                url = r.Item.Installer.Url, sha256 = r.Item.Installer.Sha256,
                file = r.FileName, ok = r.Success, cached = r.Cached, message = r.Message,
            }),
            warnings = plan.Warnings,
        }, json));

        var ok = results.Where(r => r.Success && r.FilePath != null)
                        .OrderBy(r => r.Item.IsDependency ? 0 : 1)
                        .Select(r => (new IndexedInstaller
                        {
                            FilePath = r.FilePath!, Size = new FileInfo(r.FilePath!).Length, Sha256 = r.Item.Installer.Sha256,
                        }, r.Item.Installer))
                        .ToList();
        if (ok.Count > 0)
            File.WriteAllText(Path.Combine(outDir, "install-offline.ps1"),
                InstallScriptBuilder.Build(ok, Path.GetFullPath(outDir)), new UTF8Encoding(true));

        int failed = results.Count(r => !r.Success);
        Console.WriteLine($"\nDone: {ok.Count} verified installer(s), {failed} failed -> {Path.GetFullPath(outDir)}");
        Console.WriteLine("Copy this folder to the offline machine: Settings > Installers > Index now (or run install-offline.ps1).");
        return failed == 0 ? 0 : 4;
    }

    private static string Fit(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s.PadRight(n) : s[..(n - 2)] + "..";
    }
}
