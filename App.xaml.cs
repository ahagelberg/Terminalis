using System.Data;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TabbySSH.Models;
using TabbySSH.Services;

namespace TabbySSH;

public partial class App : Application
{
    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(ProcessDpiAwareness value);

    private enum ProcessDpiAwareness
    {
        ProcessDpiUnaware = 0,
        ProcessSystemDpiAware = 1,
        ProcessPerMonitorDpiAware = 2
    }

    static App()
    {
        // Set per-monitor DPI awareness before any windows are created
        try
        {
            SetProcessDpiAwareness(ProcessDpiAwareness.ProcessPerMonitorDpiAware);
        }
        catch
        {
            // If shcore.dll is not available (Windows 7), fall back to manifest
        }
    }

    public static ThemeManager ThemeManager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
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

