using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace WingetOffline.Core.Export;

/// <summary>خطأ استيراد بنص مفهوم للمستخدم (يُعرض كما هو).</summary>
public sealed class ImportException : Exception
{
    public ImportException(string message) : base(message) { }
}

/// <summary>جدول مقروء من ملف تصدير (JSON/CSV/XLSX) قبل ربطه بصفحة؛ القيم نصوص ومفاتيحها هي مفاتيح JSON.</summary>
public sealed class ImportedTable
{
    public string FileName { get; init; } = "";
    public ExportFormat Format { get; init; }
    /// <summary>الصفحة المعلنة داخل الملف (حقل kind في JSON).</summary>
    public string? DeclaredKind { get; init; }
    /// <summary>الصفحة المستنتجة من الأعمدة (CSV وExcel، وJSON القديم بلا kind).</summary>
    public string? DetectedKind { get; init; }
    public string? Kind => DeclaredKind ?? DetectedKind;
    public DateTime? CreatedUtc { get; init; }
    public List<Dictionary<string, string>> Rows { get; } = new();

    /// <summary>
    /// يضمن أن الملف صادر من الصفحة المتوقعة. ملف صفحة أخرى يُرفض برسالة تدلّ على الصفحة الصحيحة،
    /// فلا يختلط محتوى الصفحات.
    /// </summary>
    public void RequireKind(string expectedKind)
    {
        if (string.Equals(Kind, expectedKind, StringComparison.OrdinalIgnoreCase)) return;

        var expectedPage = ExportSpecs.PageName(expectedKind);
        if (Kind is ExportSpecs.KindInstalled or ExportSpecs.KindUpdates or ExportSpecs.KindLibrary)
            throw new ImportException(
                $"\"{FileName}\" was exported from the {ExportSpecs.PageName(Kind)} page, so it cannot be imported on the {expectedPage} page.\n" +
                $"Open the {ExportSpecs.PageName(Kind)} page and import it there.");
        throw new ImportException(
            $"\"{FileName}\" is not an export from the {expectedPage} page (or from this program).\n" +
            "Import only files created by the Export buttons: .xlsx, .csv or .json.");
    }
}

public static class TableImporter
{
    private const long MaxBytes = 200L * 1024 * 1024;

    public static ImportedTable Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ImportException("The file was not found: " + path);
        if (info.Length > MaxBytes) throw new ImportException("The file is too large to import (over 200 MB).");
        var format = TableExporter.FormatFromPath(path)
            ?? throw new ImportException("Unsupported file type. Import files created by the Export buttons: .xlsx, .csv or .json.");

        try
        {
            return format switch
            {
                ExportFormat.Json => ReadJson(path, info.Name),
                ExportFormat.Csv => ReadCsv(path, info.Name),
                _ => ReadXlsx(path, info.Name),
            };
        }
        catch (ImportException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or XmlException or IOException or FormatException)
        {
            throw new ImportException($"\"{info.Name}\" could not be read: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- JSON

    private static ImportedTable ReadJson(string path, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        JsonElement items;
        string? declared = null;
        DateTime? created = null;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String) declared = k.GetString();
            if (root.TryGetProperty("createdUtc", out var c) && c.ValueKind == JsonValueKind.String
                && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                created = dt;
            if (!root.TryGetProperty("items", out items) || items.ValueKind != JsonValueKind.Array)
                throw new ImportException($"\"{name}\" has no \"items\" list, so it is not an export from this program.");
        }
        else if (root.ValueKind == JsonValueKind.Array) items = root;
        else throw new ImportException($"\"{name}\" is not an export from this program.");

        var rows = new List<Dictionary<string, string>>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in items.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject())
            {
                row[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => p.Value.GetRawText(),
                };
                keys.Add(p.Name);
            }
            rows.Add(row);
        }

        var table = new ImportedTable
        {
            FileName = name, Format = ExportFormat.Json, DeclaredKind = declared?.ToLowerInvariant(),
            DetectedKind = ExportSpecs.DetectKindFromKeys(keys), CreatedUtc = created,
        };
        table.Rows.AddRange(rows);
        return table;
    }

    // ----------------------------------------------------------------- CSV

    private static ImportedTable ReadCsv(string path, string name)
    {
        var text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
        var records = ParseCsv(text);
        return FromGrid(records, name, ExportFormat.Csv);
    }

    /// <summary>محلل CSV وفق RFC 4180: اقتباس مزدوج، فواصل وأسطر داخل الحقول، CRLF أو LF.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;

        void EndField() { row.Add(field.ToString()); field.Clear(); any = true; }
        void EndRow()
        {
            if (any || field.Length > 0) { EndField(); records.Add(row); }
            row = new List<string>();
            any = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { quoted = true; any = true; }
            else if (c == ',') EndField();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else field.Append(c);
        }
        EndRow();
        return records;
    }

    // ---------------------------------------------------------------- XLSX

    private static ImportedTable ReadXlsx(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst != null)
        {
            using var s = sst.Open();
            using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            // XNode.ReadFrom يقرأ العنصر كاملاً ويتقدّم بعده، فلا نستدعي Read بعده (وإلا فاتت العقدة التالية)
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                {
                    var si = (System.Xml.Linq.XElement)System.Xml.Linq.XNode.ReadFrom(r);
                    shared.Add(string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
                }
                else r.Read();
            }
        }

        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml"))
                          .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new ImportException($"\"{name}\" has no worksheet, so it is not an export from this program.");

        var grid = new List<List<string>>();
        using (var s = sheet.Open())
        using (var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            List<string>? row = null;
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
                {
                    row = new List<string>();
                    r.Read();
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row" && row != null)
                {
                    grid.Add(row);
                    if (grid.Count > 1_100_000) throw new ImportException("The worksheet has too many rows.");
                    row = null;
                    r.Read();
                }
                else if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && row != null)
                {
                    var c = (System.Xml.Linq.XElement)System.Xml.Linq.XNode.ReadFrom(r);
                    string? type = c.Attribute("t")?.Value;
                    string? v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                    string inline = string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    string value = type switch
                    {
                        "s" => int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "",
                        "inlineStr" => inline,
                        "b" => v == "1" ? "true" : "false",
                        "str" or "e" => v ?? "",
                        _ => NumberText(v),
                    };
                    int col = ColumnIndex(c.Attribute("r")?.Value);
                    while (row.Count <= col) row.Add("");
                    row[col] = value;
                }
                else r.Read();
            }
        }
        return FromGrid(grid, name, ExportFormat.Xlsx);
    }

    private static int ColumnIndex(string? cellRef)
    {
        int n = 0;
        foreach (var ch in cellRef ?? "A")
        {
            if (!char.IsLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return Math.Max(0, n - 1);
    }

    private static string NumberText(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && Math.Abs(d) < 1e15 && d == Math.Floor(d))
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return v;
    }

    // -------------------------------------------------------- شبكة ← جدول

    private static ImportedTable FromGrid(List<List<string>> grid, string name, ExportFormat format)
    {
        var headerRow = grid.FirstOrDefault(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            ?? throw new ImportException($"\"{name}\" is empty.");
        int headerIndex = grid.IndexOf(headerRow);

        var keys = headerRow.Select(h => ExportSpecs.HeaderToKey.TryGetValue(h.Trim(), out var k) ? k : h.Trim()).ToList();
        var table = new ImportedTable
        {
            FileName = name, Format = format, DetectedKind = ExportSpecs.DetectKindFromHeaders(headerRow),
        };

        for (int i = headerIndex + 1; i < grid.Count; i++)
        {
            var cells = grid[i];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < keys.Count && c < cells.Count; c++)
                if (keys[c].Length > 0) row[keys[c]] = cells[c];
            table.Rows.Add(row);
        }
        return table;
    }
}
