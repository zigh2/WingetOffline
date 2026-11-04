using WingetOffline.Core.Install;

namespace WingetOffline.UI.ViewModels;

/// <summary>صف في صفحة Installers: ملف مثبّت مع نتيجة التعرّف عليه.</summary>
public sealed class InstallerFileRow
{
    public InstallerFileRow(IndexedInstaller item) => Item = item;

    public IndexedInstaller Item { get; }
    public string FileName => Path.GetFileName(Item.FilePath);
    public string FilePath => Item.FilePath;
    public string Sha256 => Item.Sha256;
    public string SizeText => $"{Item.Size / 1048576.0:0.0} MB";
    public bool IsMatched => Item.IsMatched;
    public string StatusText => IsMatched ? "Recognised" : "Unknown";
    public string Package => Item.Best?.PackageId ?? "";
    public string Version => Item.Best?.Version ?? "";
    public string Type => Item.Best?.Type ?? "";
    public string Arch => Item.Best?.Arch ?? "";
    public string MatchNote => Item.Matches.Count > 1 ? $"{Item.Matches.Count} catalog entries share this hash." : "";
}
