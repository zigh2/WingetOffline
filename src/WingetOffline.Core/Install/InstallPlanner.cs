namespace WingetOffline.Core.Install;

public enum PlanKind { Process, Portable, RemoveFolder }

/// <summary>خطة تنفيذ جاهزة للعرض على المستخدم قبل التشغيل.</summary>
public sealed record InstallPlan(
    PlanKind Kind, string FileName, string Arguments, string Description,
    bool NeedsElevation, bool Interactive, string? Warning = null, string? Target = null,
    bool IsUninstall = false, InstallPlan? InteractiveFallback = null)
{
    public string CommandLine => Kind switch
    {
        PlanKind.Process => $"\"{FileName}\" {Arguments}".Trim(),
        PlanKind.Portable => $"extract/copy \"{FileName}\" -> \"{Target}\"",
        _ => $"delete folder \"{FileName}\"",
    };
}

public sealed class InstallOptions
{
    /// <summary>تشغيل المثبّت بواجهته (بلا مفاتيح صامتة).</summary>
    public bool Interactive { get; set; }
    /// <summary>ملف سجل MSI (/L*v) اختياري.</summary>
    public string? MsiLogFile { get; set; }
    public string PortableRoot { get; set; } = DefaultPortableRoot;

    public static string DefaultPortableRoot => AppPaths.Combine("Portable");
}

/// <summary>
/// يحوّل (ملف مثبّت + بيانات الكتالوج) إلى أمر تثبيت. المفاتيح الصامتة تأتي من المانيفست أولاً،
/// وإلا تُستخدم الافتراضيات المعروفة لنوع المثبّت (نفس ما يفعله winget).
/// </summary>
public static class InstallPlanner
{
    public static InstallPlan Plan(string filePath, CatalogInstaller inst, InstallOptions opt)
    {
        var plan = PlanCore(filePath, inst, opt);
        if (opt.Interactive || plan.Kind != PlanKind.Process || plan.Interactive) return plan;

        // بديل عند فشل التشغيل الصامت: نفس المثبّت بواجهته (فكرة Bulk Crap Uninstaller)
        try
        {
            var interactive = new InstallOptions { Interactive = true, MsiLogFile = opt.MsiLogFile, PortableRoot = opt.PortableRoot };
            var fb = PlanCore(filePath, inst, interactive);
            if (fb.FileName != plan.FileName || fb.Arguments != plan.Arguments)
                return plan with { InteractiveFallback = fb };
        }
        catch { /* بلا بديل */ }
        return plan;
    }

    private static InstallPlan PlanCore(string filePath, CatalogInstaller inst, InstallOptions opt)
    {
        var type = (inst.Type ?? "").Trim().ToLowerInvariant();
        var label = string.IsNullOrEmpty(inst.Version) ? inst.PackageId : $"{inst.PackageId} {inst.Version}";
        bool elevate = !string.Equals(inst.Scope, "user", StringComparison.OrdinalIgnoreCase);
        var custom = opt.Interactive ? null : inst.Custom;

        switch (type)
        {
            case "msi":
            case "wix":
            {
                var sw = opt.Interactive ? "" : Join(inst.Silent ?? "/quiet /norestart", custom);
                var log = opt.MsiLogFile != null ? $" /L*v \"{opt.MsiLogFile}\"" : "";
                var args = $"/i \"{filePath}\"" + (sw.Length > 0 ? " " + sw : "") + log;
                return new InstallPlan(PlanKind.Process, "msiexec.exe", args, $"Install {label} (MSI)", elevate, opt.Interactive);
            }
            case "burn":
                return Exe(filePath, label, "Burn bundle", inst.Silent ?? "/quiet /norestart", custom, elevate, opt);
            case "inno":
                return Exe(filePath, label, "Inno Setup", inst.Silent ?? "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", custom, elevate, opt);
            case "nullsoft":
                return Exe(filePath, label, "NSIS", inst.Silent ?? "/S", custom, elevate, opt);

            case "msix":
            case "appx":
            {
                var ps = $"Add-AppxPackage -Path '{filePath.Replace("'", "''")}'";
                return new InstallPlan(PlanKind.Process, "powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps}\"", $"Install {label} (MSIX/APPX)", false, false);
            }

            case "zip":
            case "portable":
            {
                var folder = SafeName(inst.PackageId == "(manual)" ? Path.GetFileNameWithoutExtension(filePath) : inst.PackageId);
                var target = Path.Combine(opt.PortableRoot, folder);
                var warn = inst.PortableAlias != null
                    ? $"No PATH alias is created automatically (manifest alias: {inst.PortableAlias})."
                    : "No PATH alias is created automatically.";
                return new InstallPlan(PlanKind.Portable, filePath, "", $"Install {label} (portable)", false, false, warn, target);
            }

            case "pwa":
                throw new NotSupportedException("PWA packages cannot be installed offline.");

            default: // exe وغيره: نحتاج مفتاحاً صامتاً من المانيفست
            {
                if (!opt.Interactive && !string.IsNullOrWhiteSpace(inst.Silent))
                    return Exe(filePath, label, "EXE", inst.Silent!, custom, elevate, opt);
                return new InstallPlan(PlanKind.Process, filePath, "", $"Install {label} (EXE)", elevate, true,
                    "The manifest has no silent switch for this installer; it will run with its normal UI.");
            }
        }
    }

    private static InstallPlan Exe(string file, string label, string kind, string silent, string? custom, bool elevate, InstallOptions opt)
    {
        var args = opt.Interactive ? "" : Join(silent, custom);
        return new InstallPlan(PlanKind.Process, file, args, $"Install {label} ({kind})", elevate, opt.Interactive);
    }

    private static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    internal static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().Length == 0 ? "package" : s.Trim();
    }
}
