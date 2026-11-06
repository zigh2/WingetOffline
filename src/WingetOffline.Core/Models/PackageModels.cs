namespace WingetOffline.Core.Models;

public sealed class AppsAndFeaturesEntry
{
    public string? DisplayName { get; set; }
    public string? Publisher { get; set; }
    public string? DisplayVersion { get; set; }
    public string? ProductCode { get; set; }
    public string? UpgradeCode { get; set; }
    public string? InstallerType { get; set; }
}

public sealed class InstallerInfo
{
    public string Architecture { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public string? NestedInstallerType { get; set; }
    public string Scope { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Silent { get; set; }
    public string? SilentWithProgress { get; set; }
    public string? Custom { get; set; }
    public string? ProductCode { get; set; }
    public string? UpgradeCode { get; set; }
    public string? PortableAlias { get; set; }
    public List<string> Commands { get; set; } = new();
    public List<string> NestedFiles { get; set; } = new();
    public List<AppsAndFeaturesEntry> AppsAndFeatures { get; set; } = new();
}

/// <summary>إصدار واحد من حزمة winget (مانيفست واحد).</summary>
public sealed class PackageVersion
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string? Description { get; set; }
    public string? License { get; set; }
    public string? Homepage { get; set; }
    public string? Moniker { get; set; }
    public string? ReleaseDate { get; set; }
    public string? MinOsVersion { get; set; }
    public string? Locale { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>معرّفات الحزم التي تعتمد عليها هذه الحزمة (PackageDependencies في المانيفست).</summary>
    public List<string> Dependencies { get; set; } = new();
    public List<InstallerInfo> Installers { get; set; } = new();
}
