namespace WingetOffline.Core;

/// <summary>
/// مجلد بيانات التطبيق (الإعدادات، الكاش، السجلات). الأولوية:
/// 1) متغير البيئة WINGETOFFLINE_DATA  2) وضع محمول: وجود ملف portable.txt بجانب الملف التنفيذي → مجلد data بجانبه
/// 3) %LOCALAPPDATA%\WingetOffline.
/// </summary>
public static class AppPaths
{
    private static bool _portable;
    private static readonly Lazy<string> Data = new(Resolve);

    public static string DataDir => Data.Value;

    public static bool IsPortable { get { _ = DataDir; return _portable; } }

    public static string Combine(params string[] parts) =>
        Path.Combine(new[] { DataDir }.Concat(parts).ToArray());

    private static string Resolve()
    {
        string dir;
        var env = Environment.GetEnvironmentVariable("WINGETOFFLINE_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            dir = env;
        }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            _portable = true;
            dir = Path.Combine(AppContext.BaseDirectory, "data");
        }
        else
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WingetOffline");
        }
        try { Directory.CreateDirectory(dir); } catch { /* يُنشأ عند أول كتابة */ }
        return dir;
    }
}
