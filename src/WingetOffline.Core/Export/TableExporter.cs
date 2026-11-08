using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace WingetOffline.Core.Export;

/// <summary>خيارات التصدير.</summary>
public sealed class ExportOptions
{
    /// <summary>
    /// اتجاه ورقة xlsx من اليمين لليسار. null = تلقائي: يُفعَّل عندما تكون أغلب الخلايا النصية
    /// بحروف عربية أو عبرية أو فارسية.
    /// </summary>
    public bool? RightToLeft { get; init; }
    public string Creator { get; init; } = "WingetOffline";
}

/// <summary>
/// يكتب <see cref="ExportTable"/> إلى JSON وCSV وXLSX بلا أي مكتبة خارجية.
/// كل الصيغ UTF-8 فتعمل العربية وكل اللغات الأخرى: CSV بعلامة BOM ليفهمها Excel،
/// وXLSX بنصوص مضمَّنة، وJSON بلا تحويل الحروف إلى \uXXXX.
/// </summary>
public static class TableExporter
{
    public const int ExcelMaxRows = 1_048_576;
    public const int ExcelMaxCellChars = 32_767;

    /// <summary>يستنتج الصيغة من الامتداد (.json .csv .xlsx)؛ يعيد null لغير ذلك.</summary>
    public static ExportFormat? FormatFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => ExportFormat.Json,
            ".csv" => ExportFormat.Csv,
            ".xlsx" => ExportFormat.Xlsx,
            _ => null,
        };

    public static void Write(string path, ExportTable table, ExportOptions? options = null)
    {
        var format = FormatFromPath(path)
            ?? throw new NotSupportedException($"Unsupported export extension: {Path.GetExtension(path)}");
        Write(path, table, format, options);
    }

    public static void Write(string path, ExportTable table, ExportFormat format, ExportOptions? options = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // نكتب إلى ملف مؤقت ثم نستبدل: فشل في المنتصف لا يتلف ملفاً سابقاً.
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                Write(fs, table, format, options);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* تنظيف فقط */ } }
        }
    }

    public static void Write(Stream stream, ExportTable table, ExportFormat format, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        switch (format)
        {
            case ExportFormat.Json: WriteJson(stream, table); break;
            case ExportFormat.Csv: WriteCsv(stream, table); break;
            case ExportFormat.Xlsx: WriteXlsx(stream, table, options); break;
            default: throw new NotSupportedException(format.ToString());
        }
    }

    // ------------------------------------------------------------------ CSV

    private static void WriteCsv(Stream stream, ExportTable t)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
        };
        w.WriteLine(string.Join(",", t.Columns.Select(c => CsvField(c.Header, guard: false))));
        foreach (var row in t.Rows)
            w.WriteLine(string.Join(",", row.Select(v => CsvField(v, guard: v is string))));
        w.Flush();
    }

    private static string CsvField(object? value, bool guard)
    {
        var s = ExportTable.CellText(value);
        // حماية CSV injection: خلية نصية تبدأ بـ = + - @ قد تُنفَّذ كصيغة في Excel.
        if (guard && s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ----------------------------------------------------------------- JSON

    private static void WriteJson(Stream stream, ExportTable t)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // العربية كما هي، لا \u0627
        });
        w.WriteStartObject();
        w.WriteNumber("format", 1);
        w.WriteString("kind", t.Kind);
        w.WriteString("createdUtc", DateTime.UtcNow);
        w.WriteNumber("count", t.Rows.Count);
        w.WriteStartArray("items");
        foreach (var row in t.Rows)
        {
            w.WriteStartObject();
            for (int i = 0; i < t.Columns.Count; i++)
            {
                var key = t.Columns[i].JsonKey;
                switch (row[i])
                {
                    case null: w.WriteNull(key); break;
                    case string s: w.WriteString(key, s); break;
                    case bool b: w.WriteBoolean(key, b); break;
                    case DateTime d: w.WriteString(key, d); break;
                    case DateTimeOffset d: w.WriteString(key, d); break;
                    case long or int or short or sbyte or byte or ushort: w.WriteNumber(key, Convert.ToInt64(row[i], CultureInfo.InvariantCulture)); break;
                    case uint or ulong: w.WriteNumber(key, Convert.ToUInt64(row[i], CultureInfo.InvariantCulture)); break;
                    case decimal m: w.WriteNumber(key, m); break;
                    case float or double:
                        var dbl = Convert.ToDouble(row[i], CultureInfo.InvariantCulture);
                        if (double.IsNaN(dbl) || double.IsInfinity(dbl)) w.WriteNull(key); else w.WriteNumber(key, dbl);
                        break;
                    default: w.WriteString(key, ExportTable.CellText(row[i])); break;
                }
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }

    // ----------------------------------------------------------------- XLSX

    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // فهارس الأنماط في styles.xml
    private const int StyleNormal = 0, StyleHeader = 1, StyleDate = 2, StyleInteger = 3, StyleDecimal = 4;

    private static void WriteXlsx(Stream stream, ExportTable t, ExportOptions opt)
    {
        if (t.Rows.Count + 1 > ExcelMaxRows)
            throw new InvalidOperationException(
                $"Too many rows for an Excel sheet ({t.Rows.Count:N0}); the limit is {ExcelMaxRows - 1:N0}. Use CSV or JSON instead.");
        if (t.Columns.Count > 16_384)
            throw new InvalidOperationException("Too many columns for an Excel sheet.");

        bool rtl = opt.RightToLeft ?? MostlyRightToLeft(t);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        AddEntry(zip, "[Content_Types].xml", w =>
        {
            w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            Override(w, "/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
            w.WriteEndElement();
        });
        AddEntry(zip, "_rels/.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            Rel(w, "rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
            w.WriteEndElement();
        });
        AddEntry(zip, "docProps/core.xml", w =>
        {
            w.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
            w.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
            w.WriteAttributeString("xmlns", "dcterms", null, "http://purl.org/dc/terms/");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteElementString("dc", "creator", null, Clean(opt.Creator));
            w.WriteStartElement("dcterms", "created", null);
            w.WriteAttributeString("xsi", "type", null, "dcterms:W3CDTF");
            w.WriteString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteEndElement();
        });
        AddEntry(zip, "xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", NsMain);
            w.WriteAttributeString("xmlns", "r", null, NsRel);
            w.WriteStartElement("sheets");
            w.WriteStartElement("sheet");
            w.WriteAttributeString("name", SafeSheetName(t.SheetName));
            w.WriteAttributeString("sheetId", "1");
            w.WriteAttributeString("id", NsRel, "rId1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        });
        AddEntry(zip, "xl/_rels/workbook.xml.rels", w =>
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", "worksheets/sheet1.xml");
            Rel(w, "rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            w.WriteEndElement();
        });
        AddEntry(zip, "xl/styles.xml", WriteStyles);
        AddEntry(zip, "xl/worksheets/sheet1.xml", w => WriteSheet(w, t, rtl));
    }

    private static void WriteStyles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", NsMain);

        w.WriteStartElement("fonts"); w.WriteAttributeString("count", "2");
        w.WriteStartElement("font"); Val(w, "sz", "11"); Val(w, "name", "Calibri"); w.WriteEndElement();
        w.WriteStartElement("font"); w.WriteElementString("b", null); Val(w, "sz", "11"); Val(w, "name", "Calibri"); w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("fills"); w.WriteAttributeString("count", "3");
        Fill(w, "none"); Fill(w, "gray125");
        w.WriteStartElement("fill"); w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", "solid");
        w.WriteStartElement("fgColor"); w.WriteAttributeString("rgb", "FFD9E2F3"); w.WriteEndElement();
        w.WriteStartElement("bgColor"); w.WriteAttributeString("indexed", "64"); w.WriteEndElement();
        w.WriteEndElement(); w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("borders"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("border");
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" }) w.WriteElementString(side, null);
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("xf"); w.WriteAttributeString("numFmtId", "0"); w.WriteAttributeString("fontId", "0");
        w.WriteAttributeString("fillId", "0"); w.WriteAttributeString("borderId", "0"); w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "5");
        Xf(w, 0, 0, 0, false);        // StyleNormal
        Xf(w, 0, 1, 2, true);         // StyleHeader: خط عريض + تعبئة
        Xf(w, 22, 0, 0, false);       // StyleDate: m/d/yy h:mm (مدمج)
        Xf(w, 3, 0, 0, false);        // StyleInteger: #,##0
        Xf(w, 2, 0, 0, false);        // StyleDecimal: 0.00
        w.WriteEndElement();

        w.WriteStartElement("cellStyles"); w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle"); w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0"); w.WriteAttributeString("builtinId", "0"); w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();

        static void Val(XmlWriter x, string name, string v) { x.WriteStartElement(name); x.WriteAttributeString("val", v); x.WriteEndElement(); }
        static void Fill(XmlWriter x, string pattern)
        {
            x.WriteStartElement("fill"); x.WriteStartElement("patternFill");
            x.WriteAttributeString("patternType", pattern); x.WriteEndElement(); x.WriteEndElement();
        }
        static void Xf(XmlWriter x, int numFmt, int font, int fill, bool header)
        {
            x.WriteStartElement("xf");
            x.WriteAttributeString("numFmtId", numFmt.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("borderId", "0");
            x.WriteAttributeString("xfId", "0");
            if (numFmt != 0) x.WriteAttributeString("applyNumberFormat", "1");
            if (font != 0) x.WriteAttributeString("applyFont", "1");
            if (fill != 0) x.WriteAttributeString("applyFill", "1");
            if (header)
            {
                x.WriteAttributeString("applyAlignment", "1");
                x.WriteStartElement("alignment"); x.WriteAttributeString("vertical", "center"); x.WriteEndElement();
            }
            x.WriteEndElement();
        }
    }

    private static void WriteSheet(XmlWriter w, ExportTable t, bool rtl)
    {
        int cols = t.Columns.Count, rows = t.Rows.Count + 1;
        string lastRef = ColName(cols - 1) + rows.ToString(CultureInfo.InvariantCulture);

        w.WriteStartElement("worksheet", NsMain);
        w.WriteStartElement("dimension"); w.WriteAttributeString("ref", "A1:" + lastRef); w.WriteEndElement();

        w.WriteStartElement("sheetViews");
        w.WriteStartElement("sheetView");
        w.WriteAttributeString("workbookViewId", "0");
        if (rtl) w.WriteAttributeString("rightToLeft", "1");
        w.WriteStartElement("pane");
        w.WriteAttributeString("ySplit", "1");
        w.WriteAttributeString("topLeftCell", "A2");
        w.WriteAttributeString("activePane", "bottomLeft");
        w.WriteAttributeString("state", "frozen");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("sheetFormatPr"); w.WriteAttributeString("defaultRowHeight", "15"); w.WriteEndElement();

        w.WriteStartElement("cols");
        var widths = ColumnWidths(t);
        for (int c = 0; c < cols; c++)
        {
            w.WriteStartElement("col");
            w.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("width", widths[c].ToString("0.##", CultureInfo.InvariantCulture));
            w.WriteAttributeString("customWidth", "1");
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("sheetData");
        w.WriteStartElement("row"); w.WriteAttributeString("r", "1");
        for (int c = 0; c < cols; c++) InlineString(w, ColName(c) + "1", t.Columns[c].Header, StyleHeader);
        w.WriteEndElement();

        for (int r = 0; r < t.Rows.Count; r++)
        {
            string rn = (r + 2).ToString(CultureInfo.InvariantCulture);
            w.WriteStartElement("row"); w.WriteAttributeString("r", rn);
            var row = t.Rows[r];
            for (int c = 0; c < cols; c++) Cell(w, ColName(c) + rn, row[c]);
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("autoFilter"); w.WriteAttributeString("ref", "A1:" + lastRef); w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Cell(XmlWriter w, string cellRef, object? v)
    {
        switch (v)
        {
            case null: return; // خلية فارغة: لا نكتب شيئاً
            case string s:
                if (s.Length == 0) return;
                InlineString(w, cellRef, s, StyleNormal);
                return;
            case bool b:
                w.WriteStartElement("c"); w.WriteAttributeString("r", cellRef); w.WriteAttributeString("t", "b");
                w.WriteElementString("v", b ? "1" : "0"); w.WriteEndElement();
                return;
            case DateTime d:
                Number(w, cellRef, (d - new DateTime(1899, 12, 30)).TotalDays.ToString("R", CultureInfo.InvariantCulture), StyleDate);
                return;
            case DateTimeOffset d:
                Number(w, cellRef, (d.DateTime - new DateTime(1899, 12, 30)).TotalDays.ToString("R", CultureInfo.InvariantCulture), StyleDate);
                return;
        }

        if (ExportTable.IsIntegral(v))
        {
            Number(w, cellRef, Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), StyleInteger);
        }
        else if (v is float or double)
        {
            var d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return;
            Number(w, cellRef, d.ToString("R", CultureInfo.InvariantCulture), StyleDecimal);
        }
        else if (v is decimal m)
        {
            Number(w, cellRef, m.ToString(CultureInfo.InvariantCulture), StyleDecimal);
        }
        else
        {
            InlineString(w, cellRef, ExportTable.CellText(v), StyleNormal);
        }
    }

    private static void Number(XmlWriter w, string cellRef, string text, int style)
    {
        w.WriteStartElement("c"); w.WriteAttributeString("r", cellRef);
        if (style != StyleNormal) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteElementString("v", text);
        w.WriteEndElement();
    }

    private static void InlineString(XmlWriter w, string cellRef, string text, int style)
    {
        text = Clean(text);
        if (text.Length > ExcelMaxCellChars) text = text.Substring(0, ExcelMaxCellChars);
        w.WriteStartElement("c");
        w.WriteAttributeString("r", cellRef);
        if (style != StyleNormal) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("t", "inlineStr");
        w.WriteStartElement("is");
        w.WriteStartElement("t");
        w.WriteAttributeString("xml", "space", null, "preserve");
        w.WriteString(text);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    // ------------------------------------------------------------- مساعدات

    private static void AddEntry(ZipArchive zip, string name, Action<XmlWriter> body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var w = XmlWriter.Create(es, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
            NewLineHandling = NewLineHandling.Entitize,
            CheckCharacters = false, // ننظّف النصوص بأنفسنا في Clean
        });
        w.WriteStartDocument(standalone: true);
        body(w);
        w.WriteEndDocument();
    }

    private static void Default(XmlWriter w, string ext, string type)
    { w.WriteStartElement("Default"); w.WriteAttributeString("Extension", ext); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }

    private static void Override(XmlWriter w, string part, string type)
    { w.WriteStartElement("Override"); w.WriteAttributeString("PartName", part); w.WriteAttributeString("ContentType", type); w.WriteEndElement(); }

    private static void Rel(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship");
        w.WriteAttributeString("Id", id); w.WriteAttributeString("Type", type); w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    /// <summary>حذف الحروف غير الصالحة في XML 1.0 (محارف التحكم والبدائل اليتيمة).</summary>
    internal static string Clean(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb?.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }
            ok = c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
            if (ok) { sb?.Append(c); }
            else if (sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
        }
        return sb?.ToString() ?? s;
    }

    private static string SafeSheetName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in Clean(name))
            sb.Append("[]:*?/\\".IndexOf(c) >= 0 ? ' ' : c);
        var s = sb.ToString().Trim().Trim('\'');
        if (s.Length > 31) s = s.Substring(0, 31).TrimEnd();
        return s.Length == 0 ? "Sheet1" : s;
    }

    internal static string ColName(int index)
    {
        var sb = new StringBuilder();
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    private static double[] ColumnWidths(ExportTable t)
    {
        var w = new double[t.Columns.Count];
        for (int c = 0; c < w.Length; c++)
        {
            int max = t.Columns[c].Header.Length + 2; // مساحة لزر الفلتر
            foreach (var row in t.Rows.Take(2000))
            {
                var len = row[c] switch { null => 0, string s => LongestLine(s), _ => 12 };
                if (len > max) max = len;
            }
            w[c] = Math.Min(60, Math.Max(8, max * 1.1 + 2));
        }
        return w;
    }

    private static int LongestLine(string s)
    {
        int best = 0, cur = 0;
        foreach (var ch in s) { if (ch == '\n') { best = Math.Max(best, cur); cur = 0; } else cur++; }
        return Math.Max(best, cur);
    }

    private static bool IsRtlChar(char c) =>
        (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);

    /// <summary>هل أغلب الخلايا النصية (والعناوين) تحوي حروفاً من لغة تُكتب من اليمين لليسار؟</summary>
    private static bool MostlyRightToLeft(ExportTable t)
    {
        int rtl = 0, total = 0;
        void Count(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            total++;
            if (s.Any(IsRtlChar)) rtl++;
        }
        foreach (var c in t.Columns) Count(c.Header);
        foreach (var row in t.Rows.Take(2000))
            foreach (var v in row) if (v is string s) Count(s);
        return total > 0 && rtl * 2 >= total;
    }
}
