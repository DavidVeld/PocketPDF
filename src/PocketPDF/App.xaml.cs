using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PocketPDF;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // An unexpected error would otherwise close the window, and every tab's unsaved markups with it.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show($"Something went wrong, but your work is still open: save it soon.\n\n{args.Exception.Message}", "PocketPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        if (AppSettings.Load().DarkMode is { } dark) SetDarkMode(dark); // otherwise App.xaml follows Windows
        // Every window, dialogs included, gets a title bar to match as it opens: the theme doesn't colour Windows' own title bar.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            UseDarkTitleBar((Window)sender, Current.ThemeMode == ThemeMode.Dark || (Current.ThemeMode == ThemeMode.System && WindowsUsesDarkMode()))));
        if (e.Args is ["--combine", var filePath, ..])
        {
            StartCombine(filePath);
            return;
        }
        StartViewer(e.Args.Select(Path.GetFullPath).ToList());
    }

    /// <summary>Switches every window to the dark or the light theme, title bars too.</summary>
    public static void SetDarkMode(bool dark)
    {
        Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        foreach (Window window in Current.Windows) UseDarkTitleBar(window, dark);
    }

    const int DwmUseImmersiveDarkMode = 20; // DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 20H1 and later

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    static void UseDarkTitleBar(Window window, bool dark)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)); // a failure leaves it light: harmless
    }

    /// <summary>Whether Windows has apps in dark mode, for the switch's tick while the app follows Windows.</summary>
    public static bool WindowsUsesDarkMode() =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    /// <summary>
    /// Opening PDFs: one window, a tab per file. Explorer starts one process per selected file, and a file opened
    /// while PocketPDF is open starts one too; they hand their files to the window already open and exit.
    /// </summary>
    void StartViewer(List<string> filePaths)
    {
        var collector = FileCollector.StartOrHandOver("Open", filePaths);
        if (collector == null)
        {
            Shutdown(); // handed to the window already open
            return;
        }
        var window = new MainWindow(filePaths);
        window.Closed += (_, _) => collector.Dispose();
        _ = collector.ListenAsync(filePath => window.Dispatcher.BeginInvoke(() => window.OpenHandedOverFile(filePath)));
        window.Show();
    }

    /// <summary>Explorer's "Combine PDFs": collect the files from all the processes Explorer started, then show one Combine window.</summary>
    void StartCombine(string filePath)
    {
        var collector = FileCollector.StartOrHandOver("Combine", [Path.GetFullPath(filePath)]);
        if (collector == null)
        {
            Shutdown(); // handed to the collector
            return;
        }
        var window = new CombineWindow(collector, Path.GetFullPath(filePath));
        window.Closed += (_, _) => collector.Dispose();
    }
}
