using System.Reflection;
using System.Runtime.InteropServices;

namespace WingetOffline.Core;

/// <summary>رقم الإصدار كما يعرضه كل برنامج (من ملف VERSION عبر خصائص التجميع).</summary>
public static class BuildInfo
{
    public static string VersionOf(Assembly? asm)
    {
        asm ??= Assembly.GetExecutingAssembly();
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString() ?? "?";
        var plus = v.IndexOf('+');            // يحذف بصمة git التي يضيفها SDK
        return plus >= 0 ? v[..plus] : v;
    }
}

/// <summary>
/// أدوات سطر الأوامر عندما يُفتح الملف بنقرة مزدوجة من المستكشف: تظهر نافذة وتُغلق في جزء من الثانية.
/// نكشف ذلك (العملية وحدها على النافذة) فنعرض شرحاً وننتظر ضغطة مفتاح قبل الإغلاق.
/// </summary>
public static class ConsoleHost
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>true إذا كانت هذه العملية وحدها على نافذة الأوامر (فُتحت بنقرة مزدوجة لا من PowerShell أو cmd).</summary>
    public static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return false;
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static void PauseIfOwnConsole()
    {
        if (!OwnsConsole() || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("Press any key to close this window...");
        Console.ReadKey(true);
    }
}
