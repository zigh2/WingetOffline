using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Manifest;
using WingetOffline.Core.Models;
using WingetOffline.Core.Versioning;

// الاستخدام:
//   WingetOffline.Builder <مجلد winget-pkgs المستنسخ> <ملف الإخراج .wglib>
// يعمل على الجهاز المتصل فقط. لا يحتاج الجهاز المعزول لأي شيء منه.

if (args.Length < 2)
{
    Console.WriteLine("Usage: WingetOffline.Builder <winget-pkgs-folder> <output.wglib>");
    return 1;
}

var root = Path.Combine(args[0], "manifests");
if (!Directory.Exists(root)) { Console.Error.WriteLine("manifests folder not found."); return 2; }

var tmp = Path.Combine(Path.GetTempPath(), "wglib_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
var dbPath = Path.Combine(tmp, "catalog.db");

int packages = 0, versions = 0, failed = 0;
var clock = System.Diagnostics.Stopwatch.StartNew();
using (var db = new CatalogDb(dbPath))
{
    db.BeginBatch();

    // كل مجلد إصدار = مجلد يحتوي ملفات yaml مباشرة. نجمعها حسب مجلد الحزمة (الأب).
    var byPackage = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
        .Where(d => Directory.EnumerateFiles(d, "*.yaml").Any())
        .GroupBy(d => Path.GetDirectoryName(d)!);

    foreach (var group in byPackage)
    {
        var parsed = new List<PackageVersion>();
        foreach (var dir in group)
        {
            var pv = ManifestParser.ParseDirectory(dir);
            if (pv == null) { failed++; continue; }
            db.AddVersion(pv);
            parsed.Add(pv);
            versions++;
        }
        if (parsed.Count == 0) continue;

        var latest = parsed.OrderBy(p => p.Version, WingetVersionComparer.Instance).Last();
        db.UpsertPackage(latest, parsed.Select(p => p.Version));
        if (++packages % 1000 == 0) Console.WriteLine($"{packages} packages... ({clock:mm\\:ss})");
    }
    db.Commit();
    db.Optimize();
}

var output = Path.GetFullPath(args[1]);
if (File.Exists(output)) File.Delete(output);

string sha;
using (var fs = File.OpenRead(dbPath)) sha = Convert.ToHexString(SHA256.HashData(fs));
File.WriteAllText(Path.Combine(tmp, "meta.json"), JsonSerializer.Serialize(new
{
    format = 1,
    created_utc = DateTime.UtcNow,
    packages, versions, db_sha256 = sha,
}, new JsonSerializerOptions { WriteIndented = true }));

ZipFile.CreateFromDirectory(tmp, output, CompressionLevel.SmallestSize, false);
Directory.Delete(tmp, true);
Console.WriteLine($"Done: {packages} packages, {versions} versions, {failed} skipped -> {output} ({clock:mm\\:ss})");
return 0;
