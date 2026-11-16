using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Packing;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Core.SelfTest;

public static partial class SelfTestEngine
{
    static partial void AddCatalogChecks(List<SelfCheck> list) => CatalogChecks.Add(list);
}

/// <summary>فحوص تحتاج الكتالوج: البحث والتفاصيل والتخطيط وفحص الجهاز والمطابقة وفهرسة المثبّتات.</summary>
internal static class CatalogChecks
{
    private const string Cat = "Catalog", Scn = "Scan", Ins = "Installers", Onl = "Online";

    private static CatalogStore Db(SelfTestContext c)
    {
        if (c.Items.TryGetValue("catalog.external", out var ext)) return (CatalogStore)ext;
        if (c.Items.TryGetValue("catalog.own", out var own)) return (CatalogStore)own;
        var db = CatalogStore.Open(c.Options.CatalogPath!);
        c.Items["catalog.own"] = db;      // يُغلق مع السياق
        return db;
    }

    [SupportedOSPlatform("windows")]
    private static List<MatchResult> ScanAndMatch(CatalogStore db)
    {
        var apps = RegistryScanner.Scan();
        var matcher = new Matcher(db);
        lock (db.Sync) return apps.Select(matcher.Match).ToList();
    }

    public static void Add(List<SelfCheck> l)
    {
        l.Add(new("CAT-1", Cat, "Catalog metadata and age", "T2.1", c =>
        {
            var sw = Stopwatch.StartNew();
            var db = Db(c);
            var age = db.CreatedUtc is { } d ? (DateTime.UtcNow - d).TotalDays : -1;
            var msg = $"{db.MetaPackages:N0} packages, {db.MetaVersions:N0} versions, built {(db.CreatedUtc is { } b ? b.ToString("yyyy-MM-dd") : "?")} ({age:0} days ago), opened in {sw.ElapsedMilliseconds} ms";
            return age > 60 ? CheckOutcome.Warn(msg + ": the catalog is old, consider a newer one.") : CheckOutcome.Pass(msg);
        }) { NeedsCatalog = true });

        l.Add(new("CAT-2", Cat, "Search: vlc, 7 zip, c++, powertoys, nonsense", "T2.2", c =>
        {
            var q = new CatalogQueries(Db(c));
            var k = new Checker(); var d = new StringBuilder();
            foreach (var (text, expectHits) in new[] { ("vlc", true), ("7 zip", true), ("c++", true), ("powertoys", true), ("zzzqqqxxx", false) })
            {
                var sw = Stopwatch.StartNew();
                var (rows, total) = q.Search(new PackageFilter { Text = text, Limit = 20 });
                d.AppendLine($"'{text}': {total} result(s) in {sw.ElapsedMilliseconds} ms" + (rows.Count > 0 ? $"; first: {rows[0].Id}" : ""));
                if (expectHits) k.True(rows.Count > 0, $"'{text}' finds something");
                else k.Eq(0, rows.Count, $"'{text}' finds nothing");
                k.True(sw.ElapsedMilliseconds < 2000, $"'{text}' answered in {sw.ElapsedMilliseconds} ms (under 2 s)");
            }
            return k.Result("search", d.ToString());
        }) { NeedsCatalog = true });

        l.Add(new("CAT-3", Cat, "Every filter runs and returns data", "T2.2", c =>
        {
            var q = new CatalogQueries(Db(c));
            var d = new StringBuilder(); var k = new Checker();
            void Try(string label, PackageFilter f)
            {
                f.Limit = 5;
                var (_, total) = q.Search(f);
                d.AppendLine($"{label}: {total}");
            }
            foreach (var t in CatalogQueries.InstallerTypes) Try("type " + t, new PackageFilter { InstallerType = t });
            foreach (var a in CatalogQueries.Architectures) Try("arch " + a, new PackageFilter { Architecture = a });
            foreach (var s in CatalogQueries.Scopes) Try("scope " + s, new PackageFilter { Scope = s });
            Try("silent only", new PackageFilter { SilentOnly = true });
            Try("portable only", new PackageFilter { PortableOnly = true });
            Try("has ProductCode", new PackageFilter { HasProductCode = true });
            Try("license MIT", new PackageFilter { License = "MIT" });
            var (_, silent) = q.Search(new PackageFilter { SilentOnly = true, Limit = 1 });
            k.True(silent > 0, "silent-install filter finds packages");
            return k.Result("all filters ran", d.ToString());
        }) { NeedsCatalog = true });

        l.Add(new("CAT-4", Cat, "Package details: versions newest-first, installers, dependencies", "T2.3", c =>
        {
            var q = new CatalogQueries(Db(c));
            var k = new Checker(); var d = new StringBuilder(); int found = 0;
            foreach (var id in new[] { "Git.Git", "VideoLAN.VLC", "7zip.7zip", "Microsoft.PowerToys", "Python.Python.3.12", "Rufus.Rufus", "WinSCP.WinSCP" })
            {
                var det = q.GetDetails(id);
                if (det == null) { d.AppendLine($"{id}: not in this catalog"); continue; }
                found++;
                bool ordered = true;
                for (int i = 0; i + 1 < det.Versions.Count && i < 200; i++)
                    if (WingetVersionComparer.Instance.Compare(det.Versions[i], det.Versions[i + 1]) < 0) { ordered = false; k.Fail($"{id}: '{det.Versions[i]}' listed before newer '{det.Versions[i + 1]}'"); break; }
                var inst = det.LatestInstallers;
                bool urls = inst.Count > 0 && inst.All(i => i.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                bool hashes = inst.Count > 0 && inst.All(i => i.Sha256.Length == 64);
                k.True(urls, $"{id}: installers have http(s) URLs"); k.True(hashes, $"{id}: installers have SHA256");
                d.AppendLine($"{id}: {det.Versions.Count} versions (latest {det.Versions.FirstOrDefault()}), ordered={ordered}, {inst.Count} installer(s), {det.Dependencies.Count} dependency(ies)");
            }
            k.True(found >= 2, $"at least two well-known packages exist in the catalog (found {found})");
            return k.Result("package details", d.ToString());
        }) { NeedsCatalog = true });

        l.Add(new("CAT-5", Cat, "Installer-pack planner: plan, dependencies, unknown id, architecture", "T4.1 T4.6 T4.7 T4.8", c =>
        {
            var db = Db(c); var q = new CatalogQueries(db);
            var k = new Checker(); var d = new StringBuilder();

            var plan = PackPlanner.Plan(db, new[] { new PackRequest("WinSCP.WinSCP"), new PackRequest("Rufus.Rufus") });
            d.AppendLine($"WinSCP+Rufus: {plan.Items.Count} item(s), {plan.Warnings.Count} warning(s)");
            k.True(plan.Items.Count >= 1, "known packages produce a plan");

            var bad = PackPlanner.Plan(db, new[] { new PackRequest("Not.Real.Package") });
            k.True(bad.Warnings.Count > 0 && bad.Items.Count == 0, "an unknown id gives a warning and no item");

            // حزمة لها اعتماديات
            string? withDeps = null;
            foreach (var id in new[] { "Microsoft.PowerToys", "Python.Python.3.12", "Git.Git", "VideoLAN.VLC", "7zip.7zip", "Microsoft.VCRedist.2015+.x64" })
            {
                var det = q.GetDetails(id);
                if (det != null && det.Dependencies.Count > 0) { withDeps = id; break; }
            }
            if (withDeps != null)
            {
                var on = PackPlanner.Plan(db, new[] { new PackRequest(withDeps) });
                var off = PackPlanner.Plan(db, new[] { new PackRequest(withDeps) }, new PackOptions { IncludeDependencies = false });
                d.AppendLine($"{withDeps}: {on.Items.Count} with dependencies, {off.Items.Count} without");
                k.True(on.Items.Count > off.Items.Count || on.Items.Any(i => i.IsDependency), "dependencies are added when enabled");
                k.True(off.Items.All(i => !i.IsDependency), "no dependency items when disabled");
            }
            else d.AppendLine("No package with dependencies among the usual candidates: dependency planning not exercised.");

            var x86 = PackPlanner.Plan(db, new[] { new PackRequest("WinSCP.WinSCP") }, new PackOptions { Architectures = new[] { "x86" } });
            d.AppendLine($"WinSCP x86: {x86.Items.Count} item(s)" + (x86.Items.Count > 0 ? $", arch {x86.Items[0].Installer.Arch}" : ""));
            if (x86.Items.Count > 0) k.True(x86.Items[0].Installer.Arch is "x86" or "neutral", "the architecture filter is honoured");
            return k.Result("pack planner", d.ToString());
        }) { NeedsCatalog = true });

        // --------------------------------------------------------------- Scan
        l.Add(new("SCN-1", Scn, "Scan this PC and match with the catalog", "T2.4 T2.6", c =>
        {
            if (!OperatingSystem.IsWindows()) return CheckOutcome.Skip("Windows only.");
            var db = Db(c);
            var sw = Stopwatch.StartNew();
            var results = ScanAndMatch(db);
            c.Items["scan.results"] = results;
            if (results.Count == 0) return CheckOutcome.Warn("The scan found no installed programs (unexpected on Windows).");
            var byConf = results.GroupBy(r => r.Confidence).OrderByDescending(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
            var byStatus = results.GroupBy(r => r.Status).Select(g => $"{g.Key}: {g.Count()}");
            var d = new StringBuilder();
            d.AppendLine($"Programs: {results.Count}  ·  scan+match {sw.ElapsedMilliseconds} ms");
            d.AppendLine("Match confidence: " + string.Join(", ", byConf));
            d.AppendLine("Status: " + string.Join(", ", byStatus));
            d.AppendLine("Newer than catalog: " + results.Count(r => r.NewerThanCatalog));
            var low = results.Where(r => r.Confidence == Confidence.None).Take(25).Select(r => $"  {r.App.DisplayName} {r.InstalledVersion} [{r.App.Publisher}]");
            d.AppendLine("Not matched (first 25):"); foreach (var s in low) d.AppendLine(s);
            return CheckOutcome.Info($"{results.Count} programs, {results.Count(r => r.PackageId != null)} matched, in {sw.ElapsedMilliseconds} ms", d.ToString());
        }) { NeedsCatalog = true, NeedsWindows = true });

        l.Add(new("SCN-2", Scn, "Known programs: Python, Everything, PowerToys, 7-Zip, Git, VLC", "T2.4", c =>
        {
            if (!c.Items.TryGetValue("scan.results", out var o)) return CheckOutcome.Skip("Needs the scan check.");
            var results = (List<MatchResult>)o;
            var d = new StringBuilder();
            foreach (var key in new[] { "Python", "Everything", "PowerToys", "7-Zip", "Git", "VLC" })
            {
                var hits = results.Where(r => r.App.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
                if (hits.Count == 0) { d.AppendLine($"{key}: not installed"); continue; }
                foreach (var r in hits)
                    d.AppendLine($"{r.App.DisplayName} | installed {r.InstalledVersion} | catalog {r.LatestVersion} | {r.Status}{(r.NewerThanCatalog ? " (newer than catalog)" : "")} | {r.Confidence} via {r.Method} | {r.PackageId}");
            }
            return CheckOutcome.Info("details of the programs from the test plan", d.ToString());
        }));

        l.Add(new("SCN-3", Scn, "Uninstall plans exist (nothing is executed)", "T2.6 T6.18", c =>
        {
            if (!c.Items.TryGetValue("scan.results", out var o)) return CheckOutcome.Skip("Needs the scan check.");
            var results = (List<MatchResult>)o;
            int plans = 0, silent = 0; var without = new List<string>();
            foreach (var r in results)
            {
                var p = UninstallPlanner.Plan(r.App);
                if (p == null) { without.Add(r.App.DisplayName); continue; }
                plans++;
                if (!p.Interactive) silent++;
            }
            var d = $"{plans} with a plan ({silent} non-interactive), {without.Count} without.\nWithout (first 25): " + string.Join("; ", without.Take(25));
            return CheckOutcome.Info($"{plans}/{results.Count} programs have an uninstall plan", d);
        }));

        l.Add(new("SCN-4", Scn, "Your library against the installed programs", "T8.7 T8.8", c =>
        {
            if (!c.Items.TryGetValue("scan.results", out var o)) return CheckOutcome.Skip("Needs the scan check.");
            var results = (List<MatchResult>)o;
            var lib = LibraryStore.Load();       // قراءة فقط من مكتبتك الحقيقية
            if (lib.Entries.Count == 0) return CheckOutcome.Skip("Your library is empty (scan folders on the Local page first).");
            var links = new LibraryMatcher(lib.Entries).Link(results.Select(r => (r.App, (string?)(r.InstalledVersion ?? r.App.DisplayVersion))));
            var rel = links.ByApp.Values.GroupBy(m => m.Relation).Select(g => $"{g.Key}: {g.Count()}");
            var d = new StringBuilder($"Library: {lib.Entries.Count} files. Installed programs found in it: {links.ByApp.Count} of {results.Count}.\nRelations: {string.Join(", ", rel)}\n");
            foreach (var kv in links.ByApp.Take(30)) d.AppendLine($"{kv.Key.DisplayName} => {kv.Value.ShortText} ({kv.Value.Confidence} via {kv.Value.Method})");
            return CheckOutcome.Info($"{links.ByApp.Count} installed programs have files in your library", d.ToString());
        }) { NeedsWindows = true });

        l.Add(new("SCN-5", Scn, "Updates list: write and read back", "T2.7 T3.5", c =>
        {
            if (!c.Items.TryGetValue("scan.results", out var o)) return CheckOutcome.Skip("Needs the scan check.");
            var results = (List<MatchResult>)o;
            var updates = results.Where(r => r.Status == UpdateStatus.UpdateAvailable && r.Confidence >= Confidence.Medium && r.PackageId != null)
                .Select(r => new UpdateEntry(r.PackageId!, r.LatestVersion, r.InstalledVersion, r.App.DisplayName)).ToList();
            var path = Path.Combine(c.TempDir, "updates.json");
            UpdateList.Write(path, updates);
            var back = UpdateList.Read(path);
            return back.Count == updates.Count
                ? CheckOutcome.Info($"{updates.Count} reliable update(s); the file reads back correctly", string.Join("\n", updates.Take(40).Select(u => $"{u.PackageId}  {u.Installed} -> {u.Version}")))
                : CheckOutcome.Fail($"Wrote {updates.Count} update(s) but read back {back.Count}");
        }));

        // ----------------------------------------------------------- Installers
        l.Add(new("INS-1", Ins, "Your installers folder: recognition, install plans, script", "T5.1 T6.1 T6.2 T6.3 T7.1", c =>
        {
            var folder = c.Options.InstallersFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return CheckOutcome.Skip("No installers folder is configured (Settings > Installers).");
            var db = Db(c);
            var sw = Stopwatch.StartNew();
            var items = InstallerIndexer.IndexFolder(db, folder);
            var t1 = sw.ElapsedMilliseconds; sw.Restart();
            var again = InstallerIndexer.IndexFolder(db, folder);
            var t2 = sw.ElapsedMilliseconds;
            var matched = items.Where(i => i.IsMatched).ToList();
            var d = new StringBuilder($"{items.Count} file(s): {matched.Count} recognised, {items.Count - matched.Count} unknown. First pass {t1} ms, second pass {t2} ms.\n");
            foreach (var m in matched.Take(5))
            {
                var plan = InstallPlanner.Plan(m.FilePath, m.Matches[0], new InstallOptions());
                d.AppendLine($"  {Path.GetFileName(m.FilePath)} ({m.Matches[0].PackageId} {m.Matches[0].Version}): {plan.CommandLine}");
            }
            string script = matched.Count > 0 ? InstallScriptBuilder.Build(matched.Select(m => (m, m.Matches[0])), folder) : "";
            var k = new Checker();
            if (matched.Count > 0)
            {
                k.True(script.Contains("$PSScriptRoot"), "generated script is relative to its own folder ($PSScriptRoot)");
                k.True(script.Contains("#Requires") || script.Contains("RunAsAdministrator") || script.Contains("Administrator"), "generated script asks for administrator rights");
            }
            k.Eq(items.Count, again.Count, "second pass sees the same files");
            return k.Ok ? CheckOutcome.Info($"{matched.Count}/{items.Count} installers recognised; second pass {t2} ms", d.ToString()) : CheckOutcome.Warn(k.Result("").Message, d.ToString());
        }) { NeedsCatalog = true });

        // --------------------------------------------------------------- Online
        l.Add(new("ONL-2", Onl, "The downloaded sample is recognised and gets a silent install plan", "T5.1 T6.1 T6.3", c =>
        {
            if (!c.Items.TryGetValue("pack.dir", out var o)) return CheckOutcome.Skip("The download check did not produce a folder.");
            var dir = (string)o; var db = Db(c);
            var items = InstallerIndexer.IndexFolder(db, dir);
            var k = new Checker(); var d = new StringBuilder();
            k.True(items.Count >= 1, "the downloaded file is indexed");
            k.True(items.All(i => i.IsMatched), "every downloaded installer is recognised by its SHA256");
            foreach (var m in items.Where(i => i.IsMatched))
            {
                var plan = InstallPlanner.Plan(m.FilePath, m.Matches[0], new InstallOptions());
                d.AppendLine($"{Path.GetFileName(m.FilePath)}: {plan.CommandLine}");
                k.True(!string.IsNullOrWhiteSpace(plan.CommandLine), "a plan is produced");
            }
            return k.Result("recognition and plan of the downloaded sample", d.ToString());
        }) { OnlineOnly = true, NeedsCatalog = true });
    }
}
