using WingetOffline.Core.Install;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;

namespace WingetOffline.UI.ViewModels;

/// <summary>صف مسطّح لعرض نتيجة مطابقة في الجدول.</summary>
public sealed class InstalledRow
{
    private readonly Lazy<bool> _hasUninstaller;

    public InstalledRow(MatchResult result, bool ignored = false, LibraryMatch? library = null)
    {
        Result = result;
        IsIgnored = ignored;
        Library = library;
        _hasUninstaller = new Lazy<bool>(() => UninstallPlanner.Plan(result.App) != null);
    }

    /// <summary>هل أُوقف تتبّع تحديثات هذه الحزمة بطلب المستخدم؟</summary>
    public bool IsIgnored { get; }
    /// <summary>برامج بلا أمر إزالة صالح (فلتر على نمط "invalid uninstaller" في BCU).</summary>
    public bool HasUninstaller => _hasUninstaller.Value;

    public MatchResult Result { get; }

    /// <summary>ملفات المكتبة المحلية المطابقة لهذا البرنامج (null إن لم يوجد).</summary>
    public LibraryMatch? Library { get; }
    public bool InLibrary => Library != null;
    public string LibraryText => Library?.ShortText ?? "";
    public string LibraryVersion => Library?.Best.Version ?? "";
    public string LibraryFile => Library?.Best.Path ?? "";
    public string LibrarySummary => Library == null
        ? "No matching file in your library"
        : $"{Library.ShortText} · {Library.Candidates.Count} file(s) · matched by {Library.Method} ({Library.Confidence})";
    public string Name => Result.App.DisplayName;
    public string Publisher => Result.App.Publisher ?? "";
    public string InstalledVersion => Result.InstalledVersion ?? "";
    public string LatestVersion => Result.LatestVersion ?? "";
    public UpdateStatus Status => Result.Status;
    public bool IsMatched => Result.Confidence != Confidence.None;
    /// <summary>تحديث موثوق: مطابقة Medium فما فوق (مطابقات Low تحتاج مراجعة يدوية).</summary>
    public bool IsReliableUpdate => !IsIgnored && Result.Status == UpdateStatus.UpdateAvailable && Result.Confidence >= Confidence.Medium;
    public string StatusText => !IsMatched ? "Not matched"
        : IsIgnored && Result.Status == UpdateStatus.UpdateAvailable ? "Update ignored"
        : Result.NewerThanCatalog ? "Newer than catalog"
        : Result.Status switch
    {
        UpdateStatus.UpdateAvailable => "Update available",
        UpdateStatus.UpToDate => "Up to date",
        _ => "Unknown version",
    };
    public string ConfidenceText => IsMatched ? Result.Confidence.ToString() : "";
    public string MatchSummary => IsMatched ? $"{Result.Method} · {Result.Confidence}" : "No match";
    public string PackageId => Result.PackageId ?? "";
    public string Method => Result.Method;
    public string Note => Result.Note ?? "";
    public string Source => Result.App.Source;
    public string Scope => Result.App.Scope;
    public string InstallLocation => Result.App.InstallLocation ?? "";
    public string UninstallCommand => Result.App.QuietUninstallString ?? Result.App.UninstallString ?? "";
}
