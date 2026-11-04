using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Matching;
using WingetOffline.Core.Scanning;
using WingetOffline.Core.Versioning;

namespace WingetOffline.UI.Services;

/// <summary>مثبّت موجود في مجلد المثبّتات ومطابق لإصدار محدد من حزمة في الكتالوج.</summary>
public sealed record OfflineInstaller(IndexedInstaller Item, CatalogInstaller Info);

/// <summary>الحالة المشتركة بين كل الصفحات. لا يوجد أي كود شبكي في التطبيق.</summary>
public sealed partial class AppState : ObservableObject
{
    private int _busyCount;
    private bool _operationActive;

    public SettingsData Settings { get; }
    public CatalogStore? Catalog { get; private set; }
    public CatalogQueries? Queries { get; private set; }
    public Matcher? Matcher { get; private set; }
    public IReadOnlyList<MatchResult> ScanResults { get; private set; } = Array.Empty<MatchResult>();
    public IReadOnlyList<IndexedInstaller> Installers { get; private set; } = Array.Empty<IndexedInstaller>();

    [ObservableProperty] private string _statusText = "No catalog loaded. Open Settings and choose catalog.wglib.";
    [ObservableProperty] private string _catalogInfo = "No catalog";
    [ObservableProperty] private bool _isBusy;

    public event EventHandler? CatalogChanged;
    public event EventHandler? ScanChanged;
    public event EventHandler? InstallersChanged;

    public AppState(SettingsData settings) => Settings = settings;

    private void BeginBusy() { _busyCount++; IsBusy = true; }
    private void EndBusy() { _busyCount = Math.Max(0, _busyCount - 1); IsBusy = _busyCount > 0; }

    // ---------------- الكتالوج ----------------

    /// <summary>يحمّل الكتالوج؛ يعيد null عند النجاح أو نص الخطأ.</summary>
    public async Task<string?> LoadCatalogAsync(string path)
    {
        BeginBusy();
        StatusText = "Loading catalog (the first load extracts and verifies the file)...";
        try
        {
            var (store, queries, matcher) = await Task.Run(() =>
            {
                var s = CatalogStore.Open(path);
                return (s, new CatalogQueries(s), new Matcher(s));
            });

            var old = Catalog;
            Catalog = store; Queries = queries; Matcher = matcher;
            Installers = Array.Empty<IndexedInstaller>();
            if (old != null) lock (old.Sync) old.Dispose();

            Settings.CatalogPath = path;
            SettingsStore.Save(Settings);

            CatalogInfo = store.MetaPackages > 0
                ? $"Catalog: {store.MetaPackages:N0} packages · built {store.CreatedUtc:yyyy-MM-dd}"
                : "Catalog loaded";
            StatusText = "Catalog ready.";
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            InstallersChanged?.Invoke(this, EventArgs.Empty);

            if (Settings.AutoScan) _ = ScanAsync();
            if (!string.IsNullOrWhiteSpace(Settings.InstallersFolder) && Directory.Exists(Settings.InstallersFolder))
                _ = IndexInstallersAsync();
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Failed to load catalog.";
            return ex.Message;
        }
        finally { EndBusy(); }
    }

    // ---------------- الفحص ----------------

    public async Task ScanAsync()
    {
        var catalog = Catalog; var matcher = Matcher;
        if (catalog == null || matcher == null) { StatusText = "Load a catalog first."; return; }

        BeginBusy();
        StatusText = "Scanning installed programs...";
        try
        {
            var folders = Settings.PortableFolders.ToList();
            bool wingetPortables = Settings.ScanWingetPortables;

            var results = await Task.Run(() =>
            {
                var apps = RegistryScanner.Scan();
                if (wingetPortables) apps.AddRange(PortableScanner.ScanWingetPackages());
                foreach (var d in folders) apps.AddRange(PortableScanner.ScanFolder(d));
                lock (catalog.Sync) return apps.Select(matcher.Match).ToList();
            });

            ScanResults = results;
            int updates = results.Count(r => r.Status == UpdateStatus.UpdateAvailable && r.Confidence >= Confidence.Medium);
            StatusText = $"Scan complete: {results.Count} items, {updates} update(s) available.";
            ScanChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { StatusText = "Scan failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    // ---------------- مجلد المثبّتات ----------------

    public async Task IndexInstallersAsync()
    {
        var catalog = Catalog;
        var folder = Settings.InstallersFolder;
        if (catalog == null) { StatusText = "Load a catalog first."; return; }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusText = "Choose an existing installers folder in Settings.";
            return;
        }

        BeginBusy();
        StatusText = "Indexing installers...";
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            var items = await Task.Run(() => InstallerIndexer.IndexFolder(catalog, folder, progress));
            Installers = items;
            StatusText = $"Indexed {items.Count} installer file(s): {items.Count(i => i.IsMatched)} recognised.";
            InstallersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { StatusText = "Indexing failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    /// <summary>أفضل مثبّت متاح لحزمة (الأعلى إصداراً)، اختيارياً بشرط أن يكون أحدث من إصدار معيّن.</summary>
    public OfflineInstaller? FindInstaller(string packageId, string? newerThan = null)
    {
        var cmp = WingetVersionComparer.Instance;
        OfflineInstaller? best = null;
        foreach (var file in Installers)
            foreach (var m in file.Matches)
            {
                if (!string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(newerThan) && cmp.Compare(m.Version, newerThan) <= 0) continue;
                if (best == null) { best = new OfflineInstaller(file, m); continue; }
                int c = cmp.Compare(m.Version, best.Info.Version);
                if (c > 0 || (c == 0 && ArchRank(m.Arch) < ArchRank(best.Info.Arch)))
                    best = new OfflineInstaller(file, m);
            }
        return best;
    }

    private static int ArchRank(string a) => a.ToLowerInvariant() switch { "x64" => 0, "neutral" => 1, "x86" => 2, _ => 3 };

    // ---------------- تجاهل التحديثات ----------------

    public bool IsIgnored(string? packageId) =>
        !string.IsNullOrEmpty(packageId) &&
        Settings.IgnoredPackages.Contains(packageId, StringComparer.OrdinalIgnoreCase);

    public void SetIgnored(string packageId, bool ignored)
    {
        Settings.IgnoredPackages.RemoveAll(x => string.Equals(x, packageId, StringComparison.OrdinalIgnoreCase));
        if (ignored) Settings.IgnoredPackages.Add(packageId);
        SettingsStore.Save(Settings);
    }

    // ---------------- التنفيذ ----------------

    public InstallPlan? TryBuildInstallPlan(OfflineInstaller offline)
    {
        try
        {
            Directory.CreateDirectory(PlanRunner.LogDir);
            var opt = new InstallOptions
            {
                MsiLogFile = Path.Combine(PlanRunner.LogDir, $"msi-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
            };
            return InstallPlanner.Plan(offline.Item.FilePath, offline.Info, opt);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>يعرض الأمر الذي سيُنفَّذ ويطلب تأكيداً صريحاً قبل التشغيل.</summary>
    public async Task<RunResult?> ConfirmAndRunAsync(InstallPlan plan, string label)
    {
        if (_operationActive)
        {
            MessageBox.Show("Another operation is still running. Please wait for it to finish.", "WingetOffline",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine(plan.Description).AppendLine();
        sb.AppendLine("Command:").AppendLine(plan.CommandLine).AppendLine();
        sb.AppendLine("Mode: " + (plan.Interactive ? "interactive (the program's own window may appear)" : "silent"));
        sb.AppendLine("Administrator permission: " + (plan.NeedsElevation
            ? (Settings.UseUac ? "will be requested (UAC)" : "needed, but UAC is turned off in Settings")
            : "not needed"));
        if (plan.Warning != null) sb.AppendLine().AppendLine("Warning: " + plan.Warning);
        sb.AppendLine().Append("Run it now?");

        var icon = plan.Warning != null || plan.Interactive ? MessageBoxImage.Warning : MessageBoxImage.Question;
        if (MessageBox.Show(sb.ToString(), "WingetOffline - confirm", MessageBoxButton.YesNo, icon) != MessageBoxResult.Yes)
            return null;

        return await RunPlanAsync(plan, label);
    }

    public async Task<RunResult> RunPlanAsync(InstallPlan plan, string label, bool showResult = true, bool rescan = true)
    {
        _operationActive = true;
        BeginBusy();
        StatusText = plan.Description + "...";
        RunResult res;
        try { res = await Task.Run(() => PlanRunner.Run(plan, label, Settings.UseUac)); }
        finally { _operationActive = false; EndBusy(); }

        StatusText = $"{plan.Description}: {res.Message}";

        // فشل التشغيل الصامت: نعرض إعادة المحاولة بواجهة البرنامج نفسه (كما في Bulk Crap Uninstaller)
        if (showResult && !res.Success && plan.InteractiveFallback != null)
        {
            var retry = MessageBox.Show(
                $"{res.Message}\n\nThe silent run did not succeed. Retry showing the program's own window?",
                plan.Description, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (retry == MessageBoxResult.Yes)
                return await RunPlanAsync(plan.InteractiveFallback, label, showResult, rescan);
        }

        if (showResult)
        {
            var text = $"{res.Message}\n\nExit code: {res.ExitCode}\nLog: {res.LogPath}" +
                       (res.RebootRequired ? "\n\nA reboot is required to finish." : "");
            MessageBox.Show(text, plan.Description, MessageBoxButton.OK,
                res.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        if (res.Success && rescan) _ = ScanAsync();
        return res;
    }
}
