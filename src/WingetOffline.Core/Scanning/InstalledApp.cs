namespace WingetOffline.Core.Scanning;

/// <summary>برنامج مكتشف على الجهاز (من السجل أو مجلد winget أو ملف تنفيذي محمول).</summary>
public sealed class InstalledApp
{
    public string Source { get; set; } = "";          // Registry | WinGetFolder | Portable
    public string DisplayName { get; set; } = "";
    public string? Publisher { get; set; }
    public string? DisplayVersion { get; set; }
    public string? ProductCode { get; set; }          // اسم مفتاح السجل إن كان GUID (أو GUID_is1)
    public string? UninstallString { get; set; }
    public string? QuietUninstallString { get; set; }
    public string? InstallLocation { get; set; }
    public string Scope { get; set; } = "";           // Machine | User
    public string Architecture { get; set; } = "";
    public string? RegistryKeyName { get; set; }
    public string? ExeName { get; set; }              // للمحمولة: اسم الملف بدون .exe
    public string? ExePath { get; set; }
    public string? HintPackageId { get; set; }        // معرّف الحزمة المستخرج من اسم مجلد winget
}
