using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.Core.Packing;
using WingetOffline.UI.ViewModels;

namespace WingetOffline.UI.Services;

/// <summary>يحوّل صفوف الواجهة إلى <see cref="ExportTable"/> (مصدر واحد للصيغ الثلاث).</summary>
public static class ExportBuilders
{
    /// <summary>
    /// صفحة Installed. مفاتيح JSON (name, installed, version, confidence, packageId) هي نفسها التي
    /// يقرؤها <c>Builder pack --from-scan</c>، فالملف يصلح مباشرة لتجهيز مثبّتات على جهاز متصل.
    /// </summary>
    public static ExportTable Installed(IEnumerable<InstalledRow> rows)
    {
        var t = new ExportTable("installed", new[]
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
        }, "Installed");

        foreach (var r in rows)
            t.Add(r.Name, r.Publisher, r.InstalledVersion, r.LatestVersion, r.StatusText, r.ConfidenceText,
                  r.PackageId, r.Method, r.Source, r.Scope, r.InstallLocation, r.UninstallCommand, r.Note,
                  r.LibraryText, r.LibraryVersion, r.LibraryFile);
        return t;
    }

    /// <summary>قائمة التحديثات المطلوبة بصيغة جدول (للقراءة البشرية: Excel وCSV).</summary>
    public static ExportTable Updates(IEnumerable<UpdateEntry> entries)
    {
        var t = new ExportTable("updates", new[]
        {
            new ExportColumn("Package ID", "packageId"),
            new ExportColumn("Name", "name"),
            new ExportColumn("Installed", "installed"),
            new ExportColumn("Latest", "version"),
        }, "Updates");
        foreach (var e in entries) t.Add(e.PackageId, e.Name, e.Installed, e.Version);
        return t;
    }

    /// <summary>صفحة Local: الجدول نفسه الذي يصدّره سطر الأوامر (wgo library).</summary>
    public static ExportTable Library(IEnumerable<LocalRow> rows)
    {
        var list = rows.ToList();
        var links = list.Where(r => r.Installed != null).ToDictionary(r => r.Path, r => r.Installed!, StringComparer.OrdinalIgnoreCase);
        return LibraryExport.ToTable(list.Select(r => r.Entry), links);
    }
}
