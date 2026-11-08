using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Manifest;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Models;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Tests;

public class ManifestParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-manifest-" + Guid.NewGuid().ToString("N"));

    public ManifestParserTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Multi_file_manifest_is_parsed_with_inherited_installer_fields()
    {
        File.WriteAllText(Path.Combine(_dir, "Foo.Bar.yaml"),
            "PackageIdentifier: Foo.Bar\nPackageVersion: 1.2.3\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.6.0\n");
        File.WriteAllText(Path.Combine(_dir, "Foo.Bar.installer.yaml"),
            "PackageIdentifier: Foo.Bar\nPackageVersion: 1.2.3\nInstallerType: inno\nScope: machine\n" +
            "Installers:\n" +
            "- Architecture: x64\n  InstallerUrl: https://example.com/a.exe\n  InstallerSha256: AAAA\n  ProductCode: '{GUID}_is1'\n" +
            "- Architecture: x86\n  InstallerUrl: https://example.com/b.exe\n  InstallerSha256: BBBB\n" +
            "ManifestType: installer\nManifestVersion: 1.6.0\n");
        File.WriteAllText(Path.Combine(_dir, "Foo.Bar.locale.en-US.yaml"),
            "PackageIdentifier: Foo.Bar\nPackageVersion: 1.2.3\nPackageLocale: en-US\nPublisher: Foo Inc\nPackageName: Foo Bar\n" +
            "License: MIT\nShortDescription: A tool\nTags:\n- foo\n- bar\nManifestType: defaultLocale\nManifestVersion: 1.6.0\n");

        var pv = ManifestParser.ParseDirectory(_dir);

        Assert.NotNull(pv);
        Assert.Equal("Foo.Bar", pv!.Id);
        Assert.Equal("1.2.3", pv.Version);
        Assert.Equal("Foo Bar", pv.Name);
        Assert.Equal("Foo Inc", pv.Publisher);
        Assert.Equal(new[] { "foo", "bar" }, pv.Tags);
        Assert.Equal(2, pv.Installers.Count);
        Assert.Equal("inno", pv.Installers[0].InstallerType);   // موروث من الجذر
        Assert.Equal("machine", pv.Installers[1].Scope);
        Assert.Equal("{GUID}_is1", pv.Installers[0].ProductCode);
        Assert.Null(pv.Installers[1].ProductCode);
    }

    [Fact]
    public void Broken_yaml_is_skipped_without_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.yaml"), "::: not [valid yaml");
        Assert.Null(ManifestParser.ParseDirectory(_dir));
    }
}

public sealed class CatalogFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "wgo-catalog-" + Guid.NewGuid().ToString("N"));
    public string InstallerFile { get; }
    public string InstallerSha { get; }
    public CatalogStore Store { get; }

    public CatalogFixture()
    {
        Directory.CreateDirectory(Dir);
        InstallerFile = Path.Combine(Dir, "foo-setup.exe");
        File.WriteAllBytes(InstallerFile, Encoding.UTF8.GetBytes("fake installer content"));
        InstallerSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(InstallerFile)));

        var dbPath = Path.Combine(Dir, "catalog.db");
        using (var db = new CatalogDb(dbPath))
        {
            db.BeginBatch();

            var foo = new PackageVersion
            {
                Id = "Foo.Bar", Version = "1.2.3", Name = "Foo Bar", Publisher = "Foo Inc",
                Description = "A tool for foo", License = "MIT", Homepage = "https://example.com",
                Tags = { "foo", "bar" },
                Dependencies = { "Other.Tool" },
                Installers =
                {
                    new InstallerInfo
                    {
                        Architecture = "x64", InstallerType = "inno", Scope = "machine",
                        Url = "https://example.com/a.exe", Sha256 = InstallerSha, Silent = "/VERYSILENT",
                        ProductCode = "{AAAAAAAA-0000-0000-0000-000000000000}_is1",
                    },
                },
            };
            var fooOld = new PackageVersion { Id = "Foo.Bar", Version = "1.0.0", Name = "Foo Bar", Publisher = "Foo Inc" };
            var tool = new PackageVersion
            {
                Id = "Other.Tool", Version = "0.5", Name = "Other Tool", Publisher = "Someone Else",
                Description = "Utility", License = "GPL-3.0", Tags = { "util" },
                Installers = { new InstallerInfo { Architecture = "x64", InstallerType = "portable", Url = "https://example.com/t.exe", Sha256 = "CCCC" } },
            };

            db.AddVersion(foo); db.AddVersion(fooOld); db.AddVersion(tool);
            db.UpsertPackage(foo, new[] { "1.0.0", "1.2.3" });
            db.UpsertPackage(tool, new[] { "0.5" });
            db.Commit();
        }
        Store = CatalogStore.Open(dbPath);
    }

    public void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class CatalogTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture _f;
    public CatalogTests(CatalogFixture f) => _f = f;

    private List<string> Ids(PackageFilter filter) =>
        new CatalogQueries(_f.Store).Search(filter).Rows.Select(r => r.Id).ToList();

    [Theory]
    [InlineData("vlc media", "\"vlc\"* \"media\"*")]
    [InlineData("7-zip", "\"7\"* \"zip\"*")]
    [InlineData("c++", "\"c\"*")]
    public void Fts_query_is_built_safely(string input, string expected) =>
        Assert.Equal(expected, CatalogQueries.BuildFts(input));

    [Fact]
    public void Fts_query_is_null_for_blank_input() => Assert.Null(CatalogQueries.BuildFts("   "));

    [Fact]
    public void Search_by_text_supports_prefixes()
    {
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { Text = "foo" }));
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { Text = "fo" }));
        Assert.Empty(Ids(new PackageFilter { Text = "nonexistent" }));
    }

    [Fact]
    public void Installer_filters_apply_to_the_latest_version()
    {
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { InstallerType = "inno" }));
        Assert.Equal(new[] { "Other.Tool" }, Ids(new PackageFilter { InstallerType = "portable" }));
        Assert.Equal(new[] { "Other.Tool" }, Ids(new PackageFilter { PortableOnly = true }));
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { SilentOnly = true }));
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { HasProductCode = true }));
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { Scope = "machine" }));
    }

    [Fact]
    public void Metadata_filters_and_sorting()
    {
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { Publisher = "foo" }));
        Assert.Equal(new[] { "Other.Tool" }, Ids(new PackageFilter { License = "gpl" }));
        Assert.Equal(new[] { "Foo.Bar" }, Ids(new PackageFilter { Tag = "bar" }));
        Assert.Empty(Ids(new PackageFilter { Tag = "ba" }));   // الوسم يُطابَق كاملاً
        Assert.Equal(new[] { "Foo.Bar", "Other.Tool" }, Ids(new PackageFilter { Sort = PackageSort.Name }));
        Assert.Equal("Foo.Bar", Ids(new PackageFilter { Sort = PackageSort.Versions })[0]);
    }

    [Fact]
    public void Details_list_versions_newest_first_and_latest_installers()
    {
        var d = new CatalogQueries(_f.Store).GetDetails("Foo.Bar");
        Assert.NotNull(d);
        Assert.Equal(new[] { "1.2.3", "1.0.0" }, d!.Versions);
        Assert.Equal(new[] { "Other.Tool" }, d.Dependencies);
        Assert.Single(d.LatestInstallers);
        Assert.Equal("inno", d.LatestInstallers[0].Type);
        Assert.Equal("1.2.3", d.Package.Latest);
        Assert.Null(new CatalogQueries(_f.Store).GetDetails("No.Such"));
    }

    [Fact]
    public void Installer_file_is_recognised_by_sha256()
    {
        var item = InstallerIndexer.IndexFile(_f.Store, _f.InstallerFile);
        Assert.True(item.IsMatched);
        Assert.Equal("Foo.Bar", item.Best!.PackageId);
        Assert.Equal("1.2.3", item.Best.Version);
        Assert.Equal("inno", item.Best.Type);
    }

    [Fact]
    public void Folder_index_separates_known_and_unknown_files()
    {
        var folder = Path.Combine(_f.Dir, "pack");
        Directory.CreateDirectory(folder);
        File.Copy(_f.InstallerFile, Path.Combine(folder, "known.exe"), true);
        File.WriteAllText(Path.Combine(folder, "unknown.exe"), "something else");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored (not an installer extension)");

        var items = InstallerIndexer.IndexFolder(_f.Store, folder);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items.Count(i => i.IsMatched));
    }

    [Fact]
    public void Matcher_uses_version_in_catalog_to_reach_high_confidence()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar", Publisher = "Foo Inc", DisplayVersion = "1.0.0",
        });
        Assert.Equal("Foo.Bar", r.PackageId);
        Assert.Equal(Confidence.High, r.Confidence);
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
        Assert.Equal("1.2.3", r.LatestVersion);
    }

    [Fact]
    public void Matcher_flags_installed_versions_newer_than_the_catalog()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar", Publisher = "Foo Inc", DisplayVersion = "5.0.0",
        });
        Assert.Equal("Foo.Bar", r.PackageId);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
        Assert.True(r.NewerThanCatalog);
    }

    [Fact]
    public void Matcher_ignores_a_trailing_channel_suffix_when_the_version_is_known()
    {
        // PowerToys: الاسم المثبّت "PowerToys (Preview) x64" والكتالوج "PowerToys"
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar (Preview) x64", Publisher = "Foo Inc", DisplayVersion = "1.0.0",
        });
        Assert.Equal("Foo.Bar", r.PackageId);
        Assert.Equal(Confidence.Medium, r.Confidence);
        Assert.Equal("Name(no channel suffix)+Publisher", r.Method);
        Assert.Equal(UpdateStatus.UpdateAvailable, r.Status);
    }

    [Fact]
    public void Channel_suffix_match_with_an_unknown_version_is_low_confidence()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar (Preview)", Publisher = "Foo Inc", DisplayVersion = "5.0.0",
        });
        Assert.Equal("Foo.Bar", r.PackageId);
        Assert.Equal(Confidence.Low, r.Confidence);   // لا يُعرض لها تحديث موثوق
    }

    [Fact]
    public void Channel_suffix_match_requires_a_similar_publisher()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar (Preview)", Publisher = "Other Vendor Corp", DisplayVersion = "1.0.0",
        });
        Assert.Equal(Confidence.None, r.Confidence);
    }

    [Theory]
    [InlineData("Python 3.14.6 (64-bit)", "3.14-64", "3.14.6")]               // DisplayVersion مبتور: نفضّل الاسم
    [InlineData("Microsoft .NET Runtime - 8.0.21 (x64)", "8.0.21.33", "8.0.21.33")] // المسجَّل أدق من الاسم
    [InlineData("LAME v3.99.3 (for Windows)", null, "3.99.3")]                // لا DisplayVersion: من الاسم
    [InlineData("Foo", "1.2.3", "1.2.3")]
    [InlineData("Foo 9", null, null)]
    [InlineData("Everything 1.5.0.1424b (x64)", "1.5.0.1424", "1.5.0.1424")]
    public void Installed_version_is_resolved(string name, string? displayVersion, string? expected)
    {
        var app = new InstalledApp { DisplayName = name, DisplayVersion = displayVersion };
        Assert.Equal(expected, Matcher.ResolveInstalledVersion(app));
    }

    [Fact]
    public void Matcher_matches_by_product_code_first()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Totally different name", DisplayVersion = "9.9",
            ProductCode = "{AAAAAAAA-0000-0000-0000-000000000000}_is1",
        });
        Assert.Equal("Foo.Bar", r.PackageId);
        Assert.Equal("ProductCode", r.Method);
        Assert.Equal(Confidence.High, r.Confidence);
        Assert.Equal(UpdateStatus.UpToDate, r.Status);
    }

    [Fact]
    public void Matcher_rejects_same_name_with_different_publisher_and_unknown_version()
    {
        var matcher = new Matcher(_f.Store);
        var r = matcher.Match(new InstalledApp
        {
            Source = "Registry", DisplayName = "Foo Bar", Publisher = "Completely Different Vendor", DisplayVersion = "77.0",
        });
        Assert.Equal(Confidence.None, r.Confidence);
        Assert.NotNull(r.Note);
    }
}

public class PortableInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-portable-" + Guid.NewGuid().ToString("N"));

    public PortableInstallerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeZip(string entryName)
    {
        var zip = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(zip);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        var e = za.CreateEntry(entryName);
        using var w = new StreamWriter(e.Open());
        w.Write("content");
        return zip;
    }

    [Fact]
    public void Zip_slip_entries_are_rejected()
    {
        var zip = MakeZip("../evil.txt");
        Assert.Throws<InvalidDataException>(() => PortableInstaller.Install(zip, Path.Combine(_dir, "target")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public void Normal_zip_is_extracted_with_subfolders()
    {
        var zip = MakeZip("bin/tool.exe");
        var target = Path.Combine(_dir, "target");
        PortableInstaller.Install(zip, target);
        Assert.True(File.Exists(Path.Combine(target, "bin", "tool.exe")));
    }
}
