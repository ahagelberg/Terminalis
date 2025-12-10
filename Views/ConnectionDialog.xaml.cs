using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using Microsoft.Win32;
using TabbySSH.Models;
using TabbySSH.Views;

namespace TabbySSH.Views;

public class BoolToIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? 0 : 1;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue == 0;
        }
        return true;
    }
}

public partial class ConnectionDialog : Window
{
    public SshSessionConfiguration? Configuration { get; private set; }
    public bool IsLiveEditMode { get; private set; }
    private Services.SessionManager? _sessionManager;

    public ConnectionDialog(Services.SessionManager? sessionManager = null)
    {
        InitializeComponent();
        _sessionManager = sessionManager;
        PortForwardingDataGrid.ItemsSource = new ObservableCollection<PortForwardingRule>();
        PasswordAuthRadio_Checked(null, null);
        NameTextBox.Focus();
        
        _selectedForegroundColor = null;
        _selectedBackgroundColor = null;
        _selectedAccentColor = null;
        
        Loaded += (s, e) =>
        {
            UpdateColorBoxes();
            LoadGatewaySessions();
        };
    }

    public ConnectionDialog(SshSessionConfiguration existingConfig, bool liveEditMode = false, Services.SessionManager? sessionManager = null) : this(sessionManager)
    {
        NameTextBox.Text = existingConfig.Name;
        HostTextBox.Text = existingConfig.Host;
        PortTextBox.Text = existingConfig.Port.ToString();
        UsernameTextBox.Text = existingConfig.Username;
        PasswordBox.Password = existingConfig.Password ?? string.Empty;
        PrivateKeyPathTextBox.Text = existingConfig.PrivateKeyPath ?? string.Empty;
        PrivateKeyPassphraseBox.Password = existingConfig.PrivateKeyPassphrase ?? string.Empty;
        PasswordAuthRadio.IsChecked = existingConfig.UsePasswordAuthentication;
        KeyAuthRadio.IsChecked = !existingConfig.UsePasswordAuthentication;
        
        KeepAliveTextBox.Text = existingConfig.KeepAliveInterval.ToString();
        TimeoutTextBox.Text = existingConfig.ConnectionTimeout.ToString();
        CompressionCheckBox.IsChecked = existingConfig.CompressionEnabled;
        ResetScrollOnUserInputCheckBox.IsChecked = existingConfig.ResetScrollOnUserInput;
        ResetScrollOnServerOutputCheckBox.IsChecked = existingConfig.ResetScrollOnServerOutput;
        ScreenSessionNameTextBox.Text = existingConfig.ScreenSessionName ?? string.Empty;
        X11ForwardingCheckBox.IsChecked = existingConfig.X11ForwardingEnabled;
        
        BellNotificationComboBox.SelectedIndex = existingConfig.BellNotification switch
        {
            "Flash" => 0,
            "Line Flash" => 1,
            "Sound" => 2,
            "None" => 3,
            _ => 0
        };
        
        TerminalResizeMethodComboBox.SelectedIndex = existingConfig.TerminalResizeMethod switch
        {
            "SSH" => 0,
            "ANSI" => 1,
            "STTY" => 2,
            "XTERM" => 3,
            "NONE" => 4,
            _ => 0
        };
        
        _selectedAccentColor = existingConfig.Color;
        
        FontFamilyComboBox.Text = existingConfig.FontFamily;
        FontSizeTextBox.Text = existingConfig.FontSize.ToString();
        
        _selectedForegroundColor = existingConfig.ForegroundColor;
        _selectedBackgroundColor = existingConfig.BackgroundColor;
        UpdateColorBoxes();
        
        var normalizedLineEnding = ConvertLineEndingString(existingConfig.LineEnding);
        LineEndingComboBox.SelectedIndex = normalizedLineEnding switch
        {
            "\n" => 0,
            "\r\n" => 1,
            _ => 0
        };
        
        BackspaceKeyComboBox.SelectedIndex = existingConfig.BackspaceKey switch
        {
            "DEL" => 0,
            "CtrlH" => 1,
            _ => 0
        };
        
        AutoReconnectComboBox.SelectedIndex = existingConfig.AutoReconnectMode switch
        {
            AutoReconnectMode.None => 0,
            AutoReconnectMode.OnDisconnect => 1,
            AutoReconnectMode.OnFocus => 2,
            _ => 0
        };
        
        if (existingConfig.PortForwardingRules != null && existingConfig.PortForwardingRules.Count > 0)
        {
            var collection = PortForwardingDataGrid.ItemsSource as ObservableCollection<PortForwardingRule>;
            collection?.Clear();
            foreach (var rule in existingConfig.PortForwardingRules)
            {
                collection?.Add(rule);
            }
        }
        
        PasswordAuthRadio_Checked(null, null);
        Title = liveEditMode ? "Edit Session Settings" : "Edit Session";
        _existingConfig = existingConfig;
        IsLiveEditMode = liveEditMode;
        
        if (liveEditMode)
        {
            HideConnectionFields();
        }
    }
    
    private void HideConnectionFields()
    {
        var connectionGroupBox = FindName("ConnectionGroupBox") as GroupBox;
        if (connectionGroupBox != null)
        {
            connectionGroupBox.Visibility = Visibility.Collapsed;
        }
        
        var authenticationGroupBox = FindName("AuthenticationGroupBox") as GroupBox;
        if (authenticationGroupBox != null)
        {
            authenticationGroupBox.Visibility = Visibility.Collapsed;
        }
        
        var connectionOptionsGroupBox = FindName("ConnectionOptionsGroupBox") as GroupBox;
        if (connectionOptionsGroupBox != null)
        {
            var keepAliveGrid = FindName("KeepAliveGrid") as Grid;
            if (keepAliveGrid != null) keepAliveGrid.Visibility = Visibility.Collapsed;
            
            var timeoutGrid = FindName("TimeoutGrid") as Grid;
            if (timeoutGrid != null) timeoutGrid.Visibility = Visibility.Collapsed;
            
            var compressionCheckBox = FindName("CompressionCheckBox") as CheckBox;
            if (compressionCheckBox != null) compressionCheckBox.Visibility = Visibility.Collapsed;
        }
        
        var forwardingGroupBox = FindName("ForwardingGroupBox") as GroupBox;
        if (forwardingGroupBox != null)
        {
            forwardingGroupBox.Visibility = Visibility.Collapsed;
        }
    }

    private SshSessionConfiguration? _existingConfig;
    private string? _selectedForegroundColor;
    private string? _selectedBackgroundColor;
    private string? _selectedAccentColor;

    private void LoadGatewaySessions()
    {
        if (_sessionManager != null && GatewaySessionComboBox != null)
        {
            var sessions = new List<object>();
            sessions.Add(new { Id = (string?)null, Name = "(None)" });
            
            var sshSessions = _sessionManager.Sessions
                .OfType<SshSessionConfiguration>()
                .Where(s => s.Id != _existingConfig?.Id)
                .OrderBy(s => s.Name)
                .ToList();
            
            sessions.AddRange(sshSessions);
            GatewaySessionComboBox.ItemsSource = sessions;
            
            if (_existingConfig?.GatewaySessionId != null)
            {
                GatewaySessionComboBox.SelectedValue = _existingConfig.GatewaySessionId;
            }
            else
            {
                GatewaySessionComboBox.SelectedValue = null;
            }
        }
    }

    private void PasswordAuthRadio_Checked(object? sender, RoutedEventArgs? e)
    {
        if (PasswordBox != null)
        {
            PasswordBox.IsEnabled = true;
            PasswordBox.Visibility = Visibility.Visible;
        }
    }

    private void KeyAuthRadio_Checked(object? sender, RoutedEventArgs? e)
    {
        if (PasswordBox != null)
        {
            PasswordBox.IsEnabled = false;
            PasswordBox.Visibility = Visibility.Collapsed;
        }
    }

    private void BrowseKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Private Key Files (*.pem;*.key;*.ppk)|*.pem;*.key;*.ppk|All Files (*.*)|*.*",
            Title = "Select Private Key File"
        };

        if (dialog.ShowDialog() == true)
        {
            PrivateKeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void AddLocalForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardingDataGrid.ItemsSource is ObservableCollection<PortForwardingRule> collection)
        {
            collection.Add(new PortForwardingRule
            {
                Name = $"Local Forward {collection.Count + 1}",
                IsLocal = true,
                Enabled = true
            });
        }
    }

    private void AddRemoteForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (PortForwardingDataGrid.ItemsSource is ObservableCollection<PortForwardingRule> collection)
        {
            collection.Add(new PortForwardingRule
            {
                Name = $"Remote Forward {collection.Count + 1}",
                IsLocal = false,
                Enabled = true
            });
        }
    }

    private void TypeComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.DataContext is PortForwardingRule rule)
        {
            comboBox.SelectedIndex = rule.IsLocal ? 0 : 1;
        }
    }

    private void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.DataContext is PortForwardingRule rule)
        {
            rule.IsLocal = comboBox.SelectedIndex == 0;
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostTextBox.Text))
        {
            MessageBox.Show("Host is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            HostTextBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            MessageBox.Show("Username is required.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            UsernameTextBox.Focus();
            return;
        }

        if (!int.TryParse(PortTextBox.Text, out int port))
        {
            port = 22;
        }

        if (KeyAuthRadio.IsChecked == true && string.IsNullOrWhiteSpace(PrivateKeyPathTextBox.Text))
        {
            MessageBox.Show("Private key file is required when using key authentication.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            PrivateKeyPathTextBox.Focus();
            return;
        }

        if (KeyAuthRadio.IsChecked == true && !File.Exists(PrivateKeyPathTextBox.Text))
        {
            MessageBox.Show("Private key file does not exist.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            PrivateKeyPathTextBox.Focus();
            return;
        }

        if (!int.TryParse(KeepAliveTextBox.Text, out int keepAlive) || keepAlive < 0)
        {
            keepAlive = 30;
        }

        if (!int.TryParse(TimeoutTextBox.Text, out int timeout) || timeout < 0)
        {
            timeout = 30;
        }

        var sessionName = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sessionName))
        {
            sessionName = $"{UsernameTextBox.Text}@{HostTextBox.Text}";
        }

        var portForwardingRules = new List<PortForwardingRule>();
        if (PortForwardingDataGrid.ItemsSource is ObservableCollection<PortForwardingRule> collection)
        {
            portForwardingRules.AddRange(collection);
        }

        Configuration = new SshSessionConfiguration
        {
            Id = _existingConfig?.Id ?? Guid.NewGuid().ToString(),
            Host = HostTextBox.Text,
            Port = port,
            Username = UsernameTextBox.Text,
            Password = PasswordBox.Password,
            Name = sessionName,
            UsePasswordAuthentication = PasswordAuthRadio.IsChecked == true,
            PrivateKeyPath = KeyAuthRadio.IsChecked == true ? PrivateKeyPathTextBox.Text : null,
            PrivateKeyPassphrase = KeyAuthRadio.IsChecked == true ? PrivateKeyPassphraseBox.Password : null,
            KeepAliveInterval = keepAlive,
            ConnectionTimeout = timeout,
            CompressionEnabled = CompressionCheckBox.IsChecked == true,
            ResetScrollOnUserInput = ResetScrollOnUserInputCheckBox.IsChecked == true,
            ResetScrollOnServerOutput = ResetScrollOnServerOutputCheckBox.IsChecked == true,
            ScreenSessionName = string.IsNullOrWhiteSpace(ScreenSessionNameTextBox.Text) ? null : ScreenSessionNameTextBox.Text.Trim(),
            X11ForwardingEnabled = X11ForwardingCheckBox.IsChecked == true,
            BellNotification = BellNotificationComboBox.SelectedIndex switch
            {
                0 => "Flash",
                1 => "Line Flash",
                2 => "Sound",
                3 => "None",
                _ => "Flash"
            },
            TerminalResizeMethod = TerminalResizeMethodComboBox.SelectedItem is ComboBoxItem resizeItem && resizeItem.Tag is string resizeTag ? resizeTag : "SSH",
            Color = _selectedAccentColor,
            LineEnding = ConvertLineEndingString(LineEndingComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "\n"),
            Encoding = _existingConfig?.Encoding ?? "UTF-8",
            Group = _existingConfig?.Group,
            Order = _existingConfig?.Order ?? 0,
            PortForwardingRules = portForwardingRules,
            FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text) ? "Consolas" : FontFamilyComboBox.Text,
                FontSize = double.TryParse(FontSizeTextBox.Text, out double fontSize) && fontSize > 0 ? fontSize : 12.0,
            ForegroundColor = _selectedForegroundColor,
            BackgroundColor = _selectedBackgroundColor,
            BackspaceKey = BackspaceKeyComboBox.SelectedItem is ComboBoxItem backspaceItem && backspaceItem.Tag is string backspaceTag ? backspaceTag : "DEL",
            AutoReconnectMode = AutoReconnectComboBox.SelectedIndex switch
            {
                0 => AutoReconnectMode.None,
                1 => AutoReconnectMode.OnDisconnect,
                2 => AutoReconnectMode.OnFocus,
                _ => AutoReconnectMode.None
            },
            GatewaySessionId = GatewaySessionComboBox?.SelectedValue == null || GatewaySessionComboBox.SelectedValue.ToString() == "(None)" 
                ? null 
                : GatewaySessionComboBox.SelectedValue.ToString()
        };

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button)
            {
                return;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source) ?? System.Windows.LogicalTreeHelper.GetParent(source);
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == System.Windows.WindowState.Maximized ? System.Windows.WindowState.Normal : System.Windows.WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
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
            var titleBarForeground = Application.Current.Resources["TitleBarForeground"] as SolidColorBrush;
            if (titleBarForeground != null)
            {
                button.Foreground = titleBarForeground;
            }
            else
            {
                button.Foreground = new SolidColorBrush(Colors.Black);
            }
        }
    }

    private void FontSizeUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(FontSizeTextBox.Text, out double size))
        {
            size = Math.Min(size + 1, 72);
            FontSizeTextBox.Text = size.ToString();
        }
        else
        {
            FontSizeTextBox.Text = "12";
        }
    }

    private void FontSizeDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(FontSizeTextBox.Text, out double size))
        {
            size = Math.Max(size - 1, 6);
            FontSizeTextBox.Text = size.ToString();
        }
        else
        {
            FontSizeTextBox.Text = "12";
        }
    }

    private void KeepAliveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(KeepAliveTextBox.Text, out int value))
        {
            value = Math.Min(value + 1, 3600);
            KeepAliveTextBox.Text = value.ToString();
        }
        else
        {
            KeepAliveTextBox.Text = "30";
        }
    }

    private void KeepAliveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(KeepAliveTextBox.Text, out int value))
        {
            value = Math.Max(value - 1, 0);
            KeepAliveTextBox.Text = value.ToString();
        }
        else
        {
            KeepAliveTextBox.Text = "30";
        }
    }

    private void TimeoutUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TimeoutTextBox.Text, out int value))
        {
            value = Math.Min(value + 1, 3600);
            TimeoutTextBox.Text = value.ToString();
        }
        else
        {
            TimeoutTextBox.Text = "30";
        }
    }

    private void TimeoutDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TimeoutTextBox.Text, out int value))
        {
            value = Math.Max(value - 1, 0);
            TimeoutTextBox.Text = value.ToString();
        }
        else
        {
            TimeoutTextBox.Text = "30";
        }
    }

    private void PortUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PortTextBox.Text, out int port))
        {
            port = Math.Min(port + 1, 65535);
            PortTextBox.Text = port.ToString();
        }
        else
        {
            PortTextBox.Text = "22";
        }
    }

    private void PortDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PortTextBox.Text, out int port))
        {
            port = Math.Max(port - 1, 1);
            PortTextBox.Text = port.ToString();
        }
        else
        {
            PortTextBox.Text = "22";
        }
    }


    private void ForegroundColorBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dialog = new ColorPickerDialog(_selectedForegroundColor)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedForegroundColor = dialog.SelectedColor;
            UpdateColorBoxes();
        }
    }

    private void BackgroundColorBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dialog = new ColorPickerDialog(_selectedBackgroundColor)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedBackgroundColor = dialog.SelectedColor;
            UpdateColorBoxes();
        }
    }

    private void ForegroundColorDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedForegroundColor = null;
        UpdateColorBoxes();
    }

    private void BackgroundColorDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedBackgroundColor = null;
        UpdateColorBoxes();
    }

    private void UpdateColorBoxes()
    {
        if (ForegroundColorBox != null)
        {
            if (string.IsNullOrEmpty(_selectedForegroundColor))
            {
                ForegroundColorBox.Background = new SolidColorBrush(Colors.LightGray);
            }
            else
            {
                try
                {
                    var brush = new BrushConverter().ConvertFromString(_selectedForegroundColor) as SolidColorBrush;
                    ForegroundColorBox.Background = brush ?? new SolidColorBrush(Colors.LightGray);
                }
                catch
                {
                    ForegroundColorBox.Background = new SolidColorBrush(Colors.LightGray);
                }
            }
        }

        if (BackgroundColorBox != null)
        {
            if (string.IsNullOrEmpty(_selectedBackgroundColor))
            {
                BackgroundColorBox.Background = new SolidColorBrush(Colors.Black);
            }
            else
            {
                try
                {
                    var brush = new BrushConverter().ConvertFromString(_selectedBackgroundColor) as SolidColorBrush;
                    BackgroundColorBox.Background = brush ?? new SolidColorBrush(Colors.Black);
                }
                catch
                {
                    BackgroundColorBox.Background = new SolidColorBrush(Colors.Black);
                }
            }
        }

        if (AccentColorBox != null)
        {
            if (string.IsNullOrEmpty(_selectedAccentColor))
            {
                AccentColorBox.Background = Brushes.Transparent;
            }
            else
            {
                try
                {
                    var brush = new BrushConverter().ConvertFromString(_selectedAccentColor) as SolidColorBrush;
                    AccentColorBox.Background = brush ?? Brushes.Transparent;
                }
                catch
                {
                    AccentColorBox.Background = Brushes.Transparent;
                }
            }
        }
    }

    private void AccentColorBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dialog = new ColorPickerDialog(_selectedAccentColor)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedAccentColor = dialog.SelectedColor;
            UpdateColorBoxes();
        }
    }

    private void AccentColorDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedAccentColor = null;
        UpdateColorBoxes();
    }

    private static string ConvertLineEndingString(string lineEnding)
    {
        if (string.IsNullOrEmpty(lineEnding))
        {
            return "\n";
        }

        if (lineEnding == "\\n" || lineEnding == @"\n")
        {
            return "\n";
        }

        if (lineEnding == "\\r\\n" || lineEnding == @"\r\n")
        {
            return "\r\n";
        }

        if (lineEnding == "\n" || lineEnding == "\r\n")
        {
            return lineEnding;
        }

        return "\n";
    }
}

