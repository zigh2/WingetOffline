using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WingetOffline.Core;
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
        PortableFolders = new ObservableCollection<string>(s.PortableFolders);
        _state.ThemeChanged += (_, _) => { if (Theme != _state.Theme) Theme = _state.Theme; };
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
