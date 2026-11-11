namespace WingetOffline.Core.Export;

/// <summary>
/// تعريف أعمدة تصدير كل صفحة في مكان واحد: يستخدمه التصدير والاستيراد معاً فلا يختلف شكل الملف بين الاتجاهين.
/// وسم "kind" في الملف هو ما يمنع استيراد ملف صفحة في صفحة أخرى.
/// </summary>
public static class ExportSpecs
{
    public const string KindInstalled = "installed";
    public const string KindUpdates = "updates";
    public const string KindLibrary = "library";

    public static IReadOnlyList<ExportColumn> InstalledColumns { get; } = new[]
    {
        new ExportColumn("Name", "name"),
        new ExportColumn("Publisher", "publisher"),
        new ExportColumn("Installed", "installed"),
        new ExportColumn("Latest", "version"),
        new ExportColumn("Status", "status"),
        new ExportColumn("Match", "confidence"),
        new ExportColumn("Package ID", "packageId"),
        new ExportColumn("Matched by", "matchedBy"),
        new ExportColumn("Source", "source"),
        new ExportColumn("Scope", "scope"),
        new ExportColumn("Location", "installLocation"),
        new ExportColumn("Uninstall command", "uninstallCommand"),
        new ExportColumn("Note", "note"),
        new ExportColumn("Library", "library"),
        new ExportColumn("Library version", "libraryVersion"),
        new ExportColumn("Library file", "libraryFile"),
    };

    public static IReadOnlyList<ExportColumn> UpdatesColumns { get; } = new[]
    {
        new ExportColumn("Package ID", "packageId"),
        new ExportColumn("Name", "name"),
        new ExportColumn("Installed", "installed"),
        new ExportColumn("Latest", "version"),
    };

    public static IReadOnlyList<ExportColumn> LibraryColumns { get; } = new[]
    {
        new ExportColumn("Name", "name"),
        new ExportColumn("Version", "version"),
        new ExportColumn("Publisher", "publisher"),
        new ExportColumn("Type", "type"),
        new ExportColumn("Architecture", "architecture"),
        new ExportColumn("Installer type", "installerType"),
        new ExportColumn("Size (bytes)", "sizeBytes"),
        new ExportColumn("Modified", "modified"),
        new ExportColumn("File name", "fileName"),
        new ExportColumn("Folder", "folder"),
        new ExportColumn("Full path", "path"),
        new ExportColumn("Identified", "identified"),
        new ExportColumn("Confidence", "confidence"),
        new ExportColumn("Read from", "source"),
        new ExportColumn("Winget ID", "wingetId"),
        new ExportColumn("ProductCode", "productCode"),
        new ExportColumn("SHA256", "sha256"),
        new ExportColumn("MD5", "md5"),
        new ExportColumn("xxHash64", "xxHash64"),
        new ExportColumn("Partial hash", "partialHash"),
        new ExportColumn("Partial hash covers", "partialHashCovers"),
        new ExportColumn("Installed", "installed"),
        new ExportColumn("Installed program", "installedProgram"),
        new ExportColumn("Installed version", "installedVersion"),
        new ExportColumn("Notes", "notes"),
    };

    /// <summary>اسم العمود المعروض (بأحرف صغيرة) ← مفتاح JSON، لكل أعمدة الصفحات الثلاث.</summary>
    internal static IReadOnlyDictionary<string, string> HeaderToKey { get; } = BuildHeaderMap();

    private static Dictionary<string, string> BuildHeaderMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in InstalledColumns.Concat(UpdatesColumns).Concat(LibraryColumns))
            map[c.Header.Trim()] = c.JsonKey;
        return map;
    }

    /// <summary>صفحة ملف التصدير بحسب أعمدة المفاتيح (JSON): null إن لم تُعرف.</summary>
    public static string? DetectKindFromKeys(IEnumerable<string> keys)
    {
        var k = new HashSet<string>(keys.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        if (k.Contains("path") && k.Contains("sizeBytes")) return KindLibrary;
        if (k.Contains("matchedBy") || k.Contains("uninstallCommand")) return KindInstalled;
        var updateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "packageId", "name", "installed", "version" };
        if (k.Contains("packageId") && k.IsSubsetOf(updateKeys)) return KindUpdates;
        return null;
    }

    /// <summary>صفحة ملف التصدير بحسب عناوين الأعمدة (CSV وExcel): null إن لم تُعرف.</summary>
    public static string? DetectKindFromHeaders(IEnumerable<string> headers)
    {
        var keys = headers.Select(h => HeaderToKey.TryGetValue(h.Trim(), out var key) ? key : h.Trim()).ToList();
        return DetectKindFromKeys(keys);
    }

    /// <summary>اسم الصفحة التي تستورد هذا النوع.</summary>
    public static string PageName(string? kind) => kind switch
    {
        KindInstalled => "Installed",
        KindUpdates => "Updates",
        KindLibrary => "Local",
        _ => "(unknown)",
    };
}
