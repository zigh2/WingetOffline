using System.Text.Json;
using WingetOffline.Core;

namespace WingetOffline.UI.Services;

public sealed class SettingsData
{
    public string? CatalogPath { get; set; }
    public string Theme { get; set; } = "System";          // System | Light | Dark
    public List<string> PortableFolders { get; set; } = new();
    public bool ScanWingetPortables { get; set; } = true;
    public bool AutoScan { get; set; } = true;
    public string? InstallersFolder { get; set; }
    /// <summary>مجلدات المكتبة المحلية (صفحة Local): تُفحص رؤوس الملفات فقط ولا يُنفَّذ أي شيء.</summary>
    public List<string> LibraryFolders { get; set; } = new();
    /// <summary>البحث في كل الأقراص الثابتة بدل المجلدات المضافة فقط.</summary>
    public bool LibraryWholeComputer { get; set; }
    /// <summary>مع البحث في كامل الكمبيوتر: أضف الأقراص القابلة للإزالة.</summary>
    public bool LibraryIncludeRemovable { get; set; }
    /// <summary>وضع البصمات: Off أو Full (الملف كاملاً) أو Quick (الملف الكبير على أول جزء منه فقط).</summary>
    public string LibraryHashingMode { get; set; } = "Off";
    /// <summary>XXH64 (الافتراضي، الأسرع) أو SHA256 (الوحيد الذي يطابق الكتالوج) أو MD5.</summary>
    public string LibraryHashingAlgorithm { get; set; } = "XXH64";
    /// <summary>الوضع السريع: الملفات الأكبر من هذا الحد (MB) تُبصَم على أول جزء فقط.</summary>
    public int LibraryQuickAboveMB { get; set; } = 30;
    /// <summary>الوضع السريع: حجم الجزء الأول المُبصَم (MB).</summary>
    public int LibraryQuickHeadMB { get; set; } = 10;
    /// <summary>الاستثناءات: سطر لكل قاعدة (مسار بعلامات * أو لاحقة أو نص أو تعبير نمطي يبدأ بـ re:).</summary>
    public string LibraryExclusionRules { get; set; } = WingetOffline.Core.Library.LibraryExclusions.DefaultText;
    public bool UseUac { get; set; } = true;
    /// <summary>حزم لا نعرض/نطبّق تحديثاتها (فكرة hold في Scoop وقوائم الاستبعاد في WAU و UniGetUI).</summary>
    public List<string> IgnoredPackages { get; set; } = new();
}

public static class SettingsStore
{
    private static string FilePath => AppPaths.Combine("settings.json");

    public static SettingsData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new SettingsData();
        }
        catch { /* ملف إعدادات تالف: نبدأ بالافتراضي */ }
        return new SettingsData();
    }

    public static void Save(SettingsData s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* فشل الحفظ لا يجب أن يوقف التطبيق */ }
    }
}
