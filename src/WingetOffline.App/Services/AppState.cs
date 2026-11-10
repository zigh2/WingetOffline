using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Library;
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

    /// <summary>المكتبة المحلية (library.json): ما عُرف عن ملفات المثبّتات في مجلداتك.</summary>
    public LibraryStore Library { get; } = LibraryStore.Load();
    /// <summary>ربط البرامج المثبّتة بملفات المكتبة في الاتجاهين (يُحدَّث بعد كل فحص ومزامنة).</summary>
    public LibraryLinks LibraryLinks { get; private set; } = new();
    /// <summary>ذاكرة ما فُحص بالحجم + تاريخ التعديل (library-cache.json): النقل وإعادة التسمية لا يعيدان القراءة ولا البصمة.</summary>
    public LibraryFileCache LibraryCache { get; } = LibraryFileCache.Load();
    private CancellationTokenSource? _libraryCts;

    [ObservableProperty] private string _statusText = "No catalog loaded. Open Settings and choose catalog.wglib.";
    [ObservableProperty] private string _catalogInfo = "No catalog";
    [ObservableProperty] private bool _isBusy;

    public event EventHandler? CatalogChanged;
    public event EventHandler? ScanChanged;
    public event EventHandler? InstallersChanged;
    public event EventHandler? ThemeChanged;
    public event EventHandler? LibraryChanged;

    public AppState(SettingsData settings) => Settings = settings;

    // ---------------- الثيم ----------------

    /// <summary>الإعداد المحفوظ: System أو Light أو Dark.</summary>
    public string Theme => Settings.Theme;

    /// <summary>الثيم المطبَّق فعلاً الآن (يحسم System).</summary>
    public bool IsDark => Application.Current is App app && app.IsDark;

    /// <summary>يطبّق الثيم فوراً ويحفظه ويُعلم الواجهات (القائمة الجانبية والإعدادات).</summary>
    public void SetTheme(string theme)
    {
        if (theme is not ("System" or "Light" or "Dark")) theme = "System";
        Settings.Theme = theme;
        SettingsStore.Save(Settings);
        ((App)Application.Current).ApplyTheme(theme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ضغطة واحدة: من الداكن إلى الفاتح وبالعكس، انطلاقاً مما يراه المستخدم الآن.</summary>
    public void ToggleTheme() => SetTheme(IsDark ? "Light" : "Dark");

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
            _ = RelinkLibraryAsync();

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
            RebuildLibraryLinks();
            int updates = results.Count(r => r.Status == UpdateStatus.UpdateAvailable && r.Confidence >= Confidence.Medium);
            StatusText = $"Scan complete: {results.Count} items, {updates} update(s) available.";
            ScanChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { StatusText = "Scan failed: " + ex.Message; }
        finally { EndBusy(); }
    }

    // ---------------- المكتبة المحلية ----------------

    public bool IsLibrarySyncing => _libraryCts != null;

    public void CancelLibrarySync() => _libraryCts?.Cancel();

    /// <summary>المجلدات الجذرية الفعلية للفحص: المجلدات المضافة و/أو كل الأقراص حسب الإعدادات.</summary>
    public IReadOnlyList<string> GetLibraryRoots() =>
        LibraryRoots.Compute(Settings.LibraryWholeComputer, Settings.LibraryIncludeRemovable, Settings.LibraryFolders);

    public LibraryExclusions GetLibraryExclusions() => LibraryExclusions.Parse(Settings.LibraryExclusionRules);

    public LibraryHashPolicy GetLibraryHashPolicy()
    {
        const long MB = 1024L * 1024;
        return new LibraryHashPolicy
        {
            Mode = Enum.TryParse<LibraryHashMode>(Settings.LibraryHashingMode, ignoreCase: true, out var m) ? m : LibraryHashMode.Off,
            Algorithm = string.Equals(Settings.LibraryHashingAlgorithm, "MD5", StringComparison.OrdinalIgnoreCase)
                ? LibraryHashAlgorithm.Md5 : LibraryHashAlgorithm.Sha256,
            QuickThresholdBytes = Math.Max(1, Settings.LibraryQuickAboveMB) * MB,
            QuickHeadBytes = Math.Max(1, Settings.LibraryQuickHeadMB) * MB,
        };
    }

    /// <summary>تُستدعى من الإعدادات عند تغيير نطاق البحث ليحدّث صفحة Local ملخصها.</summary>
    public void NotifyLibrarySettingsChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);

    public void AddLibraryFolder(string folder)
    {
        if (Settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        Settings.LibraryFolders.Add(folder);
        SettingsStore.Save(Settings);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveLibraryFolder(string folder)
    {
        Settings.LibraryFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(Settings);
        Library.Prune(GetLibraryRoots());
        RebuildLibraryLinks();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// يزامن المكتبة مع مجلداتها: الملفات الجديدة أو المتغيّرة فقط تُقرأ (إلا مع rescan).
    /// البصمات تُحسب حسب سياسة الإعدادات (GetLibraryHashPolicy)، وSHA256 الكاملة تُطابَق مع الكتالوج المحمَّل إن وُجد.
    /// </summary>
    public async Task SyncLibraryAsync(bool rescan)
    {
        if (_libraryCts != null) return;
        var folders = GetLibraryRoots().ToList();
        if (folders.Count == 0)
        {
            StatusText = "Add a folder on the Local page, or turn on \"Search the whole computer\" in Settings.";
            return;
        }
        var exclusions = GetLibraryExclusions();
        bool wholeComputer = Settings.LibraryWholeComputer;
        var hashing = GetLibraryHashPolicy();

        _libraryCts = new CancellationTokenSource();
        var ct = _libraryCts.Token;
        BeginBusy();
        StatusText = "Scanning the library...";
        try
        {
            var catalog = Catalog;
            var progress = new Progress<LibraryProgress>(p =>
                StatusText = p.Total > 0 ? $"{p.Phase}: {p.Done}/{p.Total}  {p.Current}" : $"{p.Phase}...");

            var result = await Task.Run(() =>
            {
                var r = Library.Sync(folders, new LibrarySyncOptions
                {
                    Rescan = rescan,
                    Hashing = hashing,
                    Cache = LibraryCache,
                    Exclusions = exclusions,
                    KeepEntriesOnMissingDrives = wholeComputer,
                }, progress, ct);
                LinkLibraryToCatalog(catalog);
                return r;
            });

            RebuildLibraryLinks();
            LibraryChanged?.Invoke(this, EventArgs.Empty);

            var sb = new StringBuilder(result.Cancelled ? "Library scan cancelled. " : "Library scan complete: ");
            sb.Append($"{result.Added} new, {result.Updated} changed, {result.Unchanged} unchanged, {result.Removed} removed");
            if (result.Hashed > 0) sb.Append($", {result.Hashed} hashed");
            if (result.FromCache > 0) sb.Append($", {result.FromCache} recognised without re-reading (moved or renamed)");
            if (result.HashReused > 0) sb.Append($", {result.HashReused} hash(es) reused");
            if (result.Errors > 0) sb.Append($", {result.Errors} with read errors");
            if (result.OfflineRoots.Count > 0) sb.Append($". Not reachable (kept as is): {string.Join(", ", result.OfflineRoots)}");
            if (result.ExcludedDirectories + result.ExcludedFiles > 0)
                sb.Append($". Skipped by exclusions: {result.ExcludedDirectories} folder(s), {result.ExcludedFiles} file(s)");
            if (result.SkippedOnlineOnly > 0) sb.Append($". {result.SkippedOnlineOnly} online-only cloud file(s) left unread");
            if (exclusions.Errors.Count > 0) sb.Append($". {exclusions.Errors.Count} exclusion rule(s) ignored (see Settings)");
            foreach (var f in Settings.LibraryFolders)
                if (exclusions.FindDirectory(f) is { } rule) sb.Append($". Folder \"{f}\" is excluded by rule \"{rule.Text}\"");
            StatusText = sb.ToString();
        }
        catch (Exception ex) { StatusText = "Library scan failed: " + ex.Message; }
        finally
        {
            _libraryCts?.Dispose();
            _libraryCts = null;
            EndBusy();
        }
    }

    private void LinkLibraryToCatalog(CatalogStore? catalog)
    {
        if (catalog == null || !Library.Entries.Any(e => e.Sha256 != null)) return;
        try
        {
            LibraryCatalogLinker.Link(Library.Entries, catalog);
            Library.Save();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // كتالوج أُغلق أثناء العمل أو فشل الحفظ: يُعاد الربط في المرة التالية
        }
    }

    private async Task RelinkLibraryAsync()
    {
        var catalog = Catalog;
        if (catalog == null || !Library.Entries.Any(e => e.Sha256 != null)) return;
        await Task.Run(() => LinkLibraryToCatalog(catalog));
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>يعيد ربط البرامج المثبّتة بالمكتبة (بعد فحص جديد أو تغيّر المكتبة).</summary>
    public void RebuildLibraryLinks()
    {
        LibraryLinks = ScanResults.Count == 0 || Library.Entries.Count == 0
            ? new LibraryLinks()
            : new LibraryMatcher(Library.Entries).Link(
                ScanResults.Select(r => (r.App, (string?)(r.InstalledVersion ?? r.App.DisplayVersion))));
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
