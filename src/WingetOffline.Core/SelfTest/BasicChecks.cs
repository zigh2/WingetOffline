using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Packing;

namespace WingetOffline.Core.SelfTest;

/// <summary>فحوص البيئة والبصمات والتصدير والاستيراد والاستثناءات: لا تحتاج كتالوجاً ولا ملفاتك.</summary>
internal static class BasicChecks
{
    private const string Env = "Environment", Hash = "Hashing", Exi = "Export/Import", Exc = "Exclusions";

    public static void Add(List<SelfCheck> l)
    {
        // ---------------------------------------------------------- Environment
        l.Add(new("ENV-1", Env, "Program, runtime and executable", "T9.1 T9.2", c =>
        {
            var r = SelfTestReport.NewFor(c.Options);
            bool singleFile = string.IsNullOrEmpty(typeof(object).Assembly.Location);
            var d = new StringBuilder();
            d.AppendLine($"Version: {r.AppVersion}");
            d.AppendLine($"Runtime: {r.Runtime}   Architecture: {r.Architecture}");
            d.AppendLine($"Executable: {r.ProcessPath}");
            d.AppendLine($"Executable size: {r.ProcessBytes / 1048576.0:0.##} MB");
            d.AppendLine($"Single-file bundle: {singleFile}");
            d.AppendLine($"Base directory: {AppContext.BaseDirectory}");
            d.AppendLine($"Shared runtime in use: {(string.IsNullOrEmpty(typeof(object).Assembly.Location) ? "(bundled or single-file)" : Path.GetDirectoryName(typeof(object).Assembly.Location))}");
            return CheckOutcome.Info($"{r.AppVersion} on {r.Runtime}, executable {r.ProcessBytes / 1048576.0:0.##} MB", d.ToString());
        }));

        l.Add(new("ENV-2", Env, "Operating system", "T7.4", c =>
        {
            var os = Environment.OSVersion.Version;
            if (!c.IsWindows) return CheckOutcome.Info($"Not Windows ({System.Runtime.InteropServices.RuntimeInformation.OSDescription}): Windows-only checks are skipped.");
            return os.Major < 10
                ? CheckOutcome.Warn($"Windows {os} is older than Windows 10; the program targets Windows 10/11.")
                : CheckOutcome.Pass($"Windows {os.Major}.{os.Minor} build {os.Build}");
        }));

        l.Add(new("ENV-3", Env, "Elevation", "T7.2", c =>
            CheckOutcome.Info(SelfTestReport.NewFor(c.Options).Elevated ? "Running as administrator." : "Running as a standard (non-elevated) user.")));

        l.Add(new("ENV-4", Env, "Data folder is writable", "T2.9", c =>
        {
            var dir = AppPaths.DataDir;
            var probe = Path.Combine(dir, ".selftest-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                File.WriteAllText(probe, "ok");
                var back = File.ReadAllText(probe);
                File.Delete(probe);
                return back == "ok"
                    ? CheckOutcome.Pass($"{dir} ({(AppPaths.IsPortable ? "portable mode" : "normal mode")})")
                    : CheckOutcome.Fail("Wrote a file but read back different content: " + dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CheckOutcome.Fail($"Cannot write to the data folder {dir}: {ex.Message}");
            }
        }));

        l.Add(new("ENV-5", Env, "Temp folder is writable and has space", "T7.1", c =>
        {
            var free = new DriveInfo(Path.GetPathRoot(c.TempDir)!).AvailableFreeSpace;
            return free < 500L * 1024 * 1024
                ? CheckOutcome.Warn($"Only {free / 1048576} MB free on the temp drive; some checks need up to ~400 MB.")
                : CheckOutcome.Pass($"{free / 1073741824.0:0.#} GB free on the temp drive");
        }));

        l.Add(new("ENV-6", Env, "Culture and number format", "T2.14", c =>
        {
            var ci = CultureInfo.CurrentCulture;
            return CheckOutcome.Info($"{ci.Name}: decimal '{ci.NumberFormat.NumberDecimalSeparator}', list separator '{ci.TextInfo.ListSeparator}'" +
                (ci.TextInfo.ListSeparator != "," ? " (Excel opens a comma CSV in one column here: use the Excel format or Data > From Text/CSV)" : ""),
                $"Culture: {ci.Name} ({ci.EnglishName})\nUI culture: {CultureInfo.CurrentUICulture.Name}\nRight-to-left: {ci.TextInfo.IsRightToLeft}");
        }));

        l.Add(new("ENV-7", Env, ".NET runtimes installed", "T9.8", c =>
        {
            var r = ProcRun.Run("dotnet", "--list-runtimes", 8000);
            if (!r.Started) return CheckOutcome.Skip("The 'dotnet' command is not available (normal for a standalone install).");
            return CheckOutcome.Info($"{r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} runtime(s) listed", r.Output);
        }));

        l.Add(new("ENV-8", Env, "Catalog cache keeps a single folder", "T2.11", c =>
        {
            var cache = AppPaths.Combine("cache");
            if (!Directory.Exists(cache)) return CheckOutcome.Info("No cache folder yet.");
            var n = Directory.GetDirectories(cache).Length;
            return n <= 1 ? CheckOutcome.Pass($"{n} folder(s) in {cache}") : CheckOutcome.Warn($"{n} folders in {cache}; only the newest should stay.");
        }));

        l.Add(new("ENV-9", Env, "Drives", "T8.16", c =>
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    sb.AppendLine(d.IsReady ? $"{d.Name}  {d.DriveType}  {d.DriveFormat}  {d.TotalSize / 1073741824.0:0.#} GB total, {d.AvailableFreeSpace / 1073741824.0:0.#} GB free"
                                            : $"{d.Name}  {d.DriveType}  (not ready)");
            }
            catch (IOException ex) { sb.AppendLine("Could not list drives: " + ex.Message); }
            return CheckOutcome.Info($"{sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} drive(s)", sb.ToString());
        }));

        // -------------------------------------------------------------- Hashing
        l.Add(new("HSH-1", Hash, "xxHash64 matches the reference implementation", "T8.26", c =>
        {
            var k = new Checker();
            var vectors = new (int Len, string Hex)[]
            {
                (0,"EF46DB3751D8E999"),(1,"A96C7F0CE858BBB7"),(3,"56E6957632A487F9"),(4,"C60D15B1E3FF8F04"),(7,"AFBEFC3D6C6F9A8E"),
                (8,"3DA5C7AA269683E0"),(15,"AE2A37EB9357CAA7"),(31,"4A74F3A1A39AD4A1"),(32,"8D57D6A4671CC43D"),(33,"62C9FD21ED857664"),
                (63,"5C320A0D2707057F"),(64,"7BBABBC45729D17E"),(100,"EFA0AD2D3E70C151"),(1000,"99594F4828043D35"),(4109,"4CB4DE5EEA111F8F"),
            };
            foreach (var (len, hex) in vectors) k.Eq(hex, XxHash64.HashHex(SelfTestFixtures.Det(len)), $"XXH64 of {len} bytes");
            k.Eq("44BC2CF5AD770999", XxHash64.HashHex(Encoding.ASCII.GetBytes("abc")), "XXH64('abc')");
            return k.Result("xxHash64 reference vectors");
        }));

        l.Add(new("HSH-2", Hash, "xxHash64 streaming gives the same result in any chunking", "T8.26", c =>
        {
            var k = new Checker();
            var data = SelfTestFixtures.Det(4109);
            foreach (var chunk in new[] { 1, 5, 31, 32, 33, 100, 1000, 4109 })
            {
                var x = new XxHash64();
                for (int i = 0; i < data.Length; i += chunk) x.Append(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
                k.Eq("4CB4DE5EEA111F8F", x.CurrentHashHex(), $"chunk size {chunk}");
            }
            return k.Result("streaming equals one-shot");
        }));

        l.Add(new("HSH-3", Hash, "SHA256 and MD5 known values", "T8.28", c =>
        {
            var k = new Checker();
            var abc = Encoding.ASCII.GetBytes("abc");
            k.Eq("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Convert.ToHexString(SHA256.HashData(abc)), "SHA256 abc");
            k.Eq("900150983CD24FB0D6963F7D28E17F72", Convert.ToHexString(MD5.HashData(abc)), "MD5 abc");
            k.Eq("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), "SHA256 empty");
            return k.Result("SHA256/MD5 reference values");
        }));

        l.Add(new("HSH-4", Hash, "File hashing: whole file and first part, all algorithms", "T8.26 T8.28", c =>
        {
            var k = new Checker();
            var data = SelfTestFixtures.Det(5000);
            var path = Path.Combine(c.TempDir, "hash.bin");
            File.WriteAllBytes(path, data);
            var head = data.Take(1000).ToArray();
            k.Eq(XxHash64.HashHex(data), LibraryHasher.Compute(path, LibraryHashAlgorithm.XxHash64, null), "xxHash64 whole");
            k.Eq(XxHash64.HashHex(head), LibraryHasher.Compute(path, LibraryHashAlgorithm.XxHash64, 1000), "xxHash64 first 1000 bytes");
            k.Eq(Convert.ToHexString(SHA256.HashData(data)), LibraryHasher.Compute(path, LibraryHashAlgorithm.Sha256, null), "SHA256 whole");
            k.Eq(Convert.ToHexString(SHA256.HashData(head)), LibraryHasher.Compute(path, LibraryHashAlgorithm.Sha256, 1000), "SHA256 first 1000 bytes");
            k.Eq(Convert.ToHexString(MD5.HashData(data)), LibraryHasher.Compute(path, LibraryHashAlgorithm.Md5, null), "MD5 whole");
            k.Eq(Convert.ToHexString(MD5.HashData(head)), LibraryHasher.Compute(path, LibraryHashAlgorithm.Md5, 1000), "MD5 first 1000 bytes");
            k.Eq(XxHash64.HashHex(data), LibraryHasher.Compute(path, LibraryHashAlgorithm.XxHash64, 999999), "first N larger than the file = whole file");
            return k.Result("hashing a real file");
        }));

        l.Add(new("HSH-5", Hash, "Hashing speed on this machine", "T8.26 T7.1", c =>
        {
            var path = Path.Combine(c.TempDir, "speed.bin");
            var block = SelfTestFixtures.Det(1 << 20);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                for (int i = 0; i < 32; i++) fs.Write(block);
            double Mbps(LibraryHashAlgorithm a)
            {
                var sw = Stopwatch.StartNew();
                LibraryHasher.Compute(path, a, null, c.Ct);
                return 32.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            }
            var xx = Mbps(LibraryHashAlgorithm.XxHash64); var sha = Mbps(LibraryHashAlgorithm.Sha256); var md = Mbps(LibraryHashAlgorithm.Md5);
            return CheckOutcome.Info($"xxHash64 {xx:0} MB/s · SHA256 {sha:0} MB/s · MD5 {md:0} MB/s (32 MB file, mostly cached)");
        }));

        // --------------------------------------------------------- Export/Import
        l.Add(new("EXI-1", Exi, "Export then import round trip: 3 pages x 3 formats", "T2.7 T2.13 T2.14 T2.16 T10.1 T10.5 T10.6", c =>
        {
            var k = new Checker();
            foreach (var ext in new[] { ".json", ".csv", ".xlsx" })
            {
                var inst = Roundtrip(c, ExiData.Installed(), "installed" + ext);
                k.Eq(ExportSpecs.KindInstalled, inst.Kind ?? "", $"installed{ext} kind");
                var rows = SnapshotMapper.ToInstalled(inst);
                k.Eq(4, rows.Count, $"installed{ext} rows");
                if (rows.Count == 4)
                {
                    k.Eq("7-Zip 24.08 (x64)", rows[0].Result.App.DisplayName, $"installed{ext} name");
                    k.Eq("25.01", rows[0].Result.LatestVersion ?? "", $"installed{ext} latest");
                    k.Eq("Newer 25.01", rows[0].Library?.ShortText ?? "", $"installed{ext} library text");
                    k.Eq("برنامج عربي, بفاصلة", rows[1].Result.App.DisplayName, $"installed{ext} Arabic name");
                    k.True(rows[3].Ignored, $"installed{ext} ignored flag");
                }

                var upd = Roundtrip(c, ExiData.Updates(), "updates" + ext);
                k.Eq(2, SnapshotMapper.ToUpdates(upd).Count, $"updates{ext} rows");

                var lib = Roundtrip(c, ExiData.Library(), "library" + ext);
                var (entries, links) = SnapshotMapper.ToLibrary(lib);
                k.Eq(2, entries.Count, $"library{ext} rows");
                if (entries.Count == 2)
                {
                    k.Eq(1_500_000L, entries[0].Size, $"library{ext} size");
                    k.Eq("99594F4828043D35", entries[0].XxHash ?? "", $"library{ext} xxHash");
                    k.Eq("تطبيق كبير 2.0.iso", entries[1].FileName, $"library{ext} Arabic file name");
                    k.Eq(5_000_000_000L, entries[1].Size, $"library{ext} large size");
                    k.True(links.ContainsKey(entries[0].Path), $"library{ext} installed link");
                }
            }
            return k.Result("all 9 round trips");
        }));

        l.Add(new("EXI-2", Exi, "Every page rejects every other page's export (18 combinations)", "T10.2 T10.3", c =>
        {
            var k = new Checker();
            var kinds = new[] { ExportSpecs.KindInstalled, ExportSpecs.KindUpdates, ExportSpecs.KindLibrary };
            foreach (var ext in new[] { ".json", ".csv", ".xlsx" })
                foreach (var made in kinds)
                {
                    var table = made switch { ExportSpecs.KindInstalled => ExiData.Installed(), ExportSpecs.KindUpdates => ExiData.Updates(), _ => ExiData.Library() };
                    var imported = Roundtrip(c, table, $"x-{made}{ext}");
                    foreach (var page in kinds)
                    {
                        if (page == made) { try { imported.RequireKind(page); k.True(true, ""); } catch (ImportException ex) { k.Fail($"{made}{ext} rejected on its own page: {ex.Message}"); } continue; }
                        try { imported.RequireKind(page); k.Fail($"{made}{ext} was ACCEPTED on {page}"); }
                        catch (ImportException ex)
                        {
                            k.True(ex.Message.Contains($"exported from the {ExportSpecs.PageName(made)} page") && ex.Message.Contains($"cannot be imported on the {ExportSpecs.PageName(page)} page"),
                                   $"{made}{ext} on {page}: unclear message '{ex.Message.Split('\n')[0]}'");
                        }
                    }
                }
            return k.Result("page separation holds in every format");
        }));

        l.Add(new("EXI-3", Exi, "CSV details: UTF-8 BOM, quoting, formula guard", "T2.14", c =>
        {
            var k = new Checker();
            var t = new ExportTable("x", new[] { new ExportColumn("A"), new ExportColumn("B") });
            t.Add("=HYPERLINK(\"http://evil\")", "a,b");
            t.Add("-5", "say \"hi\"\nsecond");
            var path = Path.Combine(c.TempDir, "details.csv");
            TableExporter.Write(path, t);
            var bytes = File.ReadAllBytes(path);
            k.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM present");
            var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            k.True(text.Contains("\"'=HYPERLINK("), "formula neutralised");
            k.True(text.Contains("\r\n-5,"), "plain negative number untouched");
            k.True(text.Contains("\"a,b\""), "comma quoted");
            k.True(text.Contains("\"say \"\"hi\"\"\nsecond\""), "quotes and newline escaped");
            return k.Result("CSV writer");
        }));

        l.Add(new("EXI-4", Exi, "XLSX integrity and Excel-style file reading", "T2.13 T10.7", c =>
        {
            var k = new Checker();
            var path = Path.Combine(c.TempDir, "integrity.xlsx");
            var t = new ExportTable("x", new[] { new ExportColumn("الاسم"), new ExportColumn("Size"), new ExportColumn("Date") }, "Sheet <1>");
            t.Add("برنامج", 1234567L, new DateTime(2026, 11, 8, 14, 30, 0));
            t.Add("中文 日本語 Русский", 12.5, null);
            TableExporter.Write(path, t);
            using (var zip = ZipFile.OpenRead(path))
            {
                foreach (var part in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
                    k.True(zip.GetEntry(part) != null, $"part {part}");
                foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
                {
                    try { using var s = e.Open(); System.Xml.Linq.XDocument.Load(s); }
                    catch (Exception ex) { k.Fail($"{e.FullName} is not valid XML: {ex.Message}"); }
                }
                using var sr = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
                var sheet = sr.ReadToEnd();
                k.True(sheet.Contains("برنامج") && sheet.Contains("中文 日本語 Русский"), "Arabic, Chinese and Russian text written");
                k.True(sheet.Contains("<v>1234567</v>"), "real number cell");
            }

            // ورقة أغلب نصوصها عربي تتحول تلقائياً إلى اتجاه اليمين لليسار، وغيرها لا
            var ar = new ExportTable("x", new[] { new ExportColumn("الاسم"), new ExportColumn("الإصدار") });
            ar.Add("برنامج", "١٫٢");
            var arPath = Path.Combine(c.TempDir, "arabic.xlsx");
            TableExporter.Write(arPath, ar);
            using (var z2 = ZipFile.OpenRead(arPath))
            using (var sr2 = new StreamReader(z2.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8))
                k.True(sr2.ReadToEnd().Contains("rightToLeft=\"1\""), "Arabic-majority sheet is right-to-left");
            using (var z3 = ZipFile.OpenRead(path))
            using (var sr3 = new StreamReader(z3.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8))
                k.True(!sr3.ReadToEnd().Contains("rightToLeft"), "mixed-language sheet is left-to-right");

            // نمط ما يكتبه Excel: sharedStrings + خلايا متجاورة (يكشف خطأ تخطي العقد في قارئ XML)
            var excel = Path.Combine(c.TempDir, "excel.xlsx");
            using (var z = ZipFile.Open(excel, ZipArchiveMode.Create))
            {
                void Add(string name, string xml) { using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(xml); }
                Add("xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Package ID</t></si><si><t>Name</t></si><si><t>Installed</t></si><si><t>Latest</t></si><si><t>Git.Git</t></si><si><r><t>Gi</t></r><r><t>t</t></r></si></sst>");
                Add("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>4</v></c><c r=\"B2\" t=\"s\"><v>5</v></c><c r=\"C2\"><v>2.46</v></c><c r=\"D2\"><v>2.47</v></c></row></sheetData></worksheet>");
            }
            var imp = TableImporter.Read(excel);
            k.Eq(ExportSpecs.KindUpdates, imp.Kind ?? "", "Excel-style file kind");
            if (imp.Rows.Count == 1)
            {
                k.Eq("Git", imp.Rows[0]["name"], "rich-text cell");
                k.Eq("2.46", imp.Rows[0]["installed"], "number next to shared strings");
                k.Eq("2.47", imp.Rows[0]["version"], "last cell not lost");
            }
            else k.Fail($"Excel-style file gave {imp.Rows.Count} rows instead of 1");
            return k.Result("XLSX writer and reader");
        }));

        l.Add(new("EXI-5", Exi, "Bad files give readable errors (no crash)", "T10.8", c =>
        {
            var k = new Checker();
            void Expect(string name, string content, string expectedPart)
            {
                var p = Path.Combine(c.TempDir, name);
                File.WriteAllText(p, content);
                try { var t = TableImporter.Read(p); t.RequireKind(ExportSpecs.KindInstalled); k.Fail($"{name} was accepted"); }
                catch (ImportException ex) { k.True(ex.Message.Contains(expectedPart, StringComparison.OrdinalIgnoreCase), $"{name}: message '{ex.Message.Split('\n')[0]}' lacks '{expectedPart}'"); }
                catch (Exception ex) { k.Fail($"{name}: unexpected {ex.GetType().Name}"); }
            }
            Expect("a.txt", "x", "Unsupported");
            Expect("empty.csv", "", "empty");
            Expect("random.csv", "colA,colB\r\n1,2\r\n", "not an export");
            Expect("bad.json", "{ not json", "could not be read");
            Expect("bad.xlsx", "not a zip", "could not be read");
            Expect("other.json", "{ \"kind\": \"bundle\", \"items\": [ { \"x\": 1 } ] }", "not an export");
            try { TableImporter.Read(Path.Combine(c.TempDir, "missing.json")); k.Fail("missing file accepted"); }
            catch (ImportException ex) { k.True(ex.Message.Contains("not found"), "missing file message"); }
            return k.Result("error handling");
        }));

        l.Add(new("EXI-6", Exi, "Older export formats still import", "T10.8", c =>
        {
            var k = new Checker();
            File.WriteAllText(Path.Combine(c.TempDir, "legacy.json"), "{ \"format\": 1, \"items\": [ { \"packageId\": \"Git.Git\", \"version\": \"2.47.1\", \"installed\": \"2.46.0\", \"name\": \"Git\" } ] }");
            var legacy = TableImporter.Read(Path.Combine(c.TempDir, "legacy.json"));
            k.Eq(ExportSpecs.KindUpdates, legacy.Kind ?? "", "kind-less updates JSON");

            var cols = ExportSpecs.InstalledColumns.Take(13).ToList();
            var t = new ExportTable(ExportSpecs.KindInstalled, cols, "Installed");
            t.Add("App", "Pub", "1.0", "2.0", "Update available", "High", "A.B", "Name", "Registry", "Machine", "", "", "");
            var csv = Path.Combine(c.TempDir, "v0992.csv");
            TableExporter.Write(csv, t);
            var imp = TableImporter.Read(csv);
            k.Eq(ExportSpecs.KindInstalled, imp.Kind ?? "", "13-column Installed CSV kind");
            k.Eq(1, SnapshotMapper.ToInstalled(imp).Count, "13-column Installed CSV rows");
            return k.Result("backward compatibility");
        }));

        l.Add(new("EXI-7", Exi, "The Updates JSON keeps the format Builder pack reads", "T2.7 T3.5", c =>
        {
            var k = new Checker();
            var path = Path.Combine(c.TempDir, "updates.json");
            UpdateList.Write(path, new[] { new UpdateEntry("Git.Git", "2.47.1", "2.46.0", "Git"), new UpdateEntry("A.B", "2", "1", "تطبيق") });
            var json = File.ReadAllText(path, Encoding.UTF8);
            k.True(json.Contains("\"format\": 1") && json.Contains("\"items\""), "format and items present");
            k.True(json.Contains("\"kind\": \"updates\""), "kind marker present");
            k.True(json.Contains("تطبيق") && !json.Contains("\\u062"), "Arabic written as-is");
            var read = UpdateList.Read(path);
            k.Eq(2, read.Count, "pack can read it back");
            if (read.Count == 2) k.Eq("تطبيق", read[1].Name ?? "", "Arabic name survives");
            return k.Result("updates JSON");
        }));

        // ------------------------------------------------------------ Exclusions
        l.Add(new("EXC-1", Exc, "Exclusion rules: paths, extensions, names, text and regex", "T8.18 T8.19", c =>
        {
            var k = new Checker();
            var cases = new (string Rule, string Path, bool Dir, bool Excluded)[]
            {
                (@"C:\windows\*", @"C:\Windows", true, true), (@"C:\windows\*", @"C:\WINDOWS\System32", true, true),
                (@"C:\windows\*", @"C:\Windows\System32\setup.exe", false, true), (@"C:\windows\*", @"C:\Windowsx\a.exe", false, false),
                (@"C:\program files\*", @"C:\Program Files\Git\x.exe", false, true), (@"C:\program files\*", @"C:\Program Files (x86)\Git\x.exe", false, false),
                ("C:/Windows/*", @"C:\Windows\a.exe", false, true),
                (".tmp", @"C:\a\b.TMP", false, true), (".tmp", @"C:\a\b.tmp.exe", false, false), (".tmp", @"C:\a\folder.tmp", true, false),
                ("setup*.log", @"C:\x\setup-1.log", false, true), ("setup*.log", @"C:\x\mysetup.log", false, false),
                (@"*\skip\*", @"C:\x\skip\a.exe", false, true),
                ("node_modules", @"C:\a\node_modules", true, true), ("node_modules", @"C:\a\node_modules\b.exe", false, true),
                (@"re:\\(temp|cache)\\", @"C:\Users\me\Temp\a.exe", false, true), (@"re:\\(temp|cache)\\", @"C:\Users\me\Templates\a.exe", false, false),
                (@"re:\.(iso|img)$", @"D:\Images\win.ISO", false, true), (@"re:\.(iso|img)$", @"D:\Images\win.isox", false, false),
            };
            foreach (var (rule, path, dir, excluded) in cases)
            {
                var ex = LibraryExclusions.Parse(rule);
                k.True(ex.Errors.Count == 0 && ex.Rules.Count == 1, $"'{rule}' parses to one rule");
                k.Eq(excluded, ex.Find(path, dir) != null, $"rule '{rule}' on '{path}'");
            }
            return k.Result("19 rule/path cases");
        }));

        l.Add(new("EXC-2", Exc, "Default exclusions cover Windows and Program Files", "T8.16 T8.18", c =>
        {
            var k = new Checker();
            var ex = LibraryExclusions.Parse(LibraryExclusions.DefaultText);
            k.Eq(0, ex.Errors.Count, "default text has no errors");
            k.True(ex.FindDirectory(@"C:\windows") != null, @"C:\windows");
            k.True(ex.FindDirectory(@"c:\PROGRAM FILES\Git") != null, "program files (case-insensitive)");
            k.True(ex.FindDirectory(@"C:\Program Files (x86)\Foo") != null, "program files (x86)");
            k.True(ex.FindFile(@"C:\Windows\Installer\x.msi") != null, "file under Windows");
            k.True(ex.FindFile(@"D:\Software\vlc.exe") == null, "other drive stays free");
            k.True(ex.FindDirectory(@"C:\Users\me\Downloads") == null, "Downloads stays free");
            return k.Result("defaults");
        }));

        l.Add(new("EXC-3", Exc, "A bad or catastrophic regex cannot break or hang a scan", "T8.20", c =>
        {
            var k = new Checker();
            var ex = LibraryExclusions.Parse("re:(\n.tmp\nre:(a+)+$");
            k.Eq(1, ex.Errors.Count, "one invalid regex reported");
            k.True(ex.Find(@"C:\a\x.tmp", false) != null, "other rules still work");
            var sw = Stopwatch.StartNew();
            _ = ex.Find(@"C:\" + new string('a', 60) + "b", false);
            k.True(sw.ElapsedMilliseconds < 3000, $"catastrophic regex returned in {sw.ElapsedMilliseconds} ms");
            return k.Result("regex safety");
        }));

        l.Add(new("EXC-4", Exc, "Environment variables in rules (%SystemRoot%)", "T8.18", c =>
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var ex = LibraryExclusions.Parse(@"%SystemRoot%\*");
            return ex.FindDirectory(Path.Combine(win, "System32")) != null
                ? CheckOutcome.Pass($"%SystemRoot% expanded to {win}")
                : CheckOutcome.Fail($"%SystemRoot%\\* did not exclude {win}\\System32");
        }) { NeedsWindows = true });
    }

    private static ImportedTable Roundtrip(SelfTestContext c, ExportTable t, string fileName)
    {
        var path = Path.Combine(c.TempDir, fileName);
        TableExporter.Write(path, t);
        return TableImporter.Read(path);
    }
}

/// <summary>جداول عيّنة بكل اللغات لفحوص التصدير والاستيراد.</summary>
internal static class ExiData
{
    public static ExportTable Installed()
    {
        var t = new ExportTable(ExportSpecs.KindInstalled, ExportSpecs.InstalledColumns, "Installed");
        t.Add("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", "25.01", "Update available", "High", "7zip.7zip", "ARP name", "Registry", "Machine", @"C:\Program Files\7-Zip", @"""C:\Program Files\7-Zip\Uninstall.exe"" /S", "", "Newer 25.01", "25.01", @"D:\lib\7z2501-x64.exe");
        t.Add("برنامج عربي, بفاصلة", "شركة", "1.0", "", "Not matched", "", "", "", "Registry", "User", "", "", "ملاحظة\nبسطرين", "", "", "");
        t.Add("Everything", "voidtools", "1.5.0", "1.4.1", "Newer than catalog", "Medium", "voidtools.Everything", "Name+Publisher", "Registry", "Machine", "", "", "", "Same", "1.5.0", @"D:\lib\Everything.exe");
        t.Add("Ignored App", "X", "1.0", "2.0", "Update ignored", "High", "X.App", "ProductCode", "Registry", "Machine", "", "", "", "", "", "");
        return t;
    }

    public static ExportTable Updates()
    {
        var t = new ExportTable(ExportSpecs.KindUpdates, ExportSpecs.UpdatesColumns, "Updates");
        t.Add("Git.Git", "Git", "2.46.0", "2.47.1");
        t.Add("VideoLAN.VLC", "VLC media player", "3.0.20", "3.0.21");
        return t;
    }

    public static ExportTable Library()
    {
        var a = new LibraryEntry
        {
            Path = @"D:\lib\7z2501-x64.exe", Root = @"D:\lib", Size = 1_500_000, ModifiedUtc = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            Kind = LibraryKind.Exe, ProductName = "7-Zip", Version = "25.01", Publisher = "Igor Pavlov", Architecture = "x64", InstallerType = "NSIS",
            Confidence = LibraryConfidence.High, Source = "pe", XxHash = "99594F4828043D35", ProductCode = "{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}",
        };
        var b = new LibraryEntry
        {
            Path = @"D:\lib\تطبيق كبير 2.0.iso", Root = @"D:\lib", Size = 5_000_000_000, ModifiedUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Kind = LibraryKind.Iso, Confidence = LibraryConfidence.Low, Source = "filename", PartialHash = "49B45332E280F187",
            PartialHashAlgorithm = "XXH64", PartialHashBytes = 10 * 1024 * 1024,
        };
        var links = new Dictionary<string, InstalledLink> { [a.Path] = new InstalledLink("7-Zip 24.08 (x64)", "24.08", LibraryRelation.LibraryNewer, Confidence.High) };
        return LibraryExport.ToTable(new[] { a, b }, links);
    }
}
