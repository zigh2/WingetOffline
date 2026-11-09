using System.IO.Compression;

namespace WingetOffline.Core.Library;

/// <summary>
/// يتعرّف على ملف واحد: يختار القارئ المناسب لنوعه، ثم يكمّل ما نقص من اسم الملف.
/// لا يغيّر الملف أبداً ولا يشغّله.
/// </summary>
public static class LibraryScanner
{
    /// <summary>يرتفع عند تحسين القراءة فيُعاد فحص الملفات المخزّنة تلقائياً.</summary>
    public const int Version = 1;

    public static readonly string[] Extensions =
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".msu", ".cab",
        ".zip", ".7z", ".rar", ".iso", ".img",
    };

    private const long MaxInnerBytes = 64L * 1024 * 1024;

    // أسماء عامة لأدوات بناء المثبّتات لا تعبّر عن البرنامج
    private static readonly string[] GenericProductNames =
    {
        "Inno Setup", "Nullsoft Install System", "NSIS", "InstallShield", "Windows Installer XML", "WiX Toolset",
        "Advanced Installer", "Setup Factory", "7-Zip SFX", "Microsoft Windows Operating System",
    };

    public static bool IsLibraryFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static LibraryKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".exe" => LibraryKind.Exe,
        ".msi" => LibraryKind.Msi,
        ".msix" or ".msixbundle" or ".appx" or ".appxbundle" => LibraryKind.Msix,
        ".msp" => LibraryKind.Msp,
        ".msu" => LibraryKind.Msu,
        ".cab" => LibraryKind.Cab,
        ".zip" => LibraryKind.Zip,
        ".7z" => LibraryKind.SevenZip,
        ".rar" => LibraryKind.Rar,
        ".iso" => LibraryKind.Iso,
        ".img" => LibraryKind.Image,
        _ => LibraryKind.Other,
    };

    /// <summary>يبني سجلاً كاملاً لملف. لا يرمي استثناءً: الأخطاء تُسجَّل في Error.</summary>
    public static LibraryEntry Identify(FileInfo file, string root)
    {
        var entry = new LibraryEntry
        {
            Path = file.FullName,
            Root = root,
            Size = file.Length,
            ModifiedUtc = file.LastWriteTimeUtc,
            ScannedUtc = DateTime.UtcNow,
            ScanVersion = Version,
            Kind = KindOf(file.FullName),
        };

        var sources = new List<string>();
        FileFacts? facts = null;
        try
        {
            switch (entry.Kind)
            {
                case LibraryKind.Exe:
                    facts = PeVersionReader.Read(file.FullName);
                    if (facts != null) sources.Add("pe");
                    break;
                case LibraryKind.Msi:
                    facts = MsiReader.Read(file.FullName);
                    if (facts != null) sources.Add("msi");
                    break;
                case LibraryKind.Msix:
                    facts = MsixReader.Read(file.FullName);
                    if (facts != null) sources.Add("msix");
                    break;
                case LibraryKind.Zip:
                    facts = ReadZip(file.FullName);
                    if (facts != null) sources.Add("zip");
                    break;
                case LibraryKind.Iso:
                    var iso = IsoReader.Read(file.FullName);
                    if (iso != null)
                    {
                        facts = new FileFacts { Publisher = iso.Publisher, Notes = $"Volume label: {iso.VolumeId}" + (iso.Created is { } c ? $" · created {c:yyyy-MM-dd}" : "") };
                        sources.Add("iso");
                    }
                    break;
                case LibraryKind.SevenZip:
                    entry.Notes = "7z contents are not read; identified from the file name.";
                    break;
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.GetType().Name + ": " + ex.Message;
        }

        Merge(entry, facts, sources, file.Name);
        return entry;
    }

    /// <summary>يدمج ما قرأه القارئ مع اسم الملف ويحسب الثقة.</summary>
    internal static void Merge(LibraryEntry e, FileFacts? facts, List<string> sources, string fileName)
    {
        var parsed = FileNameParser.Parse(fileName);

        if (facts != null)
        {
            e.ProductName = facts.ProductName;
            e.Publisher = facts.Publisher;
            e.Version = facts.Version;
            e.Architecture = facts.Architecture;
            e.InstallerType = facts.InstallerType;
            e.ProductCode = facts.ProductCode;
            e.UpgradeCode = facts.UpgradeCode;
            e.PackageName = facts.PackageName;
            e.Description = facts.Description;
            if (!string.IsNullOrEmpty(facts.Notes)) e.Notes = facts.Notes;
        }

        // اسم عام لأداة البناء ليس اسم البرنامج
        if (e.ProductName != null && GenericProductNames.Any(g => e.ProductName.Contains(g, StringComparison.OrdinalIgnoreCase)))
            e.ProductName = null;

        // وصف الملف مثل "VLC media player Setup" يصلح اسماً احتياطياً
        if (e.ProductName == null && e.Kind == LibraryKind.Exe && !string.IsNullOrWhiteSpace(e.Description))
        {
            var d = System.Text.RegularExpressions.Regex.Replace(e.Description!, @"\s+(setup|installer|installation)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (d.Length >= 3 && !GenericProductNames.Any(g => d.Contains(g, StringComparison.OrdinalIgnoreCase)) && !d.Equals("setup", StringComparison.OrdinalIgnoreCase))
            {
                e.ProductName = d;
                sources.Add("description");
            }
        }

        bool fromContent = e.ProductName != null;
        bool usedName = false;
        if (e.ProductName == null && parsed.Name != null) { e.ProductName = parsed.Name; usedName = true; }
        if (e.Version == null && parsed.Version != null) { e.Version = parsed.Version; usedName = true; }
        if (e.Architecture == null && parsed.Architecture != null) { e.Architecture = parsed.Architecture; usedName = true; }
        if (usedName) sources.Add("filename");
        e.Source = string.Join("+", sources.Distinct());

        e.Confidence = Score(e, fromContent, facts);
    }

    private static LibraryConfidence Score(LibraryEntry e, bool nameFromContent, FileFacts? facts)
    {
        if (e.ProductName == null) return LibraryConfidence.None;
        bool structured = e.Kind is LibraryKind.Msi or LibraryKind.Msix;
        if (structured && facts != null && nameFromContent && e.Version != null) return LibraryConfidence.High;
        if (nameFromContent && e.Version != null && e.Publisher != null) return LibraryConfidence.High;
        if (nameFromContent && e.Version != null) return LibraryConfidence.Medium;
        if (nameFromContent) return LibraryConfidence.Medium;
        // اسم من اسم الملف فقط
        return LibraryConfidence.Low;
    }

    // ------------------------------------------------------------------ ZIP

    private static FileFacts? ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var files = zip.Entries.Where(x => x.Name.Length > 0).ToList();
        if (files.Count == 0) return new FileFacts { Notes = "Empty archive" };

        string[] installerExts = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };
        int Depth(ZipArchiveEntry x) => x.FullName.Count(c => c == '/');
        var inner = files.Where(x => installerExts.Contains(Path.GetExtension(x.Name), StringComparer.OrdinalIgnoreCase) && Depth(x) <= 2).ToList();

        var facts = new FileFacts
        {
            Notes = $"{files.Count} file(s)" + (inner.Count > 0 ? "; contains " + string.Join(", ", inner.Take(4).Select(x => x.Name)) + (inner.Count > 4 ? ", ..." : "") : ""),
        };

        // برنامج محمول أو مثبّت داخل zip: نقرأ إصدار أكبر exe/msi قريب من الجذر
        var candidate = inner
            .Where(x => !x.Name.Contains("unins", StringComparison.OrdinalIgnoreCase) && x.Length is > 0 and <= MaxInnerBytes && Depth(x) <= 1)
            .OrderByDescending(x => x.Length).FirstOrDefault();
        if (candidate != null)
        {
            using var ms = new MemoryStream();
            using (var es = candidate.Open()) es.CopyTo(ms);
            ms.Position = 0;
            var ext = Path.GetExtension(candidate.Name).ToLowerInvariant();
            FileFacts? innerFacts = ext == ".msi" ? MsiReader.Read(ms) : ext == ".exe" ? PeVersionReader.Read(ms) : null;
            if (innerFacts != null)
            {
                facts.ProductName = innerFacts.ProductName;
                facts.Publisher = innerFacts.Publisher;
                facts.Version = innerFacts.Version;
                facts.Architecture = innerFacts.Architecture;
                facts.ProductCode = innerFacts.ProductCode;
                facts.UpgradeCode = innerFacts.UpgradeCode;
                facts.Description = innerFacts.Description;
                facts.InstallerType = "ZIP › " + (innerFacts.InstallerType ?? ext.TrimStart('.').ToUpperInvariant());
            }
        }
        return facts;
    }
}
