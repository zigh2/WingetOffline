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

    /// <summary>مثل "SHA256 of the first 10 MB" (فارغ إن لا بصمة جزئية).</summary>
    public static string PartialHashText(this LibraryEntry e) =>
        e.PartialHash == null ? "" : $"{e.PartialHashAlgorithm} of the first {FormatSize(e.PartialHashBytes ?? 0)}";

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
        var t = new ExportTable(ExportSpecs.KindLibrary, ExportSpecs.LibraryColumns, "Library");

        foreach (var e in entries)
        {
            InstalledLink? link = null;
            links?.TryGetValue(e.Path, out link);
            t.Add(e.DisplayName, e.Version ?? "", e.Publisher ?? "", e.KindText(), e.Architecture ?? "", e.InstallerType ?? "",
                  e.Size, e.ModifiedUtc.ToLocalTime(), e.FileName, e.Folder, e.Path, e.IdentifiedText(), e.ConfidenceText(),
                  e.Source, e.WingetId ?? "", e.ProductCode ?? "", e.Sha256 ?? "", e.Md5 ?? "", e.XxHash ?? "", e.PartialHash ?? "", e.PartialHashText(),
                  link.InstalledText(), link?.AppName ?? "", link?.InstalledVersion ?? "", e.Notes ?? "");
        }
        return t;
    }
}
