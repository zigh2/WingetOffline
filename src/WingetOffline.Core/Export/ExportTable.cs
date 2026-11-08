using System.Globalization;
using System.Text;

namespace WingetOffline.Core.Export;

/// <summary>الصيغ المدعومة للتصدير. تُحدَّد من امتداد الملف.</summary>
public enum ExportFormat { Json, Csv, Xlsx }

/// <summary>عمود في جدول التصدير: العنوان المعروض (csv/xlsx) ومفتاح JSON.</summary>
public sealed record ExportColumn(string Header, string? Key = null)
{
    /// <summary>مفتاح JSON: المعطى صراحةً، وإلا العنوان بصيغة camelCase ("Package ID" ← packageId).</summary>
    public string JsonKey => string.IsNullOrWhiteSpace(Key) ? ExportTable.ToCamelKey(Header) : Key!;
}

/// <summary>
/// جدول محايد للصيغ يُبنى مرة واحدة ويُكتب إلى json أو csv أو xlsx بنفس المحتوى.
/// الخلايا: نص، أو رقم (يُكتب رقماً حقيقياً في xlsx)، أو تاريخ، أو منطقي، أو null.
/// </summary>
public sealed class ExportTable
{
    public ExportTable(string kind, IReadOnlyList<ExportColumn> columns, string? sheetName = null)
    {
        if (columns.Count == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        Kind = kind;
        Columns = columns;
        SheetName = string.IsNullOrWhiteSpace(sheetName) ? kind : sheetName!;
    }

    /// <summary>وسم نوع الجدول يُكتب في JSON (مثل installed أو updates).</summary>
    public string Kind { get; }
    public string SheetName { get; }
    public IReadOnlyList<ExportColumn> Columns { get; }
    public List<object?[]> Rows { get; } = new();

    public void Add(params object?[] cells)
    {
        if (cells.Length != Columns.Count)
            throw new ArgumentException($"Expected {Columns.Count} cells but got {cells.Length}.", nameof(cells));
        Rows.Add(cells);
    }

    /// <summary>نص الخلية كما يظهر في CSV (ثقافة ثابتة، بلا تنسيقات محلية).</summary>
    internal static string CellText(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    internal static bool IsIntegral(object? v) => v is sbyte or byte or short or ushort or int or uint or long or ulong;

    internal static bool IsNumber(object? v) =>
        IsIntegral(v) || v is float or double or decimal;

    internal static string ToCamelKey(string header)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        foreach (var ch in header)
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(ch);
            else if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        if (words.Count == 0) return "column";

        var sb = new StringBuilder(words[0].ToLowerInvariant());
        for (int i = 1; i < words.Count; i++)
        {
            var w = words[i];
            sb.Append(char.ToUpperInvariant(w[0]));
            if (w.Length > 1) sb.Append(w.AsSpan(1).ToString().ToLowerInvariant());
        }
        return sb.ToString();
    }
}
