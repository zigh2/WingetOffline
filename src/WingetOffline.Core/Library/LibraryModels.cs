using System.Text.Json.Serialization;

namespace WingetOffline.Core.Library;

/// <summary>نوع الملف في المكتبة (من الامتداد).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LibraryKind { Exe, Msi, Msix, Msp, Msu, Cab, Zip, SevenZip, Rar, Iso, Image, Other }

/// <summary>درجة الثقة في ما استُخلص عن الملف.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LibraryConfidence { None = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>ملف واحد في المكتبة المحلية مع كل ما عُرف عنه. يُحفظ كما هو في library.json.</summary>
public sealed class LibraryEntry
{
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime ScannedUtc { get; set; }
    /// <summary>نسخة قارئ الملفات التي أنتجت هذا السجل؛ ترتفع عند تحسين القراءة فيُعاد فحص الملفات تلقائياً.</summary>
    public int ScanVersion { get; set; }
    public LibraryKind Kind { get; set; }

    public string? ProductName { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Architecture { get; set; }
    /// <summary>Inno Setup / NSIS / WiX Burn / InstallShield ... (كشف بالتوقيع، قد يكون تقريبياً).</summary>
    public string? InstallerType { get; set; }
    public string? ProductCode { get; set; }
    public string? UpgradeCode { get; set; }
    /// <summary>هوية حزمة MSIX (Name).</summary>
    public string? PackageName { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }

    /// <summary>من أين جاءت المعلومات: msi / pe / msix / iso / zip / filename (يُجمع بـ +).</summary>
    public string Source { get; set; } = "";
    public LibraryConfidence Confidence { get; set; }

    /// <summary>بصمة SHA256 للملف <b>كاملاً</b> (وحدها تطابق الكتالوج).</summary>
    public string? Sha256 { get; set; }
    /// <summary>بصمة MD5 للملف كاملاً (إن اخترتها خوارزمية).</summary>
    public string? Md5 { get; set; }
    /// <summary>بصمة أول جزء من الملف الكبير (الوضع السريع): تعرّف الملف لاحقاً لكنها لا تطابق الكتالوج.</summary>
    public string? PartialHash { get; set; }
    public string? PartialHashAlgorithm { get; set; }
    /// <summary>عدد البايتات الأولى التي شملتها البصمة الجزئية.</summary>
    public long? PartialHashBytes { get; set; }
    /// <summary>معرّف حزمة winget وإصدارها إن طابقت البصمة الكتالوج.</summary>
    public string? WingetId { get; set; }
    public string? WingetVersion { get; set; }
    public string? Error { get; set; }

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    [JsonIgnore] public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    /// <summary>هل عُرف الملف بما يكفي لعرضه باسم منتج؟</summary>
    [JsonIgnore] public bool IsIdentified => Confidence >= LibraryConfidence.Medium || WingetId != null;

    /// <summary>الاسم المعروض: اسم المنتج أو اسم الملف.</summary>
    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(ProductName) ? ProductName! : FileName;
}

/// <summary>ما استخرجه قارئ واحد (PE/MSI/MSIX/ISO/ZIP) قبل دمجه في <see cref="LibraryEntry"/>.</summary>
public sealed class FileFacts
{
    public string? ProductName { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public string? Architecture { get; set; }
    public string? InstallerType { get; set; }
    public string? ProductCode { get; set; }
    public string? UpgradeCode { get; set; }
    public string? PackageName { get; set; }
    public string? Description { get; set; }
    public string? OriginalFileName { get; set; }
    public string? Notes { get; set; }

    public bool HasAnything => ProductName != null || Version != null || Publisher != null || ProductCode != null || PackageName != null;
}
