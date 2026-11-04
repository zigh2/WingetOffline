using CommunityToolkit.Mvvm.ComponentModel;
using WingetOffline.UI.Services;

namespace WingetOffline.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppState State { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private object? _currentPage;

    public MainViewModel(AppState state)
    {
        State = state;
        NavItems = new NavItem[]
        {
            new("Discover",  "\uE721", new DiscoverViewModel(state)),
            new("Installed", "\uE71D", new InstalledListViewModel(state, false)),
            new("Updates",   "\uE895", new InstalledListViewModel(state, true)),
            new("Installers","\uE896", new InstallersViewModel(state)),
            new("Bundles",   "\uE8F1", new PlaceholderViewModel("Bundles & library",
                "Create, export and import your own package library (JSON bundles).\nPlanned for a later release.")),
            new("Settings",  "\uE713", new SettingsViewModel(state)),
        };
        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value) => CurrentPage = value?.Page;
}

public sealed class PlaceholderViewModel
{
    public PlaceholderViewModel(string title, string text) { Title = title; Text = text; }
    public string Title { get; }
    public string Text { get; }
}
