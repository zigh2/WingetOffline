using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WingetOffline.Core.Export;
using WingetOffline.Core.Packing;

namespace WingetOffline.Tests;

public class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-export-" + Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ExportTable Sample()
    {
        var t = new ExportTable("installed", new[]
        {
            new ExportColumn("Name"), new ExportColumn("Installed", "installed"),
            new ExportColumn("Latest", "version"), new ExportColumn("Match", "confidence"),
            new ExportColumn("Package ID"), new ExportColumn("Size"),
        });
        t.Add("7-Zip", "24.08", "25.01", "High", "7zip.7zip", 1234567L);
        t.Add("برنامج عربي, بفاصلة", "1.0", "", "", "", null);
        t.Add("Say \"hi\"\nsecond line", "1", "2", "Medium", "A.B", 5L);
        return t;
    }

    private static string ReadZipText(string path, string entry)
    {
        using var zip = ZipFile.OpenRead(path);
        using var s = zip.GetEntry(entry)!.Open();
        return new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    // ---------------------------------------------------------------- الصيغة

    [Theory]
    [InlineData("a.json", ExportFormat.Json)]
    [InlineData("a.CSV", ExportFormat.Csv)]
    [InlineData("a.xlsx", ExportFormat.Xlsx)]
    public void Format_is_taken_from_the_extension(string name, ExportFormat expected)
        => Assert.Equal(expected, TableExporter.FormatFromPath(name));

    [Fact]
    public void Unknown_extension_is_rejected()
    {
        Assert.Null(TableExporter.FormatFromPath("a.txt"));
        Assert.Throws<NotSupportedException>(() => TableExporter.Write(Path.Combine(_dir, "a.txt"), Sample()));
    }

    [Theory]
    [InlineData("Package ID", "packageId")]
    [InlineData("Match confidence", "matchConfidence")]
    [InlineData("Name", "name")]
    public void Json_keys_default_to_camel_case(string header, string key)
        => Assert.Equal(key, new ExportColumn(header).JsonKey);

    [Fact]
    public void Row_with_wrong_cell_count_is_rejected()
        => Assert.Throws<ArgumentException>(() => Sample().Add("only one"));

    // ------------------------------------------------------------------- CSV

    [Fact]
    public void Csv_has_bom_crlf_quoting_and_arabic_text()
    {
        var path = Path.Combine(_dir, "t.csv");
        TableExporter.Write(path, Sample());

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Name,Installed,Latest,Match,Package ID,Size\r\n", text);
        Assert.Contains("\"برنامج عربي, بفاصلة\",1.0,,,,\r\n", text);
        Assert.Contains("\"Say \"\"hi\"\"\nsecond line\"", text);
        Assert.Contains("7-Zip,24.08,25.01,High,7zip.7zip,1234567\r\n", text);
    }

    [Fact]
    public void Csv_neutralises_formula_injection_but_keeps_plain_numbers()
    {
        var t = new ExportTable("x", new[] { new ExportColumn("A") });
        t.Add("=HYPERLINK(\"http://evil\")");
        t.Add("@SUM(A1)");
        t.Add("-5");
        t.Add("+cmd");
        var path = Path.Combine(_dir, "i.csv");
        TableExporter.Write(path, t);

        var lines = File.ReadAllText(path, Encoding.UTF8).Split("\r\n");
        Assert.Equal("\"'=HYPERLINK(\"\"http://evil\"\")\"", lines[1]);
        Assert.Equal("'@SUM(A1)", lines[2]);
        Assert.Equal("-5", lines[3]);
        Assert.Equal("'+cmd", lines[4]);
    }

    // ------------------------------------------------------------------ JSON

    [Fact]
    public void Json_keeps_arabic_unescaped_and_types_numbers()
    {
        var path = Path.Combine(_dir, "t.json");
        TableExporter.Write(path, Sample());

        var raw = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("برنامج عربي, بفاصلة", raw);
        Assert.DoesNotContain("\\u06", raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("installed", root.GetProperty("kind").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        var first = root.GetProperty("items")[0];
        Assert.Equal("7zip.7zip", first.GetProperty("packageId").GetString());
        Assert.Equal("25.01", first.GetProperty("version").GetString());
        Assert.Equal(1234567L, first.GetProperty("size").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("items")[1].GetProperty("size").ValueKind);
    }

    [Fact]
    public void Installed_json_can_feed_pack_and_drops_low_confidence_and_unmatched_rows()
    {
        var t = new ExportTable("installed", new[]
        {
            new ExportColumn("Name"), new ExportColumn("Installed", "installed"),
            new ExportColumn("Latest", "version"), new ExportColumn("Match", "confidence"),
            new ExportColumn("Package ID"),
        });
        t.Add("Good", "1.0", "2.0", "High", "Good.App");
        t.Add("Weak", "1.0", "2.0", "Low", "Weak.App");
        t.Add("Nothing", "1.0", "", "", "");
        var path = Path.Combine(_dir, "inst.json");
        TableExporter.Write(path, t);

        var read = UpdateList.Read(path);
        var only = Assert.Single(read);
        Assert.Equal(new UpdateEntry("Good.App", "2.0", "1.0", "Good"), only);
        Assert.Equal(2, UpdateList.Read(path, includeLowConfidence: true).Count);
    }

    [Fact]
    public void Update_list_json_keeps_arabic_names_readable()
    {
        var path = Path.Combine(_dir, "u.json");
        UpdateList.Write(path, new[] { new UpdateEntry("A.B", "2", "1", "تطبيق") });
        Assert.Contains("تطبيق", File.ReadAllText(path, Encoding.UTF8));
        Assert.Equal("تطبيق", Assert.Single(UpdateList.Read(path)).Name);
    }

    // ------------------------------------------------------------------ XLSX

    [Fact]
    public void Xlsx_is_a_valid_package_with_the_expected_parts()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TableExporter.Write(path, Sample());

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                                     "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            Assert.Contains(part, names);

        // كل جزء XML يجب أن يُحلَّل بلا أخطاء
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        {
            using var s = e.Open();
            System.Xml.Linq.XDocument.Load(s);
        }
    }

    [Fact]
    public void Xlsx_stores_arabic_numbers_header_style_frozen_pane_and_filter()
    {
        var path = Path.Combine(_dir, "t.xlsx");
        TableExporter.Write(path, Sample());
        var sheet = ReadZipText(path, "xl/worksheets/sheet1.xml");

        Assert.Contains("برنامج عربي, بفاصلة", sheet);
        Assert.Contains("<c r=\"F2\" s=\"3\"><v>1234567</v></c>", sheet); // رقم حقيقي لا نص
        Assert.Contains("t=\"inlineStr\"", sheet);
        Assert.Contains("state=\"frozen\"", sheet);
        Assert.Contains("<autoFilter ref=\"A1:F4\"", sheet);
        Assert.Contains("<dimension ref=\"A1:F4\"", sheet);
        Assert.DoesNotContain("r=\"F3\"", sheet); // الخلية الفارغة لا تُكتب
    }

    [Fact]
    public void Xlsx_never_turns_text_into_a_formula()
    {
        var t = new ExportTable("x", new[] { new ExportColumn("A") });
        t.Add("=1+1");
        var path = Path.Combine(_dir, "f.xlsx");
        TableExporter.Write(path, t);
        var sheet = ReadZipText(path, "xl/worksheets/sheet1.xml");
        Assert.DoesNotContain("<f>", sheet);
        Assert.Contains("=1+1", sheet);
    }

    [Fact]
    public void Xlsx_right_to_left_is_automatic_for_arabic_majority_and_overridable()
    {
        var ar = new ExportTable("x", new[] { new ExportColumn("الاسم"), new ExportColumn("الإصدار") });
        ar.Add("برنامج", "١٫٢");
        var arPath = Path.Combine(_dir, "ar.xlsx");
        TableExporter.Write(arPath, ar);
        Assert.Contains("rightToLeft=\"1\"", ReadZipText(arPath, "xl/worksheets/sheet1.xml"));

        var enPath = Path.Combine(_dir, "en.xlsx");
        TableExporter.Write(enPath, Sample());
        Assert.DoesNotContain("rightToLeft", ReadZipText(enPath, "xl/worksheets/sheet1.xml"));

        var forced = Path.Combine(_dir, "forced.xlsx");
        TableExporter.Write(forced, Sample(), ExportFormat.Xlsx, new ExportOptions { RightToLeft = true });
        Assert.Contains("rightToLeft=\"1\"", ReadZipText(forced, "xl/worksheets/sheet1.xml"));
    }

    [Fact]
    public void Xlsx_survives_control_characters_and_astral_symbols_and_bad_sheet_names()
    {
        var t = new ExportTable("x", new[] { new ExportColumn("A") }, sheetName: "Bad:/Name?*[]" + new string('z', 40));
        t.Add("ok\u0001\u0008text");
        t.Add("emoji 😀 and \uD800 lone");
        var path = Path.Combine(_dir, "c.xlsx");
        TableExporter.Write(path, t);

        var sheet = ReadZipText(path, "xl/worksheets/sheet1.xml");
        Assert.Contains("oktext", sheet);
        Assert.Contains("😀", sheet);
        var wb = ReadZipText(path, "xl/workbook.xml");
        var name = System.Xml.Linq.XDocument.Parse(wb).Descendants().First(e => e.Name.LocalName == "sheet").Attribute("name")!.Value;
        Assert.True(name.Length <= 31);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('/', name);
    }

    [Fact]
    public void Column_letters_follow_excel_naming()
    {
        var t = new ExportTable("x", new[] { new ExportColumn("A") });
        Assert.Equal("A", TableExporter.ColName(0));
        Assert.Equal("Z", TableExporter.ColName(25));
        Assert.Equal("AA", TableExporter.ColName(26));
        Assert.Equal("XFD", TableExporter.ColName(16383));
        Assert.Empty(t.Rows);
    }

    [Fact]
    public void Write_replaces_an_existing_file_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "t.csv");
        File.WriteAllText(path, "old");
        TableExporter.Write(path, Sample());
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("Name", Encoding.UTF8.GetString(bytes, 3, 4));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
