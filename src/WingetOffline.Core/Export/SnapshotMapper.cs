using System.Globalization;
using System.Text.RegularExpressions;
using WingetOffline.Core.Library;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Core.Export;

/// <summary>صف "Installed" أو "Updates" مستورد: نتيجة مطابقة مُعاد بناؤها من الملف (لا فحص حيّ).</summary>
public sealed class InstalledSnapshotRow
{
    public MatchResult Result { get; init; } = null!;
    public bool Ignored { get; init; }
    public LibraryMatch? Library { get; init; }
}

/// <summary>
/// يحوّل جدولاً مستورداً إلى كائنات الصفحة نفسها لتعرضه بالجدول والفلاتر ذاتها.
/// البيانات المستوردة <b>للعرض فقط</b>: لا يُنفَّذ منها شيء (انظر وضع اللقطة في الصفحات).
/// </summary>
public static class SnapshotMapper
{
    private static string G(Dictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v.Trim() : "";
    private static string? N(string s) => s.Length == 0 ? null : s;

    // ----------------------------------------------------- Installed / Updates

    public static List<InstalledSnapshotRow> ToInstalled(ImportedTable t)
    {
        var list = new List<InstalledSnapshotRow>();
        foreach (var r in t.Rows)
        {
            var name = G(r, "name");
            if (name.Length == 0) continue;

            var statusText = G(r, "status");
            var confidence = ParseConfidence(G(r, "confidence"));
            ParseStatus(statusText, out var status, out var newer, out var ignored);

            var app = new InstalledApp
            {
                DisplayName = name,
                Publisher = N(G(r, "publisher")),
                DisplayVersion = N(G(r, "installed")),
                InstallLocation = N(G(r, "installLocation")),
                UninstallString = N(G(r, "uninstallCommand")),   // للعرض والنسخ فقط، لا تنفيذ
                Source = G(r, "source").Length > 0 ? G(r, "source") : "Imported",
                Scope = G(r, "scope"),
            };
            var result = new MatchResult
            {
                App = app,
                PackageId = N(G(r, "packageId")),
                Confidence = confidence,
                Method = G(r, "matchedBy"),
                InstalledVersion = N(G(r, "installed")),
                LatestVersion = N(G(r, "version")),
                Status = status,
                Note = N(G(r, "note")),
                NewerThanCatalog = newer,
            };
            list.Add(new InstalledSnapshotRow
            {
                Result = result,
                Ignored = ignored,
                Library = ParseLibrary(G(r, "library"), G(r, "libraryVersion"), G(r, "libraryFile")),
            });
        }
        return list;
    }

    /// <summary>ملف "Updates": قائمة بسيطة (معرّف، اسم، المثبّت، الأحدث) كلها تحديثات موثوقة.</summary>
    public static List<InstalledSnapshotRow> ToUpdates(ImportedTable t)
    {
        var list = new List<InstalledSnapshotRow>();
        foreach (var r in t.Rows)
        {
            var id = G(r, "packageId");
            if (id.Length == 0) continue;
            var name = G(r, "name").Length > 0 ? G(r, "name") : id;
            list.Add(new InstalledSnapshotRow
            {
                Result = new MatchResult
                {
                    App = new InstalledApp { DisplayName = name, DisplayVersion = N(G(r, "installed")), Source = "Imported" },
                    PackageId = id,
                    Confidence = Confidence.High,
                    Method = "imported updates list",
                    InstalledVersion = N(G(r, "installed")),
                    LatestVersion = N(G(r, "version")),
                    Status = UpdateStatus.UpdateAvailable,
                },
            });
        }
        return list;
    }

    private static Confidence ParseConfidence(string s) => s.ToLowerInvariant() switch
    {
        "high" => Confidence.High,
        "medium" => Confidence.Medium,
        "low" => Confidence.Low,
        _ => Confidence.None,
    };

    /// <summary>عكس نصوص الحالة التي يعرضها الجدول (StatusText).</summary>
    private static void ParseStatus(string text, out UpdateStatus status, out bool newerThanCatalog, out bool ignored)
    {
        newerThanCatalog = false; ignored = false; status = UpdateStatus.Unknown;
        switch (text.ToLowerInvariant())
        {
            case "update available": status = UpdateStatus.UpdateAvailable; break;
            case "update ignored": status = UpdateStatus.UpdateAvailable; ignored = true; break;
            case "up to date": status = UpdateStatus.UpToDate; break;
            case "newer than catalog": status = UpdateStatus.UpToDate; newerThanCatalog = true; break;
        }
    }

    private static LibraryMatch? ParseLibrary(string text, string version, string file)
    {
        if (text.Length == 0 && file.Length == 0) return null;
        var rel = text.StartsWith("Newer", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.LibraryNewer
                : text.StartsWith("Older", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.LibraryOlder
                : text.StartsWith("Same", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.Same
                : LibraryRelation.Unknown;
        var best = new LibraryEntry { Path = file, Version = N(version), Source = "imported", Confidence = LibraryConfidence.Medium };
        return new LibraryMatch
        {
            Best = best, Relation = rel, Confidence = Confidence.Medium, Method = "imported",
            Candidates = new[] { best }, HasSameVersion = rel == LibraryRelation.Same,
        };
    }

    // ------------------------------------------------------------------ Local

    public static (List<LibraryEntry> Entries, Dictionary<string, InstalledLink> Links) ToLibrary(ImportedTable t)
    {
        var entries = new List<LibraryEntry>();
        var links = new Dictionary<string, InstalledLink>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in t.Rows)
        {
            var fileName = G(r, "fileName");
            var path = G(r, "path");
            if (path.Length == 0 && fileName.Length > 0) path = (G(r, "folder").Length > 0 ? G(r, "folder").TrimEnd('\\', '/') + "\\" : "") + fileName;
            if (path.Length == 0) continue;
            if (fileName.Length == 0) fileName = path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];

            var name = G(r, "name");
            var entry = new LibraryEntry
            {
                Path = path,
                Root = G(r, "folder"),
                Size = ParseLong(G(r, "sizeBytes")),
                ModifiedUtc = ParseDate(G(r, "modified")),
                ScannedUtc = t.CreatedUtc ?? DateTime.UtcNow,
                Kind = KindFromText(G(r, "type")),
                // الاسم المصدَّر هو اسم الملف حين لا يُعرف اسم منتج
                ProductName = name.Length == 0 || name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ? null : name,
                Version = N(G(r, "version")),
                Publisher = N(G(r, "publisher")),
                Architecture = N(G(r, "architecture")),
                InstallerType = N(G(r, "installerType")),
                ProductCode = N(G(r, "productCode")),
                WingetId = N(G(r, "wingetId")),
                Source = G(r, "source"),
                Confidence = ParseLibraryConfidence(G(r, "confidence")),
                Sha256 = N(G(r, "sha256")),
                Md5 = N(G(r, "md5")),
                XxHash = N(G(r, "xxHash64")),
                Notes = N(G(r, "notes")),
            };
            ParsePartial(G(r, "partialHash"), G(r, "partialHashCovers"), entry);
            entries.Add(entry);

            var installedText = G(r, "installed");
            if (installedText.Length > 0)
            {
                var rel = installedText.StartsWith("Newer", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.LibraryNewer
                        : installedText.StartsWith("Older", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.LibraryOlder
                        : installedText.Contains("same", StringComparison.OrdinalIgnoreCase) ? LibraryRelation.Same
                        : LibraryRelation.Unknown;
                links[path] = new InstalledLink(G(r, "installedProgram"), N(G(r, "installedVersion")), rel, Confidence.Medium);
            }
        }
        return (entries, links);
    }

    private static LibraryKind KindFromText(string t) => t.ToLowerInvariant() switch
    {
        "exe" => LibraryKind.Exe, "msi" => LibraryKind.Msi, "msix" => LibraryKind.Msix, "msp" => LibraryKind.Msp,
        "msu" => LibraryKind.Msu, "cab" => LibraryKind.Cab, "zip" => LibraryKind.Zip, "7z" => LibraryKind.SevenZip,
        "rar" => LibraryKind.Rar, "iso" => LibraryKind.Iso, "img" => LibraryKind.Image, _ => LibraryKind.Other,
    };

    private static LibraryConfidence ParseLibraryConfidence(string s) => s.ToLowerInvariant() switch
    {
        "high" => LibraryConfidence.High, "medium" => LibraryConfidence.Medium, "low" => LibraryConfidence.Low, _ => LibraryConfidence.None,
    };

    private static long ParseLong(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    /// <summary>Excel يخزّن التاريخ رقماً تسلسلياً، وCSV نصاً "yyyy-MM-dd HH:mm:ss"، وJSON نص ISO 8601؛ كلها بالتوقيت المحلي.</summary>
    private static DateTime ParseDate(string s)
    {
        if (s.Length == 0) return default;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 100000)
            return DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Local).ToUniversalTime();
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d)) return d;
        return default;
    }

    private static void ParsePartial(string hash, string covers, LibraryEntry e)
    {
        if (hash.Length == 0) return;
        e.PartialHash = hash;
        // مثل "XXH64 of the first 10 MB"
        var m = Regex.Match(covers, @"^(\w+)\s+of the first\s+([\d.]+)\s*(B|KB|MB|GB|TB)$", RegexOptions.IgnoreCase);
        if (!m.Success) return;
        e.PartialHashAlgorithm = m.Groups[1].Value.ToUpperInvariant();
        double n = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        long mult = m.Groups[3].Value.ToUpperInvariant() switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1 };
        e.PartialHashBytes = (long)(n * mult);
    }
}
