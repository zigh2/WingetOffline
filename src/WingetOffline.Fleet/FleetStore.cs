using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WingetOffline.Fleet;

public sealed record Device(string Name, string Host, int Port, string Fingerprint);

public sealed record RefreshResult(bool Ok, string Message, InventorySnapshot? Inventory);

/// <summary>
/// جهة المسؤول: قائمة الأجهزة المقترَنة، آخر جرد لكل جهاز، هوية المسؤول، وسجل التدقيق.
/// يستعمله wgo-admin والواجهة الرسومية معاً، فيريان الأجهزة نفسها.
/// </summary>
public sealed class FleetStore
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();
    private X509Certificate2? _identity;

    public string Dir { get; }
    public string AuditPath => Path.Combine(Dir, "audit.jsonl");
    public AuditLog Audit { get; }

    public FleetStore(string? dir = null)
    {
        Dir = dir ?? Environment.GetEnvironmentVariable("WINGETOFFLINE_ADMIN_DATA")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WingetOffline", "Admin");
        Directory.CreateDirectory(Dir);
        Audit = new AuditLog(AuditPath);
    }

    public X509Certificate2 Identity => _identity ??= WingetOffline.Fleet.Identity.LoadOrCreate(Path.Combine(Dir, "admin-identity.pfx"), "wgo-admin");
    public string MyFingerprint => WingetOffline.Fleet.Identity.Fingerprint(Identity);

    private string DevicesPath => Path.Combine(Dir, "devices.json");
    private string InvDir => Path.Combine(Dir, "inventories");

    public List<Device> Devices()
    {
        lock (_lock)
            return File.Exists(DevicesPath)
                ? JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(DevicesPath), Pretty) ?? new()
                : new();
    }

    public Device? Find(string name) => Devices().FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>يضيف أو يستبدل جهازاً بالاسم نفسه. يرفض العناوين العامة والبصمات غير الكاملة.</summary>
    public Device Add(string name, string endpoint, string fingerprint)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64) throw new FormatException("The name must be 1-64 characters.");
        var (host, port) = ParseEndpoint(endpoint);
        if (System.Net.IPAddress.TryParse(host, out var ip) && !PrivateNetwork.IsAllowed(ip))
            throw new FormatException("Only private-network addresses (10.x, 172.16-31.x, 192.168.x) are accepted.");
        var fp = TrustStore.Normalize(fingerprint);
        var d = new Device(name, host, port, fp);
        lock (_lock)
        {
            var list = Devices();
            list.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Add(d);
            File.WriteAllText(DevicesPath, JsonSerializer.Serialize(list, Pretty));
        }
        Audit.Append("local", "add " + name, "ok");
        return d;
    }

    public bool Remove(string name)
    {
        bool removed;
        lock (_lock)
        {
            var list = Devices();
            removed = list.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) File.WriteAllText(DevicesPath, JsonSerializer.Serialize(list, Pretty));
        }
        Audit.Append("local", "remove " + name, removed ? "ok" : "not found");
        return removed;
    }

    public InventorySnapshot? LoadInventory(string name)
    {
        var p = InvPath(name);
        lock (_lock) return File.Exists(p) ? JsonSerializer.Deserialize<InventorySnapshot>(File.ReadAllText(p), FleetJson.Options) : null;
    }

    public void SaveInventory(string name, InventorySnapshot s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(InvDir);
            File.WriteAllText(InvPath(name), JsonSerializer.Serialize(s, FleetJson.Options));
        }
    }

    public FleetReport BuildReport() => FleetReport.Build(Devices().Select(d => (d.Name, LoadInventory(d.Name))));

    /// <summary>يتصل بجهاز (ping أو inventory)، يحفظ الجرد، ويكتب سجل التدقيق. لا يرمي استثناءً: يعيد رسالة مفهومة.</summary>
    public async Task<RefreshResult> RefreshAsync(Device d, string type, CancellationToken ct)
    {
        try
        {
            var r = await new AdminClient(Identity).SendAsync(d.Host, d.Port, d.Fingerprint, type, ct);
            Audit.Append("local", $"{type} {d.Name}", r.Ok ? "ok" : "error: " + r.Error);
            if (!r.Ok) return new RefreshResult(false, $"refused ({r.Error})", null);
            if (type == RequestTypes.Ping) return new RefreshResult(true, "online", null);
            SaveInventory(d.Name, r.Inventory!);
            return new RefreshResult(true, $"{r.Inventory!.Apps.Count} programs, {r.Inventory.Apps.Count(a => a.Status == "UpdateAvailable")} with updates", r.Inventory);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            Audit.Append("local", $"{type} {d.Name}", "failed: connection closed");
            return new RefreshResult(false, "the agent closed the connection. It probably does not trust this admin yet: give it this admin's fingerprint (wgo-agent trust <fingerprint>).", null);
        }
        catch (Exception ex)
        {
            Audit.Append("local", $"{type} {d.Name}", "failed: " + ex.GetType().Name);
            return new RefreshResult(false, ex is OperationCanceledException ? "timed out (is the agent running and the firewall open?)" : $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    public static (string host, int port) ParseEndpoint(string s)
    {
        s = s.Trim();
        var i = s.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(s[(i + 1)..], out var p) || p is < 1 or > 65535) throw new FormatException("The address must look like 192.168.1.20:51234.");
        return (s[..i].Trim('[', ']'), p);
    }

    private string InvPath(string name) => Path.Combine(InvDir, string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");
}
