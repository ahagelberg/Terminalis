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
using TabbySSH.Utils;
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
        
#if DEBUG
        DebugModeMenuItem.Visibility = Visibility.Visible;
        UpdateDebugMenuState();
        // Initialize debug panel to be hidden on startup
        UpdateDebugPanel();
#endif
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
        var statePath = Path.Combine(_configManager.AppDataPath, WINDOW_STATE_FILE);
        var state = _configManager.LoadWindowState<Models.WindowState>(statePath);
        if (state != null && state.State == System.Windows.WindowState.Maximized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }
        
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
            if (IsValidWindowPosition(state.Left, state.Top, state.Width, state.Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = state.Left;
                Top = state.Top;
                Width = state.Width;
                Height = state.Height;
            }
        }
    }

    private void SaveWindowState()
    {
        var bounds = WindowState == System.Windows.WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        var state = new Models.WindowState
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            State = WindowState
        };
        var statePath = Path.Combine(_configManager.AppDataPath, WINDOW_STATE_FILE);
        _configManager.SaveWindowState(state, statePath);
    }

    private bool IsValidWindowPosition(double left, double top, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var virtualScreenLeft = SystemParameters.VirtualScreenLeft;
        var virtualScreenTop = SystemParameters.VirtualScreenTop;
        var virtualScreenWidth = SystemParameters.VirtualScreenWidth;
        var virtualScreenHeight = SystemParameters.VirtualScreenHeight;
        
        var virtualScreenRect = new Rect(
            virtualScreenLeft,
            virtualScreenTop,
            virtualScreenWidth,
            virtualScreenHeight
        );
        
        var windowRect = new Rect(left, top, width, height);
        
        return virtualScreenRect.IntersectsWith(windowRect);
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
        var tabs = new List<(TerminalTabItem tab, SshSessionConfiguration config)>();
        
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

                    var activeConfig = CopySessionConfiguration(sshConfig);
                    var tab = new TerminalTabItem();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        TerminalTabs.Items.Add(tab);
                    });
                    
                    tabs.Add((tab, activeConfig));
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

            foreach (var item in tabs)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TerminalTabs.SelectedItem = item.tab;
                });
                await Dispatcher.InvokeAsync(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Loaded);
                try
                {
                    var (connected, error) = await ConnectSessionAsync(item.tab, item.config, removeTabOnFailure: true);
                    if (!connected)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error connecting session {item.config.Id}: {error ?? "Unknown error"}");
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
                    });
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (tabs.Count > 0 && TerminalTabs.Items.Contains(tabs[0].tab))
                {
                    TerminalTabs.SelectedItem = tabs[0].tab;
                }
                UpdateTitleBarColor();
            });

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
        var dialog = new ConnectionDialog(_sessionManager)
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
            var dialog = new ConnectionDialog(sshConfig, false, _sessionManager)
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
                sshConfig.GatewaySessionId = newConfig.GatewaySessionId;
                _sessionManager.UpdateSession(sshConfig);
                SessionManagerPanel.RefreshAfterEdit();
            }
        }
    }

    private SshSessionConfiguration CopySessionConfiguration(SshSessionConfiguration source)
    {
        return new SshSessionConfiguration
        {
            Id = source.Id,
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            UsePasswordAuthentication = source.UsePasswordAuthentication,
            KeepAliveInterval = source.KeepAliveInterval,
            ConnectionTimeout = source.ConnectionTimeout,
            CompressionEnabled = source.CompressionEnabled,
            X11ForwardingEnabled = source.X11ForwardingEnabled,
            BellNotification = source.BellNotification,
            PortForwardingRules = source.PortForwardingRules.Select(r => new PortForwardingRule
            {
                Name = r.Name,
                IsLocal = r.IsLocal,
                LocalHost = r.LocalHost,
                LocalPort = r.LocalPort,
                RemoteHost = r.RemoteHost,
                RemotePort = r.RemotePort,
                Enabled = r.Enabled
            }).ToList(),
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            ForegroundColor = source.ForegroundColor,
            BackgroundColor = source.BackgroundColor,
            TerminalResizeMethod = source.TerminalResizeMethod,
            ResetScrollOnUserInput = source.ResetScrollOnUserInput,
            ResetScrollOnServerOutput = source.ResetScrollOnServerOutput,
            ScreenSessionName = source.ScreenSessionName,
            BackspaceKey = source.BackspaceKey,
            AutoReconnectMode = source.AutoReconnectMode,
            GatewaySessionId = source.GatewaySessionId,
            Color = source.Color,
            LineEnding = source.LineEnding,
            Encoding = source.Encoding,
            Group = source.Group,
            Order = source.Order
        };
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

            var activeConfig = CopySessionConfiguration(config);
            var tab = new TerminalTabItem();
            TerminalTabs.Items.Add(tab);
            TerminalTabs.SelectedItem = tab;
            UpdateTitleBarColor();

            var (connected, error) = await ConnectSessionAsync(tab, activeConfig, removeTabOnFailure: true);
            
            if (connected)
            {
                StatusTextBlock.Text = $"Connected to {activeConfig.Name}";
            }
            else
            {
                StatusTextBlock.Text = "Connection failed";
                var errorMessage = string.IsNullOrEmpty(error) 
                    ? $"Failed to connect to {activeConfig.Name}" 
                    : $"Failed to connect to {activeConfig.Name}: {error}";
                ShowNotification(errorMessage, NotificationType.Error);
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
    
    private async Task<(bool connected, string? error)> ConnectSessionAsync(TerminalTabItem tab, SshSessionConfiguration config, bool removeTabOnFailure = false)
    {
        string? lastError = null;
        ITerminalConnection? connection = null;
        
        try
        {
            var activeConfig = CopySessionConfiguration(config);
            connection = ConnectionFactory.CreateConnection(activeConfig, _knownHostsManager, ShowHostKeyVerificationDialog, _sessionManager);
            
            connection.ErrorOccurred += (sender, error) =>
            {
                lastError = error;
                OnErrorOccurred(sender, error);
            };

            await Dispatcher.InvokeAsync(() =>
            {
                tab.AttachConnection(connection, activeConfig.Color, activeConfig);
            });
            
            var connected = await connection.ConnectAsync();

            if (connected)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    tab.SetConnected();
                });
                return (true, null);
            }
            else
            {
                if (removeTabOnFailure)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (TerminalTabs.Items.Contains(tab))
                        {
                            TerminalTabs.Items.Remove(tab);
                        }
                    });
                }
                connection?.Dispose();
                return (false, lastError);
            }
        }
        catch (Exception ex)
        {
            if (removeTabOnFailure)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (TerminalTabs.Items.Contains(tab))
                    {
                        TerminalTabs.Items.Remove(tab);
                    }
                });
            }
            connection?.Dispose();
            return (false, ex.Message);
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

#if DEBUG
    private Views.DebugPanel? _currentDebugPanel = null;

    private void DebugModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab)
        {
            var terminal = tab.Terminal;
            if (terminal != null)
            {
                terminal.DebugMode = !terminal.DebugMode;
                UpdateDebugMenuState();
                UpdateDebugPanel();
                
                StatusTextBlock.Text = terminal.DebugMode ? "Debug mode enabled" : "Debug mode disabled";
            }
        }
    }

    private void UpdateDebugMenuState()
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab)
        {
            var terminal = tab.Terminal;
            if (DebugModeMenuItem != null)
            {
                DebugModeMenuItem.IsChecked = terminal?.DebugMode ?? false;
            }
        }
    }

    private void UpdateDebugPanel()
    {
#if DEBUG
        // Get the Grid that contains the debug panel
        var terminalGrid = TerminalTabs.Parent as Grid;
        if (terminalGrid == null) return;
        
        // Get the column definitions
        var debugSplitterColumn = terminalGrid.ColumnDefinitions[1];
        var debugPanelColumn = terminalGrid.ColumnDefinitions[2];
        
        if (TerminalTabs.SelectedItem is TerminalTabItem tab)
        {
            var terminal = tab.Terminal;
            if (terminal != null && terminal.DebugMode && terminal._emulator != null)
            {
                if (_currentDebugPanel == null)
                {
                    _currentDebugPanel = new Views.DebugPanel(terminal._emulator, terminal);
                    DebugPanelContainer.Content = _currentDebugPanel;
                }
                DebugPanelContainer.Visibility = Visibility.Visible;
                DebugPanelSplitter.Visibility = Visibility.Visible;
                // Restore column widths and constraints
                debugSplitterColumn.Width = new GridLength(5);
                debugPanelColumn.Width = new GridLength(400, GridUnitType.Pixel);
                debugPanelColumn.MinWidth = 300;
                debugPanelColumn.MaxWidth = 600;
            }
            else
            {
                DebugPanelContainer.Visibility = Visibility.Collapsed;
                DebugPanelSplitter.Visibility = Visibility.Collapsed;
                // Set column widths to 0 and remove MinWidth constraint to hide the panel completely
                debugSplitterColumn.Width = new GridLength(0);
                debugPanelColumn.Width = new GridLength(0);
                debugPanelColumn.MinWidth = 0;
                debugPanelColumn.MaxWidth = 0;
                if (_currentDebugPanel != null)
                {
                    DebugPanelContainer.Content = null;
                    _currentDebugPanel = null;
                }
            }
        }
        else
        {
            DebugPanelContainer.Visibility = Visibility.Collapsed;
            DebugPanelSplitter.Visibility = Visibility.Collapsed;
            // Set column widths to 0 and remove MinWidth constraint to hide the panel completely
            debugSplitterColumn.Width = new GridLength(0);
            debugPanelColumn.Width = new GridLength(0);
            debugPanelColumn.MinWidth = 0;
            debugPanelColumn.MaxWidth = 0;
            if (_currentDebugPanel != null)
            {
                DebugPanelContainer.Content = null;
                _currentDebugPanel = null;
            }
        }
        
        // Update terminal size after debug panel visibility changes
        // This is a programmatic resize (not user resizing), so send immediately
        if (TerminalTabs.SelectedItem is TerminalTabItem selectedTab)
        {
            var selectedTerminal = selectedTab.Terminal;
            if (selectedTerminal != null)
            {
                // Force a size update to account for debug panel
                selectedTerminal.UpdateTerminalSize();
                // Send immediately for programmatic resize (debug panel visibility change)
                selectedTerminal.SendTerminalSizeToServer(force: true);
            }
        }
#endif
    }
#endif

    private async void TerminalTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TerminalTabItem tab && tab.Connection != null)
        {
            try
            {
                bool isConnected = tab.Connection.IsConnected;
                StatusTextBlock.Text = isConnected 
                    ? $"Connected to {tab.ConnectionName}" 
                    : $"{tab.ConnectionName} (Disconnected)";
                
                if (isConnected)
                {
                    tab.UpdateStatusIndicator(ConnectionStatus.Connected);
                    _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        tab.Terminal.UpdateTerminalSize();
                        tab.Terminal.SendTerminalSizeToServer(force: true);
                    }));
                }
                else if (tab.SessionConfig != null && 
                    (tab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnFocus || 
                     tab.SessionConfig.AutoReconnectMode == AutoReconnectMode.OnDisconnect))
                {
                    await tab.ReconnectAsync();
                }
            }
            catch (ObjectDisposedException)
            {
                tab.UpdateStatusIndicator(ConnectionStatus.Disconnected);
                StatusTextBlock.Text = $"{tab.ConnectionName} (Disconnected)";
            }
        }
        else
        {
            StatusTextBlock.Text = "Ready";
        }
        
        UpdateTitleBarColor();
        
#if DEBUG
        UpdateDebugMenuState();
        UpdateDebugPanel();
#endif
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
            var (connected, error) = await ConnectSessionAsync(tab, config, removeTabOnFailure: false);
            
            if (connected)
            {
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
                var errorMessage = string.IsNullOrEmpty(error) 
                    ? $"Failed to reconnect to {config.Name}" 
                    : $"Failed to reconnect to {config.Name}: {error}";
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

