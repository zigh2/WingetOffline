using System.Text;
using System.Text.Json;
using WingetOffline.Core.Library;

namespace WingetOffline.Core.SelfTest;

/// <summary>فحوص تشغّل wgo وBuilder الفعليين كما تفعل أنت (وجودهما بجوار البرنامج أو في PATH).</summary>
internal static class ToolChecks
{
    private const string Cat = "Tools", Onl = "Online";

    private static string? Wgo(SelfTestContext c) => ProcRun.FindTool(c, "wgo");
    private static string? Builder(SelfTestContext c) => ProcRun.FindTool(c, "WingetOffline.Builder");
    private static string? Catalog(SelfTestContext c) =>
        !string.IsNullOrWhiteSpace(c.Options.CatalogPath) && File.Exists(c.Options.CatalogPath) ? c.Options.CatalogPath : null;

    private static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    private static string BaseVersion(string v) => v.Split('-', '+', ' ')[0].Trim().TrimStart('v', 'V');

    private static string Last(string text, int lines) =>
        string.Join("\n", text.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).TakeLast(lines));

    public static void Add(List<SelfCheck> l)
    {
        l.Add(new("TLS-1", Cat, "wgo and Builder are present and report the same version", "T10.14 T9.1", c =>
        {
            var wgo = Wgo(c); var bld = Builder(c);
            if (wgo == null && bld == null) return CheckOutcome.Skip("wgo and Builder were not found next to the program or on PATH.");
            var app = BaseVersion(BuildInfo.VersionOf(System.Reflection.Assembly.GetEntryAssembly()));
            var k = new Checker(); var d = new StringBuilder();
            foreach (var (name, path) in new[] { ("wgo", wgo), ("Builder", bld) })
            {
                if (path == null) { d.AppendLine($"{name}: not found"); continue; }
                var r = ProcRun.Run(path, "--version", 15000);
                var line = Last(r.Output, 1);
                d.AppendLine($"{name}: {path} -> {line}");
                k.True(r.Started && !r.TimedOut && r.ExitCode == 0, $"{name} --version exits 0 (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")})");
                k.True(BaseVersion(line.Split(' ').LastOrDefault() ?? "") == app || line.Contains(app), $"{name} version '{line}' matches the program version {app}");
            }
            return k.Ok ? CheckOutcome.Pass($"tools report version {app}", d.ToString()) : CheckOutcome.Warn(k.Result("").Message, d.ToString());
        }));

        l.Add(new("TLS-2", Cat, "wgo stats", "T3.1", c =>
        {
            var wgo = Wgo(c); var cat = Catalog(c);
            if (wgo == null) return CheckOutcome.Skip("wgo not found.");
            if (cat == null) return CheckOutcome.Skip("No catalog file path (use the catalog from Settings or pass it on the command line).");
            var r = ProcRun.Run(wgo, $"stats {Q(cat)}", 60000);
            return r.ExitCode == 0 && !r.TimedOut ? CheckOutcome.Info("wgo stats ran", r.Output) : CheckOutcome.Fail($"wgo stats failed (exit {r.ExitCode})", r.Output);
        }));

        l.Add(new("TLS-3", Cat, "wgo search: powertoys, 7 zip, c++, nonsense", "T3.2 T2.2", c =>
        {
            var wgo = Wgo(c); var cat = Catalog(c);
            if (wgo == null || cat == null) return CheckOutcome.Skip("Needs wgo and a catalog file.");
            var d = new StringBuilder(); var k = new Checker();
            foreach (var (q, expectHits) in new[] { ("powertoys", true), ("7 zip", true), ("c++", true), ("zzzqqqxxx", false) })
            {
                var r = ProcRun.Run(wgo, $"search {Q(cat)} {Q(q)} --limit 5", 60000);
                d.AppendLine($"--- search '{q}' (exit {r.ExitCode})").AppendLine(Last(r.Output, 8));
                k.True(r.ExitCode == 0 && !r.TimedOut, $"search '{q}' exits 0 (got {r.ExitCode})");
                if (expectHits) k.True(r.Output.Split('\n').Count(x => x.Trim().Length > 0) >= 2, $"search '{q}' returned something");
            }
            return k.Ok ? CheckOutcome.Info("four searches ran", d.ToString()) : CheckOutcome.Warn(k.Result("").Message, d.ToString());
        }));

        l.Add(new("TLS-4", Cat, "wgo audit-versions (does the version order match real release dates?)", "T3.3", c =>
        {
            var wgo = Wgo(c); var cat = Catalog(c);
            if (wgo == null || cat == null) return CheckOutcome.Skip("Needs wgo and a catalog file.");
            var r = ProcRun.Run(wgo, $"audit-versions {Q(cat)} --limit 40", 240000);
            if (r.TimedOut) return CheckOutcome.Warn("audit-versions did not finish in 4 minutes.", r.Output);
            return r.ExitCode == 0 ? CheckOutcome.Info("audit finished: read the numbers in the details", Last(r.Output, 60)) : CheckOutcome.Warn($"audit-versions exit {r.ExitCode}", Last(r.Output, 60));
        }));

        l.Add(new("TLS-5", Cat, "wgo scan --updates --json and --export-updates", "T3.4 T3.5", c =>
        {
            var wgo = Wgo(c); var cat = Catalog(c);
            if (wgo == null || cat == null) return CheckOutcome.Skip("Needs wgo and a catalog file.");
            var k = new Checker();
            var json = ProcRun.Run(wgo, $"scan {Q(cat)} --updates --json", 180000);
            k.True(json.ExitCode == 0, $"scan --json exit {json.ExitCode}");
            var start = json.Output.IndexOf('[');
            try { using var doc = JsonDocument.Parse(start >= 0 ? json.Output[start..] : "x"); k.True(doc.RootElement.ValueKind == JsonValueKind.Array, "JSON array"); }
            catch (JsonException) { k.Fail("scan --json output is not valid JSON"); }
            var file = Path.Combine(c.TempDir, "updates-from-wgo.json");
            var ex = ProcRun.Run(wgo, $"scan {Q(cat)} --export-updates {Q(file)}", 180000);
            k.True(ex.ExitCode == 0, $"--export-updates exit {ex.ExitCode}");
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                k.True(text.Contains("\"items\""), "updates file has items");
            }
            return k.Result("scan JSON and updates export");
        }) { NeedsWindows = true });

        l.Add(new("TLS-6", Cat, "wgo library scan / list / export on a sample folder", "T8.13 T8.25", c =>
        {
            var wgo = Wgo(c);
            if (wgo == null) return CheckOutcome.Skip("wgo not found.");
            var k = new Checker();
            var dir = c.NewDir("cli-lib");
            File.WriteAllBytes(Path.Combine(dir, "AcmeWidget-4.2.1.msi"), SelfTestFixtures.AcmeMsi());
            File.WriteAllText(Path.Combine(dir, "Tool-1.0.0.exe"), "x");
            var work = c.NewDir("cli-work");
            var xlsx = Path.Combine(work, "lib.xlsx");

            // مكتبة wgo مستقلة عن مكتبتك الحقيقية: نوجّه مجلد بياناتها إلى مجلد مؤقت (WINGETOFFLINE_DATA)
            var isolated = new Dictionary<string, string> { ["WINGETOFFLINE_DATA"] = Path.Combine(work, "data") };   // لا نلمس مكتبتك الحقيقية
            var scan = ProcRun.Run(wgo, $"library scan {Q(dir)} --export {Q(xlsx)}", 120000, work, isolated);
            k.True(scan.ExitCode == 0, $"library scan exit {scan.ExitCode}");
            k.True(File.Exists(xlsx), "xlsx export created");
            var list = ProcRun.Run(wgo, $"library list --type msi", 60000, work, isolated);
            k.True(list.ExitCode == 0 && list.Output.Contains("Acme Widget Pro"), "library list --type msi shows the MSI");
            return k.Result("wgo library", Last(scan.Output + "\n" + list.Output, 12));
        }));

        l.Add(new("TLS-7", Cat, "Builder pack --dry-run: plan, dependencies, bad id, architecture", "T4.1 T4.6 T4.7 T4.8", c =>
        {
            var bld = Builder(c); var cat = Catalog(c);
            if (bld == null || cat == null) return CheckOutcome.Skip("Needs the Builder and a catalog file.");
            var k = new Checker(); var d = new StringBuilder();
            var out1 = c.NewDir("pack-dry");
            var plan = ProcRun.Run(bld, $"pack --catalog {Q(cat)} --ids WinSCP.WinSCP,Rufus.Rufus --out {Q(out1)} --dry-run", 120000);
            d.AppendLine("--- plan").AppendLine(Last(plan.Output, 12));
            k.True(plan.ExitCode == 0, $"dry run exit {plan.ExitCode}");
            k.True(plan.Output.Contains("DRY RUN"), "says nothing was downloaded");
            k.True(plan.Output.Contains("WinSCP.WinSCP") && plan.Output.Contains("Rufus.Rufus"), "both packages planned");
            k.True(Directory.GetFiles(out1).Length == 0, "dry run created no files");

            var bad = ProcRun.Run(bld, $"pack --catalog {Q(cat)} --ids Not.Real.Package --out {Q(out1)} --dry-run", 120000);
            d.AppendLine("--- bad id").AppendLine(Last(bad.Output, 6));
            k.True(bad.Output.Contains("WARNING") || bad.ExitCode != 0, "an unknown package id is reported, not ignored");

            var x86 = ProcRun.Run(bld, $"pack --catalog {Q(cat)} --ids WinSCP.WinSCP --arch x86 --out {Q(out1)} --dry-run", 120000);
            d.AppendLine("--- x86").AppendLine(Last(x86.Output, 6));
            k.True(x86.ExitCode is 0 or 3, $"--arch x86 exit {x86.ExitCode}");
            return k.Ok ? CheckOutcome.Info("pack plans behave", d.ToString()) : CheckOutcome.Warn(k.Result("").Message, d.ToString());
        }));

        l.Add(new("TLS-8", Cat, "wgo index: unknown files, Arabic and spaced folders, cache speed, JSON", "T5.2 T5.3 T5.4 T5.5", c =>
        {
            var wgo = Wgo(c); var cat = Catalog(c);
            if (wgo == null || cat == null) return CheckOutcome.Skip("Needs wgo and a catalog file.");
            var k = new Checker();
            var root = c.NewDir("index-sample");
            var plain = Path.Combine(root, "random-1.0.exe");
            var spaced = Path.Combine(root, "مجلد عربي with spaces"); Directory.CreateDirectory(spaced);
            File.WriteAllBytes(plain, SelfTestFixtures.Det(5000));
            File.WriteAllBytes(Path.Combine(spaced, "تطبيق عشوائي 2.0.exe"), SelfTestFixtures.Det(7000));
            File.WriteAllBytes(Path.Combine(spaced, "another one.msi"), SelfTestFixtures.AcmeMsi());
            var isolated = new Dictionary<string, string> { ["WINGETOFFLINE_DATA"] = Path.Combine(c.TempDir, "index-data") };   // لا نلمس ذاكرة البصمات الحقيقية

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r1 = ProcRun.Run(wgo, $"index {Q(cat)} {Q(root)} --json", 120000, null, isolated);
            long t1 = sw.ElapsedMilliseconds; sw.Restart();
            var r2 = ProcRun.Run(wgo, $"index {Q(cat)} {Q(root)} --json", 120000, null, isolated);
            long t2 = sw.ElapsedMilliseconds;
            k.True(r1.ExitCode == 0 && r2.ExitCode == 0, $"wgo index exits 0 (got {r1.ExitCode}/{r2.ExitCode})");
            var start = r1.Output.IndexOf('[');
            if (start < 0) start = r1.Output.IndexOf('{');
            try
            {
                using var doc = JsonDocument.Parse(start >= 0 ? r1.Output[start..] : "x");
                k.True(doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object, "output is JSON");
                var text = doc.RootElement.GetRawText();
                k.True(text.Contains("random-1.0.exe") && text.Contains("تطبيق عشوائي 2.0.exe") || text.Contains("\\u062"), "files in the Arabic/spaced folder are listed");
                k.True(!text.Contains("\"matched\":true", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"isMatched\":true", StringComparison.OrdinalIgnoreCase), "random files are not claimed as recognised");
            }
            catch (JsonException) { k.Fail("wgo index --json did not print valid JSON"); }
            return k.Ok ? CheckOutcome.Pass($"index ran twice ({t1} ms then {t2} ms with the hash cache)", Last(r1.Output, 14)) : CheckOutcome.Warn(k.Result("").Message, Last(r1.Output, 20));
        }));

        // ------------------------------------------------------------- Online
        l.Add(new("ONL-1", Onl, "Builder downloads a small program, verifies it, reuses it, repairs a corrupted file", "T4.2 T4.3 T4.4", c =>
        {
            var bld = Builder(c); var cat = Catalog(c);
            if (bld == null || cat == null) return CheckOutcome.Skip("Needs the Builder and a catalog file.");
            var k = new Checker(); var d = new StringBuilder();
            var dir = c.NewDir("pack-online");
            string Args() => $"pack --catalog {Q(cat)} --ids Rufus.Rufus --no-deps --out {Q(dir)}";

            var r1 = ProcRun.Run(bld, Args(), 600000);
            d.AppendLine("--- first run").AppendLine(Last(r1.Output, 8));
            k.True(r1.ExitCode == 0, $"download exit {r1.ExitCode} (needs internet)");
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".json") && !f.EndsWith(".ps1")).ToList();
            k.True(files.Count >= 1, "an installer file was downloaded");
            k.True(File.Exists(Path.Combine(dir, "pack-manifest.json")), "pack-manifest.json written");
            c.Items["pack.dir"] = dir;
            if (files.Count == 0) return CheckOutcome.Fail("nothing was downloaded: " + (k.Ok ? "" : k.Result("").Message), d.ToString());

            var r2 = ProcRun.Run(bld, Args(), 600000);
            d.AppendLine("--- second run").AppendLine(Last(r2.Output, 6));
            k.True(r2.ExitCode == 0, $"second run exit {r2.ExitCode}");
            k.True(r2.Output.Contains("already downloaded"), "second run reuses the verified file");

            var victim = files[0];
            var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(victim)));
            using (var fs = new FileStream(victim, FileMode.Append)) fs.WriteByte(0x00);   // أفسد الملف ببايت زائد
            var r3 = ProcRun.Run(bld, Args(), 600000);
            d.AppendLine("--- after corrupting one file").AppendLine(Last(r3.Output, 6));
            var after = File.Exists(victim) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(victim))) : "(missing)";
            k.Eq(before, after, "a corrupted file is replaced by a verified copy");
            return k.Ok ? CheckOutcome.Pass("download, reuse and repair work", d.ToString()) : CheckOutcome.Fail(k.Result("").Message, d.ToString());
        }) { OnlineOnly = true });
    }
}
