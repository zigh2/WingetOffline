using System.Windows;
using Microsoft.Win32;
using WingetOffline.UI.Services;
using WingetOffline.UI.ViewModels;

namespace WingetOffline.UI;

public partial class App : Application
{
    public AppState State { get; private set; } = null!;
    public bool IsDark { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "WingetOffline", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var settings = SettingsStore.Load();
        State = new AppState(settings);
        ApplyTheme(settings.Theme);

        var window = new MainWindow { DataContext = new MainViewModel(State) };
        window.Show();

        if (!string.IsNullOrEmpty(settings.CatalogPath) && File.Exists(settings.CatalogPath))
            _ = State.LoadCatalogAsync(settings.CatalogPath);
    }

    public void ApplyTheme(string theme)
    {
        bool dark = theme == "Dark" || (theme == "System" && SystemUsesDarkTheme());
        IsDark = dark;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };
        foreach (Window w in Windows)
            if (w is MainWindow mw) mw.ApplyTitleBarTheme(dark);
    }

    private static bool SystemUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                          "AppsUseLightTheme", 1) is int light && light == 0;
}
