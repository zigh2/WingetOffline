using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WingetOffline.Fleet;

namespace WingetOffline.UI.ViewModels;

/// <summary>صف جهاز في القائمة اليسرى.</summary>
public sealed partial class DeviceItem : ObservableObject
{
    public DeviceItem(Device d) { Device = d; }
    public Device Device { get; }
    public string Name => Device.Name;
    public string Address => $"{Device.Host}:{Device.Port}";

    [ObservableProperty] private string _status = "never scanned";
    [ObservableProperty] private int _updates;
}

public sealed record AppRow(string Name, string? Version, string? LatestVersion, string Status, string? PackageId)
{
    public string StatusText => Status switch { "UpdateAvailable" => "Update available", "UpToDate" => "Up to date", _ => "Not matched" };
}

/// <summary>
/// صفحة Devices (الإصدار 0.2، قراءة فقط): أجهزة الشبكة المحلية المقترنة، آخر جرد لكل جهاز، وتقرير الأسطول.
/// تشترك مع wgo-admin في المجلد نفسه، فالأجهزة والجرد ظاهران في الاثنين.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly FleetStore _store = new();
    private CancellationTokenSource? _cts;
    private List<AppRow> _allApps = new();

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    [ObservableProperty] private DeviceItem? _selected;
    [ObservableProperty] private IReadOnlyList<AppRow> _apps = Array.Empty<AppRow>();
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _updatesOnly;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _appsSummary = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newAddress = "";
    [ObservableProperty] private string _newFingerprint = "";
    [ObservableProperty] private string _formError = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshAllCommand), nameof(RefreshSelectedCommand), nameof(PingSelectedCommand), nameof(CancelCommand))]
    private bool _isBusy;

    public string MyFingerprint => _store.MyFingerprint;

    public DevicesViewModel() => Reload();

    private void Reload()
    {
        var keep = Selected?.Name;
        Devices.Clear();
        foreach (var d in _store.Devices())
        {
            var item = new DeviceItem(d);
            ApplyInventory(item, _store.LoadInventory(d.Name));
            Devices.Add(item);
        }
        Selected = Devices.FirstOrDefault(x => x.Name == keep) ?? Devices.FirstOrDefault();
        UpdateSummary();
    }

    private static void ApplyInventory(DeviceItem item, InventorySnapshot? inv)
    {
        if (inv == null) { item.Status = "never scanned"; item.Updates = 0; return; }
        item.Updates = inv.Apps.Count(a => a.Status == "UpdateAvailable");
        item.Status = $"{inv.Apps.Count} programs · scanned {inv.ScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private void UpdateSummary()
    {
        int upd = Devices.Sum(d => d.Updates);
        Summary = Devices.Count == 0
            ? "No devices yet. On each computer run `wgo-agent id`, then add it below."
            : $"{Devices.Count} devices · {upd} updates available (from the last scan of each device; nothing is fetched from the internet)";
    }

    partial void OnSelectedChanged(DeviceItem? value)
    {
        var inv = value == null ? null : _store.LoadInventory(value.Name);
        _allApps = inv == null ? new() : inv.Apps
            .OrderByDescending(a => a.Status == "UpdateAvailable").ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AppRow(a.Name, a.Version, a.LatestVersion, a.Status, a.PackageId)).ToList();
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnUpdatesOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<AppRow> q = _allApps;
        if (UpdatesOnly) q = q.Where(a => a.Status == "UpdateAvailable");
        var t = FilterText.Trim();
        if (t.Length > 0) q = q.Where(a => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (a.PackageId ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
        Apps = q.ToList();
        AppsSummary = Selected == null ? "" : $"{Apps.Count} of {_allApps.Count} programs on {Selected.Name}";
    }

    // ───── إضافة وحذف ─────
    [RelayCommand]
    private void Add()
    {
        FormError = "";
        try
        {
            _store.Add(NewName, NewAddress, NewFingerprint);
            NewName = NewAddress = NewFingerprint = "";
            Reload();
        }
        catch (FormatException ex) { FormError = ex.Message; }
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected == null) return;
        var ok = MessageBox.Show($"Forget '{Selected.Name}'? Its saved inventory stays on disk; the agent itself is not changed.",
            "WingetOffline", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _store.Remove(Selected.Name);
        Reload();
    }

    [RelayCommand]
    private void CopyMyFingerprint() { try { Clipboard.SetText(MyFingerprint); } catch { /* الحافظة مشغولة */ } }

    // ───── الاتصال (قراءة فقط) ─────
    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshAll() => RunAsync(Devices.ToList(), RequestTypes.Inventory);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RefreshSelected() => Selected == null ? Task.CompletedTask : RunAsync(new() { Selected }, RequestTypes.Inventory);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task PingSelected() => Selected == null ? Task.CompletedTask : RunAsync(new() { Selected }, RequestTypes.Ping);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(List<DeviceItem> targets, string type)
    {
        if (targets.Count == 0) return;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        int ok = 0, failed = 0;
        try
        {
            foreach (var item in targets)
            {
                Summary = $"Contacting {item.Name}… ({ok + failed + 1}/{targets.Count})";
                var r = await _store.RefreshAsync(item.Device, type, _cts.Token);
                if (r.Ok) ok++; else failed++;
                if (type == RequestTypes.Inventory && r.Inventory != null) ApplyInventory(item, r.Inventory);
                else if (!r.Ok) item.Status = "failed: " + r.Message;
                else item.Status = "online";
            }
            if (Selected != null && targets.Contains(Selected)) OnSelectedChanged(Selected);
            UpdateSummary();
            if (failed > 0) Summary += $" · {failed} could not be reached (see each device)";
        }
        catch (OperationCanceledException) { Summary = "Cancelled."; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    // ───── تقرير الأسطول ─────
    [RelayCommand]
    private void ExportReport()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save the fleet report",
            FileName = $"fleet-report-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = "Web page (*.html)|*.html|Excel-friendly CSV (updates only)|*.csv|JSON|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        var rep = _store.BuildReport();
        var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
        try
        {
            var text = ext == ".csv" ? rep.ToCsv() : ext == ".json" ? rep.ToJson() : rep.ToHtml();
            File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(false));
            _store.Audit.Append("local", "report " + (ext.Length > 0 ? ext[1..] : "html"), "ok");
            Summary = $"Report saved: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not write the report:\n{dlg.FileName}\n\n{ex.Message}", "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
