using System.Net;
using WingetOffline.Fleet;

namespace WingetOffline.Tests;

public class FleetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-fleet-" + Guid.NewGuid().ToString("N"));
    public FleetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Rig : IDisposable
    {
        public required System.Security.Cryptography.X509Certificates.X509Certificate2 AgentId, AdminId;
        public required TrustStore AgentTrusts;
        public required AuditLog Audit;
        public required string AuditPath;
        public required AgentServer Server;
        public int Calls;
        public void Dispose() => Server.Dispose();
    }

    private Rig NewRig(bool trustAdmin = true)
    {
        var agentId = Identity.Create("agent");
        var adminId = Identity.Create("admin");
        var trusts = new TrustStore(Path.Combine(_dir, "trusted-admins.json"));
        if (trustAdmin) trusts.Add(Identity.Fingerprint(adminId), "admin-1");
        var auditPath = Path.Combine(_dir, "audit.jsonl");
        var audit = new AuditLog(auditPath);
        Rig? rig = null;
        var server = new AgentServer(agentId, trusts, audit, 0, (req, ct) =>
        {
            rig!.Calls++;
            return Task.FromResult(req.Type == RequestTypes.Ping
                ? new FleetResponse { Ok = true }
                : new FleetResponse { Ok = true, Inventory = new InventorySnapshot { DeviceId = "d1", HostName = "pc", Apps = { new FleetApp { Name = "7-Zip", Version = "24.08" } } } });
        }, IPAddress.Loopback);
        rig = new Rig { AgentId = agentId, AdminId = adminId, AgentTrusts = trusts, Audit = audit, AuditPath = auditPath, Server = server };
        server.Start();
        return rig;
    }

    [Fact]
    public async Task Trusted_admin_gets_the_inventory_over_pinned_mutual_tls()
    {
        using var r = NewRig();
        var client = new AdminClient(r.AdminId);
        var resp = await client.SendAsync("127.0.0.1", r.Server.Port, Identity.Fingerprint(r.AgentId), RequestTypes.Inventory, CancellationToken.None);
        Assert.True(resp.Ok);
        Assert.Equal("7-Zip", resp.Inventory!.Apps[0].Name);
    }

    [Fact]
    public async Task Admin_with_a_wrong_pinned_fingerprint_refuses_to_talk()
    {
        using var r = NewRig();
        var client = new AdminClient(r.AdminId);
        var wrong = Identity.Fingerprint(Identity.Create("someone-else"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("127.0.0.1", r.Server.Port, wrong, RequestTypes.Ping, CancellationToken.None));
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public async Task Untrusted_admin_is_rejected_and_the_handler_never_runs()
    {
        using var r = NewRig(trustAdmin: false);
        var client = new AdminClient(r.AdminId);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("127.0.0.1", r.Server.Port, Identity.Fingerprint(r.AgentId), RequestTypes.Ping, CancellationToken.None));
        Assert.Equal(0, r.Calls);
        Assert.True(File.ReadAllText(r.AuditPath).Contains("rejected"));
    }

    [Fact]
    public async Task Only_read_only_request_types_exist()
    {
        using var r = NewRig();
        var client = new AdminClient(r.AdminId);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SendAsync("127.0.0.1", r.Server.Port, Identity.Fingerprint(r.AgentId), "install", CancellationToken.None));
        // وحتى لو تجاوز عميل معدّل هذا الفحص، يرفضه الخادم:
        var resp = await r.Server.HandleAsync(new FleetRequest { Type = "install", Nonce = "n1", SentUtc = DateTime.UtcNow }, "t", CancellationToken.None);
        Assert.False(resp.Ok);
        Assert.Equal(0, r.Calls);
    }

    [Fact]
    public async Task Replayed_and_stale_requests_are_refused()
    {
        using var r = NewRig();
        var ok = await r.Server.HandleAsync(new FleetRequest { Type = "ping", Nonce = "abc", SentUtc = DateTime.UtcNow }, "t", CancellationToken.None);
        var again = await r.Server.HandleAsync(new FleetRequest { Type = "ping", Nonce = "abc", SentUtc = DateTime.UtcNow }, "t", CancellationToken.None);
        var stale = await r.Server.HandleAsync(new FleetRequest { Type = "ping", Nonce = "old", SentUtc = DateTime.UtcNow.AddHours(-1) }, "t", CancellationToken.None);
        var noNonce = await r.Server.HandleAsync(new FleetRequest { Type = "ping", Nonce = "", SentUtc = DateTime.UtcNow }, "t", CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.False(again.Ok);
        Assert.False(stale.Ok);
        Assert.False(noNonce.Ok);
        Assert.Equal(1, r.Calls);
    }

    [Fact]
    public void Only_private_addresses_are_allowed()
    {
        foreach (var s in new[] { "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.1.9", "169.254.1.1", "127.0.0.1", "::1", "fe80::1", "fd12::5" })
            Assert.True(PrivateNetwork.IsAllowed(IPAddress.Parse(s)), s);
        foreach (var s in new[] { "8.8.8.8", "172.32.0.1", "172.15.0.1", "192.169.0.1", "11.0.0.1", "2001:4860:4860::8888" })
            Assert.False(PrivateNetwork.IsAllowed(IPAddress.Parse(s)), s);
    }

    [Fact]
    public async Task Admin_refuses_to_connect_to_a_public_address()
    {
        var client = new AdminClient(Identity.Create("admin"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.SendAsync("8.8.8.8", 443, new string('A', 64), RequestTypes.Ping, CancellationToken.None, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Audit_chain_detects_tampering_and_deletion()
    {
        var p = Path.Combine(_dir, "a.jsonl");
        var log = new AuditLog(p);
        for (int i = 0; i < 5; i++) log.Append("admin", "ping", "ok " + i);
        Assert.True(AuditLog.Verify(p, out _));

        var lines = File.ReadAllLines(p).ToList();
        File.WriteAllLines(p, lines.Select((l, i) => i == 2 ? l.Replace("ok 2", "ok X") : l));
        Assert.False(AuditLog.Verify(p, out var bad));
        Assert.Equal(3, bad);

        File.WriteAllLines(p, lines.Where((_, i) => i != 2));      // حذف سطر من الوسط
        Assert.False(AuditLog.Verify(p, out _));
    }

    [Fact]
    public void Audit_log_continues_the_chain_after_a_restart()
    {
        var p = Path.Combine(_dir, "b.jsonl");
        new AuditLog(p).Append("a", "x", "1");
        new AuditLog(p).Append("a", "x", "2");
        Assert.True(AuditLog.Verify(p, out _));
    }

    [Fact]
    public void Trust_store_requires_full_fingerprints_and_persists()
    {
        var p = Path.Combine(_dir, "t.json");
        var fp = Identity.Fingerprint(Identity.Create("x"));
        var s = new TrustStore(p);
        s.Add(fp, "label");
        Assert.True(new TrustStore(p).IsTrusted(fp));
        Assert.True(new TrustStore(p).IsTrusted(TrustStore.Normalize(string.Join(":", Enumerable.Range(0, 32).Select(i => fp.Substring(i * 2, 2))))));
        Assert.Throws<FormatException>(() => s.Add("ABCD", "short"));
        Assert.True(s.Remove(fp));
        Assert.False(new TrustStore(p).IsTrusted(fp));
    }
}

public class FleetReportTests
{
    private static InventorySnapshot Snap(string host, params FleetApp[] apps) =>
        new() { HostName = host, Os = "Windows 10", ScannedUtc = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), Apps = apps.ToList() };

    private static FleetReport Sample() => FleetReport.Build(new (string, InventorySnapshot?)[]
    {
        ("pc-b", Snap("PCB", new FleetApp { Name = "7-Zip", PackageId = "7zip.7zip", Version = "23.01", LatestVersion = "24.08", Status = "UpdateAvailable" },
                            new FleetApp { Name = "VLC", PackageId = "VideoLAN.VLC", Version = "3.0.21", Status = "UpToDate" })),
        ("pc-a", Snap("PCA", new FleetApp { Name = "7-Zip", PackageId = "7zip.7zip", Version = "24.08", Status = "UpToDate" },
                            new FleetApp { Name = "Mystery", Status = "Unknown" })),
        ("pc-c", null),
    }, new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Report_counts_devices_updates_and_version_spread()
    {
        var r = Sample();
        Assert.Equal(3, r.Devices.Count);
        Assert.Equal("pc-a", r.Devices[0].Name);                 // مرتبة بالاسم
        Assert.Equal(4, r.TotalApps);
        Assert.Equal(1, r.Outdated.Count);
        Assert.Equal("pc-b", r.Outdated[0].Device);
        Assert.Equal(1, r.Spread.Count);                          // 7-Zip بإصدارين على جهازين
        Assert.Equal(2, r.Spread[0].Devices);
        Assert.Equal(1, r.Devices.First(d => d.Name == "pc-a").Unknown);
        Assert.True(r.Devices.First(d => d.Name == "pc-c").ScannedUtc == null);
    }

    [Fact]
    public void Csv_has_a_bom_and_neutralises_formula_injection()
    {
        var r = FleetReport.Build(new (string, InventorySnapshot?)[]
        {
            ("pc", Snap("PC", new FleetApp { Name = "=HYPERLINK(\"http://x\")", Version = "1,0", LatestVersion = "2", Status = "UpdateAvailable" })),
        });
        var csv = r.ToCsv();
        Assert.True(csv.StartsWith("\uFEFF"));
        Assert.True(csv.Contains("'=HYPERLINK"));
        Assert.False(csv.Contains(",=HYPERLINK"));
        Assert.True(csv.Contains("\"1,0\""));
        Assert.Equal("a", FleetReport.Csv("a"));
        Assert.Equal("\"a\"\"b\"", FleetReport.Csv("a\"b"));
    }

    [Fact]
    public void Html_escapes_everything_and_has_no_script_or_external_resources()
    {
        var r = FleetReport.Build(new (string, InventorySnapshot?)[]
        {
            ("<img src=x onerror=alert(1)>", Snap("H", new FleetApp { Name = "<script>alert(1)</script>", Version = "1", LatestVersion = "2", Status = "UpdateAvailable" })),
        });
        var html = r.ToHtml();
        Assert.False(html.Contains("<script"));
        Assert.False(html.Contains("<img"));
        Assert.True(html.Contains("&lt;script&gt;"));
        Assert.True(html.Contains("default-src 'none'"));
        Assert.False(html.Contains("http://") || html.Contains("https://"));
    }

    [Fact]
    public void Html_marks_stale_and_never_scanned_devices()
    {
        var html = Sample().ToHtml(TimeSpan.FromDays(3));
        Assert.True(html.Contains("(stale)"));
        Assert.True(html.Contains(">never<"));
    }
}

public class FleetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private static string Fp() => Identity.Fingerprint(Identity.Create("x"));

    [Fact]
    public void Devices_persist_replace_by_name_and_remove()
    {
        var s = new FleetStore(_dir);
        s.Add("PC1", "192.168.1.5:50000", Fp());
        s.Add("pc1", "192.168.1.6:50001", Fp());                  // الاسم نفسه (غير حساس للحالة) يستبدل
        Assert.Equal(1, new FleetStore(_dir).Devices().Count);
        Assert.Equal(50001, new FleetStore(_dir).Find("PC1")!.Port);
        Assert.True(s.Remove("pc1"));
        Assert.False(s.Remove("pc1"));
        Assert.Equal(0, s.Devices().Count);
    }

    [Fact]
    public void Add_rejects_bad_input()
    {
        var s = new FleetStore(_dir);
        Assert.Throws<FormatException>(() => s.Add("a", "192.168.1.5", Fp()));            // بلا منفذ
        Assert.Throws<FormatException>(() => s.Add("a", "192.168.1.5:99999", Fp()));      // منفذ خارج المجال
        Assert.Throws<FormatException>(() => s.Add("a", "192.168.1.5:5000", "ABCD"));     // بصمة قصيرة
        Assert.Throws<FormatException>(() => s.Add("  ", "192.168.1.5:5000", Fp()));      // اسم فارغ
        Assert.Throws<FormatException>(() => s.Add("a", "8.8.8.8:5000", Fp()));           // عنوان عام
    }

    [Fact]
    public void Inventory_roundtrip_survives_odd_device_names()
    {
        var s = new FleetStore(_dir);
        s.SaveInventory("../evil/name", new InventorySnapshot { HostName = "H", Apps = { new FleetApp { Name = "A" } } });
        Assert.Equal("H", s.LoadInventory("../evil/name")!.HostName);
        Assert.Equal(1, Directory.GetFiles(Path.Combine(_dir, "inventories")).Length);     // لا مسارات هاربة
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil", "name.json")));
    }

    [Fact]
    public async Task Refresh_talks_to_an_agent_saves_the_inventory_and_explains_failures()
    {
        var admin = new FleetStore(_dir);
        var agentId = Identity.Create("agent");
        var trusts = new TrustStore(Path.Combine(_dir, "agent-trust.json"));
        trusts.Add(admin.MyFingerprint, "admin");
        using var server = new AgentServer(agentId, trusts, new AuditLog(Path.Combine(_dir, "agent-audit.jsonl")), 0,
            (req, ct) => Task.FromResult(new FleetResponse { Ok = true, Inventory = new InventorySnapshot { HostName = "PC", Apps = { new FleetApp { Name = "X", Status = "UpdateAvailable" } } } }),
            IPAddress.Loopback);
        server.Start();

        var good = admin.Add("good", "127.0.0.1:" + server.Port, Identity.Fingerprint(agentId));
        var r = await admin.RefreshAsync(good, RequestTypes.Inventory, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(1, admin.LoadInventory("good")!.Apps.Count);
        Assert.True(r.Message.Contains("1 with updates"));

        var wrong = admin.Add("wrong", "127.0.0.1:" + server.Port, Fp());                  // بصمة خاطئة: لا يتكلم
        var w = await admin.RefreshAsync(wrong, RequestTypes.Ping, CancellationToken.None);
        Assert.False(w.Ok);

        var dead = admin.Add("dead", "127.0.0.1:1", Fp());                                 // لا أحد يسمع
        var d = await admin.RefreshAsync(dead, RequestTypes.Ping, CancellationToken.None);
        Assert.False(d.Ok);
        Assert.True(AuditLog.Verify(admin.AuditPath, out _));
    }
}
