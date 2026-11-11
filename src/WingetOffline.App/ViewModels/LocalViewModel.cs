using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WingetOffline.Core.Export;
using WingetOffline.Core.Library;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

/// <summary>صفحة Local: مكتبة المثبّتات الخاصة بك مع فلاتر وتصدير وربط بالبرامج المثبّتة.</summary>
public sealed partial class LocalViewModel : ObservableObject
{
    private readonly AppState _state;
    private List<LocalRow> _all = new();

    public static string[] TypeOptions { get; } = { "Any", "exe", "msi", "msix / appx", "zip", "7z", "rar", "iso / img", "cab / msp / msu" };
    public static string[] IdentifiedOptions { get; } = { "Any", "Identified (Medium or higher)", "File name only", "Unknown", "Matched to winget (SHA256)", "Had read errors" };
    public static string[] InstalledOptions { get; } = { "Any", "Matches an installed program", "Same version as installed", "Newer than installed", "Older than installed", "Not installed" };
    public static string[] ArchOptions { get; } = { "Any", "x64", "x86", "arm64", "Unknown" };
    public static string[] SizeOptions { get; } = { "Any", "Under 10 MB", "10 MB or more", "100 MB or more", "1 GB or more" };

    public ObservableCollection<string> Folders { get; } = new();
    public ObservableCollection<string> FolderOptions { get; } = new();

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _typeFilter = "Any";
    [ObservableProperty] private string _identifiedFilter = "Any";
    [ObservableProperty] private string _installedFilter = "Any";
    [ObservableProperty] private string _archFilter = "Any";
    [ObservableProperty] private string _sizeFilter = "Any";
    [ObservableProperty] private string _folderFilter = "All folders";
    [ObservableProperty] private IReadOnlyList<LocalRow> _rows = Array.Empty<LocalRow>();
    [ObservableProperty] private LocalRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _scopeText = "";

    // ---- لقطة مستوردة (للعرض فقط) ----
    private List<LibraryEntry>? _snapshotEntries;
    private Dictionary<string, InstalledLink>? _snapshotLinks;
    private string _snapshotName = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanNewCommand), nameof(RescanCommand))]
    private bool _isSnapshot;
    [ObservableProperty] private string _snapshotText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanNewCommand), nameof(RescanCommand), nameof(CancelCommand))]
    private bool _isSyncing;

    public bool HasFolders => Folders.Count > 0;
    public bool HasEntries => _all.Count > 0;

    public LocalViewModel(AppState state)
    {
        _state = state;
        PropertyChanged += OnSelfPropertyChanged;
        _state.LibraryChanged += (_, _) => Reload();
        _state.ScanChanged += (_, _) => Reload();
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsBusy)) IsSyncing = _state.IsLibrarySyncing;
        };
        Reload();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterText) or nameof(TypeFilter) or nameof(IdentifiedFilter) or nameof(InstalledFilter)
            or nameof(ArchFilter) or nameof(SizeFilter) or nameof(FolderFilter))
            Apply();
    }

    private void Reload()
    {
        Folders.Clear();
        foreach (var f in _state.Settings.LibraryFolders) Folders.Add(f);

        // نحدّث خيارات المجلد تزايدياً: مسح القائمة كلها يجعل ComboBox يكتب null في الفلتر
        // اللقطة المستوردة: المجلدات هي ما في الملف المستورد لا مجلدات هذا الجهاز
        var roots = _snapshotEntries != null
            ? _snapshotEntries.Select(e => e.Root).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).Take(300).ToList()
            : _state.GetLibraryRoots().ToList();
        for (int i = FolderOptions.Count - 1; i >= 1; i--)
            if (!roots.Contains(FolderOptions[i], StringComparer.OrdinalIgnoreCase)) FolderOptions.RemoveAt(i);
        if (FolderOptions.Count == 0) FolderOptions.Add("All folders");
        foreach (var f in roots) if (!FolderOptions.Contains(f)) FolderOptions.Add(f);
        ScopeText = _snapshotEntries != null
            ? "Showing an imported snapshot, not the library of this computer."
            : roots.Count == 0
                ? "Nothing to search yet."
                : (_state.Settings.LibraryWholeComputer ? "Searching the whole computer: " : "Searching only the folders below: ") + string.Join(", ", roots);
        if (FolderFilter == null || !FolderOptions.Contains(FolderFilter)) FolderFilter = "All folders";
        OnPropertyChanged(nameof(HasFolders));

        var links = _snapshotLinks ?? _state.LibraryLinks.ByPath;
        var entries = _snapshotEntries ?? (IReadOnlyList<LibraryEntry>)_state.Library.Entries;
        _all = entries.Select(e => new LocalRow(e, links.GetValueOrDefault(e.Path))).ToList();
        OnPropertyChanged(nameof(HasEntries));
        Apply();
    }

    private static bool InKind(LocalRow r, string option) => option switch
    {
        "exe" => r.Entry.Kind == LibraryKind.Exe,
        "msi" => r.Entry.Kind == LibraryKind.Msi,
        "msix / appx" => r.Entry.Kind == LibraryKind.Msix,
        "zip" => r.Entry.Kind == LibraryKind.Zip,
        "7z" => r.Entry.Kind == LibraryKind.SevenZip,
        "rar" => r.Entry.Kind == LibraryKind.Rar,
        "iso / img" => r.Entry.Kind is LibraryKind.Iso or LibraryKind.Image,
        "cab / msp / msu" => r.Entry.Kind is LibraryKind.Cab or LibraryKind.Msp or LibraryKind.Msu,
        _ => true,
    };

    private void Apply()
    {
        IEnumerable<LocalRow> q = _all;

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var t = FilterText.Trim();
            q = q.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.Publisher.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.FileName.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.Folder.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.WingetId.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        if (TypeFilter != "Any") q = q.Where(r => InKind(r, TypeFilter));

        q = IdentifiedFilter switch
        {
            "Identified (Medium or higher)" => q.Where(r => r.Entry.IsIdentified),
            "File name only" => q.Where(r => r.Entry.Confidence == LibraryConfidence.Low && r.Entry.WingetId == null),
            "Unknown" => q.Where(r => r.Entry.Confidence == LibraryConfidence.None),
            "Matched to winget (SHA256)" => q.Where(r => r.Entry.WingetId != null),
            "Had read errors" => q.Where(r => r.Entry.Error != null),
            _ => q,
        };

        q = InstalledFilter switch
        {
            "Matches an installed program" => q.Where(r => r.Installed != null),
            "Same version as installed" => q.Where(r => r.Installed?.Relation == LibraryRelation.Same),
            "Newer than installed" => q.Where(r => r.Installed?.Relation == LibraryRelation.LibraryNewer),
            "Older than installed" => q.Where(r => r.Installed?.Relation == LibraryRelation.LibraryOlder),
            "Not installed" => q.Where(r => r.Installed == null),
            _ => q,
        };

        q = ArchFilter switch
        {
            "Any" => q,
            "Unknown" => q.Where(r => string.IsNullOrEmpty(r.Architecture)),
            var a => q.Where(r => r.Architecture.Contains(a, StringComparison.OrdinalIgnoreCase)),
        };

        const long MB = 1024L * 1024;
        q = SizeFilter switch
        {
            "Under 10 MB" => q.Where(r => r.SizeBytes < 10 * MB),
            "10 MB or more" => q.Where(r => r.SizeBytes >= 10 * MB),
            "100 MB or more" => q.Where(r => r.SizeBytes >= 100 * MB),
            "1 GB or more" => q.Where(r => r.SizeBytes >= 1024 * MB),
            _ => q,
        };

        if (FolderFilter is { } folder && folder != "All folders")
            q = q.Where(r => LibraryStore.IsUnderPublic(r.Path, folder));

        var list = q.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version, StringComparer.OrdinalIgnoreCase).ToList();
        Rows = list;

        if (_all.Count == 0)
        {
            Summary = _state.GetLibraryRoots().Count == 0
                ? "Add a folder, or turn on \"Search the whole computer\" in Settings, then press Scan new files."
                : "The library is empty. Press Scan new files.";
        }
        else
        {
            long total = _all.Sum(r => r.SizeBytes);
            int identified = _all.Count(r => r.Entry.IsIdentified);
            var last = _snapshotEntries != null ? $" · imported from {_snapshotName}"
                     : _state.Library.LastSyncUtc is { } d ? $" · last scan {d.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
            Summary = $"{list.Count} shown · {_all.Count} files · {identified} identified · {LocalRow.FormatSize(total)}{last}";
        }
    }

    [RelayCommand]
    private void AddFolder()
    {
        if (IsSnapshot) return;
        var dlg = new OpenFolderDialog { Title = "Choose a folder that holds your installers (exe, msi, msix, zip, 7z, iso...)" };
        if (dlg.ShowDialog() != true) return;
        _state.AddLibraryFolder(dlg.FolderName);
        _ = _state.SyncLibraryAsync(rescan: false);
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (IsSnapshot || string.IsNullOrEmpty(folder)) return;
        var ok = MessageBox.Show($"Remove this folder from the library?\n\n{folder}\n\nThe files themselves are not touched.",
            "WingetOffline", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok == MessageBoxResult.OK) _state.RemoveLibraryFolder(folder);
    }

    private bool CanSync() => !IsSyncing && !IsSnapshot;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task ScanNewAsync()
    {
        IsSyncing = true;
        try { await _state.SyncLibraryAsync(rescan: false); }
        finally { IsSyncing = _state.IsLibrarySyncing; }
    }

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task RescanAsync()
    {
        var ok = MessageBox.Show("Re-read every file in the library? This can take a while for large collections.\nUse it after updating the program or when you suspect stale results.",
            "WingetOffline", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        IsSyncing = true;
        try { await _state.SyncLibraryAsync(rescan: true); }
        finally { IsSyncing = _state.IsLibrarySyncing; }
    }

    [RelayCommand(CanExecute = nameof(IsSyncing))]
    private void Cancel() => _state.CancelLibrarySync();

    /// <summary>
    /// يفتح ملف تصدير صفحة <b>Local</b> فقط (xlsx أو csv أو json) كلقطة للعرض: يمكنك تصفية صفوفها وتصديرها
    /// دون المساس بمكتبة هذا الجهاز. ملف صفحة أخرى يُرفض برسالة تدلّ على صفحته.
    /// </summary>
    [RelayCommand]
    private void Import()
    {
        var path = ImportDialog.Ask("Import a file exported from the Local page");
        if (path == null) return;
        try
        {
            var table = TableImporter.Read(path);
            table.RequireKind(ExportSpecs.KindLibrary);
            var (entries, links) = SnapshotMapper.ToLibrary(table);
            if (entries.Count == 0)
            {
                MessageBox.Show("The file has no rows to show.", "WingetOffline - import", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _snapshotEntries = entries;
            _snapshotLinks = links;
            _snapshotName = table.FileName;
            IsSnapshot = true;
            SnapshotText = $"Imported snapshot: {table.FileName} · {entries.Count} file(s)"
                         + (table.CreatedUtc is { } d ? $" · exported {d.ToLocalTime():yyyy-MM-dd HH:mm}" : "")
                         + ". Read-only: this is not the library of this computer and scanning is disabled.";
            Selected = null;
            Reload();
        }
        catch (ImportException ex)
        {
            MessageBox.Show(ex.Message, "WingetOffline - import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void BackToLive()
    {
        _snapshotEntries = null;
        _snapshotLinks = null;
        _snapshotName = "";
        IsSnapshot = false;
        SnapshotText = "";
        Selected = null;
        Reload();
    }

    /// <summary>يصدّر الصفوف الظاهرة (بعد الفلاتر) إلى Excel أو CSV أو JSON.</summary>
    [RelayCommand]
    private void Export()
    {
        var rows = Rows;
        if (rows.Count == 0)
        {
            MessageBox.Show("There is nothing to export. Scan your folders or relax the filters.", "WingetOffline",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pick = ExportDialog.Ask("Export the library files shown", $"library-{DateTime.Now:yyyyMMdd}",
            ExportFormat.Xlsx, ExportFormat.Csv, ExportFormat.Json);
        if (pick == null) return;
        var (path, format) = pick.Value;
        try { TableExporter.Write(path, ExportBuilders.Library(rows), format); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"Could not write:\n{path}\n\n{ex.Message}\n\nIf the file is open in Excel, close it and try again.",
                "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _state.StatusText = $"Exported {rows.Count} row(s) to {path}";
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        var p = Selected?.Path;
        if (!string.IsNullOrEmpty(p) && File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (!string.IsNullOrEmpty(Selected?.Path)) Clipboard.SetText(Selected!.Path);
    }
}
