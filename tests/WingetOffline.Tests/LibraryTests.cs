using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-lib-" + Guid.NewGuid().ToString("N"));

    public LibraryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    private LibraryEntry Identify(string path) => LibraryScanner.Identify(new FileInfo(path), _dir);

    // ------------------------------------------------------- اسم الملف

    [Theory]
    [InlineData("VLC-3.0.21-win64.exe", "VLC", "3.0.21", "x64")]
    [InlineData("npp.8.7.1.Installer.x64.exe", "npp", "8.7.1", "x64")]
    [InlineData("VSCodeSetup-x64-1.95.0.exe", "VSCode", "1.95.0", "x64")]
    [InlineData("firefox_setup_131.0.2.exe", "firefox", "131.0.2", null)]
    [InlineData("Adobe Photoshop 2025.exe", "Adobe Photoshop 2025", null, null)]
    [InlineData("Git-2.47.1-64-bit.exe", "Git", "2.47.1", "x64")]
    [InlineData("Everything-1.4.1.1026.x86-Setup.exe", "Everything", "1.4.1.1026", "x86")]
    [InlineData("tool_1.2.3_arm64.msi", "tool", "1.2.3", "arm64")]
    [InlineData("setup.exe", null, null, null)]
    public void File_names_are_parsed(string file, string? name, string? version, string? arch)
    {
        var p = FileNameParser.Parse(file);
        Assert.Equal(name, p.Name);
        Assert.Equal(version, p.Version);
        Assert.Equal(arch, p.Architecture);
    }

    [Fact]
    public void Arabic_file_names_are_kept()
    {
        var p = FileNameParser.Parse("تطبيق المكتبة-2.1.0.exe");
        Assert.Equal("تطبيق المكتبة", p.Name);
        Assert.Equal("2.1.0", p.Version);
    }

    // --------------------------------------------------------- القراءة

    [Fact]
    public void Pe_version_resource_is_read_without_windows()
    {
        var f = PeVersionReader.Read(LibraryFixtures.PeFile);
        Assert.True(f != null, "PE should be readable");
        Assert.True(!string.IsNullOrEmpty(f!.ProductName), "ProductName");
        Assert.Contains("Microsoft", f.Publisher ?? "");
        Assert.True(!string.IsNullOrEmpty(f.Version), "Version");
    }

    [Fact]
    public void Non_pe_files_are_rejected_not_crashed()
    {
        File.WriteAllText(P("setup.exe"), "not a pe file at all, just text that is long enough to pass the size check....");
        Assert.Null(PeVersionReader.Read(P("setup.exe")));
        var e = Identify(P("setup.exe"));
        Assert.Equal(LibraryConfidence.None, e.Confidence);
        Assert.True(e.Error == null, "no error for unreadable-but-harmless files");
    }

    [Fact]
    public void Installer_type_is_sniffed_from_signatures()
    {
        var bytes = File.ReadAllBytes(LibraryFixtures.PeFile).Concat(Encoding.ASCII.GetBytes("\0Inno Setup Setup Data (6.2.2)\0")).ToArray();
        File.WriteAllBytes(P("app-setup.exe"), bytes);
        Assert.Equal("Inno Setup", PeVersionReader.Read(P("app-setup.exe"))!.InstallerType);
    }

    [Fact]
    public void Msi_properties_are_read_from_the_property_table()
    {
        File.WriteAllBytes(P("AcmeWidget-4.2.1.msi"), LibraryFixtures.AcmeMsi());
        var e = Identify(P("AcmeWidget-4.2.1.msi"));
        Assert.Equal("Acme Widget Pro", e.ProductName);
        Assert.Equal("4.2.1", e.Version);
        Assert.Equal("Acme Software Ltd", e.Publisher);
        Assert.Equal("{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}", e.ProductCode);
        Assert.Equal("{12345678-1234-1234-1234-123456789ABC}", e.UpgradeCode);
        Assert.Equal("x64", e.Architecture);
        Assert.Equal(LibraryConfidence.High, e.Confidence);
        Assert.Equal("msi", e.Source);
    }

    [Fact]
    public void Corrupt_msi_falls_back_to_the_file_name()
    {
        var bytes = LibraryFixtures.AcmeMsi().Take(2000).ToArray();   // ملف مقطوع
        File.WriteAllBytes(P("Broken-1.0.0.msi"), bytes);
        var e = Identify(P("Broken-1.0.0.msi"));
        Assert.Equal("Broken", e.ProductName);
        Assert.Equal("1.0.0", e.Version);
        Assert.Equal(LibraryConfidence.Low, e.Confidence);
    }

    [Fact]
    public void Msix_identity_is_read_and_publisher_cn_extracted()
    {
        using (var z = ZipFile.Open(P("Notes_3.4.5.0_x64.msix"), ZipArchiveMode.Create))
        {
            var en = z.CreateEntry("AppxManifest.xml");
            using var w = new StreamWriter(en.Open(), new UTF8Encoding(false));
            w.Write("<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">" +
                    "<Identity Name=\"Contoso.Notes\" Publisher=\"CN=Contoso Ltd, O=Contoso Ltd, C=US\" Version=\"3.4.5.0\" ProcessorArchitecture=\"x64\"/>" +
                    "<Properties><DisplayName>Contoso Notes</DisplayName><PublisherDisplayName>ms-resource:Pub</PublisherDisplayName></Properties></Package>");
        }
        var e = Identify(P("Notes_3.4.5.0_x64.msix"));
        Assert.Equal("Contoso Notes", e.ProductName);
        Assert.Equal("Contoso Ltd", e.Publisher);
        Assert.Equal("3.4.5.0", e.Version);
        Assert.Equal("Contoso.Notes", e.PackageName);
        Assert.Equal("x64", e.Architecture);
        Assert.Equal(LibraryConfidence.High, e.Confidence);
    }

    [Fact]
    public void Zip_with_an_exe_reports_the_inner_product()
    {
        using (var z = ZipFile.Open(P("portable.zip"), ZipArchiveMode.Create))
        {
            z.CreateEntryFromFile(LibraryFixtures.PeFile, "app/tool.exe");
            using var w = new StreamWriter(z.CreateEntry("app/readme.txt").Open());
            w.Write("hello");
        }
        var e = Identify(P("portable.zip"));
        Assert.Equal(LibraryKind.Zip, e.Kind);
        Assert.True(e.ProductName != null && e.ProductName.Contains(".NET"), "inner exe product name");
        Assert.Contains("tool.exe", e.Notes ?? "");
        Assert.Contains("ZIP", e.InstallerType ?? "");
    }

    [Fact]
    public void Iso_volume_label_is_read_from_the_first_64KB()
    {
        var pvd = new byte[2048];
        pvd[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1); pvd[6] = 1;
        void Put(int off, string text, int len) => Encoding.ASCII.GetBytes(text.PadRight(len)).CopyTo(pvd, off);
        Put(40, "MYTOOLS_2025", 32); Put(318, "ACME SOFT", 128); Put(813, "20250315120000", 16);
        var term = new byte[2048]; term[0] = 255; Encoding.ASCII.GetBytes("CD001").CopyTo(term, 1);
        File.WriteAllBytes(P("Acme-Tools-2025.1.iso"), new byte[32768].Concat(pvd).Concat(term).Concat(new byte[4096]).ToArray());

        var e = Identify(P("Acme-Tools-2025.1.iso"));
        Assert.Equal(LibraryKind.Iso, e.Kind);
        Assert.Equal("Acme Tools", e.ProductName);
        Assert.Equal("2025.1", e.Version);
        Assert.Equal("ACME SOFT", e.Publisher);
        Assert.Contains("MYTOOLS_2025", e.Notes ?? "");
    }

    [Fact]
    public void SevenZip_is_identified_from_the_name_and_says_so()
    {
        File.WriteAllBytes(P("Notepad++.v8.7.1.Installer.x64.7z"), new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0, 0 });
        var e = Identify(P("Notepad++.v8.7.1.Installer.x64.7z"));
        Assert.Equal(LibraryKind.SevenZip, e.Kind);
        Assert.Equal("Notepad++", e.ProductName);
        Assert.Equal("8.7.1", e.Version);
        Assert.Equal("x64", e.Architecture);
        Assert.Contains("not read", e.Notes ?? "");
    }

    // ------------------------------------------------- المزامنة التزايدية

    [Fact]
    public void Sync_is_incremental_and_tracks_changes()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "A-1.0.0.msi"), LibraryFixtures.AcmeMsi());
        File.WriteAllText(Path.Combine(root, "B-2.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "ignored");
        var store = new LibraryStore(P("library.json"));

        var r1 = store.Sync(new[] { root });
        Assert.Equal(2, r1.Added);
        Assert.Equal(2, store.Entries.Count);

        var r2 = store.Sync(new[] { root });
        Assert.Equal(0, r2.Scanned);
        Assert.Equal(2, r2.Unchanged);

        File.WriteAllText(Path.Combine(root, "B-2.0.0.exe"), "changed content!");
        File.WriteAllText(Path.Combine(root, "C-3.0.0.zip"), "z");
        var r3 = store.Sync(new[] { root });
        Assert.Equal(1, r3.Updated);
        Assert.Equal(1, r3.Added);
        Assert.Equal(1, r3.Unchanged);

        File.Delete(Path.Combine(root, "A-1.0.0.msi"));
        var r4 = store.Sync(new[] { root });
        Assert.Equal(1, r4.Removed);
        Assert.Equal(2, store.Entries.Count);

        var r5 = store.Sync(new[] { root }, new LibrarySyncOptions { Rescan = true });
        Assert.Equal(2, r5.Updated);
    }

    [Fact]
    public void Library_survives_a_reload_including_arabic_names()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "تطبيق-1.0.0.exe"), "x");
        var path = P("library.json");
        new LibraryStore(path).Sync(new[] { root });

        var raw = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("تطبيق", raw);

        var again = LibraryStore.Load(path);
        var e = Assert.Single(again.Entries);
        Assert.Equal("تطبيق", e.ProductName);
        Assert.Equal("1.0.0", e.Version);
        Assert.True(again.LastSyncUtc != null, "last sync is stored");

        // فحص بعد إعادة التحميل لا يعيد قراءة شيء
        Assert.Equal(0, again.Sync(new[] { root }).Scanned);
    }

    [Fact]
    public void Entries_of_an_offline_folder_are_kept_and_removed_folders_are_dropped()
    {
        var a = P("A"); var b = P("B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "a-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(b, "b-1.0.0.exe"), "x");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { a, b });
        Assert.Equal(2, store.Entries.Count);

        Directory.Delete(b, true);                                   // قرص خارجي مفصول
        var r = store.Sync(new[] { a, b });
        Assert.Equal(1, r.OfflineRoots.Count);
        Assert.Equal(0, r.Removed);
        Assert.Equal(2, store.Entries.Count);

        var r2 = store.Sync(new[] { a });                            // أزلتُ المجلد B من القائمة عمداً
        Assert.Equal(1, r2.Removed);
        Assert.Equal(1, store.Entries.Count);
    }

    [Fact]
    public void Cancelled_sync_changes_nothing()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a-1.0.0.exe"), "x");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { root });

        File.WriteAllText(Path.Combine(root, "b-1.0.0.exe"), "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = store.Sync(new[] { root }, ct: cts.Token);
        Assert.True(r.Cancelled, "cancelled flag");
        Assert.Equal(1, store.Entries.Count);
    }

    [Fact]
    public void Hashes_are_computed_only_when_asked_and_kept_for_unchanged_files()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a-1.0.0.exe"), "hello");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { root });
        Assert.Null(store.Entries[0].Sha256);

        var r = store.Sync(new[] { root }, new LibrarySyncOptions { ComputeHashes = true });
        Assert.Equal(1, r.Hashed);
        // SHA256("hello")
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", store.Entries[0].Sha256!);

        store.Sync(new[] { root }, new LibrarySyncOptions { Rescan = true });
        Assert.Equal("2CF24DBA5FB0A30E26E83B2AC5B9E29E1B161E5C1FA7425E73043362938B9824", store.Entries[0].Sha256!);
    }

    [Fact]
    public void A_corrupt_library_file_starts_empty_instead_of_crashing()
    {
        File.WriteAllText(P("library.json"), "{ this is not json");
        Assert.Equal(0, LibraryStore.Load(P("library.json")).Entries.Count);
    }

    [Fact]
    public void Scanning_one_folder_from_the_command_line_keeps_the_rest_of_the_library()
    {
        var a = P("A"); var b = P("B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "a-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(b, "b-1.0.0.exe"), "x");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { a, b });

        var r = store.Sync(new[] { a }, new LibrarySyncOptions { KeepEntriesOutsideRoots = true });
        Assert.Equal(0, r.Removed);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public void Prune_drops_entries_outside_the_configured_folders()
    {
        var a = P("A"); var b = P("B"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "a-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(b, "b-1.0.0.exe"), "x");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { a, b });

        Assert.Equal(1, store.Prune(new[] { a }));
        Assert.Equal(1, store.Entries.Count);
        Assert.Equal(1, LibraryStore.Load(P("library.json")).Entries.Count);   // محفوظ على القرص
        Assert.True(LibraryStore.IsUnderPublic(Path.Combine(a, "x.exe"), a), "under");
        Assert.False(LibraryStore.IsUnderPublic(Path.Combine(b, "x.exe"), a));
    }

    [Fact]
    public void Library_export_has_real_numbers_dates_and_installed_relation()
    {
        var e = new LibraryEntry
        {
            Path = @"C:\lib\7z2501-x64.exe", Size = 1_500_000, ModifiedUtc = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            Kind = LibraryKind.Exe, ProductName = "7-Zip", Version = "25.01", Publisher = "Igor Pavlov", Architecture = "x64",
            Confidence = LibraryConfidence.High, Source = "pe",
        };
        var links = new Dictionary<string, InstalledLink> { [e.Path] = new InstalledLink("7-Zip 24.08 (x64)", "24.08", LibraryRelation.LibraryNewer, Confidence.High) };
        var table = LibraryExport.ToTable(new[] { e }, links);

        Assert.Equal(24, table.Columns.Count);
        var row = Assert.Single(table.Rows);
        Assert.Equal("7-Zip", row[0]);
        Assert.Equal(1_500_000L, row[6]);
        Assert.True(row[7] is DateTime, "modified is a real date");
        Assert.Equal("Identified", row[11]);
        Assert.Equal("Newer than installed", row[20]);
        Assert.Equal("7-Zip 24.08 (x64)", row[21]);

        var path = P("lib.xlsx");
        TableExporter.Write(path, table);
        using var zip = ZipFile.OpenRead(path);
        using var s = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
        var sheet = s.ReadToEnd();
        Assert.Contains("<v>1500000</v>", sheet);
    }

    // ------------------------------------------------------- الاستثناءات

    [Theory]
    [InlineData(@"C:\windows\*", @"C:\Windows", true, true)]
    [InlineData(@"C:\windows\*", @"C:\WINDOWS\System32", true, true)]
    [InlineData(@"C:\windows\*", @"C:\Windows\System32\setup.exe", false, true)]
    [InlineData(@"C:\windows\*", @"C:\Windowsx\a.exe", false, false)]
    [InlineData(@"C:\program files\*", @"C:\Program Files\Git\x.exe", false, true)]
    [InlineData(@"C:\program files\*", @"C:\Program Files (x86)\Git\x.exe", false, false)]
    [InlineData("C:/Windows/*", @"C:\Windows\a.exe", false, true)]
    [InlineData(".tmp", @"C:\a\b.TMP", false, true)]
    [InlineData(".tmp", @"C:\a\b.tmp.exe", false, false)]
    [InlineData(".tmp", @"C:\a\folder.tmp", true, false)]
    [InlineData("setup*.log", @"C:\x\setup-1.log", false, true)]
    [InlineData("setup*.log", @"C:\x\mysetup.log", false, false)]
    [InlineData(@"*\skip\*", @"C:\x\skip\a.exe", false, true)]
    [InlineData("node_modules", @"C:\a\node_modules", true, true)]
    [InlineData("node_modules", @"C:\a\node_modules\b.exe", false, true)]
    [InlineData(@"re:\\(temp|cache)\\", @"C:\Users\me\Temp\a.exe", false, true)]
    [InlineData(@"re:\\(temp|cache)\\", @"C:\Users\me\Templates\a.exe", false, false)]
    [InlineData(@"re:\.(iso|img)$", @"D:\Images\win.ISO", false, true)]
    [InlineData(@"re:\.(iso|img)$", @"D:\Images\win.isox", false, false)]
    public void Exclusion_rules_match_paths_extensions_names_text_and_regex(string rule, string path, bool isDir, bool excluded)
    {
        var ex = LibraryExclusions.Parse(rule);
        Assert.Equal(0, ex.Errors.Count);
        Assert.Equal(1, ex.Rules.Count);
        Assert.Equal(excluded, ex.Find(path, isDir) != null);
    }

    [Fact]
    public void Exclusion_text_ignores_comments_and_blank_lines_and_reports_bad_regex_without_dropping_the_rest()
    {
        var ex = LibraryExclusions.Parse("# comment\r\n\r\n  \r\n.tmp\r\nre:(\r\nre:\r\nnode_modules\r\n");
        Assert.Equal(2, ex.Rules.Count);
        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains("Line 5", ex.Errors[0]);
        Assert.True(ex.Find(@"C:\a\x.tmp", false) != null, "other rules still work");
        Assert.True(LibraryExclusions.Parse("  ").IsEmpty && LibraryExclusions.Parse(null).IsEmpty, "empty text means no rules");
    }

    [Fact]
    public void Default_exclusions_cover_windows_and_program_files_case_insensitively()
    {
        var ex = LibraryExclusions.Parse(LibraryExclusions.DefaultText);
        Assert.Equal(0, ex.Errors.Count);
        Assert.True(ex.FindDirectory(@"C:\windows") != null, "C:\\windows");
        Assert.True(ex.FindDirectory(@"c:\PROGRAM FILES\Git") != null, "program files");
        Assert.True(ex.FindDirectory(@"C:\Program Files (x86)\Foo") != null, "x86");
        Assert.True(ex.FindFile(@"C:\Windows\Installer\x.msi") != null, "file under windows");
        Assert.True(ex.FindFile(@"D:\Software\vlc.exe") == null, "other drive is free");
        Assert.True(ex.FindDirectory(@"C:\Users\me\Downloads") == null, "downloads is free");
    }

    [Fact]
    public void Environment_variables_are_expanded_in_non_regex_rules()
    {
        Environment.SetEnvironmentVariable("WGO_TEST_ROOT", @"C:\Foo");
        try
        {
            var ex = LibraryExclusions.Parse(@"%WGO_TEST_ROOT%\*");
            Assert.True(ex.FindDirectory(@"C:\Foo\bar") != null, "expanded");
            Assert.True(ex.FindDirectory(@"C:\Other\bar") == null, "not expanded wrongly");
        }
        finally { Environment.SetEnvironmentVariable("WGO_TEST_ROOT", null); }
    }

    [Fact]
    public void Sync_does_not_descend_into_excluded_folders_and_drops_them_when_a_rule_is_added()
    {
        var root = P("lib");
        Directory.CreateDirectory(Path.Combine(root, "keep")); Directory.CreateDirectory(Path.Combine(root, "skip", "deep"));
        File.WriteAllText(Path.Combine(root, "keep", "a-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(root, "skip", "b-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(root, "skip", "deep", "c-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(root, "disc.iso"), "x");
        var store = new LibraryStore(P("library.json"));

        var rules = LibraryExclusions.Parse("*\\skip\n.iso");
        var r1 = store.Sync(new[] { root }, new LibrarySyncOptions { Exclusions = rules });
        Assert.Equal(1, r1.Added);
        Assert.Equal(1, r1.ExcludedDirectories);       // skip: لم يُنزَل إلى deep أصلاً
        Assert.Equal(1, r1.ExcludedFiles);              // disc.iso
        Assert.Equal("a-1.0.0.exe", store.Entries[0].FileName);

        var r2 = store.Sync(new[] { root });             // بلا استثناءات: يظهر كل شيء
        Assert.Equal(3, r2.Added);
        Assert.Equal(4, store.Entries.Count);

        var r3 = store.Sync(new[] { root }, new LibrarySyncOptions { Exclusions = rules });   // أُعيدت القاعدة: تُحذف سجلاتها
        Assert.Equal(3, r3.Removed);
        Assert.Equal(1, store.Entries.Count);
    }

    [Fact]
    public void Entries_on_a_drive_that_no_longer_exists_are_kept_when_asked()
    {
        if (!OperatingSystem.IsWindows()) return;   // أحرف الأقراص مفهوم ويندوزي
        var root = P("lib"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a-1.0.0.exe"), "x");
        var store = new LibraryStore(P("library.json"));
        store.Sync(new[] { root });
        var all = store.Entries.ToList();
        all.Add(new LibraryEntry { Path = @"Q:\\gone\\old-1.0.0.exe", ProductName = "old", Confidence = LibraryConfidence.Low });
        store.ReplaceAll(all);

        store.Sync(new[] { root }, new LibrarySyncOptions { KeepEntriesOnMissingDrives = true });
        Assert.Equal(2, store.Entries.Count);
        store.Sync(new[] { root });
        Assert.Equal(1, store.Entries.Count);
    }

    [Fact]
    public void Roots_are_the_added_folders_or_all_drives_without_duplicates()
    {
        var folder = P("mine"); Directory.CreateDirectory(folder);

        var only = LibraryRoots.Compute(false, false, new[] { folder, folder });
        Assert.Equal(1, only.Count);
        Assert.Equal(folder, only[0]);

        var whole = LibraryRoots.Compute(true, false, new[] { folder });
        Assert.True(whole.Count >= 1, "at least one drive");
        Assert.False(whole.Contains(folder), "a folder inside a searched drive is not listed twice");
        Assert.True(whole.Any(r => LibraryStore.IsUnderPublic(folder, r)), "the folder is covered by a drive root");

        Assert.Equal(0, LibraryRoots.Compute(false, true, Array.Empty<string>()).Count);
    }

    // ------------------------------------------- البصمات الجزئية وذاكرة الملفات

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(42).NextBytes(b);
        return b;
    }

    private static string Hex(byte[] data, bool md5 = false) =>
        Convert.ToHexString(md5 ? MD5.HashData(data) : SHA256.HashData(data));

    private LibraryEntry SyncOne(string file, LibraryHashPolicy policy, LibraryFileCache? cache = null)
    {
        var store = new LibraryStore(P("one.json"));
        store.Sync(new[] { Path.GetDirectoryName(file)! }, new LibrarySyncOptions { Hashing = policy, Cache = cache });
        return Assert.Single(store.Entries);
    }

    [Fact]
    public void Quick_mode_hashes_only_the_first_part_of_files_above_the_limit()
    {
        var root = P("q"); Directory.CreateDirectory(root);
        var data = Bytes(1000);
        File.WriteAllBytes(Path.Combine(root, "big-1.0.0.exe"), data);
        var e = SyncOne(Path.Combine(root, "big-1.0.0.exe"), new LibraryHashPolicy { Mode = LibraryHashMode.Quick, QuickThresholdBytes = 100, QuickHeadBytes = 40 });

        Assert.Equal(Hex(data.Take(40).ToArray()), e.PartialHash!);
        Assert.Equal("SHA256", e.PartialHashAlgorithm);
        Assert.Equal(40L, e.PartialHashBytes!.Value);
        Assert.Null(e.Sha256);                       // لا بصمة كاملة: لا مطابقة مع الكتالوج
        Assert.Contains("first", e.PartialHashText());
    }

    [Fact]
    public void Quick_mode_hashes_small_files_completely()
    {
        var root = P("q"); Directory.CreateDirectory(root);
        var data = Bytes(80);
        File.WriteAllBytes(Path.Combine(root, "small-1.0.0.exe"), data);
        var e = SyncOne(Path.Combine(root, "small-1.0.0.exe"), new LibraryHashPolicy { Mode = LibraryHashMode.Quick, QuickThresholdBytes = 100, QuickHeadBytes = 40 });

        Assert.Equal(Hex(data), e.Sha256!);
        Assert.Null(e.PartialHash);
    }

    [Fact]
    public void Md5_can_be_chosen_for_full_and_partial_hashes()
    {
        var root = P("q"); Directory.CreateDirectory(root);
        var data = Bytes(1000);
        File.WriteAllBytes(Path.Combine(root, "a-1.0.0.exe"), data);

        var full = SyncOne(Path.Combine(root, "a-1.0.0.exe"), new LibraryHashPolicy { Mode = LibraryHashMode.Full, Algorithm = LibraryHashAlgorithm.Md5 });
        Assert.Equal(Hex(data, md5: true), full.Md5!);
        Assert.Null(full.Sha256);

        var part = SyncOne(Path.Combine(root, "a-1.0.0.exe"), new LibraryHashPolicy { Mode = LibraryHashMode.Quick, Algorithm = LibraryHashAlgorithm.Md5, QuickThresholdBytes = 100, QuickHeadBytes = 64 });
        Assert.Equal(Hex(data.Take(64).ToArray(), md5: true), part.PartialHash!);
        Assert.Equal("MD5", part.PartialHashAlgorithm);
    }

    [Fact]
    public void Changing_the_hash_policy_computes_only_what_is_missing()
    {
        var root = P("q"); Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "a-1.0.0.exe"), Bytes(1000));
        var store = new LibraryStore(P("library.json"));
        var quick = new LibraryHashPolicy { Mode = LibraryHashMode.Quick, QuickThresholdBytes = 100, QuickHeadBytes = 40 };

        Assert.Equal(1, store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = quick }).Hashed);
        Assert.Equal(0, store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = quick }).Hashed);      // لا جديد
        Assert.Equal(1, store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = new LibraryHashPolicy { Mode = LibraryHashMode.Quick, QuickThresholdBytes = 100, QuickHeadBytes = 80 } }).Hashed); // رأس أكبر ← بصمة جديدة
        Assert.Equal(1, store.Sync(new[] { root }, new LibrarySyncOptions { Hashing = LibraryHashPolicy.FullSha256 }).Hashed);
    }

    [Fact]
    public void A_renamed_and_moved_file_is_recognised_and_its_hash_reused_without_reading_it()
    {
        var root = P("lib"); var sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
        var original = Path.Combine(root, "AcmeWidget-4.2.1.msi");
        File.WriteAllBytes(original, LibraryFixtures.AcmeMsi());
        var cache = LibraryFileCache.Load(P("cache.json"));
        var store = new LibraryStore(P("library.json"));
        var opts = new LibrarySyncOptions { Hashing = LibraryHashPolicy.FullSha256, Cache = cache };

        var r1 = store.Sync(new[] { root }, opts);
        Assert.Equal(1, r1.Hashed);
        Assert.Equal(0, r1.FromCache);
        var firstHash = store.Entries[0].Sha256!;

        // نقل + إعادة تسمية: نفس الحجم وتاريخ التعديل
        var moved = Path.Combine(sub, "totally-different-name.msi");
        File.Move(original, moved);

        var r2 = store.Sync(new[] { root }, opts);
        Assert.Equal(1, r2.FromCache);               // عُرّف دون قراءة محتواه
        Assert.Equal(0, r2.Hashed);
        var e = Assert.Single(store.Entries);
        Assert.Equal(moved, e.Path);
        Assert.Equal(firstHash, e.Sha256!);
        Assert.Equal("Acme Widget Pro", e.ProductName);   // الاسم من المحتوى لا من الاسم الجديد
        Assert.Equal("4.2.1", e.Version);
    }

    [Fact]
    public void The_cache_survives_a_restart_and_a_missing_library_file()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a-1.0.0.exe");
        File.WriteAllBytes(file, Bytes(500));
        var cachePath = P("cache.json");

        new LibraryStore(P("l1.json")).Sync(new[] { root }, new LibrarySyncOptions { Hashing = LibraryHashPolicy.FullSha256, Cache = LibraryFileCache.Load(cachePath) });
        Assert.True(File.Exists(cachePath), "cache saved");

        // مكتبة جديدة تماماً (library.json مفقود) وذاكرة محمَّلة من القرص
        var fresh = new LibraryStore(P("l2.json"));
        var r = fresh.Sync(new[] { root }, new LibrarySyncOptions { Hashing = LibraryHashPolicy.FullSha256, Cache = LibraryFileCache.Load(cachePath) });
        Assert.Equal(1, r.FromCache);
        Assert.Equal(0, r.Hashed);
        Assert.Equal(1, r.HashReused);
    }

    [Fact]
    public void A_changed_file_is_not_taken_from_the_cache()
    {
        var root = P("lib"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a-1.0.0.exe");
        File.WriteAllBytes(file, Bytes(500));
        var cache = LibraryFileCache.Load(P("cache.json"));
        var store = new LibraryStore(P("library.json"));
        var opts = new LibrarySyncOptions { Hashing = LibraryHashPolicy.FullSha256, Cache = cache };
        store.Sync(new[] { root }, opts);
        var before = store.Entries[0].Sha256!;

        File.WriteAllBytes(file, Bytes(501));        // حجم مختلف
        var r = store.Sync(new[] { root }, opts);
        Assert.Equal(0, r.FromCache);
        Assert.Equal(1, r.Hashed);
        Assert.True(store.Entries[0].Sha256 != before, "new hash");
    }

    [Fact]
    public void Cache_records_not_seen_for_long_are_pruned()
    {
        var cache = LibraryFileCache.Load(P("cache.json"));
        cache.Put(new CachedFile { Size = 1, ModifiedTicks = 1 });
        cache.Put(new CachedFile { Size = 2, ModifiedTicks = 2 });
        Assert.True(cache.TryGet(1, 1, out var old), "present");
        old.LastSeenUtc = DateTime.UtcNow.AddDays(-400);
        Assert.Equal(1, cache.Prune(TimeSpan.FromDays(180)));
        Assert.False(cache.TryGet(1, 1, out _), "pruned");
        Assert.True(cache.TryGet(2, 2, out _), "kept");
    }

    [Fact]
    public void An_exe_installer_takes_the_architecture_from_the_file_name_when_the_binary_is_a_32_bit_stub()
    {
        var f = new FileInfo(P("App-3.1.0-x64-setup.exe"));
        File.WriteAllText(f.FullName, "x");
        var e = LibraryScanner.BuildEntry(f, _dir, new FileFacts { ProductName = "App", Version = "3.1.0", Publisher = "Acme", Architecture = "x86" },
                                          new List<string> { "pe" }, null);
        Assert.Equal("x64", e.Architecture);

        var g = new FileInfo(P("Tool-1.0.exe"));
        File.WriteAllText(g.FullName, "x");
        var e2 = LibraryScanner.BuildEntry(g, _dir, new FileFacts { ProductName = "Tool", Version = "1.0", Architecture = "x86" }, new List<string> { "pe" }, null);
        Assert.Equal("x86", e2.Architecture);       // لا ادّعاء بلا دليل
    }

    // ------------------------------------------------- المطابقة مع المثبّت

    private static LibraryEntry Lib(string name, string? version, string? publisher, string src = "pe", string? code = null,
                                    LibraryConfidence conf = LibraryConfidence.High, string? path = null) => new()
    {
        Path = path ?? $@"C:\lib\{name}-{version}.exe",
        ProductName = name, Version = version, Publisher = publisher, Source = src, ProductCode = code, Confidence = conf,
    };

    private static InstalledApp App(string name, string? publisher, string? code = null) =>
        new() { DisplayName = name, Publisher = publisher, ProductCode = code, Source = "Registry" };

    [Fact]
    public void Match_by_product_code_is_exact()
    {
        var m = new LibraryMatcher(new[] { Lib("Acme Widget Pro", "4.2.1", "Acme Software Ltd", "msi", "{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}") });
        var r = m.Find(App("Totally different display name", null, "{701c0ab3-696f-43ea-94bc-db08c62d1fea}"), "4.2.1");
        Assert.True(r != null, "matched");
        Assert.Equal("ProductCode", r!.Method);
        Assert.Equal(LibraryRelation.Same, r.Relation);
        Assert.Equal(Confidence.High, r.Confidence);
    }

    [Fact]
    public void Name_and_publisher_match_picks_the_highest_version_and_reports_the_relation()
    {
        var m = new LibraryMatcher(new[]
        {
            Lib("7-Zip", "24.08", "Igor Pavlov"), Lib("7-Zip", "25.01", "Igor Pavlov"), Lib("7-Zip", "23.01", "Igor Pavlov"),
        });
        var older = m.Find(App("7-Zip 24.08 (x64)", "Igor Pavlov"), "24.08")!;
        Assert.Equal("25.01", older.Best.Version);
        Assert.Equal(LibraryRelation.LibraryNewer, older.Relation);
        Assert.True(older.HasSameVersion, "24.08 also in library");
        Assert.Equal(3, older.Candidates.Count);
        Assert.Equal("Newer 25.01", older.ShortText);

        var newer = m.Find(App("7-Zip 26.04 (x64)", "Igor Pavlov"), "26.04")!;
        Assert.Equal(LibraryRelation.LibraryOlder, newer.Relation);
        Assert.Equal("Older 25.01", newer.ShortText);
    }

    [Fact]
    public void Same_name_with_a_different_publisher_is_not_a_match()
    {
        var m = new LibraryMatcher(new[] { Lib("Notepad", "1.0", "Some Other Company") });
        Assert.Null(m.Find(App("Notepad", "Microsoft Corporation"), "11.0"));
    }

    [Fact]
    public void Versioned_product_names_stay_distinct()
    {
        var m = new LibraryMatcher(new[] { Lib("Adobe Photoshop 2020", "21.1.1", "Adobe Systems Incorporated") });
        Assert.Null(m.Find(App("Adobe Photoshop 2025", "Adobe Inc."), "26.5.0.16"));
        Assert.True(m.Find(App("Adobe Photoshop 2020", "Adobe Systems Incorporated"), "21.1.1") != null, "same year matches");
    }

    [Fact]
    public void File_name_only_entries_match_with_low_confidence()
    {
        var m = new LibraryMatcher(new[] { Lib("Everything", "1.4.1", null, "filename", conf: LibraryConfidence.Low) });
        var r = m.Find(App("Everything 1.4.1.1026 (x64)", "voidtools"), "1.4.1.1026");
        Assert.True(r != null, "matched by name");
        Assert.Equal(Confidence.Low, r!.Confidence);
    }

    [Fact]
    public void Links_work_in_both_directions()
    {
        var lib = new[]
        {
            Lib("7-Zip", "25.01", "Igor Pavlov", path: @"C:\lib\7z2501.exe"),
            Lib("7-Zip", "23.01", "Igor Pavlov", path: @"C:\lib\7z2301.exe"),
            Lib("Unrelated", "1.0", "Nobody", path: @"C:\lib\unrelated.exe"),
        };
        var app = App("7-Zip 24.08 (x64)", "Igor Pavlov");
        var links = new LibraryMatcher(lib).Link(new[] { (app, (string?)"24.08") });

        Assert.Equal(LibraryRelation.LibraryNewer, links.ByApp[app].Relation);
        Assert.Equal(LibraryRelation.LibraryNewer, links.ByPath[@"C:\lib\7z2501.exe"].Relation);
        Assert.Equal(LibraryRelation.LibraryOlder, links.ByPath[@"C:\lib\7z2301.exe"].Relation);
        Assert.False(links.ByPath.ContainsKey(@"C:\lib\unrelated.exe"));
        Assert.Equal("7-Zip 24.08 (x64)", links.ByPath[@"C:\lib\7z2501.exe"].AppName);
    }
}
