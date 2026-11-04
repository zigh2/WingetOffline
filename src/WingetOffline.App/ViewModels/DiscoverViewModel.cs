using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WingetOffline.Core.Catalog;
using WingetOffline.Core.Install;
using WingetOffline.Core.Matching;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

public sealed partial class DiscoverViewModel : ObservableObject
{
    private readonly AppState _state;
    private CancellationTokenSource? _cts;
    private OfflineInstaller? _offline;
    private InstallPlan? _uninstallPlan;

    public static string[] InstallerTypeOptions { get; } = new[] { "Any" }.Concat(CatalogQueries.InstallerTypes).ToArray();
    public static string[] ArchitectureOptions { get; } = new[] { "Any" }.Concat(CatalogQueries.Architectures).ToArray();
    public static string[] ScopeOptions { get; } = new[] { "Any" }.Concat(CatalogQueries.Scopes).ToArray();
    public static string[] SortOptions { get; } = { "Relevance", "Name", "Publisher", "Most versions" };

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _license = "";
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private string _installerType = "Any";
    [ObservableProperty] private string _architecture = "Any";
    [ObservableProperty] private string _scope = "Any";
    [ObservableProperty] private string _sortBy = "Relevance";
    [ObservableProperty] private bool _silentOnly;
    [ObservableProperty] private bool _portableOnly;
    [ObservableProperty] private bool _hasProductCode;
    [ObservableProperty] private string _resultText = "Load a catalog in Settings to browse packages.";
    [ObservableProperty] private IReadOnlyList<PackageRow> _results = Array.Empty<PackageRow>();
    [ObservableProperty] private PackageRow? _selected;
    [ObservableProperty] private PackageDetails? _details;
    [ObservableProperty] private string _offlineText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _canInstall;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private bool _canUninstall;

    public DiscoverViewModel(AppState state)
    {
        _state = state;
        PropertyChanged += OnSelfPropertyChanged;
        _state.CatalogChanged += (_, _) => ScheduleSearch();
        _state.InstallersChanged += (_, _) => RefreshActions();
        _state.ScanChanged += (_, _) => RefreshActions();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchText): case nameof(Publisher): case nameof(License): case nameof(Tag):
            case nameof(InstallerType): case nameof(Architecture): case nameof(Scope): case nameof(SortBy):
            case nameof(SilentOnly): case nameof(PortableOnly): case nameof(HasProductCode):
                ScheduleSearch();
                break;
            case nameof(Selected):
                LoadDetails();
                RefreshActions();
                break;
        }
    }

    private PackageFilter BuildFilter() => new()
    {
        Text = SearchText,
        Publisher = Publisher,
        License = License,
        Tag = Tag,
        InstallerType = InstallerType == "Any" ? null : InstallerType,
        Architecture = Architecture == "Any" ? null : Architecture,
        Scope = Scope == "Any" ? null : Scope,
        SilentOnly = SilentOnly,
        PortableOnly = PortableOnly,
        HasProductCode = HasProductCode,
        Sort = SortBy switch
        {
            "Name" => PackageSort.Name,
            "Publisher" => PackageSort.Publisher,
            "Most versions" => PackageSort.Versions,
            _ => PackageSort.Relevance,
        },
    };

    private async void ScheduleSearch()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var queries = _state.Queries;
            if (queries == null)
            {
                Results = Array.Empty<PackageRow>();
                ResultText = "Load a catalog in Settings to browse packages.";
                return;
            }
            var filter = BuildFilter();
            var (rows, total) = await Task.Run(() => queries.Search(filter), cts.Token);
            if (cts.IsCancellationRequested) return;
            Results = rows;
            ResultText = total > rows.Count
                ? $"{rows.Count:N0} shown of {total:N0} packages - refine the filters to narrow down"
                : $"{total:N0} packages";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ResultText = "Search error: " + ex.Message; }
    }

    private async void LoadDetails()
    {
        var row = Selected;
        Details = null;
        var queries = _state.Queries;
        if (row == null || queries == null) return;
        try
        {
            var d = await Task.Run(() => queries.GetDetails(row.Id));
            if (ReferenceEquals(Selected, row)) Details = d;
        }
        catch { /* تفاصيل غير متاحة */ }
    }

    /// <summary>يحدّث حالة أزرار التثبيت والإزالة للحزمة المحددة.</summary>
    private void RefreshActions()
    {
        var row = Selected;
        _offline = null;
        _uninstallPlan = null;
        if (row == null)
        {
            OfflineText = "";
            CanInstall = false;
            CanUninstall = false;
            return;
        }

        _offline = _state.FindInstaller(row.Id);
        OfflineText = _offline != null
            ? $"Offline installer: {Path.GetFileName(_offline.Item.FilePath)} (v{_offline.Info.Version})"
            : "No installer for this package in your installers folder.";
        CanInstall = _offline != null;

        var installed = _state.ScanResults.FirstOrDefault(r =>
            string.Equals(r.PackageId, row.Id, StringComparison.OrdinalIgnoreCase) && r.Confidence >= Confidence.Medium);
        _uninstallPlan = installed != null ? UninstallPlanner.Plan(installed.App) : null;
        CanUninstall = _uninstallPlan != null;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var row = Selected; var offline = _offline;
        if (row == null || offline == null) return;
        var plan = _state.TryBuildInstallPlan(offline);
        if (plan != null) await _state.ConfirmAndRunAsync(plan, row.Id);
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var row = Selected; var plan = _uninstallPlan;
        if (row == null || plan == null) return;
        await _state.ConfirmAndRunAsync(plan, "uninstall-" + row.Id);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Publisher = ""; License = ""; Tag = "";
        InstallerType = "Any"; Architecture = "Any"; Scope = "Any";
        SilentOnly = false; PortableOnly = false; HasProductCode = false;
    }

    [RelayCommand]
    private void CopyId()
    {
        if (Selected != null) Clipboard.SetText(Selected.Id);
    }
}
