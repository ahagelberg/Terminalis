using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
using System.Windows;

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
#endif
        base.OnStartup(e);
    }
}

