using System.Reflection;

namespace WingetOffline.UI.Services;

public static class AppInfo
{
    /// <summary>مثل 0.0.5-r4 (الإصدار من ملف VERSION + رقم المراجعة).</summary>
    public static string Version =>
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
