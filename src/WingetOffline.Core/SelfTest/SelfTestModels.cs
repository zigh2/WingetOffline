using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WingetOffline.Core.SelfTest;

public enum CheckStatus { Pass, Fail, Warn, Info, Skip }

/// <summary>نتيجة فحص آلي واحد.</summary>
public sealed class CheckResult
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>أرقام اختبارات docs/TEST-PLAN.md التي يغطيها هذا الفحص (للربط عند الإبلاغ).</summary>
    public string PlanIds { get; set; } = "";
    public CheckStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string? Details { get; set; }
    public long DurationMs { get; set; }
}

public readonly record struct CheckOutcome(CheckStatus Status, string Message, string? Details = null)
{
    public static CheckOutcome Pass(string message, string? details = null) => new(CheckStatus.Pass, message, details);
    public static CheckOutcome Fail(string message, string? details = null) => new(CheckStatus.Fail, message, details);
    public static CheckOutcome Warn(string message, string? details = null) => new(CheckStatus.Warn, message, details);
    public static CheckOutcome Info(string message, string? details = null) => new(CheckStatus.Info, message, details);
    public static CheckOutcome Skip(string message) => new(CheckStatus.Skip, message);
}

public sealed class SelfTestOptions
{
    /// <summary>مسار catalog.wglib (سطر الأوامر). تُفتح منه نسخة خاصة بالفحص وتُغلق بعده.</summary>
    public string? CatalogPath { get; init; }
    /// <summary>كتالوج مفتوح مسبقاً (CatalogStore) من التطبيق؛ لا يغلقه المحرك.</summary>
    public object? CatalogObject { get; init; }
    /// <summary>فحوص عميقة: تستخدم مجلداتك الحقيقية وقد تطول (فحص مكتبتك وبحث في القرص).</summary>
    public bool Deep { get; init; }
    /// <summary>فحوص تحتاج إنترنت: تنزيل برنامج صغير عبر Builder والتحقق منه.</summary>
    public bool Online { get; init; }
    public string? InstallersFolder { get; init; }
    public IReadOnlyList<string> LibraryFolders { get; init; } = Array.Empty<string>();
    /// <summary>أين يُبحث عن wgo وBuilder (افتراضياً مجلد البرنامج ثم PATH).</summary>
    public string? ToolsDirectory { get; init; }
    public IReadOnlyList<SelfCheck> ExtraChecks { get; init; } = Array.Empty<SelfCheck>();
    public string? OnlyCategory { get; init; }
}

public sealed class SelfCheck
{
    public SelfCheck(string id, string category, string name, string planIds, Func<SelfTestContext, CheckOutcome> run)
    {
        Id = id; Category = category; Name = name; PlanIds = planIds; Run = run;
    }
    public string Id { get; }
    public string Category { get; }
    public string Name { get; }
    public string PlanIds { get; }
    public Func<SelfTestContext, CheckOutcome> Run { get; }
    public bool NeedsCatalog { get; init; }
    public bool NeedsWindows { get; init; }
    public bool DeepOnly { get; init; }
    public bool OnlineOnly { get; init; }
}

/// <summary>سياق مشترك بين الفحوص: مجلد مؤقت يُحذف في النهاية وحاوية لما تتبادله الفحوص.</summary>
public sealed class SelfTestContext : IDisposable
{
    public SelfTestContext(SelfTestOptions options, CancellationToken ct)
    {
        Options = options;
        Ct = ct;
        TempDir = Path.Combine(Path.GetTempPath(), "wgo-selftest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(TempDir);
    }

    public SelfTestOptions Options { get; }
    public CancellationToken Ct { get; }
    public string TempDir { get; }
    public Dictionary<string, object> Items { get; } = new();
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>مجلد جديد فارغ داخل المجلد المؤقت.</summary>
    public string NewDir(string name)
    {
        var p = Path.Combine(TempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        // لا نُغلق كتالوج التطبيق الخارجي (catalog.external): يملكه التطبيق ويستعمله بعد انتهاء الفحص.
        foreach (var (k, v) in Items)
            if (k != "catalog.external") (v as IDisposable)?.Dispose();
        try { Directory.Delete(TempDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>مجمّع تأكيدات خفيف: الفحص يجمع كل ما فشل ثم يعرضه دفعة واحدة بدل التوقف عند أول خطأ.</summary>
public sealed class Checker
{
    private readonly List<string> _failures = new();
    private int _count;
    public int Count => _count;

    public void True(bool condition, string what)
    {
        _count++;
        if (!condition) _failures.Add(what);
    }

    public void Eq<T>(T expected, T actual, string what)
    {
        _count++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) _failures.Add($"{what}: expected '{expected}', got '{actual}'");
    }

    public void Fail(string what) { _count++; _failures.Add(what); }

    public bool Ok => _failures.Count == 0;

    public CheckOutcome Result(string passMessage, string? details = null) =>
        Ok ? CheckOutcome.Pass($"{passMessage} ({_count} assertions)", details)
           : CheckOutcome.Fail($"{_failures.Count} of {_count} assertions failed: {_failures[0]}", string.Join("\n", _failures) + (details != null ? "\n\n" + details : ""));
}

/// <summary>خانة في قائمة الفحص اليدوي (ما لا يمكن أتمتته: واجهة، تثبيت فعلي، Excel...).</summary>
public sealed class ManualCheck
{
    public ManualCheck(string id, string priority, string action, string expected)
    {
        Id = id; Priority = priority; Action = action; Expected = expected;
    }
    public string Id { get; }
    public string Priority { get; }
    public string Action { get; }
    public string Expected { get; }
}

public sealed class ManualResult
{
    public string Id { get; set; } = "";
    /// <summary>NotTested أو Pass أو Fail.</summary>
    public string Result { get; set; } = "NotTested";
    public string Note { get; set; } = "";
}

/// <summary>إخفاء اسم المستخدم والجهاز ومسار الملف الشخصي من التقرير قبل مشاركته.</summary>
public static class Redactor
{
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            var user = Environment.UserName;
            if (user.Length >= 3) s = s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
            var machine = Environment.MachineName;
            if (machine.Length >= 3) s = s.Replace(machine, "<machine>", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) { }
        return s;
    }
}

/// <summary>تقرير كامل قابل للتصدير (Markdown / JSON / نص).</summary>
public sealed class SelfTestReport
{
    public DateTime StartedUtc { get; init; }
    public double Seconds { get; set; }
    public string AppVersion { get; init; } = "";
    public string Os { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public long ProcessBytes { get; init; }
    public string Culture { get; init; } = "";
    public bool Elevated { get; init; }
    public bool Deep { get; init; }
    public bool Online { get; init; }
    public bool Cancelled { get; set; }
    public List<CheckResult> Results { get; } = new();
    public List<ManualResult> Manual { get; set; } = new();

    public int Count(CheckStatus s) => Results.Count(r => r.Status == s);
    public bool HasFailures => Results.Any(r => r.Status == CheckStatus.Fail);

    public static SelfTestReport NewFor(SelfTestOptions o)
    {
        string path = Environment.ProcessPath ?? "";
        long size = 0;
        try { if (path.Length > 0 && File.Exists(path)) size = new FileInfo(path).Length; } catch (IOException) { }
        bool elevated = false;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException) { }
        }
        return new SelfTestReport
        {
            StartedUtc = DateTime.UtcNow,
            AppVersion = BuildInfo.VersionOf(System.Reflection.Assembly.GetEntryAssembly()),
            Os = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessPath = path,
            ProcessBytes = size,
            Culture = CultureInfo.CurrentCulture.Name + " / UI " + CultureInfo.CurrentUICulture.Name,
            Elevated = elevated,
            Deep = o.Deep,
            Online = o.Online,
        };
    }

    private static string Icon(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "PASS", CheckStatus.Fail => "**FAIL**", CheckStatus.Warn => "WARN", CheckStatus.Info => "info", _ => "skip",
    };

    private static string Cell(string? s, bool redact) =>
        (redact ? Redactor.Apply(s) : s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    public string ToMarkdown(bool redact = true)
    {
        string R(string? s) => redact ? Redactor.Apply(s) : s ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("# WingetOffline self-test report").AppendLine();
        sb.AppendLine($"- **Program:** {AppVersion}  ·  **Date:** {StartedUtc:yyyy-MM-dd HH:mm} UTC  ·  **Duration:** {Seconds:0.#} s" + (Cancelled ? "  ·  **CANCELLED**" : ""));
        sb.AppendLine($"- **OS:** {R(Os)}  ·  **Architecture:** {Architecture}  ·  **Elevated:** {Elevated}");
        sb.AppendLine($"- **Runtime:** {Runtime}  ·  **Culture:** {Culture}");
        sb.AppendLine($"- **Executable:** {R(ProcessPath)} ({ProcessBytes / 1048576.0:0.##} MB)");
        sb.AppendLine($"- **Deep checks:** {(Deep ? "on" : "off")}  ·  **Online checks:** {(Online ? "on" : "off")}  ·  **Redacted:** {redact}").AppendLine();
        sb.AppendLine($"**Summary:** {Count(CheckStatus.Pass)} pass · {Count(CheckStatus.Fail)} fail · {Count(CheckStatus.Warn)} warn · {Count(CheckStatus.Info)} info · {Count(CheckStatus.Skip)} skipped").AppendLine();

        var attention = Results.Where(r => r.Status is CheckStatus.Fail or CheckStatus.Warn).ToList();
        sb.AppendLine("## Needs attention").AppendLine();
        if (attention.Count == 0) sb.AppendLine("Nothing failed and nothing warned.").AppendLine();
        foreach (var r in attention)
        {
            sb.AppendLine($"### {Icon(r.Status)} `{r.Id}` {r.Name}" + (r.PlanIds.Length > 0 ? $" — plan: {r.PlanIds}" : ""));
            sb.AppendLine(R(r.Message));
            if (!string.IsNullOrWhiteSpace(r.Details)) sb.AppendLine().AppendLine("```").AppendLine(R(r.Details)).AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## All automated results").AppendLine();
        sb.AppendLine("| Status | ID | Check | Plan | ms | Message |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in Results)
            sb.AppendLine($"| {Icon(r.Status)} | {r.Id} | {Cell(r.Name, redact)} | {r.PlanIds} | {r.DurationMs} | {Cell(r.Message, redact)} |");

        var withDetails = Results.Where(r => r.Status is CheckStatus.Info or CheckStatus.Pass && !string.IsNullOrWhiteSpace(r.Details)).ToList();
        if (withDetails.Count > 0)
        {
            sb.AppendLine().AppendLine("## Details of informational checks").AppendLine();
            foreach (var r in withDetails)
                sb.AppendLine($"### `{r.Id}` {r.Name}").AppendLine("```").AppendLine(R(r.Details)).AppendLine("```").AppendLine();
        }

        sb.AppendLine().AppendLine("## Manual checklist").AppendLine();
        var done = Manual.Where(m => m.Result != "NotTested").ToList();
        sb.AppendLine($"{Manual.Count(m => m.Result == "Pass")} pass · {Manual.Count(m => m.Result == "Fail")} fail · {Manual.Count(m => m.Result == "NotTested")} not tested (of {Manual.Count})").AppendLine();
        sb.AppendLine("| ID | Result | Note |").AppendLine("|---|---|---|");
        foreach (var m in Manual.Where(m => m.Result != "NotTested" || m.Note.Length > 0))
            sb.AppendLine($"| {m.Id} | {m.Result} | {Cell(m.Note, redact)} |");
        var notTested = Manual.Where(m => m.Result == "NotTested" && m.Note.Length == 0).Select(m => m.Id).ToList();
        if (notTested.Count > 0) sb.AppendLine().AppendLine("Not tested: " + string.Join(", ", notTested));
        return sb.ToString();
    }

    public string ToJson(bool redact = true)
    {
        string R(string? s) => redact ? Redactor.Apply(s) : s ?? "";
        var obj = new
        {
            format = 1,
            kind = "selftest",
            startedUtc = StartedUtc,
            seconds = Seconds,
            cancelled = Cancelled,
            program = AppVersion,
            os = R(Os), runtime = Runtime, architecture = Architecture, culture = Culture, elevated = Elevated,
            executable = R(ProcessPath), executableBytes = ProcessBytes,
            deep = Deep, online = Online, redacted = redact,
            summary = new { pass = Count(CheckStatus.Pass), fail = Count(CheckStatus.Fail), warn = Count(CheckStatus.Warn), info = Count(CheckStatus.Info), skip = Count(CheckStatus.Skip) },
            results = Results.Select(r => new
            {
                id = r.Id, category = r.Category, name = r.Name, plan = r.PlanIds, status = r.Status.ToString(),
                message = R(r.Message), details = r.Details == null ? null : R(r.Details), ms = r.DurationMs,
            }),
            manual = Manual.Select(m => new { id = m.Id, result = m.Result, note = R(m.Note) }),
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
