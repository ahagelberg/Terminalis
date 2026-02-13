using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TabbySSH.Views;

public partial class RawOutputWindow : Window
{
    private const int MaxDisplayLines = 5000;

    private readonly TerminalEmulator _terminal;
    private bool _pendingUpdate;

    public RawOutputWindow(TerminalEmulator terminal, string? titleSuffix = null)
    {
        InitializeComponent();
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        if (!string.IsNullOrEmpty(titleSuffix))
        {
            Title = $"Raw output — {titleSuffix}";
            TitleTextBlock.Text = Title;
        }

        _terminal.RawBufferUpdated += Terminal_RawBufferUpdated;
        Loaded += (_, _) => RefreshFromSnapshot();
    }

    private void Terminal_RawBufferUpdated(object? sender, EventArgs e)
    {
        if (_pendingUpdate)
            return;
        _pendingUpdate = true;
        Dispatcher.BeginInvoke(() =>
        {
            _pendingUpdate = false;
            RefreshFromSnapshot();
        });
    }

    private void RefreshFromSnapshot()
    {
        var (lines, pending) = _terminal.GetRawBufferSnapshot();
        var sb = new StringBuilder();
        var allLines = new List<string>(lines);
        if (pending.Length > 0)
            allLines.Add(pending);
        int start = Math.Max(0, allLines.Count - MaxDisplayLines);
        if (start > 0)
            sb.AppendLine($"[… earlier {start} lines omitted …]");
        for (int i = start; i < allLines.Count; i++)
            sb.AppendLine(TerminalEmulator.EscapeRawLineForDisplay(allLines[i]));

        RawTextBox.Text = sb.ToString();
        // Keep scroll at end vertically so latest data is visible
        RawTextBox.ScrollToEnd();
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source == null) return;
        var current = source;
        while (current != null)
        {
            if (current is Button)
                return;
            if (current == sender)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    e.Handled = true;
                }
                else
                {
                    DragMove();
                    e.Handled = true;
                }
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CloseButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            button.Background = new SolidColorBrush(Colors.Red);
            button.Foreground = new SolidColorBrush(Colors.White);
        }
    }

    private void CloseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            button.Background = Brushes.Transparent;
            var menuForeground = Application.Current.Resources["MenuForeground"] as SolidColorBrush;
            if (menuForeground != null)
                button.Foreground = menuForeground;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _terminal.RawBufferUpdated -= Terminal_RawBufferUpdated;
        base.OnClosed(e);
    }
}
