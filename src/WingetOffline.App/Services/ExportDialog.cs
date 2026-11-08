using Microsoft.Win32;
using WingetOffline.Core.Export;

namespace WingetOffline.UI.Services;

/// <summary>نافذة حفظ موحّدة للتصدير: Excel وCSV وJSON. الصيغة الأولى في القائمة هي الافتراضية.</summary>
public static class ExportDialog
{
    public static (string Path, ExportFormat Format)? Ask(string title, string baseName, params ExportFormat[] order)
    {
        var formats = order.Length > 0 ? order : new[] { ExportFormat.Xlsx, ExportFormat.Csv, ExportFormat.Json };
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = string.Join("|", formats.Select(FilterText)),
            FileName = baseName + Extension(formats[0]),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return null;

        var typed = TableExporter.FormatFromPath(dlg.FileName);
        if (typed != null) return (dlg.FileName, typed.Value);

        // امتداد غير معروف أو بلا امتداد: نتّبع الصيغة المختارة في القائمة
        var chosen = formats[Math.Clamp(dlg.FilterIndex - 1, 0, formats.Length - 1)];
        return (dlg.FileName + Extension(chosen), chosen);
    }

    public static string Extension(ExportFormat f) => f switch
    {
        ExportFormat.Json => ".json",
        ExportFormat.Csv => ".csv",
        _ => ".xlsx",
    };

    private static string FilterText(ExportFormat f) => f switch
    {
        ExportFormat.Json => "JSON (*.json)|*.json",
        ExportFormat.Csv => "CSV, UTF-8 (*.csv)|*.csv",
        _ => "Excel workbook (*.xlsx)|*.xlsx",
    };
}
