using System.Diagnostics;

namespace WingetOffline.Core.Scanning;

public static class PortableScanner
{
    private static readonly string[] SkipPrefixes =
        { "unins", "uninstall", "setup", "update", "crash", "vc_redist", "vcredist", "dxsetup", "installer" };

    private const string WingetSourceSuffix = "_Microsoft.Winget.Source";

    /// <summary>البرامج المحمولة التي ثبّتها winget نفسه: اسم المجلد = المعرّف + _Microsoft.Winget.Source_...</summary>
    public static List<InstalledApp> ScanWingetPackages()
    {
        var result = new List<InstalledApp>();
        var dirs = new (string path, string scope)[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"), "User"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"), "Machine"),
        };
        foreach (var (path, scope) in dirs)
        {
            if (!Directory.Exists(path)) continue;
            foreach (var d in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(d);
                var idx = name.IndexOf(WingetSourceSuffix, StringComparison.OrdinalIgnoreCase);
                if (idx <= 0) continue;
                var id = name[..idx];
                result.Add(new InstalledApp
                {
                    Source = "WinGetFolder",
                    DisplayName = id,
                    HintPackageId = id,
                    InstallLocation = d,
                    Scope = scope,
                });
            }
        }
        return result;
    }

    /// <summary>فحص مجلد يختاره المستخدم بحثاً عن ملفات exe محمولة (عمق محدود لتقليل الضجيج).</summary>
    public static List<InstalledApp> ScanFolder(string root, int maxDepth = 2)
    {
        var result = new List<InstalledApp>();
        if (!Directory.Exists(root)) return result;

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maxDepth,
            IgnoreInaccessible = true,
        };
        foreach (var exe in Directory.EnumerateFiles(root, "*.exe", opts))
        {
            var file = Path.GetFileNameWithoutExtension(exe);
            if (SkipPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                var name = FirstNonEmpty(vi.ProductName, vi.FileDescription, file)!;
                var ver = FirstNonEmpty(vi.ProductVersion, vi.FileVersion)?.Replace(", ", ".");
                result.Add(new InstalledApp
                {
                    Source = "Portable",
                    DisplayName = name.Trim(),
                    Publisher = vi.CompanyName?.Trim(),
                    DisplayVersion = ver?.Trim(),
                    ExeName = file,
                    ExePath = exe,
                    InstallLocation = Path.GetDirectoryName(exe),
                    Scope = "Portable",
                });
            }
            catch { /* ملف غير قابل للقراءة */ }
        }
        return result;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
