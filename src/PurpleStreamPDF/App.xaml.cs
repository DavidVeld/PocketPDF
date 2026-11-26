using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PurpleStreamPDF;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // An unexpected error would otherwise close the window, and every tab's unsaved markups with it.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show($"Something went wrong, but your work is still open: save it soon.\n\n{args.Exception.Message}", "PurpleStreamPDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
        if (AppSettings.Load().DarkMode is { } dark) SetDarkMode(dark); // otherwise App.xaml follows Windows
        TintBackgrounds();
        // Following Windows, the theme reloads when Windows switches between light and dark: tint the new one once it's in.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(TintBackgrounds, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // Every window, dialogs included, gets a title bar to match as it opens: the theme doesn't colour Windows' own title bar.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var window = (Window)sender;
            window.Background = PurpleWash; // the theme leaves windows clear for Windows' backdrop: a faint purple over it
            UseDarkTitleBar(window, Current.ThemeMode == ThemeMode.Dark || (Current.ThemeMode == ThemeMode.System && WindowsUsesDarkMode()));
        }));
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
        TintBackgrounds();
        foreach (Window window in Current.Windows) UseDarkTitleBar(window, dark);
    }

    /// <summary>Purple at 3%: over a light or a dark backdrop alike, a cast you sense more than see.</summary>
    static readonly Brush PurpleWash = new SolidColorBrush(Color.FromArgb(0x08, 0x7E, 0x57, 0xC2));

    /// <summary>
    /// Gives the theme's grey backgrounds a faint purple cast, light and dark alike: a few steps less green and more blue,
    /// at the same lightness. Greys already tinted aren't grey any more, so tinting twice changes nothing.
    /// </summary>
    static void TintBackgrounds()
    {
        static byte Shift(byte value, int by) => (byte)Math.Clamp(value + by, 0, 255);
        static void Tint(ResourceDictionary dictionary)
        {
            foreach (var key in dictionary.Keys.OfType<string>().Where(key => key.Contains("Background")).ToList())
                if (dictionary[key] is SolidColorBrush { Color: { A: 255 } grey } && grey.R == grey.G && grey.G == grey.B)
                {
                    var purple = new SolidColorBrush(Color.FromRgb(Shift(grey.R, -1), Shift(grey.G, -3), Shift(grey.B, 4)));
                    purple.Freeze();
                    dictionary[key] = purple;
                }
            foreach (var merged in dictionary.MergedDictionaries) Tint(merged);
        }
        Tint(Current.Resources);
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
    /// while PurpleStreamPDF is open starts one too; they hand their files to the window already open and exit.
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
