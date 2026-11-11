using System.IO.Compression;
using System.Text;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Packing;

namespace WingetOffline.Tests;

public class ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-import-" + Guid.NewGuid().ToString("N"));

    public ImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static readonly string[] Exts = { ".json", ".csv", ".xlsx" };

    // ------------------------------------------------------------ عيّنات

    private static ExportTable InstalledSample()
    {
        var t = new ExportTable(ExportSpecs.KindInstalled, ExportSpecs.InstalledColumns, "Installed");
        //     Name, Publisher, Installed, Latest, Status, Match, PackageId, MatchedBy, Source, Scope, Location, Uninstall, Note, Library, LibVer, LibFile
        t.Add("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", "25.01", "Update available", "High", "7zip.7zip", "ARP name", "Registry", "Machine", @"C:\Program Files\7-Zip", @"""C:\Program Files\7-Zip\Uninstall.exe"" /S", "", "Newer 25.01", "25.01", @"D:\lib\7z2501-x64.exe");
        t.Add("برنامج عربي, بفاصلة", "شركة", "1.0", "", "Not matched", "", "", "", "Registry", "User", "", "", "ملاحظة\nبسطرين", "", "", "");
        t.Add("Everything", "voidtools", "1.5.0", "1.4.1", "Newer than catalog", "Medium", "voidtools.Everything", "Name+Publisher", "Registry", "Machine", "", "", "", "Same", "1.5.0", @"D:\lib\Everything.exe");
        t.Add("Ignored App", "X", "1.0", "2.0", "Update ignored", "High", "X.App", "ProductCode", "Registry", "Machine", "", "", "", "", "", "");
        return t;
    }

    private static ExportTable UpdatesSample()
    {
        var t = new ExportTable(ExportSpecs.KindUpdates, ExportSpecs.UpdatesColumns, "Updates");
        t.Add("Git.Git", "Git", "2.46.0", "2.47.1");
        t.Add("VideoLAN.VLC", "VLC media player", "3.0.20", "3.0.21");
        return t;
    }

    private static ExportTable LibrarySample()
    {
        var e = new LibraryEntry
        {
            Path = @"D:\lib\7z2501-x64.exe", Root = @"D:\lib", Size = 1_500_000, ModifiedUtc = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc),
            Kind = LibraryKind.Exe, ProductName = "7-Zip", Version = "25.01", Publisher = "Igor Pavlov", Architecture = "x64", InstallerType = "NSIS",
            Confidence = LibraryConfidence.High, Source = "pe", XxHash = "99594F4828043D35", Sha256 = "AB".PadRight(64, 'C'),
            ProductCode = "{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}", Notes = "note",
        };
        var big = new LibraryEntry
        {
            Path = @"D:\lib\تطبيق كبير 2.0.iso", Root = @"D:\lib", Size = 5_000_000_000, ModifiedUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Kind = LibraryKind.Iso, Confidence = LibraryConfidence.Low, Source = "filename", PartialHash = "49B45332E280F187",
            PartialHashAlgorithm = "XXH64", PartialHashBytes = 10 * 1024 * 1024,
        };
        var links = new Dictionary<string, InstalledLink> { [e.Path] = new InstalledLink("7-Zip 24.08 (x64)", "24.08", LibraryRelation.LibraryNewer, Confidence.High) };
        return LibraryExport.ToTable(new[] { e, big }, links);
    }

    private string Save(ExportTable t, string ext)
    {
        var path = P("sample" + ext);
        TableExporter.Write(path, t);
        return path;
    }

    // -------------------------------------------------- ذهاب وإياب Installed

    [Theory]
    [InlineData(".json")]
    [InlineData(".csv")]
    [InlineData(".xlsx")]
    public void Installed_export_round_trips_into_the_same_rows(string ext)
    {
        var table = TableImporter.Read(Save(InstalledSample(), ext));
        table.RequireKind(ExportSpecs.KindInstalled);
        var rows = SnapshotMapper.ToInstalled(table);
        Assert.Equal(4, rows.Count);

        var z = rows[0];
        Assert.Equal("7-Zip 24.08 (x64)", z.Result.App.DisplayName);
        Assert.Equal("Igor Pavlov", z.Result.App.Publisher);
        Assert.Equal("24.08", z.Result.InstalledVersion!);
        Assert.Equal("25.01", z.Result.LatestVersion!);
        Assert.Equal("7zip.7zip", z.Result.PackageId!);
        Assert.Equal(Confidence.High, z.Result.Confidence);
        Assert.Equal(UpdateStatus.UpdateAvailable, z.Result.Status);
        Assert.Equal("ARP name", z.Result.Method);
        Assert.Contains("Uninstall.exe", z.Result.App.UninstallString ?? "");
        Assert.Equal("Newer 25.01", z.Library!.ShortText);
        Assert.Equal(@"D:\lib\7z2501-x64.exe", z.Library.Best.Path);

        var ar = rows[1];
        Assert.Equal("برنامج عربي, بفاصلة", ar.Result.App.DisplayName);
        Assert.Equal(Confidence.None, ar.Result.Confidence);
        Assert.Null(ar.Result.PackageId);
        Assert.Contains("بسطرين", ar.Result.Note ?? "");
        Assert.Null(ar.Library);

        Assert.True(rows[2].Result.NewerThanCatalog, "newer than catalog");
        Assert.Equal("Same", rows[2].Library!.ShortText);
        Assert.True(rows[3].Ignored, "ignored flag");
    }

    // -------------------------------------------------- ذهاب وإياب Updates

    [Theory]
    [InlineData(".json")]
    [InlineData(".csv")]
    [InlineData(".xlsx")]
    public void Updates_export_round_trips(string ext)
    {
        var table = TableImporter.Read(Save(UpdatesSample(), ext));
        table.RequireKind(ExportSpecs.KindUpdates);
        var rows = SnapshotMapper.ToUpdates(table);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Git.Git", rows[0].Result.PackageId!);
        Assert.Equal("2.46.0", rows[0].Result.InstalledVersion!);
        Assert.Equal("2.47.1", rows[0].Result.LatestVersion!);
        Assert.Equal(UpdateStatus.UpdateAvailable, rows[0].Result.Status);
        Assert.Equal(Confidence.High, rows[0].Result.Confidence);
        Assert.Equal("VLC media player", rows[1].Result.App.DisplayName);
    }

    [Fact]
    public void The_pack_json_written_by_the_updates_page_is_importable_and_still_readable_by_pack()
    {
        var path = P("updates.json");
        UpdateList.Write(path, new[] { new UpdateEntry("Git.Git", "2.47.1", "2.46.0", "Git"), new UpdateEntry("A.B", "2", "1", "تطبيق") });

        var table = TableImporter.Read(path);
        Assert.Equal("updates", table.DeclaredKind);
        table.RequireKind(ExportSpecs.KindUpdates);
        Assert.Equal(2, SnapshotMapper.ToUpdates(table).Count);
        Assert.Equal(2, UpdateList.Read(path).Count);     // الصيغة لم تنكسر لأجل pack
    }

    [Fact]
    public void A_legacy_updates_json_without_a_kind_is_still_recognised_as_updates()
    {
        File.WriteAllText(P("old.json"), "{ \"format\": 1, \"createdUtc\": \"2026-01-01T00:00:00Z\", \"items\": [ { \"packageId\": \"Git.Git\", \"version\": \"2.47.1\", \"installed\": \"2.46.0\", \"name\": \"Git\" } ] }");
        var table = TableImporter.Read(P("old.json"));
        Assert.Null(table.DeclaredKind);
        table.RequireKind(ExportSpecs.KindUpdates);
        Assert.Equal(1, SnapshotMapper.ToUpdates(table).Count);
    }

    // -------------------------------------------------- ذهاب وإياب Local

    [Theory]
    [InlineData(".json")]
    [InlineData(".csv")]
    [InlineData(".xlsx")]
    public void Library_export_round_trips_with_hashes_dates_and_installed_links(string ext)
    {
        var table = TableImporter.Read(Save(LibrarySample(), ext));
        table.RequireKind(ExportSpecs.KindLibrary);
        var (entries, links) = SnapshotMapper.ToLibrary(table);
        Assert.Equal(2, entries.Count);

        var a = entries[0];
        Assert.Equal(@"D:\lib\7z2501-x64.exe", a.Path);
        Assert.Equal("7z2501-x64.exe", a.FileName);
        Assert.Equal("7-Zip", a.ProductName!);
        Assert.Equal("25.01", a.Version!);
        Assert.Equal(1_500_000L, a.Size);
        Assert.Equal(LibraryKind.Exe, a.Kind);
        Assert.Equal(LibraryConfidence.High, a.Confidence);
        Assert.Equal("99594F4828043D35", a.XxHash!);
        Assert.Equal("{701C0AB3-696F-43EA-94BC-DB08C62D1FEA}", a.ProductCode!);
        Assert.True(Math.Abs((a.ModifiedUtc - new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc)).TotalSeconds) < 2, "modified date survives");

        var b = entries[1];
        Assert.Equal("تطبيق كبير 2.0.iso", b.FileName);
        Assert.Null(b.ProductName);                      // الاسم المصدَّر كان اسم الملف
        Assert.Equal(5_000_000_000L, b.Size);
        Assert.Equal("49B45332E280F187", b.PartialHash!);
        Assert.Equal("XXH64", b.PartialHashAlgorithm!);
        Assert.Equal(10L * 1024 * 1024, b.PartialHashBytes!.Value);

        var link = links[a.Path];
        Assert.Equal(LibraryRelation.LibraryNewer, link.Relation);
        Assert.Equal("7-Zip 24.08 (x64)", link.AppName);
        Assert.Equal("24.08", link.InstalledVersion!);
        Assert.False(links.ContainsKey(b.Path));
    }

    // -------------------------------- منع الخلط: كل صفحة لا تقبل إلا ملفها

    public static IEnumerable<object[]> WrongPairs()
    {
        var kinds = new[] { ExportSpecs.KindInstalled, ExportSpecs.KindUpdates, ExportSpecs.KindLibrary };
        foreach (var ext in Exts)
            foreach (var made in kinds)
                foreach (var expectedPage in kinds)
                    if (made != expectedPage) yield return new object[] { ext, made, expectedPage };
    }

    [Fact]
    public void Every_page_rejects_files_exported_from_every_other_page_in_every_format()
    {
        int checkedPairs = 0;
        foreach (var args in WrongPairs())
        {
            var ext = (string)args[0]; var made = (string)args[1]; var page = (string)args[2];
            var table = made switch
            {
                ExportSpecs.KindInstalled => InstalledSample(),
                ExportSpecs.KindUpdates => UpdatesSample(),
                _ => LibrarySample(),
            };
            var imported = TableImporter.Read(Save(table, ext));
            var ex = Assert.Throws<ImportException>(() => imported.RequireKind(page));
            Assert.Contains($"exported from the {ExportSpecs.PageName(made)} page", ex.Message);
            Assert.Contains($"cannot be imported on the {ExportSpecs.PageName(page)} page", ex.Message);
            checkedPairs++;
        }
        Assert.Equal(18, checkedPairs);   // 3 صيغ × 6 تركيبات خاطئة
    }

    [Fact]
    public void A_file_that_is_not_an_export_is_rejected_on_every_page()
    {
        File.WriteAllText(P("random.csv"), "colA,colB,colC\r\n1,2,3\r\n");
        var table = TableImporter.Read(P("random.csv"));
        foreach (var kind in new[] { ExportSpecs.KindInstalled, ExportSpecs.KindUpdates, ExportSpecs.KindLibrary })
        {
            var ex = Assert.Throws<ImportException>(() => table.RequireKind(kind));
            Assert.Contains("is not an export", ex.Message);
        }

        File.WriteAllText(P("other.json"), "{ \"kind\": \"bundle\", \"items\": [ { \"x\": 1 } ] }");
        Assert.Throws<ImportException>(() => TableImporter.Read(P("other.json")).RequireKind(ExportSpecs.KindInstalled));
    }

    [Fact]
    public void Unsupported_extensions_missing_files_and_broken_files_give_readable_errors()
    {
        File.WriteAllText(P("a.txt"), "x");
        Assert.Contains("Unsupported file type", Assert.Throws<ImportException>(() => TableImporter.Read(P("a.txt"))).Message);
        Assert.Contains("was not found", Assert.Throws<ImportException>(() => TableImporter.Read(P("nope.json"))).Message);

        File.WriteAllText(P("bad.json"), "{ not json");
        Assert.Contains("could not be read", Assert.Throws<ImportException>(() => TableImporter.Read(P("bad.json"))).Message);

        File.WriteAllText(P("bad.xlsx"), "not a zip");
        Assert.Contains("could not be read", Assert.Throws<ImportException>(() => TableImporter.Read(P("bad.xlsx"))).Message);

        File.WriteAllText(P("empty.csv"), "");
        Assert.Contains("is empty", Assert.Throws<ImportException>(() => TableImporter.Read(P("empty.csv"))).Message);
    }

    // ------------------------------------------------------------ المحللات

    [Fact]
    public void Csv_parser_handles_quotes_commas_newlines_bom_and_both_line_endings()
    {
        var text = "\uFEFFName,Note\r\n\"a, b\",\"say \"\"hi\"\"\"\r\n\"two\nlines\",plain\nlast,\"\"\r\n";
        File.WriteAllText(P("c.csv"), text, new UTF8Encoding(false));
        var recs = TableImporter.ParseCsv(text.TrimStart('\uFEFF'));
        Assert.Equal(4, recs.Count);
        Assert.Equal("a, b", recs[1][0]);
        Assert.Equal("say \"hi\"", recs[1][1]);
        Assert.Equal("two\nlines", recs[2][0]);
        Assert.Equal("", recs[3][1]);
    }

    [Fact]
    public void Xlsx_saved_by_excel_with_shared_strings_is_read_without_losing_neighbouring_cells()
    {
        // نمط ما يكتبه Excel: sharedStrings + خلايا t="s" ورقمية متجاورة (يكشف خطأ تخطي العقد في قارئ XML)
        var path = P("excel.xlsx");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string xml) { using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Add("xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"6\" uniqueCount=\"6\">" +
                "<si><t>Package ID</t></si><si><t>Name</t></si><si><t>Installed</t></si><si><t>Latest</t></si>" +
                "<si><t>Git.Git</t></si><si><r><t>Gi</t></r><r><t>t</t></r></si></sst>");
            Add("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>4</v></c><c r=\"B2\" t=\"s\"><v>5</v></c><c r=\"C2\"><v>2.46</v></c><c r=\"D2\"><v>2.47</v></c></row>" +
                "</sheetData></worksheet>");
        }
        var table = TableImporter.Read(path);
        Assert.Equal(ExportSpecs.KindUpdates, table.Kind!);
        var row = Assert.Single(table.Rows);
        Assert.Equal("Git.Git", row["packageId"]);
        Assert.Equal("Git", row["name"]);              // نص منسّق من عدة مقاطع
        Assert.Equal("2.46", row["installed"]);
        Assert.Equal("2.47", row["version"]);
    }

    [Fact]
    public void Installed_csv_from_version_0_0_9_2_with_only_13_columns_still_imports()
    {
        var cols = ExportSpecs.InstalledColumns.Take(13).ToList();
        var t = new ExportTable(ExportSpecs.KindInstalled, cols, "Installed");
        t.Add("App", "Pub", "1.0", "2.0", "Update available", "High", "A.B", "Name", "Registry", "Machine", "", "", "");
        var table = TableImporter.Read(Save(t, ".csv"));
        table.RequireKind(ExportSpecs.KindInstalled);
        var rows = SnapshotMapper.ToInstalled(table);
        Assert.Equal(1, rows.Count);
        Assert.Null(rows[0].Library);
    }
}
