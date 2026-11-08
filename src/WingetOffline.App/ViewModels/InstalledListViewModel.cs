using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WingetOffline.Core.Export;
using WingetOffline.Core.Install;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Packing;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

/// <summary>صفحة "Installed" وصفحة "Updates" (نفس الكود، الثانية مفلترة على التحديثات).</summary>
public sealed partial class InstalledListViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly bool _updatesOnly;
    private List<InstalledRow> _all = new();
    private OfflineInstaller? _updateInstaller;
    private InstallPlan? _uninstallPlan;

    public string Title => _updatesOnly ? "Software updates" : "Installed packages";
    public bool ShowStatusFilter => !_updatesOnly;
    public bool IsUpdatesPage => _updatesOnly;
    public bool ShowExport => !_updatesOnly;

    public static string[] StatusOptions { get; } = { "All", "Updates available", "Up to date", "Unknown version", "Not matched", "Ignored updates", "No uninstall command" };
    public static string[] ConfidenceOptions { get; } = { "Any", "Medium or higher", "High only" };
    public static string[] SourceOptions { get; } = { "Any", "Installed (registry)", "Portable / winget folders" };
    public static string[] ScopeOptions { get; } = { "Any", "Machine", "User", "Portable" };

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _status = "All";
    [ObservableProperty] private string _minConfidence = "Any";
    [ObservableProperty] private string _source = "Any";
    [ObservableProperty] private string _scope = "Any";
    [ObservableProperty] private IReadOnlyList<InstalledRow> _rows = Array.Empty<InstalledRow>();
    [ObservableProperty] private string _summary = "No scan results yet.";
    [ObservableProperty] private InstalledRow? _selected;
    [ObservableProperty] private string _offlineText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _canUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _canUninstall;

    [ObservableProperty] private string _ignoreText = "Ignore updates";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleIgnoreCommand))]
    private bool _canIgnore;

    public InstalledListViewModel(AppState state, bool updatesOnly)
    {
        _state = state;
        _updatesOnly = updatesOnly;
        if (updatesOnly) { _status = "Updates available"; _minConfidence = "Medium or higher"; }

        PropertyChanged += OnSelfPropertyChanged;
        _state.ScanChanged += (_, _) => Reload();
        _state.InstallersChanged += (_, _) => RefreshActions();
        Reload();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterText) or nameof(Status) or nameof(MinConfidence)
            or nameof(Source) or nameof(Scope))
            Apply();
        else if (e.PropertyName == nameof(Selected))
            RefreshActions();
    }

    private void Reload()
    {
        _all = _state.ScanResults.Select(r => new InstalledRow(r, _state.IsIgnored(r.PackageId))).ToList();
        Apply();
    }

    private void Apply()
    {
        IEnumerable<InstalledRow> q = _all;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var t = FilterText.Trim();
            q = q.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.Publisher.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.PackageId.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        q = Status switch
        {
            "Updates available" => q.Where(r => r.IsMatched && r.Status == UpdateStatus.UpdateAvailable && !r.IsIgnored),
            "Up to date" => q.Where(r => r.IsMatched && r.Status == UpdateStatus.UpToDate),
            "Unknown version" => q.Where(r => r.IsMatched && r.Status == UpdateStatus.Unknown),
            "Not matched" => q.Where(r => !r.IsMatched),
            "Ignored updates" => q.Where(r => r.IsIgnored),
            "No uninstall command" => q.Where(r => !r.HasUninstaller),
            _ => q,
        };

        q = MinConfidence switch
        {
            "Medium or higher" => q.Where(r => r.Result.Confidence >= Confidence.Medium),
            "High only" => q.Where(r => r.Result.Confidence == Confidence.High),
            _ => q,
        };

        q = Source switch
        {
            "Installed (registry)" => q.Where(r => r.Source == "Registry"),
            "Portable / winget folders" => q.Where(r => r.Source != "Registry"),
            _ => q,
        };

        if (Scope != "Any") q = q.Where(r => r.Scope == Scope);

        var list = q.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Rows = list;
        int updates = _all.Count(r => r.IsReliableUpdate);
        Summary = _all.Count == 0
            ? "No scan results yet. Load a catalog, then press Scan."
            : $"{list.Count} shown · {_all.Count} total · {updates} update(s) with Medium+ match confidence";
    }

    /// <summary>يحدّث أزرار Update/Uninstall حسب العنصر المحدد ومحتوى مجلد المثبّتات.</summary>
    private void RefreshActions()
    {
        var row = Selected;
        _updateInstaller = null;
        _uninstallPlan = null;
        if (row == null)
        {
            OfflineText = "";
            CanUpdate = false;
            CanUninstall = false;
            CanIgnore = false;
            return;
        }

        CanIgnore = row.IsMatched && !string.IsNullOrEmpty(row.PackageId);
        IgnoreText = row.IsIgnored ? "Stop ignoring updates" : "Ignore updates";

        _uninstallPlan = UninstallPlanner.Plan(row.Result.App);
        CanUninstall = _uninstallPlan != null;

        if (row.IsReliableUpdate && !string.IsNullOrEmpty(row.PackageId))
        {
            _updateInstaller = _state.FindInstaller(row.PackageId, Normalizer.ComparableVersion(row.InstalledVersion));
            OfflineText = _updateInstaller != null
                ? $"Offline installer ready: {Path.GetFileName(_updateInstaller.Item.FilePath)} (v{_updateInstaller.Info.Version})"
                : "No newer installer for this package in your installers folder.";
        }
        else
        {
            OfflineText = row.IsMatched && row.Status == UpdateStatus.UpdateAvailable
                ? "Low-confidence match: updating is disabled for it."
                : "";
        }
        CanUpdate = _updateInstaller != null;
    }

    [RelayCommand]
    private Task ScanAsync() => _state.ScanAsync();

    [RelayCommand(CanExecute = nameof(CanIgnore))]
    private void ToggleIgnore()
    {
        var row = Selected;
        if (row == null || string.IsNullOrEmpty(row.PackageId)) return;
        _state.SetIgnored(row.PackageId, !row.IsIgnored);
        Reload();
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        var row = Selected; var offline = _updateInstaller;
        if (row == null || offline == null) return;
        var plan = _state.TryBuildInstallPlan(offline);
        if (plan != null) await _state.ConfirmAndRunAsync(plan, row.PackageId);
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var row = Selected; var plan = _uninstallPlan;
        if (row == null || plan == null) return;
        await _state.ConfirmAndRunAsync(plan, "uninstall-" + row.Name);
    }

    /// <summary>يثبّت كل التحديثات الموثوقة التي لها مثبّت في المجلد، واحداً بعد الآخر، بعد تأكيد واحد.</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var items = new List<(InstalledRow Row, OfflineInstaller Off)>();
        foreach (var r in _all.Where(x => x.IsReliableUpdate && !string.IsNullOrEmpty(x.PackageId)))
        {
            var off = _state.FindInstaller(r.PackageId, Normalizer.ComparableVersion(r.InstalledVersion));
            if (off != null) items.Add((r, off));
        }
        if (items.Count == 0)
        {
            MessageBox.Show("No available update has an installer in your installers folder.", "WingetOffline",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("\n", items.Select(i => $"  • {i.Row.Name}: {i.Row.InstalledVersion} -> {i.Off.Info.Version}"));
        var ask = MessageBox.Show(
            $"{items.Count} update(s) will be installed one after another, silently when possible:\n\n{names}\n\n" +
            "Windows may ask for administrator permission for each one.\n\nContinue?",
            "WingetOffline - update all", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        int ok = 0;
        var failed = new List<string>();
        foreach (var (row, off) in items)
        {
            var plan = _state.TryBuildInstallPlan(off);
            if (plan == null) { failed.Add(row.Name + " (no install command)"); continue; }
            var res = await _state.RunPlanAsync(plan, row.PackageId, showResult: false, rescan: false);
            if (res.Success) ok++; else failed.Add($"{row.Name} (exit code {res.ExitCode})");
        }

        await _state.ScanAsync();
        var summary = $"{ok} of {items.Count} update(s) succeeded." +
                      (failed.Count > 0 ? "\n\nFailed:\n  • " + string.Join("\n  • ", failed) : "") +
                      $"\n\nLogs: {PlanRunner.LogDir}";
        MessageBox.Show(summary, "WingetOffline - update all", MessageBoxButton.OK,
            failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// يحفظ قائمة التحديثات الموثوقة. JSON هو الصيغة التي يقرؤها Builder على الجهاز المتصل؛
    /// وExcel وCSV للقراءة البشرية (UTF-8، العربية وكل اللغات مدعومة).
    /// </summary>
    [RelayCommand]
    private void ExportUpdates()
    {
        var entries = _all
            .Where(r => r.IsReliableUpdate && !string.IsNullOrEmpty(r.PackageId))
            .Select(r => new UpdateEntry(r.PackageId, r.LatestVersion, r.InstalledVersion, r.Name))
            .ToList();
        if (entries.Count == 0)
        {
            MessageBox.Show("There are no reliable updates to export (Medium+ match, not ignored).", "WingetOffline",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pick = ExportDialog.Ask("Save the list of updates you need", "updates-needed",
            ExportFormat.Json, ExportFormat.Xlsx, ExportFormat.Csv);
        if (pick == null) return;
        var (path, format) = pick.Value;

        try
        {
            if (format == ExportFormat.Json) UpdateList.Write(path, entries);
            else TableExporter.Write(path, ExportBuilders.Updates(entries), format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowExportError(path, ex);
            return;
        }

        _state.StatusText = format == ExportFormat.Json
            ? $"Saved {entries.Count} update(s) to {path}. On a connected machine run: " +
              "WingetOffline.Builder pack --from-scan <file> --catalog <catalog.wglib> --out <folder>"
            : $"Saved {entries.Count} update(s) to {path}. For Builder pack on a connected machine, export JSON instead.";
    }

    /// <summary>يصدّر الصفوف الظاهرة الآن (بعد الفلاتر) إلى Excel أو CSV أو JSON.</summary>
    [RelayCommand]
    private void Export()
    {
        var rows = Rows;
        if (rows.Count == 0)
        {
            MessageBox.Show("There is nothing to export. Run a scan or relax the filters.", "WingetOffline",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pick = ExportDialog.Ask("Export the installed packages shown", $"installed-{DateTime.Now:yyyyMMdd}",
            ExportFormat.Xlsx, ExportFormat.Csv, ExportFormat.Json);
        if (pick == null) return;
        var (path, format) = pick.Value;

        try { TableExporter.Write(path, ExportBuilders.Installed(rows), format); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowExportError(path, ex);
            return;
        }
        _state.StatusText = $"Exported {rows.Count} row(s) to {path}";
    }

    private static void ShowExportError(string path, Exception ex) =>
        MessageBox.Show($"Could not write:\n{path}\n\n{ex.Message}\n\nIf the file is open in Excel, close it and try again.",
            "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Error);

    [RelayCommand]
    private void OpenFolder()
    {
        var loc = Selected?.InstallLocation;
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{loc}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyUninstallCommand()
    {
        if (!string.IsNullOrEmpty(Selected?.UninstallCommand)) Clipboard.SetText(Selected!.UninstallCommand);
    }
}
