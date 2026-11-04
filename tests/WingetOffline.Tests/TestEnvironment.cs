using System.Runtime.CompilerServices;
using WingetOffline.Core;

namespace WingetOffline.Tests;

/// <summary>يوجّه بيانات التطبيق (كاش، سجلات، إعدادات) إلى مجلد مؤقت كي لا تلمس الاختبارات بياناتك الحقيقية.</summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("WINGETOFFLINE_DATA", Path.Combine(Path.GetTempPath(), "wgo-tests-data"));
}

public class TestEnvironmentTests
{
    [Fact]
    public void Data_dir_is_redirected_to_temp()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "wgo-tests-data"), AppPaths.DataDir);
    }
}
