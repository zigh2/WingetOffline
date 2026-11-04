using WingetOffline.Core.Install;
using WingetOffline.Core.Scanning;

namespace WingetOffline.Tests;

public class ExitCodesTests
{
    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(3010, false, false, true)]
    [InlineData(1641, false, false, true)]
    [InlineData(1603, false, false, false)]
    [InlineData(1605, false, false, false)]   // المنتج غير مثبّت: فشل عند التثبيت
    [InlineData(1605, true, false, true)]     // ... لكنه نجاح عند الإزالة
    [InlineData(1614, true, false, true)]
    [InlineData(1627, true, false, false)]    // 1627 لا يُقبل إلا لمزيلات NSIS
    [InlineData(1627, true, true, true)]
    [InlineData(1627, false, true, false)]
    public void Success_rules(int code, bool uninstall, bool nsis, bool expected) =>
        Assert.Equal(expected, ExitCodes.IsSuccess(code, uninstall, nsis));

    [Fact]
    public void Describe_known_and_unknown_codes()
    {
        Assert.Contains("1603", ExitCodes.Describe(1603));
        Assert.Contains("1618", ExitCodes.Describe(1618));
        Assert.Contains("777", ExitCodes.Describe(777));
        Assert.True(ExitCodes.IsRebootCode(3010));
        Assert.False(ExitCodes.IsRebootCode(0));
    }
}

public class InstallPlannerTests
{
    private const string MsiPath = @"C:\i\a.msi";

    private static CatalogInstaller Inst(string type, string? silent = null, string? custom = null, string scope = "") =>
        new("Foo.Bar", "1.2.3", "x64", type, null, scope, "", "", silent, null, custom, null, null, null);

    [Fact]
    public void Msi_defaults_and_interactive_fallback()
    {
        var plan = InstallPlanner.Plan(MsiPath, Inst("msi"), new InstallOptions());
        Assert.Equal("msiexec.exe", plan.FileName);
        Assert.Equal("/i \"C:\\i\\a.msi\" /quiet /norestart", plan.Arguments);
        Assert.False(plan.Interactive);
        Assert.True(plan.NeedsElevation);
        Assert.NotNull(plan.InteractiveFallback);
        Assert.True(plan.InteractiveFallback!.Interactive);
        Assert.Equal("/i \"C:\\i\\a.msi\"", plan.InteractiveFallback.Arguments);
    }

    [Fact]
    public void Msi_log_file_is_appended()
    {
        var plan = InstallPlanner.Plan(MsiPath, Inst("msi"), new InstallOptions { MsiLogFile = @"C:\logs\m.log" });
        Assert.EndsWith(" /L*v \"C:\\logs\\m.log\"", plan.Arguments);
    }

    [Theory]
    [InlineData("inno", "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("nullsoft", "/S")]
    [InlineData("burn", "/quiet /norestart")]
    public void Exe_based_defaults(string type, string expectedArgs)
    {
        var plan = InstallPlanner.Plan(@"C:\i\setup.exe", Inst(type), new InstallOptions());
        Assert.Equal(@"C:\i\setup.exe", plan.FileName);
        Assert.Equal(expectedArgs, plan.Arguments);
        Assert.False(plan.Interactive);
    }

    [Fact]
    public void Wix_runs_through_msiexec() =>
        Assert.Equal("msiexec.exe", InstallPlanner.Plan(MsiPath, Inst("wix"), new InstallOptions()).FileName);

    [Fact]
    public void Manifest_switch_overrides_default_and_custom_is_appended()
    {
        var a = InstallPlanner.Plan(@"C:\i\s.exe", Inst("inno", silent: "/VERYSILENT /DIR=x"), new InstallOptions());
        Assert.Equal("/VERYSILENT /DIR=x", a.Arguments);

        var b = InstallPlanner.Plan(@"C:\i\s.exe", Inst("nullsoft", custom: @"/D=C:\App"), new InstallOptions());
        Assert.Equal(@"/S /D=C:\App", b.Arguments);
    }

    [Fact]
    public void Exe_without_silent_switch_is_interactive_with_warning()
    {
        var plan = InstallPlanner.Plan(@"C:\i\s.exe", Inst("exe"), new InstallOptions());
        Assert.True(plan.Interactive);
        Assert.Equal("", plan.Arguments);
        Assert.NotNull(plan.Warning);

        var quiet = InstallPlanner.Plan(@"C:\i\s.exe", Inst("exe", silent: "-q"), new InstallOptions());
        Assert.False(quiet.Interactive);
        Assert.Equal("-q", quiet.Arguments);
    }

    [Fact]
    public void Interactive_option_removes_silent_switches()
    {
        var plan = InstallPlanner.Plan(@"C:\i\s.exe", Inst("inno"), new InstallOptions { Interactive = true });
        Assert.True(plan.Interactive);
        Assert.Equal("", plan.Arguments);
        Assert.Null(plan.InteractiveFallback);
    }

    [Fact]
    public void User_scope_does_not_need_elevation() =>
        Assert.False(InstallPlanner.Plan(MsiPath, Inst("msi", scope: "user"), new InstallOptions()).NeedsElevation);

    [Fact]
    public void Msix_uses_powershell_without_fallback()
    {
        var plan = InstallPlanner.Plan(@"C:\i\a.msix", Inst("msix"), new InstallOptions());
        Assert.Equal("powershell.exe", plan.FileName);
        Assert.Contains("Add-AppxPackage", plan.Arguments);
        Assert.Null(plan.InteractiveFallback);
    }

    [Fact]
    public void Pwa_is_not_supported() =>
        Assert.Throws<NotSupportedException>(() => InstallPlanner.Plan(MsiPath, Inst("pwa"), new InstallOptions()));

    [Fact]
    public void Zip_is_planned_as_portable()
    {
        var plan = InstallPlanner.Plan(@"C:\i\a.zip", Inst("zip"), new InstallOptions { PortableRoot = @"C:\P" });
        Assert.Equal(PlanKind.Portable, plan.Kind);
        Assert.Equal(Path.Combine(@"C:\P", "Foo.Bar"), plan.Target);
    }
}

public class UninstallPlannerTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\7-Zip\\Uninstall.exe\" /S", "C:\\Program Files\\7-Zip\\Uninstall.exe", "/S")]
    [InlineData("MsiExec.exe /X{11111111-2222-3333-4444-555555555555}", "MsiExec.exe", "/X{11111111-2222-3333-4444-555555555555}")]
    [InlineData("C:\\Windows\\System32\\msiexec.exe /I{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", "C:\\Windows\\System32\\msiexec.exe", "/I{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}")]
    [InlineData("\"C:\\X\\unins000.exe\"", "C:\\X\\unins000.exe", "")]
    [InlineData("rundll32 shell32.dll,Foo", "rundll32", "shell32.dll,Foo")]
    public void Split_command(string cmd, string file, string args)
    {
        var (f, a) = UninstallPlanner.SplitCommand(cmd);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }

    private static InstalledApp App(string? uninstall = null, string? quiet = null) => new()
    {
        Source = "Registry", DisplayName = "App", Scope = "Machine",
        UninstallString = uninstall, QuietUninstallString = quiet,
    };

    [Fact]
    public void Msi_uninstall_becomes_silent_with_product_code()
    {
        var plan = UninstallPlanner.Plan(App("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}"));
        Assert.NotNull(plan);
        Assert.Equal("MsiExec.exe", plan!.FileName);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789012} /qn /norestart", plan.Arguments);
        Assert.False(plan.Interactive);
        Assert.True(plan.IsUninstall);
        Assert.True(plan.NeedsElevation);
    }

    [Fact]
    public void Quiet_string_is_preferred_and_normal_command_is_the_fallback()
    {
        var plan = UninstallPlanner.Plan(App("\"C:\\X\\u.exe\"", "\"C:\\X\\u.exe\" /quiet"));
        Assert.Equal("/quiet", plan!.Arguments);
        Assert.NotNull(plan.InteractiveFallback);
        Assert.True(plan.InteractiveFallback!.Interactive);
        Assert.Equal("", plan.InteractiveFallback.Arguments);
    }

    [Fact]
    public void Unknown_uninstaller_is_interactive_with_warning()
    {
        var plan = UninstallPlanner.Plan(App("\"C:\\X\\Adobe.exe\" --uninstall=1"));
        Assert.True(plan!.Interactive);
        Assert.StartsWith("Unknown", plan.Warning);
    }

    [Fact]
    public void Inno_uninstaller_gets_silent_switches()
    {
        if (!OperatingSystem.IsWindows()) return; // يعتمد على فاصل المسار \
        var plan = UninstallPlanner.Plan(App("\"C:\\Program Files\\X\\unins000.exe\""));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", plan!.Arguments);
        Assert.False(plan.Interactive);
    }

    [Fact]
    public void Missing_uninstall_command_gives_no_plan() => Assert.Null(UninstallPlanner.Plan(App()));

    [Fact]
    public void Winget_portable_folder_is_removed_only_inside_winget_packages()
    {
        var ok = new InstalledApp
        {
            Source = "WinGetFolder", DisplayName = "Foo.Bar", Scope = "User",
            InstallLocation = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Packages\Foo.Bar_Microsoft.Winget.Source_8wekyb3d8bbwe",
        };
        Assert.Equal(PlanKind.RemoveFolder, UninstallPlanner.Plan(ok)!.Kind);

        var bad = new InstalledApp { Source = "WinGetFolder", DisplayName = "X", InstallLocation = @"C:\Temp\Foo" };
        Assert.Null(UninstallPlanner.Plan(bad));
    }
}

public class InstallScriptBuilderTests
{
    [Fact]
    public void Script_uses_relative_paths_and_requires_admin_for_msi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var item = new IndexedInstaller { FilePath = @"C:\pack\app.msi", Size = 1, Sha256 = "AA" };
        var info = new CatalogInstaller("Foo.Bar", "1.2.3", "x64", "msi", null, "", "", "AA", null, null, null, null, null, null);
        var script = InstallScriptBuilder.Build(new[] { (item, info) }, @"C:\pack");

        Assert.Contains("#Requires -RunAsAdministrator", script);
        Assert.Contains("Join-Path $PSScriptRoot 'app.msi'", script);
        Assert.Contains("Invoke-Step -Name 'Foo.Bar 1.2.3'", script);
        Assert.Contains("msiexec.exe", script);
    }
}
