using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WingetOffline.Core.Scanning;

[SupportedOSPlatform("windows")]
public static class RegistryScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex GuidKey = new(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}(_is1)?$",
        RegexOptions.Compiled);
    private static readonly Regex UpdateName = new(
        @"\bKB\d{6,}\b|^(Security Update|Update|Hotfix) for ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<InstalledApp> Scan()
    {
        var list = new List<InstalledApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "Machine", "x64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "Machine", "x86"),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, "User",    "x64"),
            (RegistryHive.CurrentUser,  RegistryView.Registry32, "User",    "x86"),
        };

        foreach (var (hive, view, scope, arch) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(UninstallPath);
            if (key == null) continue;

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(name);
                    if (sub == null) continue;

                    var display = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    if (AsInt(sub.GetValue("SystemComponent")) == 1) continue;
                    if (sub.GetValue("ParentKeyName") != null) continue;
                    var release = sub.GetValue("ReleaseType") as string;
                    if (release is "Update" or "Hotfix" or "Security Update") continue;
                    if (UpdateName.IsMatch(display)) continue;

                    var version = sub.GetValue("DisplayVersion") as string;
                    if (!seen.Add($"{scope}|{name}|{display}|{version}")) continue;

                    list.Add(new InstalledApp
                    {
                        Source = "Registry",
                        DisplayName = display.Trim(),
                        Publisher = (sub.GetValue("Publisher") as string)?.Trim(),
                        DisplayVersion = version?.Trim(),
                        ProductCode = GuidKey.IsMatch(name) ? name : null,
                        UninstallString = sub.GetValue("UninstallString") as string,
                        QuietUninstallString = sub.GetValue("QuietUninstallString") as string,
                        InstallLocation = sub.GetValue("InstallLocation") as string,
                        Scope = scope,
                        Architecture = arch,
                        RegistryKeyName = name,
                    });
                }
                catch { /* مفتاح غير قابل للقراءة: نتجاوزه */ }
            }
        }
        return list;
    }

    private static int AsInt(object? o) => o is int i ? i : 0;
}
