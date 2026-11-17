using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WingetOffline.Core;
using WingetOffline.Core.Library;
using WingetOffline.Core.SelfTest;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

/// <summary>
/// صفحة الفحص الذاتي: تعيد كل الاختبارات الآلية على جهازك بنقرة، وتحوي قائمة الاختبارات اليدوية بنتائجها،
/// وتصدّر تقريراً واحداً (Markdown أو JSON) ترسله لمن يتابع المشروع.
/// </summary>
public sealed partial class SelfTestViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Func<IReadOnlyList<string>> _navTitles;
    private readonly List<CheckRow> _all = new();
    private CancellationTokenSource? _cts;
    private SelfTestReport? _report;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string[] FilterOptions { get; } = { "All", "Needs attention", "FAIL", "WARN", "PASS", "INFO", "SKIP" };

    public ObservableCollection<CheckRow> Results { get; } = new();
    public ObservableCollection<ManualRow> Manual { get; } = new();

    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private bool _deepChecks;
    [ObservableProperty] private bool _onlineChecks;
    [ObservableProperty] private bool _redact = true;
    [ObservableProperty] private CheckRow? _selected;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private string _statusLine = "Press \"Run all checks\". Do not use other pages while it runs.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _manualSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(ExportReportCommand), nameof(CopyReportCommand))]
    private bool _isRunning;

    public SelfTestViewModel(AppState state, Func<IReadOnlyList<string>> navTitles)
    {
        _state = state;
        _navTitles = navTitles;
        var saved = LoadManual();
        foreach (var m in ManualChecks.All)
        {
            var row = new ManualRow(m, saved.FirstOrDefault(s => s.Id == m.Id));
            row.PropertyChanged += OnManualChanged;
            Manual.Add(row);
        }
        UpdateManualSummary();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    // ---------------------------------------------------------------- تشغيل

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        _all.Clear();
        Results.Clear();
        Selected = null;
        Summary = "";
        ProgressValue = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var options = new SelfTestOptions
        {
            CatalogObject = _state.Catalog,
            CatalogPath = _state.Settings.CatalogPath,
            Deep = DeepChecks,
            Online = OnlineChecks,
            InstallersFolder = _state.Settings.InstallersFolder,
            LibraryFolders = _state.Settings.LibraryFolders.ToList(),
            ToolsDirectory = AppContext.BaseDirectory,
            ExtraChecks = UiChecks(),
        };
        var progress = new Progress<SelfTestProgress>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            if (p.Last != null)
            {
                var row = new CheckRow(p.Last);
                _all.Add(row);
                if (Matches(row)) Results.Add(row);
                StatusLine = $"{p.Done}/{p.Total}  {p.Last.Id}  {p.Last.Name}";
            }
        });

        try
        {
            _report = await Task.Run(() => SelfTestEngine.Run(options, progress, ct));
            _report.Manual = Manual.Select(m => m.ToResult()).ToList();
            Summary = $"{_report.Count(CheckStatus.Pass)} pass · {_report.Count(CheckStatus.Fail)} fail · {_report.Count(CheckStatus.Warn)} warn · {_report.Count(CheckStatus.Info)} info · {_report.Count(CheckStatus.Skip)} skipped  in {_report.Seconds:0.#} s"
                    + (_report.Cancelled ? "  (cancelled)" : "");
            StatusLine = _report.HasFailures
                ? "Some checks FAILED. Export the report and send it."
                : "Done. Export the report and send it together with the manual checklist.";
        }
        catch (Exception ex)
        {
            StatusLine = "The self-test crashed: " + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    // ----------------------------------------------------------------- عرض

    private bool Matches(CheckRow r) => Filter switch
    {
        "Needs attention" => r.Status is "FAIL" or "WARN",
        "FAIL" or "WARN" or "PASS" or "INFO" or "SKIP" => r.Status == Filter,
        _ => true,
    };

    private void ApplyFilter()
    {
        Results.Clear();
        foreach (var r in _all.Where(Matches)) Results.Add(r);
    }

    // --------------------------------------------------------------- تصدير

    private SelfTestReport? CurrentReport()
    {
        if (_report == null) return null;
        _report.Manual = Manual.Select(m => m.ToResult()).ToList();
        return _report;
    }

    private bool CanExport() => !IsRunning;

    /// <summary>يحفظ التقرير الكامل (الآلي واليدوي) Markdown أو JSON. يخفي اسم المستخدم والجهاز ما لم تُلغِ الخيار.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportReport()
    {
        var report = CurrentReport();
        if (report == null)
        {
            MessageBox.Show("Run the checks first.", "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Save the self-test report",
            Filter = "Markdown report (*.md)|*.md|JSON report (*.json)|*.json",
            FileName = $"selftest-report-{DateTime.Now:yyyyMMdd-HHmm}.md",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var text = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? report.ToJson(Redact) : report.ToMarkdown(Redact);
            File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
            StatusLine = "Report saved to " + dlg.FileName + (Redact ? " (user name and machine name hidden)" : " (NOT redacted)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not write the report:\n{dlg.FileName}\n\n{ex.Message}", "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void CopyReport()
    {
        var report = CurrentReport();
        if (report == null) return;
        Clipboard.SetText(report.ToMarkdown(Redact));
        StatusLine = "The report (Markdown) was copied to the clipboard.";
    }

    // -------------------------------------------------------------- اليدوي

    private static string ManualPath => AppPaths.Combine("selftest-manual.json");

    private static List<ManualResult> LoadManual()
    {
        try
        {
            if (File.Exists(ManualPath))
                return JsonSerializer.Deserialize<List<ManualResult>>(File.ReadAllText(ManualPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    private void OnManualChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateManualSummary();
        try { File.WriteAllText(ManualPath, JsonSerializer.Serialize(Manual.Select(m => m.ToResult()).ToList(), Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void UpdateManualSummary() =>
        ManualSummary = $"{Manual.Count(m => m.Result == "Pass")} pass · {Manual.Count(m => m.Result == "Fail")} fail · {Manual.Count(m => m.Result == "NotTested")} not tested  (of {Manual.Count})";

    [RelayCommand]
    private void ResetManual()
    {
        var ok = MessageBox.Show("Mark every manual check as not tested and clear the notes?", "WingetOffline", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        foreach (var m in Manual) { m.Result = "NotTested"; m.Note = ""; }
    }

    // ----------------------------------------------------- فحوص الواجهة (منّا)

    /// <summary>فحوص لا يعرفها Core: ترتيب القائمة، الإصدار، الثيم، الإعدادات الافتراضية. تُنفَّذ على خيط الواجهة حين يلزم.</summary>
    private IReadOnlyList<SelfCheck> UiChecks()
    {
        const string cat = "UI";
        var list = new List<SelfCheck>
        {
            new("UI-1", cat, "Sidebar order: Updates first, all pages present", "T10.10", c =>
            {
                var titles = Dispatch(() => _navTitles().ToList());
                var k = new Checker();
                k.Eq("Updates", titles.FirstOrDefault() ?? "", "first page");
                foreach (var p in new[] { "Discover", "Installed", "Installers", "Local", "Devices", "Settings", "Self-test" })
                    k.True(titles.Contains(p), $"page '{p}' is in the sidebar");
                return k.Result("sidebar: " + string.Join(", ", titles));
            }),
            new("UI-2", cat, "Version is shown in the sidebar and matches the program", "T10.10 T9.1", c =>
            {
                var shown = AppInfo.Version;
                var asm = BuildInfo.VersionOf(typeof(AppInfo).Assembly);
                return string.IsNullOrWhiteSpace(shown) || !shown.StartsWith(asm.Split('-')[0])
                    ? CheckOutcome.Fail($"Sidebar shows '{shown}' but the assembly version is '{asm}'.")
                    : CheckOutcome.Pass($"v{shown}");
            }),
            new("UI-3", cat, "One-click theme switch flips and restores", "T2.12", c =>
            {
                var original = _state.Theme;
                bool wasDark = Dispatch(() => _state.IsDark);
                try
                {
                    bool flipped = Dispatch(() => { _state.ToggleTheme(); return _state.IsDark; });
                    bool back = Dispatch(() => { _state.ToggleTheme(); return _state.IsDark; });
                    var k = new Checker();
                    k.True(flipped != wasDark, "the first click changes the theme");
                    k.True(back == wasDark, "the second click returns to the original theme");
                    return k.Result($"started {(wasDark ? "dark" : "light")}, flipped, came back");
                }
                finally { Dispatch(() => { _state.SetTheme(original); return 0; }); }
            }),
            new("UI-4", cat, "Settings defaults: xxHash64, hashing off, default exclusions", "T10.12 T8.16", c =>
            {
                var d = new SettingsData();
                var k = new Checker();
                k.Eq("XXH64", d.LibraryHashingAlgorithm, "default hash algorithm");
                k.Eq("Off", d.LibraryHashingMode, "hashing is off by default");
                k.Eq(30, d.LibraryQuickAboveMB, "quick mode threshold (MB)");
                k.Eq(10, d.LibraryQuickHeadMB, "quick mode head (MB)");
                k.Eq(LibraryExclusions.DefaultText, d.LibraryExclusionRules, "default exclusion rules");
                k.True(!d.LibraryWholeComputer, "whole-computer search is off by default");
                return k.Result("defaults of a fresh settings file");
            }),
            new("UI-5", cat, "Catalog state in the app", "T2.1", c =>
            {
                var db = _state.Catalog;
                return db == null
                    ? CheckOutcome.Info("No catalog is loaded in the app (catalog checks were skipped unless you passed a path).")
                    : CheckOutcome.Info($"{db.MetaPackages:N0} packages loaded, built {(db.CreatedUtc is { } d ? d.ToString("yyyy-MM-dd") : "?")}");
            }),
        };
        return list;
    }

    private static T Dispatch<T>(Func<T> f) =>
        Application.Current?.Dispatcher is { } d && !d.CheckAccess() ? d.Invoke(f) : f();
}
