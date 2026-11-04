using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WingetOffline.Core.Install;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

/// <summary>صفحة مجلد المثبّتات: الملفات المتعرَّف عليها، ومعاينة أمر التثبيت، وتثبيت ملف محدد.</summary>
public sealed partial class InstallersViewModel : ObservableObject
{
    private readonly AppState _state;
    private List<InstallerFileRow> _all = new();

    public static string[] StatusOptions { get; } = { "All", "Recognised", "Unknown" };
    public static string[] TypeOptions { get; } = { "(auto)", "msi", "inno", "nullsoft", "burn", "exe", "msix", "zip", "portable" };

    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _status = "All";
    [ObservableProperty] private IReadOnlyList<InstallerFileRow> _rows = Array.Empty<InstallerFileRow>();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private InstallerFileRow? _selected;
    [ObservableProperty] private string _manualType = "(auto)";
    [ObservableProperty] private string _manualArgs = "";
    [ObservableProperty] private bool _interactive;
    [ObservableProperty] private string _planPreview = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _canInstall;

    public string FolderText => string.IsNullOrWhiteSpace(_state.Settings.InstallersFolder)
        ? "No installers folder chosen. Set one in Settings."
        : "Folder: " + _state.Settings.InstallersFolder;

    public InstallersViewModel(AppState state)
    {
        _state = state;
        PropertyChanged += OnSelfPropertyChanged;
        _state.InstallersChanged += (_, _) => Reload();
        Reload();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FilterText): case nameof(Status):
                Apply();
                break;
            case nameof(Selected): case nameof(ManualType): case nameof(ManualArgs): case nameof(Interactive):
                RefreshPreview();
                break;
        }
    }

    private void Reload()
    {
        _all = _state.Installers.Select(i => new InstallerFileRow(i)).ToList();
        OnPropertyChanged(nameof(FolderText));
        Apply();
    }

    private void Apply()
    {
        IEnumerable<InstallerFileRow> q = _all;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var t = FilterText.Trim();
            q = q.Where(r => r.FileName.Contains(t, StringComparison.OrdinalIgnoreCase)
                          || r.Package.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        q = Status switch
        {
            "Recognised" => q.Where(r => r.IsMatched),
            "Unknown" => q.Where(r => !r.IsMatched),
            _ => q,
        };
        var list = q.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        Rows = list;
        Summary = _all.Count == 0
            ? "No installer files indexed yet. Choose a folder in Settings, then press Index folder."
            : $"{list.Count} shown · {_all.Count} total · {_all.Count(r => r.IsMatched)} recognised";
    }

    /// <summary>يبني خطة التثبيت للملف المحدد (أو يعيد سبب العجز).</summary>
    private InstallPlan? BuildPlan(out string? error)
    {
        error = null;
        var row = Selected;
        if (row == null) { error = "Select an installer file."; return null; }

        CatalogInstaller? inst = row.Item.Best;
        if (ManualType != "(auto)")
        {
            inst = new CatalogInstaller("(manual)", "", "", ManualType, null, "", "", row.Sha256,
                string.IsNullOrWhiteSpace(ManualArgs) ? null : ManualArgs.Trim(), null, null, null, null, null);
        }
        if (inst == null)
        {
            error = "Unknown installer: its SHA256 is not in the catalog. Choose a type under 'Manual override' to install it anyway.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(PlanRunner.LogDir);
            var opt = new InstallOptions
            {
                Interactive = Interactive,
                MsiLogFile = Path.Combine(PlanRunner.LogDir, $"msi-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
            };
            return InstallPlanner.Plan(row.FilePath, inst, opt);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void RefreshPreview()
    {
        var plan = BuildPlan(out var error);
        if (plan == null)
        {
            PlanPreview = error ?? "";
            CanInstall = false;
            return;
        }
        PlanPreview = plan.CommandLine + (plan.Warning != null ? "\n\nWarning: " + plan.Warning : "");
        CanInstall = true;
    }

    [RelayCommand]
    private Task IndexAsync() => _state.IndexInstallersAsync();

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var plan = BuildPlan(out var error);
        if (plan == null)
        {
            System.Windows.MessageBox.Show(error ?? "Cannot build an install command.", "WingetOffline");
            return;
        }
        var label = !string.IsNullOrEmpty(Selected?.Package) ? Selected!.Package : (Selected?.FileName ?? "installer");
        await _state.ConfirmAndRunAsync(plan, label);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Path.GetDirectoryName(Selected?.FilePath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
