using System.Text.Json;
using WingetOffline.Core.SelfTest;

namespace WingetOffline.Tests;

public class SelfTestTests
{
    private static string? FindTestPlan()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "docs", "TEST-PLAN.md");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void The_whole_automatic_suite_passes_on_this_machine_without_a_catalog()
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "wgo-selftest-*").Length;
        var report = SelfTestEngine.Run(new SelfTestOptions());

        var failures = report.Results.Where(r => r.Status == CheckStatus.Fail).Select(r => $"{r.Id}: {r.Message}").ToList();
        Assert.True(failures.Count == 0, "Self-test failures:\n" + string.Join("\n", failures));
        Assert.True(report.Count(CheckStatus.Pass) >= 15, $"only {report.Count(CheckStatus.Pass)} checks passed");
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "wgo-selftest-*").Length);   // لا يترك مجلداً مؤقتاً
    }

    [Fact]
    public void Checks_have_unique_ids_names_and_categories()
    {
        var checks = SelfTestEngine.BuildChecks(new SelfTestOptions());
        Assert.True(checks.Count >= 40, $"only {checks.Count} checks are registered");
        Assert.Equal(checks.Count, checks.Select(c => c.Id).Distinct().Count());
        Assert.True(checks.All(c => c.Name.Length > 0 && c.Category.Length > 0), "every check has a name and a category");
    }

    [Fact]
    public void Catalog_checks_are_skipped_not_failed_when_there_is_no_catalog()
    {
        var report = SelfTestEngine.Run(new SelfTestOptions { OnlyCategory = "Catalog" });
        Assert.NotEmpty(report.Results);
        Assert.True(report.Results.All(r => r.Status == CheckStatus.Skip), "catalog checks must skip when no catalog is given");
    }

    [Fact]
    public void Deep_and_online_checks_are_off_by_default()
    {
        var report = SelfTestEngine.Run(new SelfTestOptions());
        Assert.Equal(CheckStatus.Skip, report.Results.First(r => r.Id == "LIB-12").Status);
        Assert.Equal(CheckStatus.Skip, report.Results.First(r => r.Id == "ONL-1").Status);
    }

    [Fact]
    public void A_crashing_extra_check_becomes_a_failure_and_the_run_continues()
    {
        var extra = new[]
        {
            new SelfCheck("X-1", "Extra", "boom", "", c => throw new InvalidOperationException("kaboom")),
            new SelfCheck("X-2", "Extra", "fine", "", c => CheckOutcome.Pass("ok")),
        };
        var report = SelfTestEngine.Run(new SelfTestOptions { ExtraChecks = extra, OnlyCategory = "Extra" });
        Assert.Equal(2, report.Results.Count);
        Assert.Equal(CheckStatus.Fail, report.Results[0].Status);
        Assert.Contains("kaboom", report.Results[0].Message);
        Assert.Equal(CheckStatus.Pass, report.Results[1].Status);
        Assert.True(report.HasFailures, "report flags the failure");
    }

    [Fact]
    public void A_cancelled_run_stops_early_and_says_so()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = SelfTestEngine.Run(new SelfTestOptions(), null, cts.Token);
        Assert.True(report.Cancelled, "cancelled flag");
        Assert.Empty(report.Results);
    }

    [Fact]
    public void Reports_render_to_markdown_and_json_and_hide_the_user_name_on_request()
    {
        var report = SelfTestReport.NewFor(new SelfTestOptions());
        var user = Environment.UserName;
        report.Results.Add(new CheckResult { Id = "T-1", Category = "C", Name = "n", Status = CheckStatus.Warn, Message = $@"path C:\Users\{user}\x.exe" });
        report.Manual = new List<ManualResult> { new() { Id = "T2.1", Result = "Pass", Note = "fine" }, new() { Id = "T2.2" } };

        var md = report.ToMarkdown(redact: true);
        Assert.Contains("# WingetOffline self-test report", md);
        Assert.Contains("Needs attention", md);
        Assert.Contains("| T2.1 | Pass | fine |", md);
        if (user.Length >= 3) Assert.DoesNotContain(user, md.Replace("<user>", ""), StringComparison.OrdinalIgnoreCase);
        if (user.Length >= 3) Assert.Contains(user, report.ToMarkdown(redact: false), StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(report.ToJson());
        Assert.Equal("selftest", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("summary").GetProperty("warn").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("manual").GetArrayLength());
    }

    [Fact]
    public void The_manual_checklist_is_complete_unique_and_matches_the_test_plan()
    {
        var all = ManualChecks.All;
        Assert.True(all.Count >= 60, $"only {all.Count} manual checks");
        Assert.Equal(all.Count, all.Select(m => m.Id).Distinct().Count());
        Assert.True(all.All(m => m.Action.Length > 0 && m.Expected.Length > 0), "each manual check has steps and an expected result");

        var plan = FindTestPlan();
        if (plan == null) return;   // خارج شجرة المستودع: نكتفي بما سبق
        var text = File.ReadAllText(plan);
        foreach (var m in all) Assert.True(text.Contains($"| {m.Id} |"), $"manual check {m.Id} is not in docs/TEST-PLAN.md");
    }
}
