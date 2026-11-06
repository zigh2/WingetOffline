using WingetOffline.Core.Scanning;

namespace WingetOffline.Core.Matching;

public enum Confidence { None = 0, Low = 1, Medium = 2, High = 3 }
public enum UpdateStatus { Unknown, UpToDate, UpdateAvailable }

public sealed class MatchResult
{
    public InstalledApp App { get; init; } = null!;
    public string? PackageId { get; init; }
    public string? PackageName { get; init; }
    public Confidence Confidence { get; init; }
    public string Method { get; init; } = "";
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public UpdateStatus Status { get; init; }
    public string? Note { get; init; }
    /// <summary>الإصدار المثبّت أحدث من آخر إصدار في الكتالوج (مثل نسخ alpha/beta غير المنشورة في winget).</summary>
    public bool NewerThanCatalog { get; init; }
}
