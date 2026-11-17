using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;
using WingetOffline.Fleet;

// wgo-agent: يجيب المسؤول المقترَن بجرد البرامج المثبّتة. قراءة فقط. لا يثبّت ولا يزيل ولا ينفّذ أي أمر.
var data = Environment.GetEnvironmentVariable("WINGETOFFLINE_AGENT_DATA")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WingetOffline", "Agent");
Directory.CreateDirectory(data);
var cfgPath = Path.Combine(data, "config.json");
var cfg = File.Exists(cfgPath) ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(cfgPath)) ?? new() : new();
if (cfg.DeviceId == "") cfg.DeviceId = Guid.NewGuid().ToString("N");
if (cfg.Port == 0) cfg.Port = RandomNumberGenerator.GetInt32(49152, 65536);   // منفذ مختلف لكل جهاز، يُثبَّت عند أول تشغيل
File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfg));

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
var identity = Identity.LoadOrCreate(Path.Combine(data, "agent-identity.pfx"), Environment.MachineName);
var admins = new TrustStore(Path.Combine(data, "trusted-admins.json"));
var audit = new AuditLog(Path.Combine(data, "audit.jsonl"));

if (args.Length == 0 || args[0] is "help" or "-h" or "--help") { Help(); return 0; }
switch (args[0].ToLowerInvariant())
{
    case "--version": Console.WriteLine(version); return 0;
    case "id":
        Console.WriteLine($"Fingerprint: {Identity.Fingerprint(identity)}");
        Console.WriteLine($"Port:        {cfg.Port}");
        Console.WriteLine("On the admin PC:  wgo-admin add <name> <this-pc-address>:" + cfg.Port + " <fingerprint>");
        return 0;
    case "trust":   // trust <admin-fingerprint> [label]
        if (args.Length < 2) { Console.Error.WriteLine("usage: wgo-agent trust <admin-fingerprint> [label]"); return 1; }
        try { admins.Add(args[1], args.Length > 2 ? args[2] : "admin"); }
        catch (FormatException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        audit.Append("local", "trust " + TrustStore.Normalize(args[1])[..12], "ok");
        Console.WriteLine("Trusted. Compare this fingerprint with the one shown on the admin PC (wgo-admin id) before relying on it.");
        return 0;
    case "untrust":
        if (args.Length < 2) return 1;
        var removed = admins.Remove(args[1]);
        audit.Append("local", "untrust " + args[1][..Math.Min(12, args[1].Length)], removed ? "ok" : "not found");
        Console.WriteLine(removed ? "Removed." : "Not in the list.");
        return removed ? 0 : 1;
    case "list":
        foreach (var kv in admins.All()) Console.WriteLine($"{kv.Value,-20} {kv.Key}");
        return 0;
    case "run":
        return await Run(args);
    default:
        Console.Error.WriteLine("unknown command"); Help(); return 1;
}

async Task<int> Run(string[] a)
{
    if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("The agent scans Windows programs; run it on Windows."); return 1; }
    string? catalogPath = null;
    for (int i = 1; i < a.Length; i++)
    {
        if (a[i] == "--catalog" && i + 1 < a.Length) catalogPath = a[++i];
        else if (a[i] == "--port" && i + 1 < a.Length && int.TryParse(a[++i], out var p)) cfg.Port = p;
    }
    if (admins.All().Count == 0) { Console.Error.WriteLine("No admin is trusted yet. Run: wgo-agent trust <admin-fingerprint> <label>"); return 1; }

    CatalogStore? db = null;
    if (catalogPath != null) db = CatalogStore.Open(catalogPath);

    using var server = new AgentServer(identity, admins, audit, cfg.Port, (req, ct) =>
        Task.FromResult(req.Type == RequestTypes.Ping
            ? new FleetResponse { Ok = true }
            : new FleetResponse { Ok = true, Inventory = BuildInventory(db) }));
    server.Start();

    Console.WriteLine($"Agent {version} listening on port {server.Port} (private networks only, read-only).");
    Console.WriteLine("Windows Firewall: allow this program, not a port number — it can be changed:");
    Console.WriteLine($"  netsh advfirewall firewall add rule name=\"WingetOffline Agent\" dir=in action=allow program=\"{Environment.ProcessPath}\" profile=private");
    Console.WriteLine("Press Ctrl+C to stop.");
    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    await stop.Task;
    db?.Dispose();
    return 0;
}

[SupportedOSPlatform("windows")]
InventorySnapshot BuildInventory(CatalogStore? db)
{
    var apps = RegistryScanner.Scan();
    apps.AddRange(PortableScanner.ScanWingetPackages());
    var snap = new InventorySnapshot
    {
        DeviceId = cfg.DeviceId,
        HostName = Environment.MachineName,
        Os = Environment.OSVersion.VersionString,
        AgentVersion = version,
        ScannedUtc = DateTime.UtcNow,
        CatalogBuilt = db?.CreatedUtc?.ToString("yyyy-MM-dd"),
    };
    if (db == null)
    {
        foreach (var x in apps)
            snap.Apps.Add(new FleetApp { Name = x.DisplayName, Publisher = x.Publisher, Version = x.DisplayVersion, Architecture = x.Architecture, Scope = x.Scope });
        return snap;
    }
    List<MatchResult> results;
    var matcher = new Matcher(db);
    lock (db.Sync) results = apps.Select(matcher.Match).ToList();
    foreach (var m in results)
        snap.Apps.Add(new FleetApp
        {
            Name = m.App.DisplayName, Publisher = m.App.Publisher, Version = m.InstalledVersion ?? m.App.DisplayVersion,
            PackageId = m.PackageId, LatestVersion = m.LatestVersion, Status = m.Status.ToString(),
            Architecture = m.App.Architecture, Scope = m.App.Scope,
        });
    return snap;
}

static void Help() => Console.WriteLine("""
wgo-agent — answers a paired admin with the list of installed programs (read-only)

  id                                  show this PC's fingerprint and port (enter them on the admin PC)
  trust <admin-fingerprint> [label]   allow this admin (compare the fingerprint with `wgo-admin id` first)
  untrust <admin-fingerprint>         revoke an admin
  list                                admins that are trusted
  run [--catalog catalog.wglib] [--port N]
                                      start answering. With a catalog, updates are detected offline.

Private-network connections only. This version cannot install, uninstall or run anything.
""");

sealed class AgentConfig
{
    public string DeviceId { get; set; } = "";
    public int Port { get; set; }
}
