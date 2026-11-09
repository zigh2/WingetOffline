using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WingetOffline.Core;
using WingetOffline.Core.Library;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppState _state;

    public static string[] ThemeOptions { get; } = { "System", "Light", "Dark" };
    public ObservableCollection<string> PortableFolders { get; }
    public string VersionText { get; }
    public string DataFolderText { get; }

    [ObservableProperty] private string _catalogPath;
    [ObservableProperty] private string _installersFolder;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _scanWingetPortables;
    [ObservableProperty] private bool _autoScan;
    [ObservableProperty] private bool _useUac;
    [ObservableProperty] private string _message = "";

    // ---- المكتبة المحلية ----
    [ObservableProperty] private bool _libraryWholeComputer;
    [ObservableProperty] private bool _libraryIncludeRemovable;
    [ObservableProperty] private bool _libraryComputeHashes;
    [ObservableProperty] private string _libraryExclusionRules;
    [ObservableProperty] private string _exclusionsStatus = "";
    [ObservableProperty] private string _exclusionTestPath = "";
    [ObservableProperty] private string _exclusionTestResult = "";
    [ObservableProperty] private string _libraryScopeText = "";

    public SettingsViewModel(AppState state)
    {
        _state = state;
        var s = state.Settings;
        _catalogPath = s.CatalogPath ?? "";
        _installersFolder = s.InstallersFolder ?? "";
        _theme = s.Theme;
        _scanWingetPortables = s.ScanWingetPortables;
        _autoScan = s.AutoScan;
        _useUac = s.UseUac;
        _libraryWholeComputer = s.LibraryWholeComputer;
        _libraryIncludeRemovable = s.LibraryIncludeRemovable;
        _libraryComputeHashes = s.LibraryComputeHashes;
        _libraryExclusionRules = s.LibraryExclusionRules ?? LibraryExclusions.DefaultText;
        PortableFolders = new ObservableCollection<string>(s.PortableFolders);
        _state.ThemeChanged += (_, _) => { if (Theme != _state.Theme) Theme = _state.Theme; };
        _state.LibraryChanged += (_, _) => UpdateScopeText();
        UpdateExclusionsStatus();
        UpdateScopeText();
        VersionText = "WingetOffline " + AppInfo.Version;
        DataFolderText = "Data folder: " + AppPaths.DataDir + (AppPaths.IsPortable ? "  (portable mode)" : "");
    }

    private void Persist()
    {
        var s = _state.Settings;
        s.ScanWingetPortables = ScanWingetPortables;
        s.AutoScan = AutoScan;
        s.UseUac = UseUac;
        s.InstallersFolder = string.IsNullOrWhiteSpace(InstallersFolder) ? null : InstallersFolder.Trim();
        s.PortableFolders = PortableFolders.ToList();
        s.LibraryWholeComputer = LibraryWholeComputer;
        s.LibraryIncludeRemovable = LibraryIncludeRemovable;
        s.LibraryComputeHashes = LibraryComputeHashes;
        s.LibraryExclusionRules = LibraryExclusionRules ?? "";
        SettingsStore.Save(s);
    }

    partial void OnThemeChanged(string value)
    {
        // التطبيق والحفظ في AppState كي يبقى زر القائمة الجانبية والإعدادات متزامنين.
        if (value != _state.Theme) _state.SetTheme(value);
    }
    partial void OnScanWingetPortablesChanged(bool value) => Persist();
    partial void OnAutoScanChanged(bool value) => Persist();
    partial void OnUseUacChanged(bool value) => Persist();
    partial void OnInstallersFolderChanged(string value) => Persist();

    partial void OnLibraryWholeComputerChanged(bool value) { Persist(); _state.NotifyLibrarySettingsChanged(); }
    partial void OnLibraryIncludeRemovableChanged(bool value) { Persist(); _state.NotifyLibrarySettingsChanged(); }
    partial void OnLibraryComputeHashesChanged(bool value) => Persist();

    partial void OnLibraryExclusionRulesChanged(string value)
    {
        Persist();
        UpdateExclusionsStatus();
        UpdateExclusionTest();
    }

    partial void OnExclusionTestPathChanged(string value) => UpdateExclusionTest();

    private void UpdateExclusionsStatus()
    {
        var ex = LibraryExclusions.Parse(LibraryExclusionRules);
        ExclusionsStatus = ex.Errors.Count == 0
            ? $"{ex.Rules.Count} rule(s) active."
            : $"{ex.Rules.Count} rule(s) active. Ignored: " + string.Join("; ", ex.Errors);
    }

    /// <summary>يجرّب مساراً على القواعد الحالية ويقول أي سطر يستثنيه، فيمكن اختبار التعابير النمطية قبل الفحص.</summary>
    private void UpdateExclusionTest()
    {
        if (string.IsNullOrWhiteSpace(ExclusionTestPath)) { ExclusionTestResult = ""; return; }
        var ex = LibraryExclusions.Parse(LibraryExclusionRules);
        var path = ExclusionTestPath.Trim().Trim('"');
        var rule = ex.Find(path, isDirectory: false) ?? ex.Find(path, isDirectory: true);
        ExclusionTestResult = rule == null ? "Not excluded." : $"Excluded by line {rule.Line}: {rule.Text}";
    }

    private void UpdateScopeText()
    {
        var roots = _state.GetLibraryRoots();
        LibraryScopeText = roots.Count == 0
            ? "Nothing to search yet: add a folder on the Local page or turn on the whole-computer search."
            : (_state.Settings.LibraryWholeComputer ? "The scan covers: " : "The scan covers only: ") + string.Join(", ", roots);
    }

    [RelayCommand]
    private void RestoreDefaultExclusions() => LibraryExclusionRules = LibraryExclusions.DefaultText;

    [RelayCommand]
    private void ScanLibraryNow()
    {
        Message = "Library scan started. Progress is shown at the bottom of the window.";
        _ = _state.SyncLibraryAsync(rescan: false, LibraryComputeHashes);
    }

    [RelayCommand]
    private void BrowseCatalog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Catalog (*.wglib;*.db)|*.wglib;*.db|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) CatalogPath = dlg.FileName;
    }

    [RelayCommand]
    private async Task LoadCatalogAsync()
    {
        if (string.IsNullOrWhiteSpace(CatalogPath)) return;
        Message = "Loading...";
        var error = await _state.LoadCatalogAsync(CatalogPath.Trim());
        Message = error == null ? "Catalog loaded." : "Error: " + error;
    }

    [RelayCommand]
    private void BrowseInstallers()
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder that contains installer files" };
        if (dlg.ShowDialog() == true) InstallersFolder = dlg.FolderName;
    }

    [RelayCommand]
    private Task IndexInstallersAsync() => _state.IndexInstallersAsync();

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder that contains portable programs" };
        if (dlg.ShowDialog() == true && !PortableFolders.Contains(dlg.FolderName))
        {
            PortableFolders.Add(dlg.FolderName);
            Persist();
        }
    }

    [RelayCommand]
    private void RemoveFolder(string? folder)
    {
        if (folder != null && PortableFolders.Remove(folder)) Persist();
    }

    [RelayCommand]
    private Task RescanAsync() => _state.ScanAsync();
}
