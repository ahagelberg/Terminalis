using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TabbySSH.Models;
using TabbySSH.Services;
using TabbySSH.Services.Connections;
using TabbySSH.Views;

namespace TabbySSH;

public partial class MainWindow : Window
{
    private const string WINDOW_STATE_FILE = "windowstate.json";

    private readonly ConfigurationManager _configManager;
    private readonly SessionManager _sessionManager;
    private ApplicationSettings _appSettings;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += MainWindow_KeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        
        _configManager = new ConfigurationManager();
        _sessionManager = new SessionManager(_configManager);
        
        _appSettings = LoadApplicationSettings();
        
        SessionManagerPanel.SetSessionManager(_sessionManager);
        SessionManagerPanel.SessionSelected += SessionManagerPanel_SessionSelected;
        SessionManagerPanel.SessionEditRequested += SessionManagerPanel_SessionEditRequested;
        
        LoadWindowState();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadWindowState();
        
        if (_appSettings.RestoreActiveSessionsOnStartup)
        {
            RestoreActiveSessions();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
        
        if (_appSettings.RestoreActiveSessionsOnStartup)
        {
            SaveActiveSessions();
        }
    }

    private void LoadWindowState()
    {
        var statePath = Path.Combine(_configManager.AppDataPath, WINDOW_STATE_FILE);
        var state = _configManager.LoadWindowState<Models.WindowState>(statePath);
        if (state != null)
        {
            Left = state.Left;
            Top = state.Top;
            Width = state.Width;
            Height = state.Height;
            WindowState = state.State;
        }
    }

    private void SaveWindowState()
    {
        var state = new Models.WindowState
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            State = WindowState
        };
        var statePath = Path.Combine(_configManager.AppDataPath, WINDOW_STATE_FILE);
        _configManager.SaveWindowState(state, statePath);
    }

    private ApplicationSettings LoadApplicationSettings()
    {
        var settings = _configManager.LoadConfiguration<ApplicationSettings>(_configManager.SettingsFilePath);
        return settings ?? new ApplicationSettings();
    }

    private void SaveApplicationSettings()
    {
        _configManager.SaveConfiguration(_appSettings, _configManager.SettingsFilePath);
    }

    private void SaveActiveSessions()
    {
        var activeSessions = new List<ActiveSessionInfo>();
        
        for (int i = 0; i < TerminalTabs.Items.Count; i++)
        {
            if (TerminalTabs.Items[i] is TerminalTabItem tab && tab.SessionConfig != null && tab.Connection?.IsConnected == true)
            {
                activeSessions.Add(new ActiveSessionInfo
                {
                    SessionId = tab.SessionConfig.Id,
                    TabIndex = i
                });
            }
        }
        
        _configManager.SaveConfiguration(activeSessions, _configManager.ActiveSessionsFilePath);
    }

    private async void RestoreActiveSessions()
    {
        var activeSessions = _configManager.LoadConfiguration<List<ActiveSessionInfo>>(_configManager.ActiveSessionsFilePath);
        if (activeSessions == null || activeSessions.Count == 0)
        {
            return;
        }

        foreach (var sessionInfo in activeSessions.OrderBy(s => s.TabIndex))
        {
            var session = _sessionManager.GetSession(sessionInfo.SessionId);
            if (session is SshSessionConfiguration sshConfig)
            {
                await CreateNewTab(sshConfig);
            }
        }
    }

    private void OptionsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsDialog(_appSettings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Settings != null)
        {
            _appSettings = dialog.Settings;
            SaveApplicationSettings();
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NewTabMenuItem_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CloseTabMenuItem_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                PreviousTab();
            }
            else
            {
                NextTab();
            }
            e.Handled = true;
        }
    }

    private async void NewTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Configuration != null)
        {
            await CreateNewTab(dialog.Configuration);
        }
    }

    private async void SessionManagerPanel_SessionSelected(object? sender, SessionConfiguration session)
    {
        if (session is SshSessionConfiguration sshConfig)
        {
            await CreateNewTab(sshConfig);
        }
    }

    private void SessionManagerPanel_SessionEditRequested(object? sender, SessionConfiguration session)
    {
        if (session is SshSessionConfiguration sshConfig)
        {
            var dialog = new ConnectionDialog(sshConfig)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.Configuration != null)
            {
                var newConfig = dialog.Configuration;
                sshConfig.Host = newConfig.Host;
                sshConfig.Port = newConfig.Port;
                sshConfig.Username = newConfig.Username;
                sshConfig.Password = newConfig.Password;
                sshConfig.Name = newConfig.Name;
                sshConfig.UsePasswordAuthentication = newConfig.UsePasswordAuthentication;
                sshConfig.PrivateKeyPath = newConfig.PrivateKeyPath;
                sshConfig.PrivateKeyPassphrase = newConfig.PrivateKeyPassphrase;
                sshConfig.KeepAliveInterval = newConfig.KeepAliveInterval;
                sshConfig.ConnectionTimeout = newConfig.ConnectionTimeout;
                sshConfig.CompressionEnabled = newConfig.CompressionEnabled;
                sshConfig.X11ForwardingEnabled = newConfig.X11ForwardingEnabled;
                sshConfig.BellNotification = newConfig.BellNotification;
                sshConfig.Color = newConfig.Color;
                sshConfig.LineEnding = newConfig.LineEnding;
                sshConfig.Encoding = newConfig.Encoding;
                sshConfig.PortForwardingRules = newConfig.PortForwardingRules;
                sshConfig.FontFamily = newConfig.FontFamily;
                sshConfig.FontSize = newConfig.FontSize;
                sshConfig.ForegroundColor = newConfig.ForegroundColor;
                sshConfig.BackgroundColor = newConfig.BackgroundColor;
                _sessionManager.UpdateSession(sshConfig);
                SessionManagerPanel.RefreshAfterEdit();
            }
        }
    }

    private async Task CreateNewTab(SshSessionConfiguration config)
    {
        StatusTextBlock.Text = "Connecting...";

        try
        {
            var existingSession = _sessionManager.GetSession(config.Id);
            if (existingSession == null && !string.IsNullOrEmpty(config.Name))
            {
                _sessionManager.AddSession(config);
            }

            var connection = ConnectionFactory.CreateConnection(config);
            connection.ErrorOccurred += OnErrorOccurred;

            var tab = new TerminalTabItem();
            TerminalTabs.Items.Add(tab);
            TerminalTabs.SelectedItem = tab;

            tab.AttachConnection(connection, config.Color, config);

            var connected = await connection.ConnectAsync();

            if (connected)
            {
                tab.SetConnected();
                StatusTextBlock.Text = $"Connected to {config.Name}";
            }
            else
            {
                TerminalTabs.Items.Remove(tab);
                StatusTextBlock.Text = "Connection failed";
                ShowNotification($"Failed to connect to {config.Name}", NotificationType.Error);
                connection.Dispose();
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CloseTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab)
        {
            await tab.CleanupAsync();
            TerminalTabs.Items.Remove(tab);
            if (TerminalTabs.Items.Count == 0)
            {
                StatusTextBlock.Text = "Ready";
            }
        }
    }

    private void NextTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        NextTab();
    }

    private void PreviousTabMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PreviousTab();
    }

    public void NextTab()
    {
        if (TerminalTabs.Items.Count == 0)
        {
            return;
        }

        var currentIndex = TerminalTabs.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = (currentIndex + 1) % TerminalTabs.Items.Count;
        TerminalTabs.SelectedIndex = nextIndex;
    }

    public void PreviousTab()
    {
        if (TerminalTabs.Items.Count == 0)
        {
            return;
        }

        var currentIndex = TerminalTabs.SelectedIndex;
        if (currentIndex < 0)
        {
            currentIndex = TerminalTabs.Items.Count - 1;
        }

        var previousIndex = (currentIndex - 1 + TerminalTabs.Items.Count) % TerminalTabs.Items.Count;
        TerminalTabs.SelectedIndex = previousIndex;
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TerminalTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab && tab.Connection != null)
        {
            StatusTextBlock.Text = tab.Connection.IsConnected 
                ? $"Connected to {tab.ConnectionName}" 
                : $"{tab.ConnectionName} (Disconnected)";
        }
        else
        {
            StatusTextBlock.Text = "Ready";
        }
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusTextBlock.Text = $"Error: {error}";
        }));
    }

    public async Task ReconnectTab(TerminalTabItem tab, SshSessionConfiguration config)
    {
        try
        {
            var connection = ConnectionFactory.CreateConnection(config);
            connection.ErrorOccurred += OnErrorOccurred;

            tab.AttachConnection(connection, config.Color, config);

            var connected = await connection.ConnectAsync();

            if (connected)
            {
                tab.SetConnected();
                StatusTextBlock.Text = $"Reconnected to {config.Name}";
                ShowNotification($"Reconnected to {config.Name}", NotificationType.Success);
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    tab.UpdateStatusIndicator(ConnectionStatus.Error);
                }));
                StatusTextBlock.Text = "Reconnection failed";
                ShowNotification($"Failed to reconnect to {config.Name}", NotificationType.Error);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tab.UpdateStatusIndicator(ConnectionStatus.Error);
            }));
            StatusTextBlock.Text = $"Reconnection error: {ex.Message}";
            ShowNotification($"Reconnection error: {ex.Message}", NotificationType.Error);
        }
    }

    public void ShowNotification(string message, NotificationType type)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NotificationText.Text = message;
            NotificationIcon.Fill = type switch
            {
                NotificationType.Info => new SolidColorBrush(Colors.Blue),
                NotificationType.Warning => new SolidColorBrush(Colors.Orange),
                NotificationType.Error => new SolidColorBrush(Colors.Red),
                NotificationType.Success => new SolidColorBrush(Colors.Green),
                _ => new SolidColorBrush(Colors.Gray)
            };
            NotificationBorder.Visibility = Visibility.Visible;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                NotificationBorder.Visibility = Visibility.Collapsed;
            };
            timer.Start();
        }));
    }

    protected override async void OnClosed(EventArgs e)
    {
        foreach (TerminalTabItem tab in TerminalTabs.Items)
        {
            if (tab.Connection != null)
            {
                try
                {
                    await tab.Connection.DisconnectAsync();
                    tab.Connection.Dispose();
                }
                catch
                {
                }
            }
        }
        base.OnClosed(e);
    }
}

