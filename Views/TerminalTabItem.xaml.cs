using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TabbySSH.Models;
using TabbySSH.Services.Connections;

namespace TabbySSH.Views;

public partial class TerminalTabItem : TabItem
{
    private const string CONNECTING_TEXT = "Connecting...";
    private const string DISCONNECTED_TEXT = "Disconnected";
    private const string ERROR_TEXT = "Error";

    public ITerminalConnection? Connection { get; private set; }
    public string ConnectionName { get; private set; } = "New Tab";
    public string? ConnectionColor { get; private set; }
    public SshSessionConfiguration? SessionConfig { get; private set; }

    public TerminalTabItem()
    {
        InitializeComponent();
        UpdateStatusIndicator(ConnectionStatus.Disconnected);
        ContextMenuOpening += TerminalTabItem_ContextMenuOpening;
    }

    private void TerminalTabItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!IsSelected)
        {
            IsSelected = true;
        }
        ReconnectMenuItem.IsEnabled = Connection != null && !Connection.IsConnected && SessionConfig != null;
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
        
        TerminalControl.AttachConnection(connection, config?.LineEnding, config?.FontFamily, config?.FontSize, config?.ForegroundColor, config?.BackgroundColor, config?.BellNotification);
        
        if (connection.IsConnected)
        {
            UpdateStatusIndicator(ConnectionStatus.Connected);
        }
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
                    var accentBrush = new SolidColorBrush(brush.Color)
                    {
                        Opacity = 0.25
                    };
                    Background = accentBrush;
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
                TabTitle.Foreground = new SolidColorBrush(Colors.Gray);
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
            TabTitle.Foreground = new SolidColorBrush(Colors.Black);
            UpdateStatusIndicator(ConnectionStatus.Connected);
            ReconnectMenuItem.IsEnabled = false;
            
            if (Connection != null && Connection.IsConnected)
            {
                TerminalControl.SendTerminalSizeToServer();
            }
        }));
    }

    private void Header_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!IsSelected)
        {
            IsSelected = true;
        }
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

        Dispatcher.BeginInvoke(new Action(() =>
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
            Dispatcher.BeginInvoke(new Action(() =>
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
        
        TerminalControl.UpdateSettings(
            config.LineEnding,
            config.FontFamily,
            config.FontSize,
            config.ForegroundColor,
            config.BackgroundColor,
            config.BellNotification);
        
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
        CloseButton.Foreground = new SolidColorBrush(Colors.Black);
        CloseButton.Background = new SolidColorBrush(Colors.LightGray);
    }

    private void CloseButton_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseButton.Foreground = new SolidColorBrush(Colors.Gray);
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
}

