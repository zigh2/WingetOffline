using WingetOffline.Core.Export;

namespace WingetOffline.Core.Library;

/// <summary>نصوص العرض المشتركة بين صفحة Local وسطر الأوامر والتصدير، كي لا يختلف وصف الملف بين الأماكن.</summary>
public static class LibraryPresentation
{
    public static string KindText(this LibraryEntry e) => e.Kind switch
    {
        LibraryKind.SevenZip => "7z",
        LibraryKind.Image => "img",
        _ => e.Kind.ToString().ToLowerInvariant(),
    };

    public static string ConfidenceText(this LibraryEntry e) => e.Confidence == LibraryConfidence.None ? "" : e.Confidence.ToString();

    public static string IdentifiedText(this LibraryEntry e) => e.WingetId != null ? "winget match"
        : e.Confidence >= LibraryConfidence.Medium ? "Identified"
        : e.Confidence == LibraryConfidence.Low ? "File name only"
        : "Unknown";

    /// <summary>علاقة الملف بالبرنامج المثبّت (فارغ إن لم يطابق أي برنامج).</summary>
    public static string InstalledText(this InstalledLink? link) => link == null ? "" : link.Relation switch
    {
        LibraryRelation.Same => "Installed (same version)",
        LibraryRelation.LibraryNewer => "Newer than installed",
        LibraryRelation.LibraryOlder => "Older than installed",
        _ => "Installed (version unknown)",
    };

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>جدول تصدير المكتبة (Excel/CSV/JSON): الحجم رقم حقيقي والتاريخ تاريخ حقيقي في Excel.</summary>
public static class LibraryExport
{
    public static ExportTable ToTable(IEnumerable<LibraryEntry> entries, IReadOnlyDictionary<string, InstalledLink>? links = null)
    {
        var t = new ExportTable("library", new[]
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
            new ExportColumn("Installed", "installed"),
            new ExportColumn("Installed program", "installedProgram"),
            new ExportColumn("Installed version", "installedVersion"),
            new ExportColumn("Notes", "notes"),
        }, "Library");

        foreach (var e in entries)
        {
            InstalledLink? link = null;
            links?.TryGetValue(e.Path, out link);
            t.Add(e.DisplayName, e.Version ?? "", e.Publisher ?? "", e.KindText(), e.Architecture ?? "", e.InstallerType ?? "",
                  e.Size, e.ModifiedUtc.ToLocalTime(), e.FileName, e.Folder, e.Path, e.IdentifiedText(), e.ConfidenceText(),
                  e.Source, e.WingetId ?? "", e.ProductCode ?? "", e.Sha256 ?? "",
                  link.InstalledText(), link?.AppName ?? "", link?.InstalledVersion ?? "", e.Notes ?? "");
        }
        return t;
    }
}
