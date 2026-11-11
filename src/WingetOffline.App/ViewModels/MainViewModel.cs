using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppState State { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _themeGlyph = "";
    [ObservableProperty] private string _themeLabel = "";
    [ObservableProperty] private string _themeTip = "";

    /// <summary>رقم الإصدار يظهر في القائمة الجانبية دون فتح Settings.</summary>
    public string VersionText { get; } = "v" + AppInfo.Version;

    public MainViewModel(AppState state)
    {
        State = state;
        NavItems = new NavItem[]
        {
            new("Updates",   "\uE895", new InstalledListViewModel(state, true)),   // الأولى: أكثر صفحة تُستعمل
            new("Discover",  "\uE721", new DiscoverViewModel(state)),
            new("Installed", "\uE71D", new InstalledListViewModel(state, false)),
            new("Installers","\uE896", new InstallersViewModel(state)),
            new("Local",     "\uE8B7", new LocalViewModel(state)),
            new("Bundles",   "\uE8F1", new PlaceholderViewModel("Bundles",
                "Create, export and import your own lists of packages (JSON bundles).\nPlanned for a later release.")),
            new("Settings",  "\uE713", new SettingsViewModel(state)),
        };
        SelectedNav = NavItems[0];

        State.ThemeChanged += (_, _) => UpdateThemeButton();
        UpdateThemeButton();
    }

    /// <summary>زر الثيم أسفل القائمة: يعرض الثيم الحالي، وضغطة واحدة تبدّله فوراً.</summary>
    [RelayCommand]
    private void ToggleTheme() => State.ToggleTheme();

    private void UpdateThemeButton()
    {
        bool dark = State.IsDark;
        ThemeGlyph = dark ? "\uE708" : "\uE706"; // قمر / شمس (Segoe MDL2 Assets)
        ThemeLabel = (dark ? "Dark" : "Light") + (State.Theme == "System" ? " (auto)" : "");
        ThemeTip = $"Click to switch to the {(dark ? "light" : "dark")} theme";
    }

    partial void OnSelectedNavChanged(NavItem? value) => CurrentPage = value?.Page;
}

public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title, string text) { Title = title; Text = text; }
    public string Title { get; }
    public string Text { get; }
}
