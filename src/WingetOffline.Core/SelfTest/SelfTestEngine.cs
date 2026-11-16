using System.Diagnostics;

namespace WingetOffline.Core.SelfTest;

public sealed record SelfTestProgress(int Done, int Total, CheckResult? Last);

/// <summary>
/// يشغّل كل الفحوص الآلية ويجمع تقريراً. الفحوص تعمل على بيانات مؤقتة (مجلد مؤقت يُحذف) ولا تغيّر مكتبتك
/// ولا إعداداتك، وما يلمس بياناتك الحقيقية فيها (الفحوص العميقة) يقرأ فقط.
/// </summary>
public static partial class SelfTestEngine
{
    /// <summary>نقطة تسجيل الفحوص التي تحتاج الكتالوج (ملف منفصل يعتمد على SQLite).</summary>
    static partial void AddCatalogChecks(List<SelfCheck> list);

    public static IReadOnlyList<SelfCheck> BuildChecks(SelfTestOptions options)
    {
        var list = new List<SelfCheck>();
        BasicChecks.Add(list);
        LibraryChecks.Add(list);
        ToolChecks.Add(list);
        AddCatalogChecks(list);
        list.AddRange(options.ExtraChecks);
        if (!string.IsNullOrWhiteSpace(options.OnlyCategory))
            list = list.Where(c => c.Category.Equals(options.OnlyCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        return list;
    }

    public static SelfTestReport Run(SelfTestOptions options, IProgress<SelfTestProgress>? progress = null, CancellationToken ct = default)
    {
        var checks = BuildChecks(options);
        var report = SelfTestReport.NewFor(options);
        var started = Stopwatch.StartNew();
        using var ctx = new SelfTestContext(options, ct);
        if (options.CatalogObject != null) ctx.Items["catalog.external"] = options.CatalogObject;

        int done = 0;
        foreach (var check in checks)
        {
            if (ct.IsCancellationRequested) { report.Cancelled = true; break; }

            var result = new CheckResult { Id = check.Id, Category = check.Category, Name = check.Name, PlanIds = check.PlanIds };
            CheckOutcome outcome;
            var sw = Stopwatch.StartNew();
            try
            {
                if (check.NeedsWindows && !ctx.IsWindows) outcome = CheckOutcome.Skip("Windows only.");
                else if (check.DeepOnly && !options.Deep) outcome = CheckOutcome.Skip("Deep checks are off (they use your real folders and can take long).");
                else if (check.OnlineOnly && !options.Online) outcome = CheckOutcome.Skip("Online checks are off (they download a small program).");
                else if (check.NeedsCatalog && !HasCatalog(ctx)) outcome = CheckOutcome.Skip("No catalog loaded. Load catalog.wglib in the app, or pass it on the command line.");
                else outcome = check.Run(ctx);
            }
            catch (OperationCanceledException) { report.Cancelled = true; break; }
            catch (Exception ex)
            {
                outcome = CheckOutcome.Fail($"Unexpected {ex.GetType().Name}: {ex.Message}", ex.ToString());
            }
            sw.Stop();

            result.Status = outcome.Status;
            result.Message = outcome.Message;
            result.Details = outcome.Details;
            result.DurationMs = sw.ElapsedMilliseconds;
            report.Results.Add(result);
            progress?.Report(new SelfTestProgress(++done, checks.Count, result));
        }

        report.Seconds = started.Elapsed.TotalSeconds;
        return report;
    }

    internal static bool HasCatalog(SelfTestContext ctx) =>
        ctx.Options.CatalogObject != null
        || (!string.IsNullOrWhiteSpace(ctx.Options.CatalogPath) && File.Exists(ctx.Options.CatalogPath));
}
