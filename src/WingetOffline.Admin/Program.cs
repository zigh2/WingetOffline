using System.Reflection;
using System.Text.Json;
using WingetOffline.Fleet;

// wgo-admin: جرد البرامج على أجهزة الشبكة المحلية (قراءة فقط). يتصل بالـ Agent عبر TLS بمصادقة متبادلة وببصمة مثبّتة.
if (args.Length == 0 || args[0] is "help" or "-h" or "--help") { Help(); return 0; }
if (args[0] == "--version") { Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"); return 0; }

var store = new FleetStore();
var cmd = args[0].ToLowerInvariant();
int Fail(string m) { Console.Error.WriteLine(m); return 1; }
List<Device>? Select(string? what)
{
    var all = store.Devices();
    if (what == null || what.Equals("all", StringComparison.OrdinalIgnoreCase)) return all;
    var l = all.Where(d => d.Name.Equals(what, StringComparison.OrdinalIgnoreCase)).ToList();
    return l.Count > 0 ? l : null;
}

try
{
    switch (cmd)
    {
        case "id":
            Console.WriteLine("This admin's fingerprint (give it to each agent: wgo-agent trust <fingerprint> <label>):");
            Console.WriteLine(store.MyFingerprint);
            return 0;

        case "add":      // add <name> <host:port> <agent-fingerprint>
        {
            if (args.Length != 4) return Fail("usage: wgo-admin add <name> <host:port> <agent-fingerprint>");
            var d = store.Add(args[1], args[2], args[3]);
            Console.WriteLine($"Added '{d.Name}' ({d.Host}:{d.Port}). Pinned fingerprint {d.Fingerprint[..16]}…");
            return 0;
        }
        case "remove":
            if (args.Length != 2) return Fail("usage: wgo-admin remove <name>");
            if (store.Remove(args[1])) { Console.WriteLine("Removed."); return 0; }
            Console.WriteLine("No such device."); return 1;

        case "list":
        {
            var list = store.Devices();
            if (list.Count == 0) Console.WriteLine("No devices. Add one with: wgo-admin add <name> <host:port> <agent-fingerprint>");
            foreach (var d in list)
            {
                var inv = store.LoadInventory(d.Name);
                Console.WriteLine($"{d.Name,-20} {d.Host + ":" + d.Port,-24} " + (inv == null ? "never scanned" : $"{inv.Apps.Count} apps, scanned {inv.ScannedUtc:yyyy-MM-dd HH:mm} UTC"));
            }
            return 0;
        }
        case "ping":
        case "inventory":
        {
            var targets = Select(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
            if (targets == null) return Fail("unknown device (use a name or 'all')");
            int failed = 0;
            foreach (var d in targets)
            {
                var r = await store.RefreshAsync(d, cmd, CancellationToken.None);
                if (r.Ok) Console.WriteLine($"{d.Name}: {r.Message}");
                else { failed++; Console.Error.WriteLine($"{d.Name}: {r.Message}"); }
            }
            return failed == 0 ? 0 : 2;
        }
        case "updates":     // من آخر جرد محفوظ (لا اتصال)
        {
            var rep = store.BuildReport();
            foreach (var o in rep.Outdated) Console.WriteLine($"{o.Device,-18} {o.App,-44} {o.Version} -> {o.LatestVersion}");
            if (rep.Outdated.Count == 0) Console.WriteLine("No updates in the saved inventories. Run: wgo-admin inventory all");
            return 0;
        }
        case "search":      // أين هذا البرنامج؟ (من الجرد المحفوظ)
        {
            if (args.Length < 2) return Fail("usage: wgo-admin search <text>");
            var q = string.Join(' ', args.Skip(1));
            int n = 0;
            foreach (var d in store.Devices())
            {
                var inv = store.LoadInventory(d.Name); if (inv == null) continue;
                foreach (var a in inv.Apps.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (a.PackageId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)))
                { Console.WriteLine($"{d.Name,-18} {a.Name,-44} {a.Version,-16} {a.Status}"); n++; }
            }
            if (n == 0) Console.WriteLine("Not found in the saved inventories.");
            return 0;
        }
        case "report":      // report [--format html|csv|json] [--out file]
        {
            string fmt = "html"; string? outPath = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) fmt = args[++i].ToLowerInvariant();
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else return Fail("usage: wgo-admin report [--format html|csv|json] [--out file]");
            }
            var rep = store.BuildReport();
            string text = fmt switch { "html" => rep.ToHtml(), "csv" => rep.ToCsv(), "json" => rep.ToJson(), _ => "" };
            if (text == "") return Fail("format must be html, csv or json");
            outPath ??= Path.Combine(store.Dir, $"fleet-report-{DateTime.Now:yyyyMMdd-HHmm}.{fmt}");
            File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));   // ToCsv يضع BOM بنفسه
            store.Audit.Append("local", "report " + fmt, "ok");
            Console.WriteLine($"{rep.Devices.Count} devices, {rep.Outdated.Count} updates available -> {outPath}");
            return 0;
        }
        case "verify-audit":
            if (AuditLog.Verify(store.AuditPath, out var bad)) { Console.WriteLine("Audit log is intact."); return 0; }
            Console.WriteLine($"Audit log was modified: first bad entry is line {bad}."); return 3;

        default: return Fail($"unknown command '{cmd}'");
    }
}
catch (FormatException ex) { return Fail(ex.Message); }

static void Help() => Console.WriteLine("""
wgo-admin — inventory of programs on your local network (read-only, no internet)

  id                                  show this admin's fingerprint (paste it into each agent)
  add <name> <host:port> <agent-fp>   register a device; the agent's fingerprint is pinned
  remove <name>                       forget a device
  list                                devices and last scan
  ping <name|all>                     is the agent reachable and does it trust this admin?
  inventory <name|all>                fetch and save the installed programs (marked with update status)
  updates                             programs with an update, on all devices (from saved inventories)
  search <text>                       which devices have this program (from saved inventories)
  report [--format html|csv|json] [--out file]
                                      fleet report from saved inventories (works offline)
  verify-audit                        check that the audit log was not altered

Only private-network addresses are accepted. Install/uninstall/update are not part of this version.
""");

