using System.IO.Compression;
using System.Text.Json;

namespace WingetOffline.Core.Install;

public sealed record PortableEntry(string PackageId, string Version, string Path, DateTime InstalledUtc);

/// <summary>تثبيت البرامج المحمولة: فك zip (مع حماية من مسارات خبيثة) أو نسخ exe، وتسجيلها في ملف تتبّع.</summary>
public static class PortableInstaller
{
    public static string RegistryFile => AppPaths.Combine("portables.json");

    public static void Install(string source, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var root = System.IO.Path.GetFullPath(targetDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;

        if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // مجلد
                var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(targetDir, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in archive: {entry.FullName}");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
        }
        else
        {
            File.Copy(source, System.IO.Path.Combine(targetDir, System.IO.Path.GetFileName(source)), true);
        }
    }

    public static void Record(PortableEntry entry)
    {
        try
        {
            var list = Load();
            list.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            list.Add(entry);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RegistryFile)!);
            File.WriteAllText(RegistryFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* التتبّع اختياري */ }
    }

    public static List<PortableEntry> Load()
    {
        try
        {
            if (File.Exists(RegistryFile))
                return JsonSerializer.Deserialize<List<PortableEntry>>(File.ReadAllText(RegistryFile)) ?? new();
        }
        catch { }
        return new();
    }
}
