using Microsoft.Win32;

namespace WingetOffline.UI.Services;

/// <summary>نافذة اختيار ملف للاستيراد: ملفات التصدير فقط (xlsx وcsv وjson).</summary>
public static class ImportDialog
{
    public static string? Ask(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "WingetOffline exports (*.xlsx;*.csv;*.json)|*.xlsx;*.csv;*.json|Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
