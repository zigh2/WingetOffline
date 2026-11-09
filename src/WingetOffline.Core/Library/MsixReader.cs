using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WingetOffline.Core.Library;

/// <summary>يقرأ هوية الحزمة من AppxManifest.xml (msix/appx) أو AppxBundleManifest.xml (الحزم المجمّعة).</summary>
public static class MsixReader
{
    public static FileFacts? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = zip.GetEntry("AppxManifest.xml");
            bool bundle = false;
            if (manifest == null) { manifest = zip.GetEntry("AppxMetadata/AppxBundleManifest.xml"); bundle = manifest != null; }
            if (manifest == null) return null;

            XDocument doc;
            using (var s = manifest.Open()) doc = XDocument.Load(s);

            var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            if (identity == null) return null;

            var facts = new FileFacts
            {
                PackageName = Attr(identity, "Name"),
                Version = Attr(identity, "Version"),
                Publisher = PublisherCn(Attr(identity, "Publisher")),
                Architecture = NormalizeArch(Attr(identity, "ProcessorArchitecture")),
                InstallerType = bundle ? "MSIX bundle" : "MSIX",
            };

            if (!bundle)
            {
                var props = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
                string? display = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
                string? pub = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
                // القيم ms-resource:... تحتاج ملف موارد؛ نتجاهلها ونكتفي بهوية الحزمة
                if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) facts.ProductName = display.Trim();
                if (!string.IsNullOrWhiteSpace(pub) && !pub.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) facts.Publisher = pub.Trim();
            }
            else
            {
                var archs = doc.Descendants().Where(e => e.Name.LocalName == "Package" && Attr(e, "Type") != "resource")
                    .Select(e => NormalizeArch(Attr(e, "Architecture"))).Where(a => a != null).Distinct().ToList();
                if (archs.Count > 0) facts.Architecture = string.Join(",", archs);
            }
            facts.ProductName ??= facts.PackageName;
            return facts;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value is { Length: > 0 } v ? v : null;

    private static string? NormalizeArch(string? a) => a?.ToLowerInvariant() switch
    {
        "x64" => "x64", "x86" => "x86", "arm64" => "arm64", "arm" => "arm", "neutral" => "neutral", null or "" => null, var o => o,
    };

    /// <summary>"CN=Microsoft Corporation, O=Microsoft Corporation, ..." ← "Microsoft Corporation".</summary>
    internal static string? PublisherCn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        var m = Regex.Match(dn, "CN=(?:\"([^\"]+)\"|([^,]+))");
        var v = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : dn.Trim();
        return v.Length > 0 ? v : null;
    }
}
