using Microsoft.Data.Sqlite;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Manifest;
using WingetOffline.Core.Models;
using WingetOffline.Core.Packing;
using WingetOffline.Core.Versioning;

namespace WingetOffline.Tests;

public class DependencyParsingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-deps-" + Guid.NewGuid().ToString("N"));

    public DependencyParsingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Dependencies_are_collected_from_root_and_from_each_installer()
    {
        File.WriteAllText(Path.Combine(_dir, "Foo.Dep.yaml"),
            "PackageIdentifier: Foo.Dep\nPackageVersion: 2.0\nPackageLocale: en-US\nPublisher: Foo\nPackageName: Foo Dep\n" +
            "License: MIT\nShortDescription: x\n" +
            "Installers:\n" +
            "- Architecture: x64\n  InstallerType: msi\n  InstallerUrl: https://example.com/a.msi\n  InstallerSha256: AAAA\n" +
            "  Dependencies:\n    PackageDependencies:\n    - PackageIdentifier: Other.Dep\n" +
            "Dependencies:\n  PackageDependencies:\n  - PackageIdentifier: Microsoft.VCRedist.2015+.x64\n    MinimumVersion: 14.0.0\n" +
            "ManifestType: singleton\nManifestVersion: 1.6.0\n");

        var pv = ManifestParser.ParseDirectory(_dir);

        Assert.NotNull(pv);
        Assert.Equal(new[] { "Microsoft.VCRedist.2015+.x64", "Other.Dep" }, pv!.Dependencies);
    }
}

public class PackPlannerTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture _f;
    public PackPlannerTests(CatalogFixture f) => _f = f;

    [Fact]
    public void Plan_lists_requested_first_then_dependencies_and_resolves_ids_case_insensitively()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("foo.bar") });

        Assert.Empty(plan.Warnings);
        Assert.Equal(new[] { "Foo.Bar", "Other.Tool" }, plan.Items.Select(i => i.PackageId));
        Assert.Equal("requested", plan.Items[0].Reason);
        Assert.Equal("dependency of Foo.Bar", plan.Items[1].Reason);
        Assert.True(plan.Items[1].IsDependency);
        Assert.Equal("1.2.3", plan.Items[0].Version);
        Assert.Equal("inno", plan.Items[0].Installer.Type);
    }

    [Fact]
    public void Dependencies_can_be_turned_off()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar") }, new PackOptions { IncludeDependencies = false });
        Assert.Single(plan.Items);
    }

    [Fact]
    public void A_requested_package_is_not_listed_again_as_a_dependency()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar"), new PackRequest("Other.Tool") });
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal("requested", plan.Items.Single(i => i.PackageId == "Other.Tool").Reason);
    }

    [Fact]
    public void Unknown_package_gives_a_warning()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("No.Such.Package") });
        Assert.Empty(plan.Items);
        Assert.Contains("not in the catalog", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void Unknown_version_falls_back_to_the_latest_with_a_warning()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar", "9.9.9") }, new PackOptions { IncludeDependencies = false });
        Assert.Contains("9.9.9", Assert.Single(plan.Warnings));
        Assert.Equal("1.2.3", Assert.Single(plan.Items).Version);
    }

    [Fact]
    public void Version_without_installers_is_reported()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar", "1.0.0") });
        Assert.Empty(plan.Items);
        Assert.Contains("no usable installer", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void Architecture_preference_filters_installers()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar") }, new PackOptions { Architectures = new[] { "arm64" } });
        Assert.Empty(plan.Items);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void File_names_are_stable_and_safe()
    {
        var plan = PackPlanner.Plan(_f.Store, new[] { new PackRequest("Foo.Bar") });
        Assert.Equal("Foo.Bar_1.2.3_x64.exe", PackNaming.FileNameFor(plan.Items[0]));
        Assert.Equal("Other.Tool_0.5_x64.exe", PackNaming.FileNameFor(plan.Items[1]));

        var noExt = new PackItem("A.B", "1/2", "requested",
            new CatalogInstaller("A.B", "1/2", "x64", "msi", null, "", "https://x.example/download?id=5", "AA", null, null, null, null, null, null));
        Assert.Equal("A.B_1_2_x64.msi", PackNaming.FileNameFor(noExt));   // الامتداد من النوع، و '/' تُستبدل (محظورة في أسماء الملفات على النظامين)
    }

    [Fact]
    public void Dependencies_query_tolerates_catalogs_built_before_the_deps_table_existed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wgo-olddb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "old.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE packages(id TEXT PRIMARY KEY, name TEXT);";
                cmd.ExecuteNonQuery();
            }
            using var store = CatalogStore.Open(path);
            Assert.Empty(new CatalogQueries(store).GetDependencies("X", "1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class UpdateListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-updates-" + Guid.NewGuid().ToString("N"));

    public UpdateListTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var path = Path.Combine(_dir, "u.json");
        UpdateList.Write(path, new[] { new UpdateEntry("A.B", "2.0", "1.0", "App") });

        var back = Assert.Single(UpdateList.Read(path));
        Assert.Equal(new UpdateEntry("A.B", "2.0", "1.0", "App"), back);
    }

    [Fact]
    public void Output_of_wgo_scan_json_is_accepted_and_low_confidence_is_filtered()
    {
        var path = Path.Combine(_dir, "scan.json");
        File.WriteAllText(path, """
            [
              {"packageId":"A.B","status":"UPDATE","confidence":"High","latest":"2.0","installed":"1.0","name":"A"},
              {"packageId":"C.D","status":"ok","confidence":"High","latest":"1.0"},
              {"packageId":"E.F","status":"UPDATE","confidence":"Low","latest":"3.0"},
              {"packageId":"A.B","status":"UPDATE","confidence":"High","latest":"2.0"}
            ]
            """);

        Assert.Equal(new[] { "A.B" }, UpdateList.Read(path).Select(e => e.PackageId));
        Assert.Equal(new[] { "A.B", "E.F" }, UpdateList.Read(path, includeLowConfidence: true).Select(e => e.PackageId));
    }
}

public class VersionAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgo-audit-" + Guid.NewGuid().ToString("N"));

    public VersionAuditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Detects_packages_whose_version_order_disagrees_with_release_dates()
    {
        var path = Path.Combine(_dir, "audit.db");
        using (var db = new CatalogDb(path))
        {
            db.BeginBatch();
            void Add(string id, string version, string? date) =>
                db.AddVersion(new PackageVersion { Id = id, Version = version, ReleaseDate = date, Name = id, Publisher = "p" });

            Add("Good.Pkg", "1.0", "2020-01-01"); Add("Good.Pkg", "2.0", "2021-01-01");
            Add("Bad.Pkg", "2.0", "2020-01-01");  Add("Bad.Pkg", "1.0", "2021-01-01");   // الأقدم تاريخاً أعلى رقماً
            Add("Same.Day", "2.0", "2020-01-01"); Add("Same.Day", "1.0", "2020-01-01");  // نفس اليوم: لا حكم
            Add("Single.Pkg", "1.0", "2020-01-01");
            Add("Undated.Pkg", "1.0", null); Add("Undated.Pkg", "2.0", null);
            db.Commit();
        }

        using var store = CatalogStore.Open(path);
        var report = VersionAudit.Run(store);

        Assert.Equal(3, report.PackagesChecked);              // Good + Bad + Same.Day
        Assert.Equal(1, report.PackagesWithViolations);
        var sample = Assert.Single(report.Samples);
        Assert.Equal("Bad.Pkg", sample.PackageId);
        Assert.Equal("2.0", sample.OlderByDate);
    }
}
