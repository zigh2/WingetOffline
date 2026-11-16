using System.Diagnostics;
using System.Text;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Core.SelfTest;

/// <summary>فحوص المكتبة المحلية: على ملفات تجريبية مؤقتة، وعلى عيّنات حقيقية من جهازك (قراءة فقط)، وعميقة (اختيارية).</summary>
internal static class LibraryChecks
{
    private const string Cat = "Library";

    public static void Add(List<SelfCheck> l)
    {
        l.Add(new("LIB-1", Cat, "File readers: MSI, EXE, MSIX, ISO, ZIP, 7z", "T8.4 T8.14", c =>
        {
            var k = new Checker();
            var dir = c.NewDir("readers");

            File.WriteAllBytes(Path.Combine(dir, "AcmeWidget-4.2.1.msi"), SelfTestFixtures.AcmeMsi());
            SelfTestFixtures.WriteMsix(Path.Combine(dir, "Notes_3.4.5.0_x64.msix"));
            SelfTestFixtures.WriteIso(Path.Combine(dir, "Acme-Tools-2025.1.iso"));
            File.WriteAllBytes(Path.Combine(dir, "Notepad++.v8.7.1.Installer.x64.7z"), new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0, 0 });
            var pe = SelfTestFixtures.FindSmallPe();
            if (pe != null)
            {
                File.Copy(pe, Path.Combine(dir, "Tool-1.2.3-x64-setup.exe"));
                SelfTestFixtures.WriteZipWith(Path.Combine(dir, "portable.zip"), pe, "app/tool.exe");
            }

            LibraryEntry Id(string name) => LibraryScanner.Identify(new FileInfo(Path.Combine(dir, name)), dir);

            var msi = Id("AcmeWidget-4.2.1.msi");
            k.Eq("Acme Widget Pro", msi.ProductName ?? "", "MSI product name");
            k.Eq("4.2.1", msi.Version ?? "", "MSI version");
            k.Eq("Acme Software Ltd", msi.Publisher ?? "", "MSI manufacturer");
            k.Eq("{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}", msi.ProductCode ?? "", "MSI ProductCode");
            k.Eq(LibraryConfidence.High, msi.Confidence, "MSI confidence");

            var msix = Id("Notes_3.4.5.0_x64.msix");
            k.Eq("Contoso Notes", msix.ProductName ?? "", "MSIX display name");
            k.Eq("Contoso Ltd", msix.Publisher ?? "", "MSIX publisher CN");
            k.Eq("3.4.5.0", msix.Version ?? "", "MSIX version");

            var iso = Id("Acme-Tools-2025.1.iso");
            k.Eq("Acme Tools", iso.ProductName ?? "", "ISO name from file name");
            k.Eq("2025.1", iso.Version ?? "", "ISO version from file name");
            k.True((iso.Notes ?? "").Contains("MYTOOLS_2025"), "ISO volume label in notes");

            var sz = Id("Notepad++.v8.7.1.Installer.x64.7z");
            k.Eq("Notepad++", sz.ProductName ?? "", "7z name");
            k.Eq("x64", sz.Architecture ?? "", "7z architecture");

            if (pe != null)
            {
                var exe = Id("Tool-1.2.3-x64-setup.exe");
                k.True(exe.Source.Contains("pe"), "EXE read through the PE reader (source: " + exe.Source + ")");
                k.True(!string.IsNullOrEmpty(exe.ProductName), "EXE product name present");
                k.Eq("x64", exe.Architecture ?? "", "EXE architecture (file name x64 beats a 32-bit stub)");
                var zip = Id("portable.zip");
                k.True((zip.Notes ?? "").Contains("tool.exe"), "ZIP lists the inner exe");
            }
            return k.Result(pe != null ? "all file readers on generated files" : "file readers (no PE file found on this machine: EXE/ZIP parts skipped)");
        }));

        l.Add(new("LIB-2", Cat, "Real MSI files from this PC (read-only sample)", "T8.4", c =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");
            if (!Directory.Exists(dir)) return CheckOutcome.Skip(@"No C:\Windows\Installer folder here.");
            var files = SafeEnum(dir, "*.msi", 30);
            if (files.Count == 0) return CheckOutcome.Info("No cached MSI files found.");
            int ok = 0; var bad = new List<string>(); var sample = new StringBuilder();
            foreach (var f in files)
            {
                c.Ct.ThrowIfCancellationRequested();
                var facts = MsiReader.Read(f);
                if (facts?.ProductName != null) { ok++; if (sample.Length < 1500) sample.AppendLine($"{facts.ProductName} | {facts.Version} | {facts.Publisher} | {facts.Architecture}"); }
                else bad.Add(Path.GetFileName(f));
            }
            double rate = 100.0 * ok / files.Count;
            var details = sample + (bad.Count > 0 ? "\nUnreadable: " + string.Join(", ", bad) : "");
            return rate >= 90 ? CheckOutcome.Pass($"{ok}/{files.Count} real MSI files read ({rate:0}%)", details)
                              : CheckOutcome.Warn($"Only {ok}/{files.Count} real MSI files could be read ({rate:0}%): send the report so the reader can be improved", details);
        }) { NeedsWindows = true });

        l.Add(new("LIB-3", Cat, "Real EXE files from Program Files (read-only sample)", "T8.4", c =>
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(Directory.Exists).Distinct().ToList();
            var files = new List<string>();
            var sw = Stopwatch.StartNew();
            foreach (var r in roots)
                foreach (var f in SafeEnumRecursive(r, "*.exe", 80 - files.Count, sw, 8000)) files.Add(f);
            if (files.Count == 0) return CheckOutcome.Skip("No EXE files found under Program Files.");
            int read = 0, named = 0; var bad = new List<string>(); var sample = new StringBuilder();
            foreach (var f in files)
            {
                c.Ct.ThrowIfCancellationRequested();
                var facts = PeVersionReader.Read(f);
                if (facts != null) { read++; if (facts.ProductName != null) { named++; if (sample.Length < 1800) sample.AppendLine($"{Path.GetFileName(f)} | {facts.ProductName} | {facts.Version} | {facts.Publisher} | {facts.Architecture} | {facts.InstallerType}"); } }
                else bad.Add(Path.GetFileName(f));
            }
            double rate = 100.0 * read / files.Count;
            var details = sample + (bad.Count > 0 ? "\nNot readable as PE: " + string.Join(", ", bad.Take(20)) : "");
            return rate >= 90 ? CheckOutcome.Pass($"{read}/{files.Count} EXE files readable, {named} with a product name", details)
                              : CheckOutcome.Warn($"Only {read}/{files.Count} EXE files readable as PE ({rate:0}%)", details);
        }) { NeedsWindows = true });

        l.Add(new("LIB-4", Cat, "Library lifecycle: incremental scan, rename, offline folder, cancel", "T8.2 T8.3 T8.9 T8.10 T8.11", c =>
        {
            var k = new Checker();
            var root = c.NewDir("life");
            File.WriteAllBytes(Path.Combine(root, "A-1.0.0.msi"), SelfTestFixtures.AcmeMsi());
            File.WriteAllText(Path.Combine(root, "B-2.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "تطبيق-1.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "ignored");
            var store = new LibraryStore(Path.Combine(c.TempDir, "life.json"));
            var cache = LibraryFileCache.Load(Path.Combine(c.TempDir, "life-cache.json"));

            var r1 = store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache });
            k.Eq(3, r1.Added, "first scan adds 3");
            var sw = Stopwatch.StartNew();
            var r2 = store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache });
            k.Eq(0, r2.Scanned, "second scan reads nothing");
            k.Eq(3, r2.Unchanged, "second scan: 3 unchanged");

            File.WriteAllText(Path.Combine(root, "B-2.0.0.exe"), "changed content!");
            File.WriteAllText(Path.Combine(root, "C-3.0.0.zip"), "z");
            var r3 = store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache });
            k.Eq(1, r3.Updated, "changed file re-read");
            k.Eq(1, r3.Added, "new file added");

            // نقل وإعادة تسمية: يُعرَّف من الذاكرة دون قراءة
            var sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
            File.Move(Path.Combine(root, "A-1.0.0.msi"), Path.Combine(sub, "totally-different.msi"));
            var r4 = store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache });
            k.Eq(1, r4.FromCache, "moved+renamed MSI recognised without re-reading");
            var moved = store.Entries.FirstOrDefault(e => e.FileName == "totally-different.msi");
            k.Eq("Acme Widget Pro", moved?.ProductName ?? "", "name comes from the content, not the new file name");

            File.Delete(Path.Combine(root, "C-3.0.0.zip"));
            k.Eq(1, store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache }).Removed, "deleted file removed");

            // قرص/مجلد غير متاح: سجلاته تبقى
            var other = c.NewDir("life-other");
            File.WriteAllText(Path.Combine(other, "o-1.0.0.exe"), "x");
            store.Sync(new[] { root, other }, new LibrarySyncOptions { Cache = cache });
            int before = store.Entries.Count;
            Directory.Delete(other, true);
            var r5 = store.Sync(new[] { root, other }, new LibrarySyncOptions { Cache = cache });
            k.Eq(1, r5.OfflineRoots.Count, "unreachable folder reported");
            k.Eq(before, store.Entries.Count, "unreachable folder keeps its entries");
            var kept = store.Entries.Count;
            store.Sync(new[] { root }, new LibrarySyncOptions { Cache = cache });
            k.Eq(kept - 1, store.Entries.Count, "removing a folder from the list drops its entries");

            // إلغاء: لا يغيّر شيئاً
            File.WriteAllText(Path.Combine(root, "new-1.0.0.exe"), "x");
            using (var cts = new CancellationTokenSource()) { cts.Cancel(); var rc = store.Sync(new[] { root }, ct: cts.Token); k.True(rc.Cancelled, "cancel flagged"); }
            k.True(store.Entries.All(e => e.FileName != "new-1.0.0.exe"), "cancelled scan changed nothing");

            // الحفظ والتحميل
            var reload = LibraryStore.Load(store.FilePath);
            k.Eq(store.Entries.Count, reload.Entries.Count, "library survives a reload");
            k.True(reload.Entries.Any(e => e.FileName == "تطبيق-1.0.0.exe"), "Arabic file name survives a reload");
            return k.Result($"lifecycle (second scan took {sw.ElapsedMilliseconds} ms)");
        }));

        l.Add(new("LIB-5", Cat, "Hash policies: xxHash64 / SHA256 / MD5, quick mode, reuse", "T8.26 T8.27 T8.28 T8.29 T8.30", c =>
        {
            var k = new Checker();
            var root = c.NewDir("hashes");
            var data = SelfTestFixtures.Det(1000);
            File.WriteAllBytes(Path.Combine(root, "big-1.0.0.exe"), data);
            File.WriteAllBytes(Path.Combine(root, "small-1.0.0.exe"), data.Take(80).ToArray());
            var store = new LibraryStore(Path.Combine(c.TempDir, "hashes.json"));
            var cache = LibraryFileCache.Load(Path.Combine(c.TempDir, "hashes-cache.json"));
            LibrarySyncResult Sync(LibraryHashPolicy p) => store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = p, Cache = cache });
            LibraryEntry E(string n) => store.Entries.First(e => e.FileName.StartsWith(n));

            var quick = new LibraryHashPolicy { Mode = LibraryHashMode.Quick, QuickThresholdBytes = 100, QuickHeadBytes = 40 };
            k.Eq(2, Sync(quick).Hashed, "quick mode hashes both files");
            k.Eq(XxHash64.HashHex(data.Take(40).ToArray()), E("big").PartialHash ?? "", "big file: xxHash64 of the first 40 bytes");
            k.Eq("XXH64", E("big").PartialHashAlgorithm ?? "", "default algorithm is xxHash64");
            k.True(E("big").Sha256 == null, "partial hash never fills SHA256 (no catalog match)");
            k.Eq(XxHash64.HashHex(data.Take(80).ToArray()), E("small").XxHash ?? "", "small file hashed completely");
            k.Eq(0, Sync(quick).Hashed, "second run reuses everything");

            var sha = new LibraryHashPolicy { Mode = LibraryHashMode.Full, Algorithm = LibraryHashAlgorithm.Sha256 };
            k.Eq(2, Sync(sha).Hashed, "switching to full SHA256 computes what is missing");
            k.Eq(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), E("big").Sha256 ?? "", "big file SHA256");
            var md5 = new LibraryHashPolicy { Mode = LibraryHashMode.Full, Algorithm = LibraryHashAlgorithm.Md5 };
            Sync(md5);
            k.Eq(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)), E("big").Md5 ?? "", "big file MD5");
            k.True(E("big").Sha256 != null, "SHA256 kept after computing MD5");

            // تغيّر الحجم يبطل الذاكرة
            File.WriteAllBytes(Path.Combine(root, "big-1.0.0.exe"), SelfTestFixtures.Det(1001));
            var rc = Sync(sha);
            k.Eq(0, rc.FromCache, "a changed file is not taken from the cache");
            k.True(rc.Hashed >= 1, "a changed file is hashed again");
            return k.Result("hash policies");
        }));

        l.Add(new("LIB-6", Cat, "Exclusions during a scan: folders are not even opened", "T8.18 T8.19 T8.21", c =>
        {
            var k = new Checker();
            var root = c.NewDir("excl");
            Directory.CreateDirectory(Path.Combine(root, "keep")); Directory.CreateDirectory(Path.Combine(root, "skip", "deep"));
            File.WriteAllText(Path.Combine(root, "keep", "a-1.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "skip", "b-1.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "skip", "deep", "c-1.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "disc.iso"), "x");
            var store = new LibraryStore(Path.Combine(c.TempDir, "excl.json"));
            var rules = LibraryExclusions.Parse("*\\skip\n.iso");
            var r1 = store.Sync(new[] { root }, new LibrarySyncOptions { Exclusions = rules });
            k.Eq(1, r1.Added, "only the non-excluded file is added");
            k.Eq(1, r1.ExcludedDirectories, "the skip folder is cut (its sub-folder is never visited)");
            k.Eq(1, r1.ExcludedFiles, "the .iso file is excluded");
            var r2 = store.Sync(new[] { root });
            k.Eq(3, r2.Added, "without rules everything appears");
            var r3 = store.Sync(new[] { root }, new LibrarySyncOptions { Exclusions = rules });
            k.Eq(3, r3.Removed, "adding the rule again removes their records");
            k.True(rules.FindDirectory(root) == null, "the scanned root itself is not excluded here");
            return k.Result("exclusions during a scan");
        }));

        l.Add(new("LIB-7", Cat, "Odd files: empty, truncated, garbage, locked", "T8.14", c =>
        {
            var k = new Checker();
            var root = c.NewDir("odd");
            File.WriteAllBytes(Path.Combine(root, "empty-1.0.exe"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(root, "truncated-1.0.msi"), SelfTestFixtures.AcmeMsi().Take(2000).ToArray());
            File.WriteAllText(Path.Combine(root, "garbage-1.0.zip"), "this is not a zip file at all");
            File.WriteAllText(Path.Combine(root, "tiny-1.0.iso"), "x");
            File.WriteAllText(Path.Combine(root, "fake-2.0.exe"), "MZ but nothing else");
            var locked = Path.Combine(root, "locked-1.0.exe");
            File.WriteAllText(locked, "x");
            var store = new LibraryStore(Path.Combine(c.TempDir, "odd.json"));
            LibrarySyncResult? r = null;
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { r = store.Sync(new[] { root }); }
                catch (Exception ex) { k.Fail($"scan threw {ex.GetType().Name}: {ex.Message}"); }
            }
            if (r != null)
            {
                k.Eq(6, store.Entries.Count, "all 6 odd files get a record instead of crashing the scan");
                k.Eq("truncated", store.Entries.First(e => e.FileName.StartsWith("truncated")).ProductName ?? "", "truncated MSI falls back to the file name");
                k.Eq(LibraryConfidence.Low, store.Entries.First(e => e.FileName.StartsWith("truncated")).Confidence, "…with low confidence");
            }
            return k.Result("odd files");
        }));

        l.Add(new("LIB-8", Cat, "A 300 MB ISO is identified from its header only", "T8.15", c =>
        {
            var free = new DriveInfo(Path.GetPathRoot(c.TempDir)!).AvailableFreeSpace;
            if (free < 2L * 1024 * 1024 * 1024) return CheckOutcome.Skip("Less than 2 GB free: not creating a large sample file.");
            var root = c.NewDir("iso");
            var path = Path.Combine(root, "Big-Disc-2.0.iso");
            SelfTestFixtures.WriteIso(path, 300L * 1024 * 1024);
            var sw = Stopwatch.StartNew();
            var e = LibraryScanner.Identify(new FileInfo(path), root);
            sw.Stop();
            var k = new Checker();
            k.Eq(LibraryKind.Iso, e.Kind, "kind");
            k.True((e.Notes ?? "").Contains("MYTOOLS_2025"), "volume label read");
            k.True(sw.ElapsedMilliseconds < 3000, $"read in {sw.ElapsedMilliseconds} ms (must not read the whole file)");
            return k.Result($"300 MB ISO identified in {sw.ElapsedMilliseconds} ms");
        }));

        l.Add(new("LIB-9", Cat, "Library vs installed programs: matching rules", "T8.7 T8.8", c =>
        {
            var k = new Checker();
            LibraryEntry Lib(string name, string? version, string? pub, string src = "pe", string? code = null, LibraryConfidence conf = LibraryConfidence.High, string? path = null) => new()
            { Path = path ?? $@"C:\lib\{name}-{version}.exe", ProductName = name, Version = version, Publisher = pub, Source = src, ProductCode = code, Confidence = conf };
            InstalledApp App(string name, string? pub, string? code = null) => new() { DisplayName = name, Publisher = pub, ProductCode = code, Source = "Registry" };

            var m = new LibraryMatcher(new[] { Lib("7-Zip", "24.08", "Igor Pavlov"), Lib("7-Zip", "25.01", "Igor Pavlov"), Lib("7-Zip", "23.01", "Igor Pavlov") });
            var older = m.Find(App("7-Zip 24.08 (x64)", "Igor Pavlov"), "24.08");
            k.Eq("25.01", older?.Best.Version ?? "", "highest library version chosen");
            k.Eq(LibraryRelation.LibraryNewer, older?.Relation ?? LibraryRelation.None, "library newer than installed");
            k.True(older?.HasSameVersion == true, "the same version is also in the library");
            k.Eq("Older 25.01", m.Find(App("7-Zip 26.04 (x64)", "Igor Pavlov"), "26.04")?.ShortText ?? "", "older relation text");
            k.True(new LibraryMatcher(new[] { Lib("Notepad", "1.0", "Other Co") }).Find(App("Notepad", "Microsoft Corporation"), "11.0") == null, "different publisher is not a match");
            k.True(new LibraryMatcher(new[] { Lib("Adobe Photoshop 2020", "21.1.1", "Adobe Systems Incorporated") }).Find(App("Adobe Photoshop 2025", "Adobe Inc."), "26.5") == null, "year in the name keeps products apart");
            var code = new LibraryMatcher(new[] { Lib("Acme Widget Pro", "4.2.1", "Acme", "msi", "{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}") })
                .Find(App("Another display name", null, "{701c0ab3-696f-43ea-94bc-db08c62d1fea}"), "4.2.1");
            k.Eq("ProductCode", code?.Method ?? "", "ProductCode match");
            return k.Result("library matching");
        }));

        l.Add(new("LIB-10", Cat, "Library export then import (snapshot) keeps every file", "T8.6 T10.6", c =>
        {
            var k = new Checker();
            var root = c.NewDir("exp");
            File.WriteAllBytes(Path.Combine(root, "AcmeWidget-4.2.1.msi"), SelfTestFixtures.AcmeMsi());
            File.WriteAllText(Path.Combine(root, "تطبيق-1.0.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "Tool-2.0.1-x64.zip"), "z");
            var store = new LibraryStore(Path.Combine(c.TempDir, "exp.json"));
            store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = new LibraryHashPolicy { Mode = LibraryHashMode.Full }, Cache = null });
            var table = LibraryExport.ToTable(store.Entries);
            foreach (var ext in new[] { ".json", ".csv", ".xlsx" })
            {
                var path = Path.Combine(c.TempDir, "lib" + ext);
                TableExporter.Write(path, table);
                var imp = TableImporter.Read(path);
                imp.RequireKind(ExportSpecs.KindLibrary);
                var (entries, _) = SnapshotMapper.ToLibrary(imp);
                k.Eq(store.Entries.Count, entries.Count, $"{ext}: same number of files");
                foreach (var orig in store.Entries)
                {
                    var back = entries.FirstOrDefault(e => e.Path == orig.Path);
                    if (back == null) { k.Fail($"{ext}: {orig.FileName} missing after import"); continue; }
                    k.Eq(orig.Size, back.Size, $"{ext}: {orig.FileName} size");
                    k.Eq(orig.Version ?? "", back.Version ?? "", $"{ext}: {orig.FileName} version");
                    k.Eq(orig.XxHash ?? "", back.XxHash ?? "", $"{ext}: {orig.FileName} hash");
                }
            }
            return k.Result("export/import of a real scanned library");
        }));

        l.Add(new("LIB-11", Cat, "Where a whole-computer search would look", "T8.16", c =>
        {
            var roots = LibraryRoots.Compute(true, false, Array.Empty<string>());
            var withRemovable = LibraryRoots.Compute(true, true, Array.Empty<string>());
            return roots.Count == 0
                ? CheckOutcome.Warn("No fixed drive found: a whole-computer search would have nothing to scan.")
                : CheckOutcome.Info($"{roots.Count} fixed drive(s): {string.Join(", ", roots)}; with removable: {withRemovable.Count}",
                    "Default exclusions skip: " + string.Join(" | ", LibraryExclusions.Parse(LibraryExclusions.DefaultText).Rules.Select(r => r.Text)));
        }));

        // ------------------------------------------------------ عميقة (اختيارية)
        l.Add(new("LIB-12", Cat, "DEEP: scan your library folders into a temporary library", "T7.1 T8.1 T8.4 T8.17", c =>
        {
            var roots = c.Options.LibraryFolders.Where(Directory.Exists).ToList();
            if (roots.Count == 0) return CheckOutcome.Skip("No library folder is configured (add one on the Local page).");
            var store = new LibraryStore(Path.Combine(c.TempDir, "deep.json"));
            var cache = LibraryFileCache.Load(Path.Combine(c.TempDir, "deep-cache.json"));
            var ex = LibraryExclusions.Parse(LibraryExclusions.DefaultText);
            var opts = new LibrarySyncOptions { Cache = cache, Exclusions = ex };

            var sw = Stopwatch.StartNew();
            var r1 = store.Sync(roots, opts, null, c.Ct);
            var t1 = sw.Elapsed.TotalSeconds;
            sw.Restart();
            var r2 = store.Sync(roots, opts, null, c.Ct);
            var t2 = sw.Elapsed.TotalSeconds;

            var byConf = store.Entries.GroupBy(e => e.Confidence).OrderByDescending(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
            var weakest = store.Entries.Where(e => e.Confidence <= LibraryConfidence.Low).Take(25)
                .Select(e => $"{e.Confidence,-6} {e.Kind,-5} {e.FileName}  =>  {e.ProductName} {e.Version} [{e.Source}]");
            var d = new StringBuilder();
            d.AppendLine($"Folders: {string.Join("; ", roots)}");
            d.AppendLine($"First scan: {t1:0.0} s ({r1.Added} files, {r1.Errors} read errors). Second scan: {t2:0.0} s ({r2.Unchanged} unchanged, {r2.Scanned} re-read).");
            d.AppendLine($"Total size: {store.Entries.Sum(e => e.Size) / 1e9:0.0} GB. Confidence: {string.Join(", ", byConf)}");
            d.AppendLine("Files identified with low confidence or not at all (up to 25):");
            foreach (var w in weakest) d.AppendLine("  " + w);
            var k = new Checker();
            k.True(r2.Scanned == 0, $"second scan re-read {r2.Scanned} file(s) (expected 0)");
            return k.Ok
                ? CheckOutcome.Pass($"{store.Entries.Count} files in {t1:0.0} s; the second scan took {t2:0.0} s", d.ToString())
                : CheckOutcome.Warn(k.Result("").Message, d.ToString());
        }) { DeepOnly = true });

        l.Add(new("LIB-13", Cat, "DEEP: 20-second whole-drive walk with the default exclusions", "T8.16 T8.17", c =>
        {
            var roots = LibraryRoots.Compute(true, false, Array.Empty<string>());
            var root = roots.FirstOrDefault(r => OperatingSystem.IsWindows() ? r.StartsWith(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", StringComparison.OrdinalIgnoreCase) : true) ?? roots.FirstOrDefault();
            if (root == null) return CheckOutcome.Skip("No fixed drive.");
            var store = new LibraryStore(Path.Combine(c.TempDir, "walk.json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var sw = Stopwatch.StartNew();
            var r = store.Sync(new[] { root }, new LibrarySyncOptions { Exclusions = LibraryExclusions.Parse(LibraryExclusions.DefaultText) }, null, cts.Token);
            double secs = sw.Elapsed.TotalSeconds;
            var msg = $"{root}: {r.Visited:N0} items in {secs:0.#} s ({r.Visited / Math.Max(0.1, secs):N0}/s), {r.ExcludedDirectories} folders skipped by the defaults, {r.Added} installer files so far, {(r.Cancelled ? "stopped at 20 s as planned" : "finished")}";
            return r.Visited > 0 ? CheckOutcome.Info(msg) : CheckOutcome.Warn("The walk visited nothing: " + msg);
        }) { DeepOnly = true });
    }

    private static List<string> SafeEnum(string dir, string pattern, int max)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                list.Add(f);
                if (list.Count >= max) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    private static IEnumerable<string> SafeEnumRecursive(string dir, string pattern, int max, Stopwatch sw, int maxMs)
    {
        int n = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0 && n < max && sw.ElapsedMilliseconds < maxMs)
        {
            var d = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>(), subs = Array.Empty<string>();
            try { files = Directory.EnumerateFiles(d, pattern); subs = Directory.EnumerateDirectories(d); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) { yield return f; if (++n >= max) yield break; }
            foreach (var s in subs) stack.Push(s);
        }
    }
}
