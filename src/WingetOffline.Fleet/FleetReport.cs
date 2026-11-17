using System.Net;
using System.Text;
using System.Text.Json;

namespace WingetOffline.Fleet;

/// <summary>تقرير مجمَّع عن أسطول الأجهزة، يُبنى من آخر جرد محفوظ لكل جهاز (بلا اتصال).</summary>
public sealed class FleetReport
{
    public sealed record DeviceRow(string Name, string HostName, string Os, int Apps, int Outdated, int Unknown, DateTime? ScannedUtc, string? CatalogBuilt);
    public sealed record OutdatedRow(string Device, string App, string? Version, string? LatestVersion, string? PackageId);
    public sealed record AppSpread(string App, int Devices, List<string> Versions);

    public DateTime GeneratedUtc { get; init; }
    public List<DeviceRow> Devices { get; init; } = new();
    public List<OutdatedRow> Outdated { get; init; } = new();
    public List<AppSpread> Spread { get; init; } = new();   // البرامج المثبّتة على أكثر من جهاز بإصدارات مختلفة
    public int TotalApps => Devices.Sum(d => d.Apps);

    /// <param name="staleAfter">اللقطة الأقدم من هذا تُعلَّم قديمة في التقرير النصي.</param>
    public static FleetReport Build(IEnumerable<(string Name, InventorySnapshot? Inv)> devices, DateTime? nowUtc = null)
    {
        var r = new FleetReport { GeneratedUtc = nowUtc ?? DateTime.UtcNow };
        var byApp = new Dictionary<string, List<(string dev, string? ver)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, inv) in devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (inv == null) { r.Devices.Add(new DeviceRow(name, "", "", 0, 0, 0, null, null)); continue; }
            r.Devices.Add(new DeviceRow(name, inv.HostName, inv.Os, inv.Apps.Count,
                inv.Apps.Count(a => a.Status == "UpdateAvailable"), inv.Apps.Count(a => a.Status == "Unknown"),
                inv.ScannedUtc, inv.CatalogBuilt));
            foreach (var a in inv.Apps)
            {
                if (a.Status == "UpdateAvailable") r.Outdated.Add(new OutdatedRow(name, a.Name, a.Version, a.LatestVersion, a.PackageId));
                var key = a.PackageId ?? a.Name;
                if (!byApp.TryGetValue(key, out var l)) byApp[key] = l = new();
                l.Add((name, a.Version));
            }
        }
        r.Outdated.Sort((x, y) => string.Compare(x.App, y.App, StringComparison.OrdinalIgnoreCase) is var c and not 0 ? c : string.Compare(x.Device, y.Device, StringComparison.OrdinalIgnoreCase));
        foreach (var (app, l) in byApp)
        {
            var vers = l.Select(x => x.ver ?? "?").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToList();
            if (l.Select(x => x.dev).Distinct().Count() > 1 && vers.Count > 1)
                r.Spread.Add(new AppSpread(app, l.Select(x => x.dev).Distinct().Count(), vers));
        }
        r.Spread.Sort((x, y) => y.Devices.CompareTo(x.Devices));
        return r;
    }

    // ───── JSON ─────
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ───── CSV (التحديثات المتاحة) ─────
    /// <summary>CSV بترميز UTF-8 مع BOM كي يفتحه Excel بالعربية. الخلايا التي تبدأ بـ = + - @ تُحصَّن من حقن الصيغ.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("\uFEFF");
        sb.AppendLine("Device,App,InstalledVersion,LatestVersion,PackageId");
        foreach (var o in Outdated) sb.AppendLine(string.Join(',', new[] { o.Device, o.App, o.Version, o.LatestVersion, o.PackageId }.Select(Csv)));
        return sb.ToString();
    }

    internal static string Csv(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ───── HTML مستقل (بلا سكربت ولا موارد خارجية) ─────
    public string ToHtml(TimeSpan? staleAfter = null)
    {
        var stale = staleAfter ?? TimeSpan.FromDays(7);
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="en" dir="auto"><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
<title>WingetOffline fleet report</title>
<style>
:root{color-scheme:light dark;--bg:#fff;--fg:#1b1b1b;--mut:#666;--line:#ddd;--warn:#b45309;--bad:#b91c1c}
@media(prefers-color-scheme:dark){:root{--bg:#161616;--fg:#eee;--mut:#9a9a9a;--line:#333;--warn:#fbbf24;--bad:#f87171}}
body{font:14px system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:2rem auto;max-width:1100px;padding:0 1rem}
h1{font-size:1.4rem}h2{font-size:1.1rem;margin-top:2rem}.mut{color:var(--mut)}
table{border-collapse:collapse;width:100%}th,td{text-align:start;padding:.35rem .6rem;border-bottom:1px solid var(--line)}
.warn{color:var(--warn)}.bad{color:var(--bad)}.wrap{overflow-x:auto}
</style></head><body>
""");
        sb.Append($"<h1>Fleet report</h1><p class=\"mut\">{Devices.Count} devices · {TotalApps} programs · {Outdated.Count} updates available · generated {GeneratedUtc:yyyy-MM-dd HH:mm} UTC</p>");

        sb.Append("<h2>Devices</h2><div class=\"wrap\"><table><tr><th>Name</th><th>Host</th><th>OS</th><th>Programs</th><th>Updates</th><th>Unmatched</th><th>Last scan (UTC)</th><th>Catalog</th></tr>");
        foreach (var d in Devices)
        {
            var age = d.ScannedUtc == null ? null : (TimeSpan?)(GeneratedUtc - d.ScannedUtc.Value);
            var scan = d.ScannedUtc == null ? "<span class=\"bad\">never</span>"
                : age > stale ? $"<span class=\"warn\">{d.ScannedUtc:yyyy-MM-dd HH:mm} (stale)</span>" : $"{d.ScannedUtc:yyyy-MM-dd HH:mm}";
            sb.Append($"<tr><td>{E(d.Name)}</td><td>{E(d.HostName)}</td><td>{E(d.Os)}</td><td>{d.Apps}</td><td>{(d.Outdated > 0 ? $"<span class=\"warn\">{d.Outdated}</span>" : "0")}</td><td>{d.Unknown}</td><td>{scan}</td><td>{E(d.CatalogBuilt)}</td></tr>");
        }
        sb.Append("</table></div>");

        sb.Append("<h2>Updates available</h2>");
        if (Outdated.Count == 0) sb.Append("<p class=\"mut\">None in the saved inventories.</p>");
        else
        {
            sb.Append("<div class=\"wrap\"><table><tr><th>Program</th><th>Device</th><th>Installed</th><th>Latest</th></tr>");
            foreach (var o in Outdated) sb.Append($"<tr><td>{E(o.App)}</td><td>{E(o.Device)}</td><td>{E(o.Version)}</td><td>{E(o.LatestVersion)}</td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Same program, different versions</h2>");
        if (Spread.Count == 0) sb.Append("<p class=\"mut\">None.</p>");
        else
        {
            sb.Append("<div class=\"wrap\"><table><tr><th>Program</th><th>Devices</th><th>Versions</th></tr>");
            foreach (var s in Spread.Take(200)) sb.Append($"<tr><td>{E(s.App)}</td><td>{s.Devices}</td><td>{E(string.Join(", ", s.Versions))}</td></tr>");
            sb.Append("</table></div>");
        }
        sb.Append("<p class=\"mut\">Built from saved inventories only; nothing here was fetched from the internet.</p></body></html>");
        return sb.ToString();
    }
}
