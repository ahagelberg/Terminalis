using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
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
    private readonly KnownHostsManager _knownHostsManager;
    private ApplicationSettings _appSettings;
    private Adorner? _tabDropIndicator;
    private TerminalTabItem? _draggedTab;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += MainWindow_KeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        
        VersionRun.Text = VersionInfo.Version;
        
        _configManager = new ConfigurationManager();
        _sessionManager = new SessionManager(_configManager);
        _knownHostsManager = new KnownHostsManager(_configManager);
        
        _appSettings = LoadApplicationSettings();
        
        if (!string.IsNullOrEmpty(_appSettings.Theme))
        {
            App.ThemeManager.LoadTheme(_appSettings.Theme);
        }
        
        App.ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        TerminalTabs.SelectionChanged += TerminalTabs_SelectionChanged;
        
        UpdateTitleBarColor();
        
        SessionManagerPanel.SetSessionManager(_sessionManager);
        SessionManagerPanel.SessionSelected += SessionManagerPanel_SessionSelected;
        SessionManagerPanel.SessionEditRequested += SessionManagerPanel_SessionEditRequested;
        
        StateChanged += MainWindow_StateChanged;
        Activated += MainWindow_Activated;
        
        LoadWindowState();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton != null)
        {
            MaximizeButton.Content = WindowState == System.Windows.WindowState.Maximized ? "2" : "1";
        }
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem activeTab && 
            activeTab.Connection != null && !activeTab.Connection.IsConnected && 
            activeTab.SessionConfig != null &&
            (activeTab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnFocus || 
             activeTab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnDisconnect))
        {
            await activeTab.ReconnectAsync();
        }
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
            if (TerminalTabs.Items[i] is TerminalTabItem tab && tab.SessionConfig != null)
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

        var orderedSessions = activeSessions.OrderBy(s => s.TabIndex).ToList();
        var tabs = new List<(TerminalTabItem tab, SshSessionConfiguration config, ITerminalConnection connection)>();
        
        foreach (var sessionInfo in orderedSessions)
        {
            try
            {
                var session = _sessionManager.GetSession(sessionInfo.SessionId);
                if (session is SshSessionConfiguration sshConfig)
                {
                    if (!string.IsNullOrEmpty(sshConfig.Name))
                    {
                        var existingSession = _sessionManager.GetSession(sshConfig.Id);
                        if (existingSession == null)
                        {
                            _sessionManager.AddSession(sshConfig);
                        }
                    }

                    var connection = ConnectionFactory.CreateConnection(sshConfig, _knownHostsManager, ShowHostKeyVerificationDialog);
                    string? lastError = null;
                    connection.ErrorOccurred += (sender, error) =>
                    {
                        lastError = error;
                        OnErrorOccurred(sender, error);
                    };

                    var tab = new TerminalTabItem();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        TerminalTabs.Items.Add(tab);
                    });
                    tab.AttachConnection(connection, sshConfig.Color, sshConfig);
                    
                    tabs.Add((tab, sshConfig, connection));
                }
                else if (session == null)
                {
                    System.Diagnostics.Debug.WriteLine($"Session with ID {sessionInfo.SessionId} not found in session manager, skipping restore.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error restoring session {sessionInfo.SessionId}: {ex.Message}");
            }
        }
        
        if (tabs.Count > 0)
        {
            TerminalTabs.SelectedItem = tabs[0].tab;
            UpdateTitleBarColor();
            
            var connectTasks = tabs.Select(async item =>
            {
                try
                {
                    var connected = await item.connection.ConnectAsync();
                    if (connected)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            item.tab.SetConnected();
                        });
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (TerminalTabs.Items.Contains(item.tab))
                            {
                                TerminalTabs.Items.Remove(item.tab);
                            }
                            item.connection.Dispose();
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error connecting session {item.config.Id}: {ex.Message}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (TerminalTabs.Items.Contains(item.tab))
                        {
                            TerminalTabs.Items.Remove(item.tab);
                        }
                        item.connection.Dispose();
                    });
                }
            });
            
            await Task.WhenAll(connectTasks);
            
            if (tabs.Count > 0 && TerminalTabs.Items.Count > 0)
            {
                StatusTextBlock.Text = $"Restored {tabs.Count} session(s)";
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
            
            if (!string.IsNullOrEmpty(_appSettings.Theme))
            {
                App.ThemeManager.LoadTheme(_appSettings.Theme);
            }
            
            UpdateTitleBarColor();
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

    internal async void NewTabMenuItem_Click(object sender, RoutedEventArgs e)
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
                sshConfig.ResetScrollOnUserInput = newConfig.ResetScrollOnUserInput;
                sshConfig.ResetScrollOnServerOutput = newConfig.ResetScrollOnServerOutput;
                sshConfig.ScreenSessionName = newConfig.ScreenSessionName;
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
                sshConfig.BackspaceKey = newConfig.BackspaceKey;
                sshConfig.AutoReconnectMode = newConfig.AutoReconnectMode;
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

            var connection = ConnectionFactory.CreateConnection(config, _knownHostsManager, ShowHostKeyVerificationDialog);
            string? lastError = null;
            connection.ErrorOccurred += (sender, error) =>
            {
                lastError = error;
                OnErrorOccurred(sender, error);
            };

            var tab = new TerminalTabItem();
            TerminalTabs.Items.Add(tab);
            TerminalTabs.SelectedItem = tab;

            tab.AttachConnection(connection, config.Color, config);
            
            UpdateTitleBarColor();

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
                var errorMessage = string.IsNullOrEmpty(lastError) 
                    ? $"Failed to connect to {config.Name}" 
                    : $"Failed to connect to {config.Name}: {lastError}";
                ShowNotification(errorMessage, NotificationType.Error);
                connection.Dispose();
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error: {ex.Message}";
            ShowNotification($"Failed to connect to {config.Name}: {ex.Message}", NotificationType.Error);
            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal async void CloseTabMenuItem_Click(object sender, RoutedEventArgs e)
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

    private async void TerminalTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab && tab.Connection != null)
        {
            StatusTextBlock.Text = tab.Connection.IsConnected 
                ? $"Connected to {tab.ConnectionName}" 
                : $"{tab.ConnectionName} (Disconnected)";
            
            if (!tab.Connection.IsConnected && tab.SessionConfig != null && 
                (tab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnFocus || 
                 tab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnDisconnect))
            {
                await tab.ReconnectAsync();
            }
        }
        else
        {
            StatusTextBlock.Text = "Ready";
        }
        
        UpdateTitleBarColor();
    }
    
    private void UpdateTitleBarColor()
    {
        TitleBarBorder.Background = Application.Current.Resources["MenuBackground"] as SolidColorBrush;
        
        if (!_appSettings.UseAccentColorForTitleBar)
        {
            TitleBarAccentOverlay.Background = Brushes.Transparent;
            return;
        }
        
        if (TerminalTabs.SelectedItem is TerminalTabItem selectedTab && !string.IsNullOrEmpty(selectedTab.ConnectionColor))
        {
            try
            {
                var brush = new BrushConverter().ConvertFromString(selectedTab.ConnectionColor) as SolidColorBrush;
                if (brush != null)
                {
                    TitleBarAccentOverlay.Background = brush;
                    return;
                }
            }
            catch
            {
            }
        }
        
        TitleBarAccentOverlay.Background = Brushes.Transparent;
    }

    private void TerminalTabs_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TerminalTabItem)) is TerminalTabItem draggedTab)
        {
            _draggedTab = draggedTab;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            var tabControl = sender as TabControl;
            if (tabControl == null) return;

            var position = e.GetPosition(tabControl);
            var targetTab = GetTabItemAtPosition(tabControl, position);

            if (targetTab != null && targetTab != draggedTab)
            {
                ShowDropIndicator(tabControl, targetTab, e.GetPosition(tabControl));
            }
            else
            {
                HideDropIndicator();
            }
        }
    }

    private void TerminalTabs_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();

        if (e.Data.GetData(typeof(TerminalTabItem)) is TerminalTabItem draggedTab)
        {
            var tabControl = sender as TabControl;
            if (tabControl == null) return;

            var position = e.GetPosition(tabControl);
            var targetTab = GetTabItemAtPosition(tabControl, position);

            if (targetTab != null && targetTab != draggedTab)
            {
                var draggedIndex = tabControl.Items.IndexOf(draggedTab);
                var targetIndex = tabControl.Items.IndexOf(targetTab);

                if (draggedIndex >= 0 && targetIndex >= 0)
                {
                    var targetPoint = targetTab.TranslatePoint(new Point(0, 0), tabControl);
                    var targetSize = targetTab.RenderSize;
                    var isLeft = position.X < targetPoint.X + targetSize.Width / 2;

                    tabControl.Items.RemoveAt(draggedIndex);
                    var newIndex = isLeft ? targetIndex : targetIndex + 1;
                    if (draggedIndex < targetIndex)
                    {
                        newIndex = isLeft ? targetIndex - 1 : targetIndex;
                    }
                    tabControl.Items.Insert(newIndex, draggedTab);
                    tabControl.SelectedItem = draggedTab;
                }
            }
        }

        _draggedTab = null;
        e.Handled = true;
    }

    private void TerminalTabs_DragLeave(object sender, DragEventArgs e)
    {
        HideDropIndicator();
    }

    private TabItem? GetTabItemAtPosition(TabControl tabControl, Point position)
    {
        var hitTestResult = VisualTreeHelper.HitTest(tabControl, position);
        if (hitTestResult == null) return null;

        var current = hitTestResult.VisualHit;
        while (current != null && current != tabControl)
        {
            if (current is TabItem tabItem && tabControl.Items.Contains(tabItem))
            {
                return tabItem;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private class TabDropIndicatorAdorner : Adorner
    {
        private readonly double _x;
        private readonly double _y;

        public TabDropIndicatorAdorner(UIElement adornedElement, double x, double y) : base(adornedElement)
        {
            _x = x;
            _y = y;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var indicatorColor = Application.Current.Resources["DropIndicatorColor"] as SolidColorBrush;
            if (indicatorColor == null)
            {
                indicatorColor = Application.Current.Resources["BorderColorDark"] as SolidColorBrush;
            }
            if (indicatorColor == null)
            {
                indicatorColor = new SolidColorBrush(Colors.Gray);
            }

            var pen = new Pen(indicatorColor, 2);
            drawingContext.DrawLine(pen, new Point(_x, _y), new Point(_x, _y + 20));
        }
    }

    private void ShowDropIndicator(TabControl tabControl, TabItem targetTab, Point position)
    {
        HideDropIndicator();

        var tabPanel = FindVisualChild<System.Windows.Controls.Primitives.TabPanel>(tabControl);
        if (tabPanel == null) return;

        var targetBounds = targetTab.TransformToAncestor(tabControl).TransformBounds(new Rect(0, 0, targetTab.ActualWidth, targetTab.ActualHeight));
        var isLeft = position.X < targetBounds.Left + targetBounds.Width / 2;

        var indicatorX = isLeft ? targetBounds.Left - 1 : targetBounds.Right + 1;
        var indicatorY = targetBounds.Top + (targetBounds.Height - 20) / 2;

        var adornerLayer = AdornerLayer.GetAdornerLayer(tabControl);
        if (adornerLayer != null)
        {
            _tabDropIndicator = new TabDropIndicatorAdorner(tabControl, indicatorX, indicatorY);
            adornerLayer.Add(_tabDropIndicator);
        }
    }

    private void HideDropIndicator()
    {
        if (_tabDropIndicator != null)
        {
            var adornerLayer = AdornerLayer.GetAdornerLayer(TerminalTabs);
            adornerLayer?.Remove(_tabDropIndicator);
            _tabDropIndicator = null;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
            {
                return result;
            }
            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null)
            {
                return childOfChild;
            }
        }
        return null;
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
            var connection = ConnectionFactory.CreateConnection(config, _knownHostsManager, ShowHostKeyVerificationDialog);
            string? lastError = null;
            connection.ErrorOccurred += (sender, error) =>
            {
                lastError = error;
                OnErrorOccurred(sender, error);
            };

            tab.AttachConnection(connection, config.Color, config);
            
            UpdateTitleBarColor();

            var connected = await connection.ConnectAsync();

            if (connected)
            {
                tab.SetConnected();
                StatusTextBlock.Text = $"Reconnected to {config.Name}";
                ShowNotification($"Reconnected to {config.Name}", NotificationType.Success);
            }
            else
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    tab.UpdateStatusIndicator(ConnectionStatus.Error);
                }));
                StatusTextBlock.Text = "Reconnection failed";
                var errorMessage = string.IsNullOrEmpty(lastError) 
                    ? $"Failed to reconnect to {config.Name}" 
                    : $"Failed to reconnect to {config.Name}: {lastError}";
                ShowNotification(errorMessage, NotificationType.Error);
            }
        }
        catch (Exception ex)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                tab.UpdateStatusIndicator(ConnectionStatus.Error);
            }));
            StatusTextBlock.Text = $"Reconnection error: {ex.Message}";
            ShowNotification($"Reconnection error: {ex.Message}", NotificationType.Error);
        }
    }

    private void ThemeManager_ThemeChanged(object? sender, Models.Theme theme)
    {
        UpdateTitleBarColor();
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (source == null)
        {
            return;
        }

        var current = source;
        while (current != null)
        {
            if (current is Button || current is MenuItem || current is Menu)
            {
                return;
            }

            if (current == sender)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == System.Windows.WindowState.Maximized 
                        ? System.Windows.WindowState.Normal 
                        : System.Windows.WindowState.Maximized;
                    e.Handled = true;
                }
                else
                {
                    DragMove();
                    e.Handled = true;
                }
                return;
            }

            DependencyObject? parent = null;
            try
            {
                parent = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                try
                {
                    parent = LogicalTreeHelper.GetParent(current);
                }
                catch
                {
                    break;
                }
            }

            if (parent == null)
            {
                break;
            }

            current = parent;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = System.Windows.WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == System.Windows.WindowState.Maximized 
            ? System.Windows.WindowState.Normal 
            : System.Windows.WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void WindowButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            var hoverColor = Application.Current.Resources["MenuHoverBackground"] as SolidColorBrush;
            if (hoverColor != null)
            {
                button.Background = hoverColor;
            }
            else
            {
                button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3FFFFFFF")!);
            }
        }
    }

    private void WindowButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
        {
            button.Background = Brushes.Transparent;
        }
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
            {
                button.Foreground = menuForeground;
            }
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

    private HostKeyVerificationResult ShowHostKeyVerificationDialog(string host, int port, string keyAlgorithm, string fingerprint, bool isChanged)
    {
        HostKeyVerificationResult result = HostKeyVerificationResult.Cancel;
        
        Dispatcher.Invoke(() =>
        {
            var dialog = new HostKeyVerificationDialog(host, port, keyAlgorithm, fingerprint, isChanged)
            {
                Owner = this
            };
            
            if (dialog.ShowDialog() == true)
            {
                result = dialog.Result;
            }
        });
        
        return result;
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

