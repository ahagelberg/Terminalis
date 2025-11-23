using System.Data;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    public static ThemeManager ThemeManager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        AllocConsole();
        var handle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (handle != IntPtr.Zero)
        {
            if (GetConsoleMode(handle, out uint mode))
            {
                mode |= ENABLE_PROCESSED_OUTPUT;
                SetConsoleMode(handle, mode);
            }
        }
        System.Console.WriteLine("Debug console allocated. Debug output will appear here.");
        System.Console.WriteLine("================================================");
        
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            System.Console.WriteLine($"Unhandled exception: {args.ExceptionObject}");
            if (args.ExceptionObject is Exception ex)
            {
                System.Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        };
        
        DispatcherUnhandledException += (sender, args) =>
        {
            System.Console.WriteLine($"Dispatcher unhandled exception: {args.Exception}");
            System.Console.WriteLine($"Stack trace: {args.Exception.StackTrace}");
            args.Handled = false;
        };
#endif
        ThemeManager = new ThemeManager();
        
        var configManager = new Services.ConfigurationManager();
        var settings = configManager.LoadConfiguration<ApplicationSettings>(configManager.SettingsFilePath);
        if (settings != null && !string.IsNullOrEmpty(settings.Theme))
        {
            ThemeManager.LoadTheme(settings.Theme);
        }
        
        base.OnStartup(e);
    }

    private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox comboBox && !comboBox.IsDropDownOpen)
        {
            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(comboBox) as UIElement;
            if (parent != null)
            {
                var newEventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = parent
                };
                parent.RaiseEvent(newEventArgs);
            }
        }
    }
}

