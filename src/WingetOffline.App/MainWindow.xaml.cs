using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WingetOffline.UI.Services;

namespace WingetOffline.UI;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        Title = "WingetOffline " + AppInfo.Version;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme(((App)Application.Current).IsDark);
    }

    /// <summary>شريط عنوان داكن في ويندوز 10/11 (يتجاهل الخطأ إن لم يكن مدعوماً).</summary>
    public void ApplyTitleBarTheme(bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch { /* غير مدعوم */ }
    }
}
