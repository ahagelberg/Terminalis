using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using Terminalis.Models;
using Terminalis.Services.Connections;

namespace Terminalis.Views;

public partial class TerminalTabItem : TabItem
{
    private const string CONNECTING_TEXT = "Connecting...";
    private const string DISCONNECTED_TEXT = "Disconnected";
    private const string ERROR_TEXT = "Error";

    public ITerminalConnection? Connection { get; private set; }
    public string ConnectionName { get; private set; } = "New Tab";
    public string? ConnectionColor { get; private set; }
    public SshSessionConfiguration? SessionConfig { get; private set; }
    public TerminalEmulator Terminal => TerminalControl;

    private bool _isDragging = false;
    private Point _dragStartPoint;
    private RawOutputWindow? _rawOutputWindow;

    public TerminalTabItem()
    {
        InitializeComponent();
        UpdateStatusIndicator(ConnectionStatus.Disconnected);
        ContextMenuOpening += TerminalTabItem_ContextMenuOpening;
        PreviewMouseLeftButtonDown += TerminalTabItem_PreviewMouseLeftButtonDown;
        MouseMove += TerminalTabItem_MouseMove;
        MouseLeftButtonUp += TerminalTabItem_MouseLeftButtonUp;
    }

    private void TerminalTabItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!IsSelected)
            IsSelected = true;
        ReconnectMenuItem.IsEnabled = Connection != null && !Connection.IsConnected && SessionConfig != null;
    }

    private void ShowRawOutputMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_rawOutputWindow != null)
        {
            _rawOutputWindow.Activate();
            return;
        }
        _rawOutputWindow = new RawOutputWindow(TerminalControl, ConnectionName);
        _rawOutputWindow.Closed += (_, _) => _rawOutputWindow = null;
        var owner = Window.GetWindow(this);
        if (owner != null)
            _rawOutputWindow.Owner = owner;
        _rawOutputWindow.Show();
    }

    public void AttachConnection(ITerminalConnection connection, string? color = null, SshSessionConfiguration? config = null)
    {
        Connection = connection;
        ConnectionName = connection.ConnectionName;
        ConnectionColor = color;
        SessionConfig = config;
        TabTitle.Text = ConnectionName;
        
        UpdateStatusIndicator(ConnectionStatus.Connecting);
        ApplyColor(color);
        
        connection.ConnectionClosed += OnConnectionClosed;
        connection.ErrorOccurred += OnErrorOccurred;
        
        TerminalControl.TitleChanged -= TerminalControl_TitleChanged;
        TerminalControl.AttachConnection(connection, config?.LineEnding, config?.FontFamily, config?.FontSize, config?.ForegroundColor, config?.BackgroundColor, config?.BellNotification, config?.ResetScrollOnUserInput ?? true, config?.ResetScrollOnServerOutput ?? false, config?.BackspaceKey, config?.AllowTitleChange ?? false);
        TerminalControl.TitleChanged += TerminalControl_TitleChanged;
        
        if (connection.IsConnected)
        {
            UpdateStatusIndicator(ConnectionStatus.Connected);
        }
    }

    private void TerminalControl_TitleChanged(object? sender, string title)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TabTitle.Text = title;
        }));
    }

    public void UpdateStatusIndicator(ConnectionStatus status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusIndicator.Fill = status switch
            {
                ConnectionStatus.Connected => new SolidColorBrush(Colors.Green),
                ConnectionStatus.Connecting => new SolidColorBrush(Colors.Yellow),
                ConnectionStatus.Disconnected => new SolidColorBrush(Colors.Gray),
                ConnectionStatus.Error => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }));
    }

    private void ApplyColor(string? color)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(color))
                {
                    Background = Brushes.Transparent;
                    return;
                }

                var brush = new BrushConverter().ConvertFromString(color) as SolidColorBrush;
                if (brush != null)
                {
                    Background = brush;
                }
                else
                {
                    Background = Brushes.Transparent;
                }
            }
            catch
            {
                Background = Brushes.Transparent;
            }
        }));
    }

    private void OnConnectionClosed(object? sender, ConnectionClosedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.IsNormalExit)
            {
                var parent = Parent as TabControl;
                if (parent != null)
                {
                    CleanupAsync().ContinueWith(_ =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            parent.Items.Remove(this);
                        }));
                    });
                }
            }
            else
            {
                TabTitle.Text = $"{ConnectionName} ({DISCONNECTED_TEXT})";
                TabTitle.ClearValue(TextBlock.ForegroundProperty);
                UpdateStatusIndicator(ConnectionStatus.Disconnected);
                ShowNonBlockingNotification($"{ConnectionName} disconnected", NotificationType.Info);
            }
        }));
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TabTitle.Text = $"{ConnectionName} ({ERROR_TEXT})";
            TabTitle.Foreground = new SolidColorBrush(Colors.Red);
            UpdateStatusIndicator(ConnectionStatus.Error);
            ShowNonBlockingNotification($"{ConnectionName}: {error}", NotificationType.Error);
        }));
    }

    private void ShowNonBlockingNotification(string message, NotificationType type)
    {
        var parentWindow = Window.GetWindow(this);
        if (parentWindow is MainWindow mainWindow)
        {
            mainWindow.ShowNotification(message, type);
        }
    }

    public void SetConnected()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TabTitle.Text = ConnectionName;
            TabTitle.ClearValue(TextBlock.ForegroundProperty);
            UpdateStatusIndicator(ConnectionStatus.Connected);
            ReconnectMenuItem.IsEnabled = false;
            
            if (Connection != null && Connection.IsConnected)
            {
                TerminalControl.SendTerminalSizeToServer(force: true);
            }
        }));
    }

    private void Header_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!IsSelected)
            IsSelected = true;
        ReconnectMenuItem.IsEnabled = Connection != null && !Connection.IsConnected && SessionConfig != null;
    }

    private async void ReconnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SessionConfig == null)
        {
            return;
        }

        await ReconnectAsync();
    }

    public async Task ReconnectAsync()
    {
        if (SessionConfig == null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateStatusIndicator(ConnectionStatus.Connecting);
            TabTitle.Text = $"{ConnectionName} ({CONNECTING_TEXT})";
            ReconnectMenuItem.IsEnabled = false;
        }));

        try
        {
            if (Connection != null)
            {
                try
                {
                    await Connection.DisconnectAsync();
                    Connection.Dispose();
                }
                catch
                {
                }
            }

            var parentWindow = Window.GetWindow(this);
            if (parentWindow is MainWindow mainWindow)
            {
                await mainWindow.ReconnectTab(this, SessionConfig);
            }
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateStatusIndicator(ConnectionStatus.Error);
                TabTitle.Text = $"{ConnectionName} ({ERROR_TEXT})";
                ShowNonBlockingNotification($"Reconnect failed: {ex.Message}", NotificationType.Error);
            }));
        }
    }

    private void EditSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SessionConfig == null) return;
        
        var dialog = new ConnectionDialog(SessionConfig, liveEditMode: true)
        {
            Owner = Window.GetWindow(this)
        };
        
        if (dialog.ShowDialog() == true && dialog.Configuration != null)
        {
            ApplySettings(dialog.Configuration);
        }
    }
    
    private void ApplySettings(SshSessionConfiguration config)
    {
        if (SessionConfig == null) return;
        
        SessionConfig.FontFamily = config.FontFamily;
        SessionConfig.FontSize = config.FontSize;
        SessionConfig.ForegroundColor = config.ForegroundColor;
        SessionConfig.BackgroundColor = config.BackgroundColor;
        SessionConfig.Color = config.Color;
        SessionConfig.LineEnding = config.LineEnding;
        SessionConfig.BellNotification = config.BellNotification;
        SessionConfig.TerminalResizeMethod = config.TerminalResizeMethod;
        SessionConfig.ResetScrollOnUserInput = config.ResetScrollOnUserInput;
        SessionConfig.ResetScrollOnServerOutput = config.ResetScrollOnServerOutput;
        SessionConfig.BackspaceKey = config.BackspaceKey;
        SessionConfig.AllowTitleChange = config.AllowTitleChange;
        
        TerminalControl.UpdateSettings(
            config.LineEnding,
            config.FontFamily,
            config.FontSize,
            config.ForegroundColor,
            config.BackgroundColor,
            config.BellNotification,
            config.ResetScrollOnUserInput,
            config.ResetScrollOnServerOutput,
            config.BackspaceKey,
            config.AllowTitleChange);
        
        ApplyColor(config.Color);
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var parent = Parent as TabControl;
        if (parent != null)
        {
            await CleanupAsync();
            parent.Items.Remove(this);
        }
    }

    private void CloseButton_MouseEnter(object sender, MouseEventArgs e)
    {
        CloseButton.ClearValue(Button.ForegroundProperty);
        var hoverBg = Application.Current.Resources["ButtonHoverBackground"] as SolidColorBrush;
        CloseButton.Background = hoverBg ?? new SolidColorBrush(Colors.LightGray);
    }

    private void CloseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseButton.ClearValue(Button.ForegroundProperty);
        CloseButton.Background = Brushes.Transparent;
    }

    public async Task CleanupAsync()
    {
        if (Connection != null)
        {
            try
            {
                await Connection.DisconnectAsync();
                Connection.Dispose();
            }
            catch
            {
            }
        }
    }

    private void TerminalTabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }
        
        var source = e.OriginalSource as System.Windows.DependencyObject;
        if (IsScrollBarOrChild(source))
        {
            _isDragging = false;
            return;
        }
        
        // Don't initiate tab drag when user is over terminal content - let terminal handle selection
        if (IsTerminalContent(source))
        {
            return;
        }
        
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void TerminalTabItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _isDragging = false;
            return;
        }
        
        var source = e.OriginalSource as System.Windows.DependencyObject;
        if (IsScrollBarOrChild(source))
        {
            _isDragging = false;
            return;
        }
        
        // Don't start tab drag when user is dragging over terminal content - let terminal handle selection
        if (IsTerminalContent(source))
        {
            return;
        }

        if (!_isDragging)
        {
            var currentPosition = e.GetPosition(null);
            var diff = _dragStartPoint - currentPosition;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || 
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _isDragging = true;
                DragDrop.DoDragDrop(this, this, DragDropEffects.Move);
                _isDragging = false;
            }
        }
    }
    
    private static bool IsScrollBarOrChild(System.Windows.DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ScrollBar ||
                element is System.Windows.Controls.Primitives.Thumb ||
                element is System.Windows.Controls.Primitives.RepeatButton)
            {
                return true;
            }
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsTerminalContent(System.Windows.DependencyObject? element)
    {
        while (element != null)
        {
            if (element is TerminalEmulator)
            {
                return true;
            }
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void TerminalTabItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
    }
}

