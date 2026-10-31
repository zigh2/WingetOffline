using WingetOffline.Core.Models;
using YamlDotNet.Serialization;

namespace WingetOffline.Core.Manifest;

/// <summary>يحلل مجلد إصدار واحد: manifests/a/Publisher/App/1.2.3/*.yaml</summary>
public static class ManifestParser
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static PackageVersion? ParseDirectory(string dir)
    {
        var files = Directory.GetFiles(dir, "*.yaml");
        if (files.Length == 0) return null;

        Dictionary<string, object?>? version = null, installer = null, defLocale = null, singleton = null;
        var locales = new List<Dictionary<string, object?>>();

        foreach (var f in files)
        {
            var d = Load(f);
            if (d == null) continue;
            switch (Str(d, "ManifestType"))
            {
                case "version": version = d; break;
                case "installer": installer = d; break;
                case "defaultLocale": defLocale = d; break;
                case "locale": locales.Add(d); break;
                case "singleton": singleton = d; break;
            }
        }

        var meta = singleton ?? defLocale;
        var inst = singleton ?? installer;
        var head = singleton ?? version ?? meta;
        if (meta == null || inst == null || head == null) return null;

        var pv = new PackageVersion
        {
            Id = Str(head, "PackageIdentifier") ?? "",
            Version = Str(head, "PackageVersion") ?? Path.GetFileName(dir),
            Name = Str(meta, "PackageName") ?? "",
            Publisher = Str(meta, "Publisher") ?? "",
            Description = Str(meta, "ShortDescription") ?? Str(meta, "Description"),
            License = Str(meta, "License"),
            Homepage = Str(meta, "PackageUrl") ?? Str(meta, "PublisherUrl"),
            Moniker = Str(meta, "Moniker"),
            Locale = Str(meta, "PackageLocale"),
            ReleaseDate = Str(inst, "ReleaseDate"),
            MinOsVersion = Str(inst, "MinimumOSVersion"),
            Tags = StrList(meta, "Tags"),
        };
        if (pv.Id.Length == 0) return null;

        // الحقول في جذر ملف المثبّتات هي قيم افتراضية لكل عنصر في Installers
        if (inst.TryGetValue("Installers", out var arr) && arr is List<object?> list)
        {
            foreach (var item in list.OfType<Dictionary<string, object?>>())
                pv.Installers.Add(BuildInstaller(inst, item));
        }
        return pv;
    }

    private static InstallerInfo BuildInstaller(Dictionary<string, object?> root, Dictionary<string, object?> e)
    {
        string? G(string k) => Str(e, k) ?? Str(root, k);
        Dictionary<string, object?>? Map(string k) =>
            (e.TryGetValue(k, out var v) ? v : root.TryGetValue(k, out var v2) ? v2 : null) as Dictionary<string, object?>;
        List<string> L(string k) => e.ContainsKey(k) ? StrList(e, k) : StrList(root, k);

        var sw = Map("InstallerSwitches");
        var info = new InstallerInfo
        {
            Architecture = G("Architecture") ?? "",
            InstallerType = G("InstallerType") ?? "",
            NestedInstallerType = G("NestedInstallerType"),
            Scope = G("Scope") ?? "",
            Url = Str(e, "InstallerUrl") ?? "",
            Sha256 = Str(e, "InstallerSha256") ?? "",
            ProductCode = G("ProductCode"),
            UpgradeCode = G("UpgradeCode"),
            Silent = sw == null ? null : Str(sw, "Silent"),
            SilentWithProgress = sw == null ? null : Str(sw, "SilentWithProgress"),
            Custom = sw == null ? null : Str(sw, "Custom"),
            Commands = L("Commands"),
        };

        var nested = e.ContainsKey("NestedInstallerFiles") ? e["NestedInstallerFiles"] : root.GetValueOrDefault("NestedInstallerFiles");
        if (nested is List<object?> nl)
            foreach (var n in nl.OfType<Dictionary<string, object?>>())
            {
                var p = Str(n, "RelativeFilePath");
                if (p != null) info.NestedFiles.Add(p);
                info.PortableAlias ??= Str(n, "PortableCommandAlias");
            }

        var arp = e.ContainsKey("AppsAndFeaturesEntries") ? e["AppsAndFeaturesEntries"] : root.GetValueOrDefault("AppsAndFeaturesEntries");
        if (arp is List<object?> al)
            foreach (var a in al.OfType<Dictionary<string, object?>>())
                info.AppsAndFeatures.Add(new AppsAndFeaturesEntry
                {
                    DisplayName = Str(a, "DisplayName"),
                    Publisher = Str(a, "Publisher"),
                    DisplayVersion = Str(a, "DisplayVersion"),
                    ProductCode = Str(a, "ProductCode"),
                    UpgradeCode = Str(a, "UpgradeCode"),
                    InstallerType = Str(a, "InstallerType"),
                });
        return info;
    }

    private static Dictionary<string, object?>? Load(string path)
    {
        try
        {
            using var r = new StreamReader(path);
            var raw = Yaml.Deserialize<object?>(r);
            return Normalize(raw) as Dictionary<string, object?>;
        }
        catch { return null; } // مانيفست تالف: نتجاوزه ولا نوقف البناء
    }

    private static object? Normalize(object? o) => o switch
    {
        IDictionary<object, object> d => d.ToDictionary(k => k.Key.ToString() ?? "", v => Normalize(v.Value)),
        IList<object> l => l.Select(Normalize).ToList(),
        _ => o?.ToString()
    };

    private static string? Str(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    private static List<string> StrList(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is List<object?> l ? l.OfType<string>().ToList() : new();
}
