using WingetOffline.Core.Library;

namespace WingetOffline.UI.ViewModels;

/// <summary>صف مسطّح لملف في المكتبة المحلية (صفحة Local).</summary>
public sealed class LocalRow
{
    public LocalRow(LibraryEntry entry, InstalledLink? installed)
    {
        Entry = entry;
        Installed = installed;
    }

    public LibraryEntry Entry { get; }
    /// <summary>البرنامج المثبّت الذي يطابق هذا الملف (إن وُجد).</summary>
    public InstalledLink? Installed { get; }

    public string Name => Entry.DisplayName;
    public string Version => Entry.Version ?? "";
    public string Publisher => Entry.Publisher ?? "";
    public string Architecture => Entry.Architecture ?? "";
    public string InstallerType => Entry.InstallerType ?? "";
    public string FileName => Entry.FileName;
    public string Folder => Entry.Folder;
    public string Path => Entry.Path;
    public long SizeBytes => Entry.Size;
    public string SizeText => FormatSize(Entry.Size);
    public DateTime Modified => Entry.ModifiedUtc.ToLocalTime();
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
    public string Notes => Entry.Notes ?? "";
    public string Error => Entry.Error ?? "";
    public string WingetId => Entry.WingetId ?? "";
    public string Sha256 => Entry.Sha256 ?? "";
    public string Md5 => Entry.Md5 ?? "";
    public string XxHash => Entry.XxHash ?? "";
    public string PartialHash => Entry.PartialHash ?? "";
    public string PartialHashText => Entry.PartialHashText();
    public string ProductCode => Entry.ProductCode ?? "";
    public string SourceText => Entry.Source;

    // النصوص مشتركة مع سطر الأوامر والتصدير (LibraryPresentation)
    public string TypeText => Entry.KindText();
    public string ConfidenceText => Entry.ConfidenceText();
    public string IdentifiedText => Entry.IdentifiedText();
    public string InstalledText => Installed.InstalledText();

    public string InstalledDetail => Installed == null ? "Not matched to an installed program"
        : $"{Installed.AppName} · installed {Installed.InstalledVersion ?? "?"} · matched with {Installed.Confidence} confidence";

    internal static string FormatSize(long bytes) => LibraryPresentation.FormatSize(bytes);
}
